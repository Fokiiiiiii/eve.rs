using System.Text.Json.Nodes;
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

public sealed class AgentMissionExpiryHostTests
{
    private const long Pilot = 140000401;
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
    public async Task Accepted_mission_deadline_survives_restart_then_expires_once_and_notifies_the_host_session()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var agentsSchema = "agent_expiry_" + suffix;
        var schedulerSchema = "agent_expiry_sched_" + suffix;
        var eventsSchema = "agent_expiry_evt_" + suffix;
        DurableScheduler? scheduler = null;
        EventBus? events = null;
        AgentMissionAnnouncer? announcer = null;
        try
        {
            await AgentMissionStore.MigrateAsync(source, agentsSchema);
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventsSchema);
            long nowMs = Start.ToUnixTimeMilliseconds();
            scheduler = NewScheduler(source, schedulerSchema, () => nowMs);
            events = NewEvents(source, scheduler, eventsSchema);
            var store = new AgentMissionStore(source, agentsSchema, scheduler);
            var expiryJobs = new AgentMissionExpiryJobs(store, scheduler, events);
            expiryJobs.Register();
            await events.PumpAsync();

            var sessions = new CharacterSessions();
            var firstSink = new Sink();
            var firstSession = new ClientSession(firstSink) { UserId = 7, CharacterId = Pilot };
            sessions.Enter(Pilot, firstSession);
            announcer = new AgentMissionAnnouncer(store, sessions);
            await OfferAndAcceptThroughRegisteredServiceAsync(store, scheduler, firstSession, firstSink, () => nowMs);

            var accepted = await store.ReadAsync(Pilot, Agent);
            Assert.Equal(AgentMissionState.Accepted, accepted!.State);
            Assert.Equal(Start + TimeSpan.FromDays(7), accepted.ExpiresAt);
            var pending = Assert.Single(await scheduler.ListAsync(AgentMissionStore.ExpiryJobKind, "pending"));
            Assert.Equal(AgentMissionStore.ExpiryJobKey(Pilot, Agent), pending.Key);
            Assert.Equal(Start.AddDays(7).ToUnixTimeMilliseconds(), pending.RunAtMs);

            await scheduler.DisposeAsync();
            scheduler = NewScheduler(source, schedulerSchema, () => nowMs);
            events = NewEvents(source, scheduler, eventsSchema);
            store = new AgentMissionStore(source, agentsSchema, scheduler);
            expiryJobs = new AgentMissionExpiryJobs(store, scheduler, events);
            expiryJobs.Register();
            await expiryJobs.RestoreAsync(_ => null);
            await events.PumpAsync();
            announcer.Dispose();

            var resumedSink = new Sink();
            var resumedSession = new ClientSession(resumedSink) { UserId = 7, CharacterId = Pilot };
            sessions.Enter(Pilot, resumedSession);
            announcer = new AgentMissionAnnouncer(store, sessions);
            nowMs = Start.AddDays(7).AddMilliseconds(1).ToUnixTimeMilliseconds();

            Assert.Equal(1, await scheduler.RunDueAsync());
            Assert.Equal(1, (await events.PumpAsync()).Delivered);
            Assert.Equal(AgentMissionState.Allocated, (await store.ReadAsync(Pilot, Agent))!.State);
            Assert.Empty(await scheduler.ListAsync(AgentMissionStore.ExpiryJobKind, "pending"));

            var expected = ClientNotification.Build(7, resumedSession.NodeId, "OnAgentMissionChange", "clientID",
                [new PyStr("offer_expired"), new PyInt(Agent)], 0);
            Assert.Equal(MarshalCodec.Encode(expected), MarshalCodec.Encode(Assert.Single(resumedSink.Packets)));
        }
        finally
        {
            announcer?.Dispose();
            if (scheduler is not null) await scheduler.DisposeAsync();
            await using (var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {agentsSchema} CASCADE; DROP SCHEMA IF EXISTS {schedulerSchema} CASCADE; DROP SCHEMA IF EXISTS {eventsSchema} CASCADE"))
                await drop.ExecuteNonQueryAsync();
            await source.DisposeAsync();
        }
    }

    private static DurableScheduler NewScheduler(NpgsqlDataSource source, string schema, Func<long> now) =>
        new(source, now, new SchedulerOptions { MaxPollMs = 50 }, schema);

    private static EventBus NewEvents(NpgsqlDataSource source, DurableScheduler scheduler, string schema) =>
        new(source, scheduler, schema: schema);

    private static async Task OfferAndAcceptThroughRegisteredServiceAsync(
        AgentMissionStore store,
        DurableScheduler scheduler,
        ClientSession session,
        Sink sink,
        Func<long> now)
    {
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
            (_, _, _) => ValueTask.FromResult<AgentMissionContent?>(
                new AgentMissionContent(5001, 9100, "", "courier", false)),
            () => DateTimeOffset.FromUnixTimeMilliseconds(now()),
            OnMissionTurnIn: async (characterId, agentId, ct) =>
                await store.CompleteAsync(characterId, agentId, DateTimeOffset.FromUnixTimeMilliseconds(now()),
                    $"expiry-dispatch-turnin:{characterId}:{agentId}", ct) is not null);
        var service = new AgentMissionService(
            (_, _) => ValueTask.FromResult<JsonObject?>(null),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>()),
            (_, _) => ValueTask.FromResult<JsonArray?>(null),
            rpcCallbacks: new AgentMissionRpcCallbacks(ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>([agent])),
            dialogue: dialogue);
        var registry = new ServiceRegistry();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, now);

        Assert.True(await dispatcher.DispatchAsync(Request(1, "agentMgr", new PyInt(1), "MachoBindObject",
            new PyTuple([new PyInt(Agent)]), PyNone.Instance), session));
        var binding = Assert.IsType<PyTuple>(ResponseResult(sink.Packets.Last()));
        var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;
        Assert.True(await dispatcher.DispatchAsync(Request(2, null, new PyStr(oid), "DoAction", PyNone.Instance), session));
        Assert.True(await dispatcher.DispatchAsync(Request(3, null, new PyStr(oid), "DoAction",
            new PyInt(AgentDialogueToken.Request)), session));
        Assert.Equal(AgentMissionState.Offered, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.True(await dispatcher.DispatchAsync(Request(4, null, new PyStr(oid), "DoAction",
            new PyInt(AgentDialogueToken.Accept)), session));
        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.Single(await scheduler.ListAsync(AgentMissionStore.ExpiryJobKind, "pending"));
    }

    private static PyValue Request(long callId, string? service, PyValue remoteObject, string method, params PyValue[] args)
    {
        var body = new PyTuple([remoteObject, new PyStr(method), new PyTuple(args), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(42));
        var destination = new MachoAddress(AddressKind.Node, Service: service, CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([
            new PyInt(MachoMessage.CallReq), source.Encode(), destination.Encode(), new PyInt(7), payload, new PyDict([]),
            PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
            PyNone.Instance, PyNone.Instance,
        ]));
    }

    private static PyValue ResponseResult(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, ((PyInt)packet[0]).Value);
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(packet[4]).Items[0]).Decoded!;
    }
}
