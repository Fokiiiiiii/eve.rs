using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public sealed class GameRpcServicesTests
{
    [Fact]
    public void Core_catalog_registers_every_audited_rpc_once()
    {
        var services = CoreServices.Create(new NoAccounts(), () => 0);
        var expected = new Dictionary<string, string[]>
        {
            ["raffleMgr"] = ["QA_SeedRaffles"],
            ["serviceGatewayMgr"] = ["qa_disable_hijack_mode", "qa_enable_hijack_mode", "qa_is_hijack_mode_enabled"],
            ["sharedSettingsMgr"] = ["GetStoredSharedSetting", "StoreSettingLinkAndGetID"],
            ["shipStanceMgr"] = ["SetShipStance"],
            ["solarsystemInterferenceMgr"] = ["GetAllInterferenceBands", "GetLocalInterferenceState"],
            ["survey"] = ["ClaimSurveyRewards", "GetActiveSurveyID", "PerformSurveyChecks"],
            ["warStatisticMgr"] = ["GetKillMail"],
        };

        foreach (var (name, methods) in expected)
        {
            var service = Assert.Single(services, candidate => candidate.Name == name);
            foreach (var method in methods)
                Assert.True(service.HasMethod(method), $"{name}.{method}");
        }
    }

    [Fact]
    public async Task Raffle_and_gateway_methods_keep_the_reference_return_shapes()
    {
        var seededQuantity = -1;
        var callbacks = new GameRpcCallbacks(
            SeedRafflesAsync: (quantity, _) =>
            {
                seededQuantity = quantity ?? -1;
                return ValueTask.FromResult<int?>(quantity ?? 8);
            });
        var services = CoreServices.Create(new NoAccounts(), () => 1_700_000_000_000,
            gameRpcCallbacks: callbacks, contractReadModel: new ContractRpcReadModel());
        var raffle = services.Single(service => service.Name == "raffleMgr");
        var raffleProxy = services.Single(service => service.Name == "raffleProxy");
        var contractProxy = services.Single(service => service.Name == "contractProxy");
        var jumpClone = services.Single(service => service.Name == "jumpCloneSvc");
        var gateway = services.Single(service => service.Name == "serviceGatewayMgr");

        Assert.Equal(new PyInt(3), await raffle.CallAsync(Call("QA_SeedRaffles", new PyInt(3))));
        Assert.Equal(3, seededQuantity);
        Assert.Equal(new PyInt(8), await raffle.CallAsync(Call("QA_SeedRaffles")));
        Assert.Equal(PyBool.False, await raffleProxy.CallAsync(Call("AmIBanned")));
        Assert.Equal(Py.KeyVal(("iskEscrow", Py.Number(0d)), ("itemsEscrow", new PyInt(0))),
            await contractProxy.CallAsync(Call("GetMyContractEscrow", Session(20))));
        Assert.Equal(new PyInt(Eve.Macho.MachoMessage.ProxyNodeId),
            await jumpClone.CallAsync(Call("MachoResolveObject")));

        Assert.Same(PyNone.Instance, await gateway.CallAsync(Call("qa_enable_hijack_mode", new PyStr("timeout"), new PyInt(15))));
        Assert.Equal(PyBool.True, await gateway.CallAsync(Call("qa_is_hijack_mode_enabled")));
        Assert.Same(PyNone.Instance, await gateway.CallAsync(Call("qa_disable_hijack_mode")));
        Assert.Equal(PyBool.False, await gateway.CallAsync(Call("qa_is_hijack_mode_enabled")));
    }

    [Fact]
    public async Task Shared_settings_store_deduplicates_payloads_and_returns_retrievable_keyvals()
    {
        JsonObject? stored = null;
        var callbacks = new GameRpcCallbacks(
            ReadSharedSettings: () => stored?.DeepClone() as JsonObject,
            WriteSharedSettingsAsync: (state, _) =>
            {
                stored = state.DeepClone() as JsonObject;
                return ValueTask.FromResult(true);
            });
        var service = Service("sharedSettingsMgr", callbacks, 1_700_000_000_000);
        var payload = new PyDict([
            new(new PyStr("z"), new PyInt(2)),
            new(new PyStr("a"), new PyStr("one")),
        ]);
        var call = Call("StoreSettingLinkAndGetID", Session(20), new PyInt(2), payload);

        var key = Assert.IsType<PyObject>(await service.CallAsync(call));
        Assert.Equal(new PyStr("util.KeyVal"), key.Name);
        var hash = Assert.IsType<PyStr>(Field(key, "hashvalue")).Value;
        Assert.Equal(40, hash.Length);
        Assert.Equal(new PyInt(1), Field(key, "sqID"));
        Assert.Equal(new PyInt(2), Field(key, "settingTypeID"));

        var storedPayload = await service.CallAsync(Call("GetStoredSharedSetting",
            new PyTuple([new PyStr(hash), new PyInt(1), new PyInt(2)])));
        Assert.Equal(new PyStr("[[\"a\",\"one\"],[\"z\",2]]"), storedPayload);

        var duplicate = Assert.IsType<PyObject>(await service.CallAsync(Call("StoreSettingLinkAndGetID", new PyInt(2),
            new PyDict([new(new PyStr("a"), new PyStr("one")), new(new PyStr("z"), new PyInt(2))]))));
        Assert.Equal(new PyInt(1), Field(duplicate, "sqID"));
        Assert.Same(PyNone.Instance, await service.CallAsync(Call("StoreSettingLinkAndGetID", new PyInt(4), payload)));
    }

    [Fact]
    public async Task Ship_stance_uses_backend_result_and_reference_custom_notify_on_failure()
    {
        long requestedShipId = 0;
        int requestedStanceId = 0;
        long requestedCharacterId = 0;
        var callbacks = new GameRpcCallbacks(SetShipStanceAsync: (shipId, stanceId, characterId, _) =>
        {
            requestedShipId = shipId;
            requestedStanceId = stanceId;
            requestedCharacterId = characterId;
            return ValueTask.FromResult<ShipStanceChange?>(new(true, 1));
        });
        var service = Service("shipStanceMgr", callbacks);
        var call = Call("SetShipStance", Session(44), new PyLong(74000001), new PyInt(3));
        Assert.Equal(new PyInt(1), await service.CallAsync(call));
        Assert.Equal(74000001, requestedShipId);
        Assert.Equal(3, requestedStanceId);
        Assert.Equal(44, requestedCharacterId);

        var failureService = Service("shipStanceMgr", new GameRpcCallbacks(
            SetShipStanceAsync: (_, _, _, _) => ValueTask.FromResult<ShipStanceChange?>(new(false, ErrorCode: "SHIP_NOT_OWNED"))));
        var failure = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await failureService.CallAsync(call));
        Assert.Equal("CustomNotify", failure.Error.ClassName);
    }

    [Fact]
    public async Task Interference_service_returns_objectex_state_and_only_active_bands()
    {
        const long now = 1_700_000_000_000;
        var nowFiletime = Py.FileTimeFromUnixMs(now);
        var table = new JsonObject
        {
            ["systems"] = new JsonObject
            {
                ["30000142"] = new JsonObject { ["interferenceLevel"] = 8000, ["lastDecayTickTimestamp"] = nowFiletime.ToString() },
                ["30000143"] = new JsonObject { ["interferenceLevel"] = 4000, ["lastDecayTickTimestamp"] = nowFiletime.ToString() },
                ["30000144"] = new JsonObject { ["interferenceLevel"] = 1000, ["lastDecayTickTimestamp"] = nowFiletime.ToString() },
                ["30000145"] = new JsonObject { ["interferenceLevel"] = 0, ["lastDecayTickTimestamp"] = nowFiletime.ToString() },
            },
        };
        var service = Service("solarsystemInterferenceMgr", new GameRpcCallbacks(
            ReadInterferenceTableAsync: _ => ValueTask.FromResult<JsonObject?>(table)), now);
        var bands = Assert.IsType<PyDict>(await service.CallAsync(Call("GetAllInterferenceBands")));
        Assert.Equal(3, bands.Entries.Count);
        Assert.Contains(bands.Entries, row => row.Key == new PyInt(30000142) && row.Value == new PyStr("H"));
        Assert.Contains(bands.Entries, row => row.Key == new PyInt(30000143) && row.Value == new PyStr("M"));
        Assert.Contains(bands.Entries, row => row.Key == new PyInt(30000144) && row.Value == new PyStr("L"));

        var localCall = Call("GetLocalInterferenceState", Session(20));
        localCall.Session.Set("solarsystemid2", new PyInt(30000142));
        var state = Assert.IsType<PyObjectEx>(await service.CallAsync(localCall));
        var header = Assert.IsType<PyTuple>(state.Header);
        Assert.Equal(new PyToken("solarsysteminterference.solarsystemInterferenceState.SolarsystemInterferenceState"), header.Items[0]);
        var fields = Assert.IsType<PyTuple>(header.Items[1]);
        Assert.Equal(6, fields.Items.Count);
        Assert.Equal(new PyInt(8000), fields.Items[0]);
        Assert.Equal(new PyLong(nowFiletime), fields.Items[1]);
    }

    [Fact]
    public async Task Survey_defaults_and_killmail_hash_follow_reference_shapes()
    {
        var events = new List<string>();
        var callbacks = new GameRpcCallbacks(
            ReadActiveSurveyIdAsync: _ => ValueTask.FromResult<int?>(73),
            RecordSurveyEventAsync: (eventName, _, _) =>
            {
                events.Add(eventName);
                return ValueTask.CompletedTask;
            });
        var survey = Service("survey", callbacks);
        Assert.Equal(new PyInt(73), await survey.CallAsync(Call("GetActiveSurveyID")));
        Assert.Same(PyNone.Instance, await survey.CallAsync(Call("PerformSurveyChecks")));
        Assert.Same(PyNone.Instance, await survey.CallAsync(Call("ClaimSurveyRewards")));
        Assert.Equal(new[] { "get_active_survey_id", "perform_checks", "claim_rewards" }, events);
        Assert.Same(PyNone.Instance, await Service("survey").CallAsync(Call("GetActiveSurveyID")));

        const string killTime = "133000000000000000";
        var record = new JsonObject
        {
            ["killID"] = 88,
            ["killTime"] = killTime,
            ["solarSystemID"] = 30000142,
            ["victimCharacterID"] = 11,
            ["victimShipTypeID"] = 600,
            ["finalCharacterID"] = 22,
            ["victimDamageTaken"] = 1500,
            ["finalDamageDone"] = 1200,
            ["killBlob"] = "blob",
        };
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("1122600" + killTime))).ToLowerInvariant();
        var warStats = Service("warStatisticMgr", new GameRpcCallbacks(
            ReadKillmailAsync: (id, _) => ValueTask.FromResult<JsonObject?>(id == 88 ? record : null)));
        var killmail = Assert.IsType<PyObject>(await warStats.CallAsync(Call("GetKillMail", new PyInt(88), new PyStr(hash))));
        Assert.Equal(new PyInt(88), Field(killmail, "killID"));
        Assert.Equal(new PyLong(long.Parse(killTime)), Field(killmail, "killTime"));
        Assert.Equal(new PyInt(1500), Field(killmail, "victimDamageTaken"));
        Assert.Equal(new PyStr("blob"), Field(killmail, "killBlob"));
        Assert.Same(PyNone.Instance, await warStats.CallAsync(Call("GetKillMail", new PyInt(88), new PyStr("bad-hash"))));
    }

    private static IService Service(string name, GameRpcCallbacks? callbacks = null, long now = 1_700_000_000_000) =>
        CoreServices.Create(new NoAccounts(), () => now,
                gameRpcCallbacks: callbacks)
            .Single(service => service.Name == name);

    private static ServiceCall Call(string method, params PyValue[] args) =>
        Call(method, new ClientSession(), args);

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session,
    };

    private static ClientSession Session(long characterId)
    {
        var session = new ClientSession();
        session.Set("charid", new PyInt(characterId));
        return session;
    }

    private static PyValue Field(PyObject value, string name)
    {
        var dict = Assert.IsType<PyDict>(value.Args);
        return dict.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }

    private sealed class ContractRpcReadModel : IContractReadModel
    {
        public ValueTask<ContractLoginInfo> GetLoginInfoAsync(
            long characterId, long corporationId, long allianceId, long nowFileTime, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ContractLoginInfo([], [], []));

        public ValueTask<ContractEscrowSummary> GetMyContractEscrowAsync(
            long characterId, long corporationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ContractEscrowSummary(0m, 0));
    }
}
