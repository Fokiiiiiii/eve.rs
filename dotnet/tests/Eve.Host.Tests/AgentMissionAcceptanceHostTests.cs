using Eve.Agents;
using Eve.Data;
using Eve.Events;
using Eve.Macho;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class AgentMissionAcceptanceHostTests
{
    private const long Pilot = 140000501;
    private const long Agent = 3008416;
    private const long Corporation = 1000001;
    private const long Faction = 500003;
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    [PgFact]
    public async Task Registered_accept_publishes_a_durable_event_that_applies_after_host_restart()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var agentsSchema = "agent_accept_" + suffix;
        var schedulerSchema = "agent_accept_sched_" + suffix;
        var eventsSchema = "agent_accept_evt_" + suffix;
        DurableScheduler? scheduler = null;
        try
        {
            await AgentMissionStore.MigrateAsync(source, agentsSchema);
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventsSchema);
            await using (var createEffects = source.CreateCommand($"CREATE TABLE {agentsSchema}.acceptance_effects (reference text PRIMARY KEY, mission_id bigint NOT NULL)"))
                await createEffects.ExecuteNonQueryAsync();

            scheduler = NewScheduler(source, schedulerSchema);
            var events = NewEvents(source, scheduler, eventsSchema);
            RegisterEffectWriter(events, agentsSchema);
            await events.PumpAsync();
            var store = new AgentMissionStore(source, agentsSchema, scheduler, events);
            var agent = new AgentMissionAgentData(
                AgentId: Agent, AgentTypeId: 2, DivisionId: 22, Level: 1, StationId: 60000001,
                CorporationId: Corporation, IsLocatorAgent: false, IsInSpace: false, CareerId: null,
                ImportantMission: false, MissionKind: "courier", MissionTypeLabel: "courier", SpecialityId: null,
                StationTypeId: null, BloodlineId: null, SolarSystemId: 30000001, DungeonId: null,
                SpawnPointId: null, TypeId: null);
            var dialogue = new AgentDialogueSources(
                store,
                (_, _, _, _, _) => ValueTask.FromResult(new AgentStandings(10, 10, 10)),
                (_, _, _) => ValueTask.FromResult(0),
                _ => Faction,
                (_, _, _) => ValueTask.FromResult<AgentMissionContent?>(new AgentMissionContent(5201, 9100, "Courier Job", "courier", false)),
                () => Start,
                OnAccepted: null);
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
            Assert.True(await dispatcher.DispatchAsync(Request(1, "agentMgr", new PyInt(1), "MachoBindObject",
                new PyTuple([new PyInt(Agent)]), PyNone.Instance), session));
            var binding = Assert.IsType<PyTuple>(ResponseResult(sink.Packets[^1]));
            var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;
            Assert.True(await dispatcher.DispatchAsync(Request(2, null, new PyStr(oid), "DoAction", PyNone.Instance), session));
            Assert.True(await dispatcher.DispatchAsync(Request(3, null, new PyStr(oid), "DoAction", new PyInt(AgentDialogueToken.Request)), session));
            Assert.True(await dispatcher.DispatchAsync(Request(4, null, new PyStr(oid), "DoAction", new PyInt(AgentDialogueToken.Accept)), session));
            Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);
            await using (var pending = source.CreateCommand($"SELECT count(*) FROM {eventsSchema}.outbox WHERE topic = $1"))
            {
                pending.Parameters.AddWithValue(AgentMissionStore.AcceptedTopic);
                Assert.Equal(1L, (long)(await pending.ExecuteScalarAsync())!);
            }

            await scheduler.DisposeAsync();
            scheduler = NewScheduler(source, schedulerSchema);
            events = NewEvents(source, scheduler, eventsSchema);
            RegisterEffectWriter(events, agentsSchema);
            Assert.Equal(1, (await events.PumpAsync()).Delivered);
            Assert.Equal(0, (await events.PumpAsync()).Delivered);

            await using var effects = source.CreateCommand($"SELECT reference, mission_id FROM {agentsSchema}.acceptance_effects");
            await using var reader = await effects.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.StartsWith($"accept:{Pilot}:{Agent}:", reader.GetString(0));
            Assert.Equal(5201L, reader.GetInt64(1));
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            if (scheduler is not null) await scheduler.DisposeAsync();
            await using (var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {agentsSchema} CASCADE; DROP SCHEMA IF EXISTS {schedulerSchema} CASCADE; DROP SCHEMA IF EXISTS {eventsSchema} CASCADE"))
                await drop.ExecuteNonQueryAsync();
            await source.DisposeAsync();
        }
    }

    private static DurableScheduler NewScheduler(NpgsqlDataSource source, string schema) =>
        new(source, () => Start.ToUnixTimeMilliseconds(), new SchedulerOptions { MaxPollMs = 50 }, schema);

    private static EventBus NewEvents(NpgsqlDataSource source, DurableScheduler scheduler, string schema) =>
        new(source, scheduler, schema: schema);

    private static void RegisterEffectWriter(EventBus events, string agentsSchema) =>
        new AgentMissionAcceptedEvents(events, async (change, context, cancellationToken) =>
        {
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO {agentsSchema}.acceptance_effects (reference, mission_id) VALUES ($1, $2)",
                context.Connection, context.Transaction)
            {
                Parameters = { new() { Value = change.Reference }, new() { Value = change.MissionId } },
            };
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }).Register();

    private static PyValue Request(long callId, string? service, PyValue remoteObject, string method, params PyValue[] args)
    {
        var body = new PyTuple([remoteObject, new PyStr(method), new PyTuple(args), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(42));
        var destination = new MachoAddress(AddressKind.Node, Service: service, CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([
            new PyInt(MachoMessage.CallReq), source.Encode(), destination.Encode(), new PyInt(7), payload,
            new PyDict([]), PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
            PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
        ]));
    }

    private static PyValue ResponseResult(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, ((PyInt)packet[0]).Value);
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(packet[4]).Items[0]).Decoded!;
    }
}
