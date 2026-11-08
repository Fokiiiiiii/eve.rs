using Eve.Agents;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class AgentMissionDispatchTests
{
    private const long Pilot = 140000021;
    private const long Agent = 3008416;
    private const long Corporation = 1000001;
    private const long Faction = 500003;
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class MethodGate(AgentMissionService inner) : IService, IBoundObjectReleaseHandler
    {
        public string? RejectedMethod { get; set; }
        public string Name => inner.Name;
        public bool HasMethod(string method) => method != RejectedMethod && inner.HasMethod(method);
        public ValueTask<PyValue> CallAsync(ServiceCall call) => inner.CallAsync(call);
        public void OnBoundObjectReleased(string oid, ClientSession? session) => inner.OnBoundObjectReleased(oid, session);
    }

    private static AgentMissionAgentData AgentData() => new(
        AgentId: Agent,
        AgentTypeId: 2,
        DivisionId: 22,
        Level: 1,
        StationId: 60000001,
        CorporationId: Corporation,
        IsLocatorAgent: false,
        IsInSpace: false,
        CareerId: null,
        ImportantMission: false,
        MissionKind: "courier",
        MissionTypeLabel: "courier",
        SpecialityId: null,
        StationTypeId: null,
        BloodlineId: null,
        SolarSystemId: 30000001,
        DungeonId: null,
        SpawnPointId: null,
        TypeId: null);

    [PgFact]
    public async Task Bound_DoAction_dispatches_offers_and_accepts_a_persistent_mission_and_checks_the_method_table()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "agent_dispatch_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await AgentMissionStore.MigrateAsync(source, schema);
            var store = new AgentMissionStore(source, schema);
            var dialogue = new AgentDialogueSources(
                store,
                (_, _, _, _, _) => ValueTask.FromResult(new AgentStandings(10, 10, 10)),
                (_, _, _) => ValueTask.FromResult(0),
                _ => Faction,
                (_, _, _) => ValueTask.FromResult<AgentMissionContent?>(new AgentMissionContent(5001, 9100, "", "courier", false)),
                () => Start,
                OnMissionTurnIn: async (characterId, agentId, ct) =>
                    await store.CompleteAsync(characterId, agentId, Start, $"dispatch-turnin:{characterId}:{agentId}", ct) is not null);
            var agent = AgentData();
            var service = new AgentMissionService(
                (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null),
                (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, System.Text.Json.Nodes.JsonObject>>(new Dictionary<long, System.Text.Json.Nodes.JsonObject>()),
                (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonArray?>(null),
                (_, _, _) => true,
                new AgentMissionRpcCallbacks(ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>([agent])),
                dialogue);
            var gated = new MethodGate(service);
            var registry = new ServiceRegistry();
            registry.Register(gated);
            var dispatcher = new PacketDispatcher(registry, () => Start.ToUnixTimeMilliseconds());
            var sink = new Sink();
            var session = new ClientSession(sink) { UserId = 7, CharacterId = Pilot };
            session.Set("charid", new PyInt(Pilot));
            session.Set("userid", new PyInt(7));
            session.Set("stationid", new PyInt(60000001));

            Assert.True(await dispatcher.DispatchAsync(Request(1, "agentMgr", new PyInt(1), "MachoBindObject",
                new PyTuple([new PyInt(Agent)]), PyNone.Instance), session));
            var bindResult = ResponseResult(sink.Packets[^1]);
            var binding = Assert.IsType<PyTuple>(bindResult);
            var boundAgent = Assert.IsType<PySubStruct>(binding.Items[0]);
            var oid = BoundObjects.ExtractName(boundAgent);
            Assert.NotNull(oid);

            Assert.True(await dispatcher.DispatchAsync(Request(2, null, new PyStr(oid!), "DoAction", PyNone.Instance), session));
            var greeting = DialogueActions(sink.Packets[^1]);
            Assert.Contains(greeting, action => action.Items[0] is PyInt id && id.Value == AgentDialogueToken.Request);

            Assert.True(await dispatcher.DispatchAsync(Request(3, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.Request)), session));
            var offered = DialogueActions(sink.Packets[^1]);
            Assert.Contains(offered, action => action.Items[0] is PyInt id && id.Value == AgentDialogueToken.Accept);
            var offeredMission = await store.ReadAsync(Pilot, Agent);
            Assert.Equal(AgentMissionState.Offered, offeredMission!.State);
            Assert.Equal(Start + TimeSpan.FromDays(7), offeredMission.ExpiresAt);

            Assert.True(await dispatcher.DispatchAsync(Request(4, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.Accept)), session));
            var acceptedActions = DialogueActions(sink.Packets[^1]);
            var quitAction = Assert.Single(acceptedActions);
            Assert.Equal(AgentDialogueToken.Quit, ((PyInt)quitAction.Items[0]).Value);
            Assert.Equal(AgentDialogueButton.Quit, ((PyInt)quitAction.Items[1]).Value);
            var acceptedMission = await new AgentMissionStore(source, schema).ReadAsync(Pilot, Agent);
            Assert.Equal(AgentMissionState.Accepted, acceptedMission!.State);
            Assert.Equal(Start + TimeSpan.FromDays(7), acceptedMission.ExpiresAt);

            await store.MarkObjectiveCompletedAsync(Pilot, Agent, Start, "dispatch-objective");
            Assert.True(await dispatcher.DispatchAsync(Request(5, null, new PyStr(oid!), "DoAction", PyNone.Instance), session));
            Assert.Contains(DialogueActions(sink.Packets[^1]), action => action.Items[0] is PyInt id && id.Value == AgentDialogueToken.CompleteReady);
            Assert.True(await dispatcher.DispatchAsync(Request(6, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.CompleteReady)), session));
            Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot, Agent))!.State);

            gated.RejectedMethod = "DoAction";
            Assert.True(await dispatcher.DispatchAsync(Request(7, null, new PyStr(oid!), "DoAction", PyNone.Instance), session));
            Assert.Equal("exceptions.AttributeError", ErrorClass(sink.Packets[^1]));

            gated.RejectedMethod = null;
            Assert.True(await dispatcher.DispatchAsync(Request(8, null, new PyStr(oid!), "NoSuchAgentMethod"), session));
            Assert.Equal("exceptions.AttributeError", ErrorClass(sink.Packets[^1]));

            Assert.True(await dispatcher.DispatchAsync(Request(9, "agentMgr", new PyInt(1), "MachoBindObject",
                new PyTuple([new PyInt(3008999)]), PyNone.Instance), session));
            Assert.Equal(MachoErrors.UserErrorClass, ErrorClass(sink.Packets[^1]));
        }
        finally
        {
            await using (var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
                await drop.ExecuteNonQueryAsync();
            await source.DisposeAsync();
        }
    }

    [PgFact]
    public async Task Concurrent_accepts_of_the_same_offer_invoke_the_post_accept_callback_once()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "agent_accept_concurrent_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await AgentMissionStore.MigrateAsync(source, schema);
            var store = new AgentMissionStore(source, schema);
            await store.OfferAsync(Pilot, Agent, new AgentMissionContent(5101, 9100, "Courier Job", "courier", false), Start, "concurrent-accept-offer");
            var callbacks = 0;
            var dialogue = new AgentDialogueSources(
                store,
                (_, _, _, _, _) => ValueTask.FromResult(new AgentStandings(10, 10, 10)),
                (_, _, _) => ValueTask.FromResult(0),
                _ => Faction,
                (_, _, _) => ValueTask.FromResult<AgentMissionContent?>(null),
                () => Start,
                OnAccepted: (_, _) =>
                {
                    Interlocked.Increment(ref callbacks);
                    return ValueTask.CompletedTask;
                });
            var agent = AgentData();
            var service = new AgentMissionService(
                (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null),
                (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, System.Text.Json.Nodes.JsonObject>>(new Dictionary<long, System.Text.Json.Nodes.JsonObject>()),
                (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonArray?>(null),
                (_, _, _) => true,
                new AgentMissionRpcCallbacks(ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>([agent])),
                dialogue);
            var registry = new ServiceRegistry();
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => Start.ToUnixTimeMilliseconds());
            var sink = new Sink();
            var session = new ClientSession(sink) { UserId = 7, CharacterId = Pilot };
            session.Set("userid", new PyInt(7));
            session.Set("charid", new PyInt(Pilot));
            Assert.True(await dispatcher.DispatchAsync(Request(1, "agentMgr", new PyInt(1), "MachoBindObject",
                new PyTuple([new PyInt(Agent)]), PyNone.Instance), session));
            var binding = Assert.IsType<PyTuple>(ResponseResult(sink.Packets[^1]));
            var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;

            await using var connection = await source.OpenConnectionAsync();
            await using var heldTransition = await connection.BeginTransactionAsync();
            await using (var lockRow = new NpgsqlCommand(
                $"SELECT 1 FROM {schema}.missions WHERE character_id = $1 AND agent_id = $2 FOR UPDATE", connection, heldTransition)
            {
                Parameters = { new() { Value = Pilot }, new() { Value = Agent } },
            })
                Assert.Equal(1, (int)(await lockRow.ExecuteScalarAsync())!);

            var first = dispatcher.DispatchAsync(Request(2, null, new PyStr(oid), "DoAction", new PyInt(AgentDialogueToken.Accept)), session);
            var second = dispatcher.DispatchAsync(Request(3, null, new PyStr(oid), "DoAction", new PyInt(AgentDialogueToken.Accept)), session);
            await WaitForTwoMissionLockWaitersAsync(source, schema);
            await heldTransition.CommitAsync();
            await Task.WhenAll(first, second);

            Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);
            Assert.Equal(1, callbacks);
            await using var journal = source.CreateCommand(
                $"SELECT count(*) FROM {schema}.journal WHERE character_id = $1 AND agent_id = $2 AND action = 'accepted'");
            journal.Parameters.AddWithValue(Pilot);
            journal.Parameters.AddWithValue(Agent);
            Assert.Equal(1L, (long)(await journal.ExecuteScalarAsync())!);
        }
        finally
        {
            await using (var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
                await drop.ExecuteNonQueryAsync();
            await source.DisposeAsync();
        }
    }

    [PgFact]
    public async Task Bound_RemoveOffer_dispatch_commits_once_survives_restart_and_preserves_an_accepted_mission()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "agent_remove_dispatch_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await AgentMissionStore.MigrateAsync(source, schema);
            var store = new AgentMissionStore(source, schema);
            var committed = new List<AgentMissionChange>();
            store.Committed += committed.Add;
            var agent = AgentData();
            var service = new AgentMissionService(
                (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null),
                (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, System.Text.Json.Nodes.JsonObject>>(new Dictionary<long, System.Text.Json.Nodes.JsonObject>()),
                (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonArray?>(null),
                rpcCallbacks: new AgentMissionRpcCallbacks(
                    ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>([agent]),
                    RemoveOfferFromJournal: async (characterId, agentId, ct) =>
                        await store.RemoveOfferAsync(characterId, agentId, Start, ct)));
            var registry = new ServiceRegistry();
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => Start.ToUnixTimeMilliseconds());
            var sink = new Sink();
            var session = new ClientSession(sink) { UserId = 7, CharacterId = Pilot };
            session.Set("charid", new PyInt(Pilot));
            session.Set("userid", new PyInt(7));

            Assert.True(await dispatcher.DispatchAsync(Request(1, "agentMgr", new PyInt(1), "MachoBindObject",
                new PyTuple([new PyInt(Agent)]), PyNone.Instance), session));
            var binding = Assert.IsType<PyTuple>(ResponseResult(sink.Packets[^1]));
            var boundAgent = Assert.IsType<PySubStruct>(binding.Items[0]);
            var oid = BoundObjects.ExtractName(boundAgent)!;

            await store.OfferAsync(Pilot, Agent, new AgentMissionContent(5001, 9100, "Courier Job", "courier", false), Start, "seed-offer");
            Assert.True(await dispatcher.DispatchAsync(Request(2, null, new PyStr(oid), "RemoveOfferFromJournal"), session));
            Assert.Equal(PyNone.Instance, ResponseResult(sink.Packets[^1]));
            Assert.Equal(AgentMissionState.Allocated, (await new AgentMissionStore(source, schema).ReadAsync(Pilot, Agent))!.State);
            Assert.Single(committed, change => change.Action == "offer_removed");

            Assert.True(await dispatcher.DispatchAsync(Request(3, null, new PyStr(oid), "RemoveOfferFromJournal"), session));
            Assert.Equal(PyNone.Instance, ResponseResult(sink.Packets[^1]));
            Assert.Single(committed, change => change.Action == "offer_removed");

            var acceptedCharacter = Pilot + 1;
            await store.OfferAsync(acceptedCharacter, Agent, new AgentMissionContent(5002, 9101, "Accepted Job", "courier", false), Start, "seed-accepted");
            await store.AcceptAsync(acceptedCharacter, Agent, Start, "seed-accept");
            session.Set("charid", new PyInt(acceptedCharacter));
            Assert.True(await dispatcher.DispatchAsync(Request(4, null, new PyStr(oid), "RemoveOfferFromJournal"), session));
            Assert.Equal(MachoErrors.UserErrorClass, ErrorClass(sink.Packets[^1]));
            Assert.Equal(AgentMissionState.Accepted, (await new AgentMissionStore(source, schema).ReadAsync(acceptedCharacter, Agent))!.State);
        }
        finally
        {
            await using (var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
                await drop.ExecuteNonQueryAsync();
            await source.DisposeAsync();
        }
    }

    private static PyValue Request(long callId, string? service, PyValue remoteObject, string method, params PyValue[] args)
    {
        var body = new PyTuple([
            remoteObject,
            new PyStr(method),
            new PyTuple(args),
            PyNone.Instance,
        ]);
        var payload = new PyTuple([
            new PyTuple([PyBool.True, new PySubStream(body)]),
        ]);
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(42));
        var destination = new MachoAddress(AddressKind.Node, Service: service, CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([
                new PyInt(MachoMessage.CallReq),
                source.Encode(),
                destination.Encode(),
                new PyInt(7),
                payload,
                new PyDict([]),
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
            ]));
    }

    private static PyValue ResponseResult(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, ((PyInt)packet[0]).Value);
        var payload = Assert.IsType<PyTuple>(packet[4]);
        return Assert.IsType<PySubStream>(payload.Items[0]).Decoded!;
    }

    private static IReadOnlyList<PyTuple> DialogueActions(PyValue response)
    {
        var root = Assert.IsType<PyTuple>(ResponseResult(response));
        var displayed = Assert.IsType<PyTuple>(root.Items[0]);
        return Assert.IsType<PyList>(displayed.Items[1]).Items
            .Select(item => Assert.IsType<PyTuple>(item))
            .ToArray();
    }

    private static string ErrorClass(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.ErrorResponse, ((PyInt)packet[0]).Value);
        var responsePayload = Assert.IsType<PyTuple>(packet[4]);
        var errorBody = Assert.IsType<PyTuple>(responsePayload.Items[2]);
        var wrapped = Assert.IsType<PySubStream>(errorBody.Items[0]).Decoded;
        var error = Assert.IsType<PyObjectEx>(wrapped);
        return Assert.IsType<PyToken>(Assert.IsType<PyTuple>(error.Header).Items[0]).Value;
    }

    private static async Task WaitForTwoMissionLockWaitersAsync(NpgsqlDataSource source, string schema)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        do
        {
            await using var command = source.CreateCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE $1
                """);
            command.Parameters.AddWithValue($"%{schema}.missions%");
            if ((long)(await command.ExecuteScalarAsync())! >= 2) return;
            await Task.Delay(10);
        } while (DateTime.UtcNow < deadline);
        Assert.Fail("both accept calls should be waiting on the locked mission row before it is released");
    }
}
