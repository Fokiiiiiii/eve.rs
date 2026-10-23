using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class FactionActivityRpcServicesTests
{
    private static ServiceCall Call(IService service, string method, ClientSession? session = null, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session ?? new ClientSession(),
    };

    [Fact]
    public void Registers_all_twelve_EveJS_methods_without_service_aliases()
    {
        IService[] services =
        [
            new RWManagerService(),
            new FwCharacterEnlistmentMgrService(),
            new FwVictoryPointMgrService(),
            new InsurgencySolarsystemService(),
        ];

        Assert.Equal(["RWManager", "fwCharacterEnlistmentMgr", "fwVictoryPointMgr", "insurgencySolarsystem"], services.Select(service => service.Name));
        Assert.Equal(12, services.Sum(service => new[]
        {
            "get_closest_rw_stations", "solarsystem_contains_rw_instances",
            "CreateMyDirectEnlistment", "GetCorpAllowedEnlistmentFactions", "GetMyDirectEnlistmentCooldownTimestamp",
            "GetMyEnlistment", "RemoveMyDirectEnlistment", "SetMyCorpAllowedEnlistmentFactions",
            "GetAllVictoryPointStates", "GetLocalVictoryPointState",
            "GetAllVisibleCampaigns", "GetLocalCampaignClientSnapshot",
        }.Count(service.HasMethod)));
    }

    [Fact]
    public async Task Insurgency_without_a_campaign_provider_returns_reference_empty_shapes()
    {
        var service = new InsurgencySolarsystemService();
        var session = new ClientSession();
        session.Set("solarsystemid2", new PyInt(30000142));

        Assert.Equal(new PyList([]),
            await service.CallAsync(Call(service, "GetAllVisibleCampaigns", session)));
        Assert.Equal(new PyList([new PyInt(30000142), PyNone.Instance]),
            await service.CallAsync(Call(service, "GetLocalCampaignClientSnapshot", session)));
    }

    [Fact]
    public async Task Resource_wars_use_per_system_callbacks_and_only_return_known_LP_corporations()
    {
        var requestedSystem = 30000142L;
        var service = new RWManagerService(new RwManagerRpcCallbacks(
            (systemId, _) =>
            {
                Assert.Equal(requestedSystem, systemId);
                return ValueTask.FromResult<IReadOnlyDictionary<long, long>>(
                    new Dictionary<long, long> { [1000284] = 60000002, [1000283] = 60000001, [90000001] = 60000003 });
            },
            (systemId, _) => ValueTask.FromResult(systemId == requestedSystem)));
        var session = new ClientSession();
        session.Set("solarsystemid2", new PyInt((int)requestedSystem));

        var stations = Assert.IsType<PyDict>(await service.CallAsync(Call(service, "get_closest_rw_stations", session, new PyInt((int)requestedSystem))));
        Assert.Equal(
            [new KeyValuePair<PyValue, PyValue>(new PyInt(1000283), new PyInt(60000001)), new KeyValuePair<PyValue, PyValue>(new PyInt(1000284), new PyInt(60000002))],
            stations.Entries);
        Assert.Equal(PyBool.True, await service.CallAsync(Call(service, "solarsystem_contains_rw_instances", session)));
    }

    [Fact]
    public async Task Resource_wars_refuse_malformed_system_queries_before_callbacks()
    {
        var callbackCalls = 0;
        var service = new RWManagerService(new RwManagerRpcCallbacks(
            (_, _) =>
            {
                callbackCalls++;
                return ValueTask.FromResult<IReadOnlyDictionary<long, long>>(new Dictionary<long, long>());
            },
            (_, _) =>
            {
                callbackCalls++;
                return ValueTask.FromResult(false);
            }));
        var session = new ClientSession();
        session.Set("solarsystemid2", new PyInt(30000142));

        foreach (var call in new[]
        {
            Call(service, "get_closest_rw_stations", session),
            Call(service, "get_closest_rw_stations", session, new PyReal(30000142)),
            Call(service, "get_closest_rw_stations", session, new PyInt(-1)),
            new ServiceCall
            {
                Method = "get_closest_rw_stations", Args = [new PyInt(30000142)],
                Kwargs = new PyDict([]), Session = session,
            },
            Call(service, "solarsystem_contains_rw_instances", session, new PyInt(30000142)),
        })
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Contains(error.Error.UserErrorMessage, new[]
            {
                "RESOURCE_WARS_ARGUMENTS_INVALID", "RESOURCE_WARS_SYSTEM_ID_INVALID",
            });
        }

        Assert.Equal(0, callbackCalls);
    }

    [Fact]
    public async Task Enlistment_reads_corporation_policy_persists_direct_membership_and_changes_session()
    {
        var fileTime = 133000000000000000L;
        FwCharacterWarfareState? character = new(500001, 0);
        long savedActor = 0;
        long savedCorporation = 0;
        IReadOnlyList<long> savedAllowedFactions = [];
        var service = new FwCharacterEnlistmentMgrService(new FwCharacterEnlistmentRpcCallbacks(
            (characterId, _) => ValueTask.FromResult(characterId == 42 ? character : null),
            (corporationId, _) => ValueTask.FromResult<long?>(corporationId == 99 ? 500002 : null),
            (corporationId, _) => ValueTask.FromResult<IReadOnlyList<long>>(corporationId == 99 ? [500001, 500002] : []),
            (actorId, corporationId, factions, _) =>
            {
                savedActor = actorId;
                savedCorporation = corporationId;
                savedAllowedFactions = factions;
                return ValueTask.FromResult(true);
            },
            (characterId, factionId, cooldown, _) =>
            {
                if (characterId != 42)
                    return ValueTask.FromResult(false);
                character = new FwCharacterWarfareState(factionId, cooldown);
                return ValueTask.FromResult(true);
            },
            () => fileTime));
        var session = new ClientSession();
        session.Set("charid", new PyInt(42));
        session.Set("corpid", new PyInt(99));

        Assert.Equal(new PyList([new PyInt(500001), new PyInt(500001), new PyInt(500002)]),
            await service.CallAsync(Call(service, "GetMyEnlistment", session)));
        Assert.Equal(new PyLong(0), await service.CallAsync(Call(service, "GetMyDirectEnlistmentCooldownTimestamp", session)));
        Assert.Equal(new PyList([new PyInt(500001), new PyInt(500002)]),
            await service.CallAsync(Call(service, "GetCorpAllowedEnlistmentFactions", session, new PyInt(99))));

        var requestedPolicy = new PyList([new PyInt(500002), new PyInt(500001)]);
        Assert.Equal(requestedPolicy,
            await service.CallAsync(Call(service, "SetMyCorpAllowedEnlistmentFactions", session, requestedPolicy)));
        Assert.Equal((42L, 99L), (savedActor, savedCorporation));
        Assert.Equal(new long[] { 500002, 500001 }, savedAllowedFactions);

        Assert.Same(PyNone.Instance, await service.CallAsync(Call(service, "CreateMyDirectEnlistment", session, new PyInt(500002))));
        Assert.Equal(new FwCharacterWarfareState(500002, 0), character);
        Assert.Equal(new PyInt(500002), session.Get("warfactionid"));

        Assert.Same(PyNone.Instance, await service.CallAsync(Call(service, "RemoveMyDirectEnlistment", session)));
        Assert.Equal(new FwCharacterWarfareState(null, fileTime + TimeSpan.TicksPerDay), character);
        Assert.Equal(new PyInt(500002), session.Get("warfactionid"));
        Assert.Equal(new PyLong(fileTime + TimeSpan.TicksPerDay),
            await service.CallAsync(Call(service, "GetMyDirectEnlistmentCooldownTimestamp", session)));
    }

    [Fact]
    public async Task Victory_point_and_insurgency_responses_match_EveJS_object_and_collection_shapes()
    {
        var points = new FwVictoryPointMgrService(new FwVictoryPointRpcCallbacks(
            _ => ValueTask.FromResult<IReadOnlyDictionary<long, FwVictoryPointState>>(
                new Dictionary<long, FwVictoryPointState> { [30000142] = new(125, 75000) })));
        var session = new ClientSession();
        session.Set("solarsystemid2", new PyInt(30000142));

        var allPoints = Assert.IsType<PyDict>(await points.CallAsync(Call(points, "GetAllVictoryPointStates", session)));
        var pointObject = Assert.IsType<PyObjectEx>(Assert.Single(allPoints.Entries).Value);
        Assert.Equal(ObjectExKind.Ex1, pointObject.Kind);
        Assert.Equal(new PyToken("factionwarfare.victoryPointState.VictoryPointState"), Assert.IsType<PyTuple>(pointObject.Header).Items[0]);
        Assert.Equal(new PyList([new PyInt(125), new PyInt(75000)]), Assert.IsType<PyTuple>(pointObject.Header).Items[1]);
        var localPoint = Assert.IsType<PyList>(await points.CallAsync(Call(points, "GetLocalVictoryPointState", session)));
        Assert.Equal(new PyInt(30000142), localPoint.Items[0]);
        Assert.Equal(pointObject, localPoint.Items[1]);

        var campaign = new InsurgencyCampaignSnapshot(
            10, 133000000000000000L, 500012, 7000001, [30000142, 30000143], 7,
            1000, 250, 90000001, 3, 30000141, 500);
        var insurgency = new InsurgencySolarsystemService(new InsurgencySolarsystemRpcCallbacks(
            (systemId, _) => ValueTask.FromResult<IReadOnlyList<InsurgencyCampaignSnapshot>>(
                systemId is null or 30000142 ? [campaign] : [])));

        var allCampaigns = Assert.IsType<PyList>(await insurgency.CallAsync(Call(insurgency, "GetAllVisibleCampaigns", session)));
        var campaignObject = Assert.IsType<PyObjectEx>(Assert.Single(allCampaigns.Items));
        Assert.Equal(ObjectExKind.Ex2, campaignObject.Kind);
        var campaignHeader = Assert.IsType<PyTuple>(campaignObject.Header).Items;
        Assert.Equal(new PyToken("pirateinsurgency.campaignClientSnapshot.CampaignClientSnapshot"),
            Assert.IsType<PyTuple>(campaignHeader[0]).Items[0]);
        var fields = Assert.IsType<PyDict>(campaignHeader[1]);
        Assert.Contains(fields.Entries, entry => entry.Key == new PyStr("_campaignID") && entry.Value == new PyInt(7));
        Assert.Contains(fields.Entries, entry => entry.Key == new PyStr("_stateExpiryTimestamp") && entry.Value == new PyLong(133000000000000000L));
        Assert.Contains(fields.Entries, entry => entry.Key == new PyStr("_coveredSolarsystemIDs") && entry.Value is PyObjectEx { Kind: ObjectExKind.Ex1 });

        var localCampaign = Assert.IsType<PyList>(await insurgency.CallAsync(Call(insurgency, "GetLocalCampaignClientSnapshot", session)));
        Assert.Equal(new PyInt(30000142), localCampaign.Items[0]);
        Assert.Equal(campaignObject, localCampaign.Items[1]);
        session.Set("solarsystemid2", new PyInt(30000144));
        var absentCampaign = Assert.IsType<PyList>(await insurgency.CallAsync(Call(insurgency, "GetLocalCampaignClientSnapshot", session)));
        Assert.Equal(new PyInt(30000144), absentCampaign.Items[0]);
        Assert.Same(PyNone.Instance, absentCampaign.Items[1]);
    }
}
