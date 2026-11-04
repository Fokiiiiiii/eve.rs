using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class DungeonKeeperClientContractTests
{
    [Fact]
    public async Task Escalation_and_operation_queries_preserve_v24_client_shapes()
    {
        var session = new ClientSession { CharacterId = 20 };
        session.Set("charid", new PyInt(20));
        session.Set("solarsystemid2", new PyInt(30000142));
        var header = new[] { "instanceID", "dungeon", "destDungeon", "srcDungeon", "expiryTime", "journalEntryTemplateID" };
        var rowset = new PyObject(
            new PyStr("eve.common.script.sys.rowset.Rowset"),
            Py.Dict(
                ("header", new PyList([.. header.Select(name => (PyValue)new PyStr(name))])),
                ("columns", new PyList([.. header.Select(name => (PyValue)new PyStr(name))])),
                ("RowClass", new PyToken("util.Row")),
                ("lines", new PyList([]))));
        var currentDungeon = Py.Tuple(new PyInt(5001), new PyInt(17), new PyInt(81), Py.Dict());
        ClientSession? dungeonSession = null;
        ClientSession? operationSession = null;
        long? requestedCharacterId = null;
        long? requestedSiteId = null;
        var inOperationSite = true;

        var services = CoreServices.Create(
            new PolicyOmegaAccounts(),
            () => 1234,
            readEscalatingPathDetails: (actualSession, _) =>
            {
                Assert.Same(session, actualSession);
                return ValueTask.FromResult<PyValue?>(rowset);
            },
            keeperRpcCallbacks: new KeeperRpcCallbacks(
                ReadCurrentDungeonAsync: (actualSession, _) =>
                {
                    dungeonSession = actualSession;
                    return ValueTask.FromResult<PyValue?>(currentDungeon);
                },
                ReadOperationSpawnpointSolarSystemAsync: (characterId, siteId, _) =>
                {
                    requestedCharacterId = characterId;
                    requestedSiteId = siteId;
                    return ValueTask.FromResult<long?>(30000142);
                },
                IsOperationSiteAsync: (actualSession, characterId, siteId, _) =>
                {
                    operationSession = actualSession;
                    requestedCharacterId = characterId;
                    requestedSiteId = siteId;
                    return ValueTask.FromResult(inOperationSite);
                }));

        var byName = services.ToDictionary(service => service.Name);
        var escalation = await byName["dungeonExplorationMgr"].CallAsync(Call("GetMyEscalatingPathDetails", session));
        Assert.Equal(ObjectCaching.MethodResult(rowset, 1234, "always"), escalation);

        var keeper = byName["keeper"];
        Assert.Equal(currentDungeon, await keeper.CallAsync(Call("GetCurrentDungeonForCharacter", session)));
        Assert.Same(session, dungeonSession);

        Assert.Equal(new PyInt(30000142), await keeper.CallAsync(Call(
            "GetOperationSpawnpointSolarSystem", session, new PyInt(20), new PyInt(901))));
        Assert.Equal(20, requestedCharacterId);
        Assert.Equal(901, requestedSiteId);

        Assert.Same(PyBool.True, await keeper.CallAsync(Call("IsOperationSite", session, new PyInt(901))));
        Assert.Same(session, operationSession);
        Assert.Equal(20, requestedCharacterId);
        Assert.Equal(901, requestedSiteId);
        inOperationSite = false;
        Assert.Same(PyBool.False, await keeper.CallAsync(Call("IsOperationSite", session, new PyInt(901))));
    }

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Kwargs = PyNone.Instance,
        Session = session,
    };

    private sealed class PolicyOmegaAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }
}
