using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class UnportedFeatureRpcServicesTests
{
    private static ServiceCall Call(string method, ClientSession? session = null, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session ?? new ClientSession(),
    };

    [Fact]
    public async Task Direct_service_rows_are_registered_with_reference_fallbacks()
    {
        IService[] services =
        [
            new AbyssalContentManagerService(),
            new DungeonExplorationMgrService(nowUnixMs: () => 1_700_000_000_000L),
            new KeeperService(),
            new MiningScanMgrService(),
            new NodeGraphService(),
            new PeaceTreatyMgrService(),
            new PublicQaToolsServerService(),
            new CorpFittingMgrService(),
            new CustomAgencyProviderService(),
            new EpicArcStatusService(),
            new SuperWeaponMgrService(),
            new TourneyMgrService(),
        ];
        var methods = new Dictionary<string, string[]>
        {
            ["abyssal_content_manager"] = ["generate_abyss_npc_spawn_report"],
            ["dungeonExplorationMgr"] = ["GetMyEscalatingPathDetails"],
            ["keeper"] = ["ActivateAccelerationGate", "GetCurrentDungeonForCharacter", "GetLevelEditor", "GetOperationSpawnpointSolarSystem", "IsOperationSite"],
            ["miningScanMgr"] = ["perform_scan"],
            ["node_graph"] = ["process_client_graph_message", "process_client_blackboard_request", "qa_subscribe", "qa_unsubscribe", "qa_start_node_graph", "qa_stop_node_graph", "qa_start_node", "qa_stop_node", "qa_stop_active_nodes"],
            ["peaceTreatyMgr"] = ["GetPeaceTreatiesForSession"],
            ["publicQaToolsServer"] = ["MoveMeTo", "SlashCmd", "CanGiveItemForMultifit"],
            ["corpFittingMgr"] = ["GetCommunityFittings"],
            ["custom_agency_provider"] = ["get_content_data", "warp_to_content"],
            ["epicArcStatus"] = ["AgentHasEpicMissionsForCharacter"],
            ["superWeaponMgr"] = ["ActivateSinglePointTargetedModule", "ActivateSlashModule"],
            ["tourneyMgr"] = ["GetTourneySetup", "QueryShipList", "GetPotentialMatches", "GetAllCurrentSeriesMatchDetails", "QueryTournamentDetails"],
        };
        Assert.Equal(methods.Keys.Order(), services.Select(service => service.Name).Order());
        foreach (var service in services)
            foreach (var method in methods[service.Name])
                Assert.True(service.HasMethod(method), $"{service.Name}.{method}");

        var abyssal = Assert.IsType<PyDict>(await ((IService)new AbyssalContentManagerService()).CallAsync(
            Call("generate_abyss_npc_spawn_report", args: [new PyInt(2), new PyInt(50), new PyInt(2000)])));
        Assert.Equal(new PyInt(2), Field(abyssal, "difficulty_tier"));
        Assert.Equal(new PyInt(50), Field(abyssal, "spawn_table_id"));
        Assert.Equal(new PyInt(1000), Field(abyssal, "repetitions"));
        Assert.Empty(Assert.IsType<PyList>(Field(abyssal, "spawns")).Items);

        var dungeon = Assert.IsType<PyObject>(await ((IService)new DungeonExplorationMgrService(
            nowUnixMs: () => 1_700_000_000_000L)).CallAsync(Call("GetMyEscalatingPathDetails")));
        var cachedArgs = Assert.IsType<PyTuple>(dungeon.Args);
        Assert.Equal(3, cachedArgs.Items.Count);
        Assert.IsType<PySubStream>(cachedArgs.Items[1]);

        Assert.Empty(Assert.IsType<PyList>(await ((IService)new MiningScanMgrService()).CallAsync(Call("perform_scan"))).Items);
        Assert.Empty(Assert.IsType<PyDict>(await ((IService)new NodeGraphService()).CallAsync(Call("qa_subscribe"))).Entries);
        Assert.Same(PyNone.Instance, await ((IService)new NodeGraphService()).CallAsync(Call("process_client_graph_message")));
        Assert.Same(PyBool.False, await ((IService)new PublicQaToolsServerService()).CallAsync(Call("CanGiveItemForMultifit")));
        Assert.Same(PyNone.Instance, await ((IService)new PublicQaToolsServerService()).CallAsync(Call("SlashCmd")));

        var treaties = Assert.IsType<PyTuple>(await ((IService)new PeaceTreatyMgrService()).CallAsync(Call("GetPeaceTreatiesForSession")));
        Assert.Equal(2, treaties.Items.Count);
        Assert.All(treaties.Items, value =>
        {
            var rowset = Assert.IsType<PyObjectEx>(value);
            Assert.Equal(ObjectExKind.Ex2, rowset.Kind);
            Assert.Empty(rowset.List);
        });

        Assert.Empty(Assert.IsType<PyList>(await ((IService)new CorpFittingMgrService()).CallAsync(Call("GetCommunityFittings"))).Items);
        var agency = (IService)new CustomAgencyProviderService();
        Assert.Empty(Assert.IsType<PyList>(await agency.CallAsync(Call("get_content_data"))).Items);
        Assert.Same(PyNone.Instance, await agency.CallAsync(Call("warp_to_content", args: [new PyInt(42)])));
        Assert.Same(PyBool.False, await ((IService)new EpicArcStatusService()).CallAsync(Call("AgentHasEpicMissionsForCharacter", args: [new PyInt(300)])));
        // unwired: an explicit user error, never PyNone (which the client reads as an activation that went through)
        var superWeapon = (IService)new SuperWeaponMgrService();
        var point = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(async () => await superWeapon.CallAsync(Call("ActivateSinglePointTargetedModule", args: [new PyInt(5), new PyInt(6), new PyStr("target")])));
        Assert.Equal("Superweapon activation is not wired on this server.", NotifyText(point));
        var slash = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(async () => await superWeapon.CallAsync(Call("ActivateSlashModule", args: [new PyInt(5), new PyStr("start"), new PyStr("end")])));
        Assert.Equal("Superweapon activation is not wired on this server.", NotifyText(slash));

        var tourney = (IService)new TourneyMgrService();
        var setup = Assert.IsType<PyTuple>(await tourney.CallAsync(Call("GetTourneySetup")));
        Assert.Equal(3, setup.Items.Count);
        Assert.All(setup.Items, item => Assert.Empty(Assert.IsType<PyList>(item).Items));
        Assert.Empty(Assert.IsType<PyList>(await tourney.CallAsync(Call("QueryShipList", args: [new PyInt(9)]))).Items);
        Assert.Empty(Assert.IsType<PyList>(await tourney.CallAsync(Call("GetPotentialMatches", args: [new PyInt(9)]))).Items);
        Assert.Empty(Assert.IsType<PyList>(await tourney.CallAsync(Call("GetAllCurrentSeriesMatchDetails", args: [new PyInt(9)]))).Items);
        Assert.Same(PyNone.Instance, await tourney.CallAsync(Call("QueryTournamentDetails", args: [new PyInt(9)])));
    }

    [Fact]
    public async Task Additional_services_are_registered_and_forward_typed_callback_data()
    {
        var session = new ClientSession();
        session.Set("charid", new PyInt(42));
        ClientSession? fittingSession = null;
        CustomAgencyContentRequest? agencyRequest = null;
        ClientSession? warpSession = null;
        long warpedContentId = 0;
        long queriedAgentId = 0;
        long queriedCharacterId = 0;
        SuperWeaponPointRequest? pointRequest = null;
        SuperWeaponSlashRequest? slashRequest = null;
        TourneyQueryRequest? tourneyRequest = null;

        var services = CoreServices.Create(
            new SubscriptionSettings(),
            new NoOmegaAccounts(),
            () => 0,
            readCommunityFittings: (actualSession, _) =>
            {
                fittingSession = actualSession;
                return ValueTask.FromResult<IReadOnlyList<PyValue>>([new PyStr("community fitting")]);
            },
            customAgencyProviderRpcCallbacks: new CustomAgencyProviderRpcCallbacks(
                ReadContentAsync: (request, _) =>
                {
                    agencyRequest = request;
                    return ValueTask.FromResult<IReadOnlyList<PyValue>>([new PyStr("agency content")]);
                },
                WarpToContentAsync: (contentId, actualSession, _) =>
                {
                    warpedContentId = contentId;
                    warpSession = actualSession;
                    return ValueTask.CompletedTask;
                }),
            readEpicArcStatus: (agentId, characterId, _) =>
            {
                queriedAgentId = agentId;
                queriedCharacterId = characterId;
                return ValueTask.FromResult(true);
            },
            superWeaponRpcCallbacks: new SuperWeaponRpcCallbacks(
                ActivatePointTargetedAsync: (request, _) =>
                {
                    pointRequest = request;
                    return ValueTask.FromResult(true);
                },
                ActivateSlashAsync: (request, _) =>
                {
                    slashRequest = request;
                    return ValueTask.FromResult(true);
                }),
            tourneyQueryCallback: (request, _) =>
            {
                tourneyRequest = request;
                return ValueTask.FromResult<PyValue?>(new PyStr("tournament details"));
            });

        var byName = services.ToDictionary(service => service.Name);
        Assert.Equal("community fitting", Assert.IsType<PyStr>(Assert.Single(Assert.IsType<PyList>(await byName["corpFittingMgr"].CallAsync(Call("GetCommunityFittings", session))).Items)).Value);
        Assert.Same(session, fittingSession);

        var content = Assert.IsType<PyList>(await byName["custom_agency_provider"].CallAsync(Call("get_content_data", session, new PyInt(7))));
        Assert.Equal(new PyStr("agency content"), Assert.Single(content.Items));
        Assert.Same(session, agencyRequest!.Session);
        Assert.Equal(new PyInt(7), Assert.Single(agencyRequest.Arguments));
        Assert.Same(PyNone.Instance, await byName["custom_agency_provider"].CallAsync(Call("warp_to_content", session, new PyInt(88))));
        Assert.Equal(88, warpedContentId);
        Assert.Same(session, warpSession);

        Assert.Same(PyBool.True, await byName["epicArcStatus"].CallAsync(Call("AgentHasEpicMissionsForCharacter", session, new PyInt(300))));
        Assert.Equal(300, queriedAgentId);
        Assert.Equal(42, queriedCharacterId);

        var pointDestination = new PyTuple([new PyInt(1), new PyInt(2), new PyInt(3)]);
        Assert.Equal(new PyInt(1), await byName["superWeaponMgr"].CallAsync(Call("ActivateSinglePointTargetedModule", session,
            new PyInt(5), new PyInt(6), pointDestination)));
        Assert.Equal(5, pointRequest!.ModuleId);
        Assert.Equal(6, pointRequest.EffectId);
        Assert.Equal(pointDestination, pointRequest.Destination);
        Assert.Same(session, pointRequest.Session);
        Assert.Equal(new PyInt(1), await byName["superWeaponMgr"].CallAsync(Call("ActivateSlashModule", session,
            new PyInt(7), new PyStr("start"), new PyStr("end"))));
        Assert.Equal(7, slashRequest!.ModuleId);
        Assert.Equal(new PyStr("start"), slashRequest.StartPoint);
        Assert.Equal(new PyStr("end"), slashRequest.EndPoint);

        Assert.Equal(new PyStr("tournament details"), await byName["tourneyMgr"].CallAsync(Call("QueryTournamentDetails", session, new PyInt(12))));
        Assert.Equal(TourneyQuery.QueryTournamentDetails, tourneyRequest!.Query);
        Assert.Equal(12, tourneyRequest.TournamentId);
        Assert.Same(session, tourneyRequest.Session);
    }

    [Fact]
    public async Task Keeper_and_nodegraph_callbacks_receive_typed_requests()
    {
        var session = new ClientSession();
        session.Set("charid", new PyInt(20));
        session.Set("solarsystemid2", new PyInt(30000142));
        long? actualCharacterId = null;
        long? actualSiteId = null;
        var keeper = new KeeperService(new KeeperRpcCallbacks(
            ReadOperationSpawnpointSolarSystemAsync: (characterId, siteId, _) =>
            {
                actualCharacterId = characterId;
                actualSiteId = siteId;
                return ValueTask.FromResult<long?>(30000142);
            }));

        Assert.Equal(new PyInt(30000142), await ((IService)keeper).CallAsync(Call(
            "GetOperationSpawnpointSolarSystem", session, new PyInt(901))));
        Assert.Equal(20, actualCharacterId);
        Assert.Equal(901, actualSiteId);

        var currentDungeon = Py.Tuple(new PyInt(5001), new PyInt(17), new PyInt(81), Py.Dict());
        ClientSession? currentDungeonSession = null;
        var currentDungeonKeeper = new KeeperService(new KeeperRpcCallbacks(
            ReadCurrentDungeonAsync: (actualSession, _) =>
            {
                currentDungeonSession = actualSession;
                return ValueTask.FromResult<PyValue?>(currentDungeon);
            }));
        Assert.Equal(currentDungeon, await ((IService)currentDungeonKeeper).CallAsync(Call(
            "GetCurrentDungeonForCharacter", session)));
        Assert.Same(session, currentDungeonSession);
        Assert.Same(PyNone.Instance, await ((IService)currentDungeonKeeper).CallAsync(Call("GetLevelEditor", session)));
        Assert.Same(PyNone.Instance, await ((IService)new KeeperService()).CallAsync(Call(
            "GetOperationSpawnpointSolarSystem", session, new PyInt(901))));
        Assert.Same(PyBool.False, await ((IService)new KeeperService()).CallAsync(Call(
            "IsOperationSite", session, new PyInt(901))));
        var missingGateRuntime = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await ((IService)new KeeperService()).CallAsync(Call(
                "ActivateAccelerationGate", session, new PyInt(902))));
        Assert.Equal("DeniedShipChanged", missingGateRuntime.Error.UserErrorMessage);

        NodeGraphRequest? observed = null;
        var nodeGraph = new NodeGraphService((request, _) =>
        {
            observed = request;
            return ValueTask.CompletedTask;
        });
        Assert.Same(PyNone.Instance, await ((IService)nodeGraph).CallAsync(Call(
            "process_client_graph_message", session,
            new PyInt(1), new PyStr("message"), new PyStr("client_health"))));
        Assert.Equal(NodeGraphOperation.ProcessClientGraphMessage, observed!.Operation);
        Assert.Same(session, observed.Session);

        observed = null;
        await ((IService)nodeGraph).CallAsync(Call(
            "process_client_graph_message", session,
            new PyInt(1), new PyStr("message"), new PyStr("server_local")));
        Assert.Null(observed);
    }


    [Fact]
    public async Task Nodegraph_rpc_shapes_match_evelogic_calls_and_evejs_fallbacks()
    {
        var session = new ClientSession();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("source"), new PyStr("client")),
        ]);
        var calls = new (string Method, NodeGraphOperation Operation, PyValue[] Args, bool ReturnsEmptyDict)[]
        {
            ("process_client_graph_message", NodeGraphOperation.ProcessClientGraphMessage,
                [new PyInt(10), new PyInt(20), new PyStr("client_health"), new PyStr("ok")], false),
            ("process_client_blackboard_request", NodeGraphOperation.ProcessClientBlackboardRequest,
                [new PyInt(10), new PyInt(20), new PyStr("health")], false),
            ("qa_subscribe", NodeGraphOperation.QaSubscribe, [], true),
            ("qa_unsubscribe", NodeGraphOperation.QaUnsubscribe, [], false),
            ("qa_stop_node_graph", NodeGraphOperation.QaStopNodeGraph, [new PyInt(20)], false),
            ("qa_start_node", NodeGraphOperation.QaStartNode, [new PyInt(20), new PyInt(3)], false),
            ("qa_stop_node", NodeGraphOperation.QaStopNode, [new PyInt(20), new PyInt(3)], false),
            ("qa_stop_active_nodes", NodeGraphOperation.QaStopActiveNodes, [new PyInt(20)], false),
        };
        var observed = new List<NodeGraphRequest>();
        var service = (IService)new NodeGraphService((request, _) =>
        {
            observed.Add(request);
            return ValueTask.CompletedTask;
        });

        foreach (var (method, operation, args, returnsEmptyDict) in calls)
        {
            var call = new ServiceCall { Method = method, Args = args, Kwargs = kwargs, Session = session };
            var fallback = await ((IService)new NodeGraphService()).CallAsync(call);
            if (returnsEmptyDict)
                Assert.Empty(Assert.IsType<PyDict>(fallback).Entries);
            else
                Assert.Same(PyNone.Instance, fallback);

            var callbackCount = observed.Count;
            var result = await service.CallAsync(call);
            if (returnsEmptyDict)
                Assert.Empty(Assert.IsType<PyDict>(result).Entries);
            else
                Assert.Same(PyNone.Instance, result);

            Assert.Equal(callbackCount + 1, observed.Count);
            var request = observed[callbackCount];
            Assert.Equal(operation, request.Operation);
            Assert.Equal(args, request.Arguments);
            Assert.Same(kwargs, request.KeywordArguments);
            Assert.Same(session, request.Session);
        }
    }

    private static PyValue Field(PyDict source, string name) =>
        source.Entries.Single(entry => entry.Key == new PyStr(name)).Value;

    private sealed class NoOmegaAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => null;
    }

    /// <summary>The text a CustomNotify user error shows the client (its "notify" argument).</summary>
    private static string? NotifyText(Eve.Services.MachoWrappedException error) =>
        error.Error.Payload is Eve.Marshal.PyObjectEx { Header: Eve.Marshal.PyTuple { Items: [_, Eve.Marshal.PyTuple { Items: [_, Eve.Marshal.PyDict arguments] }, ..] } }
            && arguments.Entries.FirstOrDefault(entry => entry.Key is Eve.Marshal.PyStr { Value: "notify" }).Value is Eve.Marshal.PyStr text
                ? text.Value
                : null;
}
