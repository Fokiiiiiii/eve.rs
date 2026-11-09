using Eve.Agents;
using Eve.Data;
using Eve.Events;
using Eve.Macho;
using Eve.Marshal;
using Eve.Relations;
using Eve.Scheduler;
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
        var schedulerSchema = "agent_dispatch_sched_" + Guid.NewGuid().ToString("N")[..12];
        var eventsSchema = "agent_dispatch_events_" + Guid.NewGuid().ToString("N")[..12];
        var relationsSchema = "agent_dispatch_relations_" + Guid.NewGuid().ToString("N")[..12];
        DurableScheduler? scheduler = null;
        AgentMissionAnnouncer? announcer = null;
        try
        {
            await AgentMissionStore.MigrateAsync(source, schema);
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventsSchema);
            await RelationStore.MigrateAsync(source, relationsSchema);
            var dialogueNow = Start;
            scheduler = new DurableScheduler(source, () => dialogueNow.ToUnixTimeMilliseconds(),
                new SchedulerOptions { Concurrency = 1 }, schedulerSchema);
            var events = new EventBus(source, scheduler, schema: eventsSchema);
            var relations = new RelationStore(source, scheduler, events, relationsSchema);
            var store = new AgentMissionStore(source, schema);
            ClientSession session = null!;
            var offerContent = new AgentMissionContent(5001, 9100, "", "courier", false);
            var mission = new MissionStatic(
                5001, "Encounter", false, null, null, null, null,
                new MissionStaticKill(3509, null, 0, null), null, null, null, 0, [],
                new Dictionary<string, string>(), 9100);
            var dialogue = new AgentDialogueSources(
                store,
                (_, _, _, _, _) => ValueTask.FromResult(new AgentStandings(10, 10, 10)),
                (_, _, _) => ValueTask.FromResult(0),
                _ => Faction,
                (_, _, _) => ValueTask.FromResult<AgentMissionContent?>(offerContent),
                () => dialogueNow,
                OnMissionTurnIn: async (characterId, agentId, ct) =>
                    await store.CompleteAsync(characterId, agentId, Start, $"dispatch-turnin:{characterId}:{agentId}", ct) is not null,
                OnMissionDecline: async (characterId, declinedAgent, now, reference, ct) =>
                    await CharacterServicesComposition.DeclineInTransactionAsync(source, store, relations,
                        characterId, declinedAgent, now, reference, ct,
                        modifications =>
                        {
                            session.SendNotification("OnStandingsModified", "charid", modifications);
                            return Task.CompletedTask;
                        }));
            var agent = AgentData();
            var service = new AgentMissionService(
                async (characterId, ct) =>
                {
                    var stored = await store.ReadAsync(characterId, ct);
                    return stored.Count == 0 ? null : AgentMissionStoreRecords.Overlay(null, stored);
                },
                (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, System.Text.Json.Nodes.JsonObject>>(new Dictionary<long, System.Text.Json.Nodes.JsonObject>()),
                (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonArray?>(null),
                (_, _, _) => true,
                new AgentMissionRpcCallbacks(
                    ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>([agent]),
                    ReadHeldMission: async (characterId, agentId, ct) =>
                    {
                        var record = await store.ReadAsync(characterId, agentId, ct);
                        return record is null ? null : new HeldMissionView(mission, record, null);
                    }),
                dialogue);
            var gated = new MethodGate(service);
            var registry = new ServiceRegistry();
            registry.Register(gated);
            var dispatcher = new PacketDispatcher(registry, () => Start.ToUnixTimeMilliseconds());
            var sink = new Sink();
            session = new ClientSession(sink) { UserId = 7, CharacterId = Pilot };
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
            Assert.True(await dispatcher.DispatchAsync(Request(5, null, new PyStr(oid!), "GetMissionObjectiveInfo"), session));
            var objectiveInfo = Assert.IsType<PyDict>(ResponseResult(sink.Packets[^1]));
            Assert.Equal(new PyInt(1), DictionaryField(objectiveInfo, "completionStatus"));
            Assert.Equal(new PyInt((int)AgentMissionState.Accepted), DictionaryField(objectiveInfo, "missionState"));
            var dungeon = Assert.IsType<PyDict>(Assert.Single(Assert.IsType<PyList>(DictionaryField(objectiveInfo, "dungeons")).Items));
            Assert.Equal(new PyInt(1), DictionaryField(dungeon, "completed"));
            Assert.Equal(new PyInt(1), DictionaryField(dungeon, "objectiveCompleted"));

            Assert.True(await dispatcher.DispatchAsync(Request(6, null, new PyStr(oid!), "DoAction", PyNone.Instance), session));
            Assert.Contains(DialogueActions(sink.Packets[^1]), action => action.Items[0] is PyInt id && id.Value == AgentDialogueToken.CompleteReady);
            Assert.True(await dispatcher.DispatchAsync(Request(7, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.CompleteReady)), session));
            Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot, Agent))!.State);

            Assert.True(await dispatcher.DispatchAsync(Request(8, null, new PyStr(oid!), "GetReplayTimestamp"), session));
            Assert.Equal(PyNone.Instance, ResponseResult(sink.Packets[^1]));

            gated.RejectedMethod = "DoAction";
            Assert.True(await dispatcher.DispatchAsync(Request(9, null, new PyStr(oid!), "DoAction", PyNone.Instance), session));
            Assert.Equal("exceptions.AttributeError", ErrorClass(sink.Packets[^1]));

            gated.RejectedMethod = null;
            Assert.True(await dispatcher.DispatchAsync(Request(10, null, new PyStr(oid!), "NoSuchAgentMethod"), session));
            Assert.Equal("exceptions.AttributeError", ErrorClass(sink.Packets[^1]));

            Assert.True(await dispatcher.DispatchAsync(Request(11, "agentMgr", new PyInt(1), "MachoBindObject",
                new PyTuple([new PyInt(3008999)]), PyNone.Instance), session));
            Assert.Equal(MachoErrors.UserErrorClass, ErrorClass(sink.Packets[^1]));

            var declinedPilot = Pilot + 1;
            dialogueNow = DateTimeOffset.UtcNow;
            session.CharacterId = declinedPilot;
            session.Set("charid", new PyInt(declinedPilot));
            var characterSessions = new CharacterSessions();
            characterSessions.Enter(declinedPilot, session);
            announcer = new AgentMissionAnnouncer(store, characterSessions);
            Assert.True(await dispatcher.DispatchAsync(Request(12, null, new PyStr(oid!), "DoAction", PyNone.Instance), session));
            Assert.Contains(DialogueActions(sink.Packets[^1]), action => action.Items[0] is PyInt id && id.Value == AgentDialogueToken.Request);
            Assert.True(await dispatcher.DispatchAsync(Request(13, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.Request)), session));
            Assert.Contains(DialogueActions(sink.Packets[^1]), action => action.Items[0] is PyInt id && id.Value == AgentDialogueToken.Decline);

            Assert.True(await dispatcher.DispatchAsync(Request(14, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.Decline)), session));
            var confirmation = MachoPacket.Decode(sink.Packets.Last(packet => MachoPacket.Decode(packet)?.Type == MachoMessage.CallReq))!;
            Assert.Equal("agents", confirmation.Service);
            var confirmationBody = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(
                Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(confirmation.Payload).Items[0]).Items[1]).Decoded);
            Assert.Equal("YesNo", Assert.IsType<PyStr>(confirmationBody.Items[1]).Value);
            var confirmationCallId = Assert.IsType<PyInt>(confirmation.CallId).Value;
            Assert.True(await dispatcher.DispatchAsync(ClientResponse(confirmationCallId, PyBool.True), session));
            var declinedMission = await WaitForMissionStateAsync(store, declinedPilot, Agent, AgentMissionState.Declined);
            var firstMissionNotices = sink.Packets.Where(packet =>
                MachoPacket.Decode(packet) is { Type: MachoMessage.Notification, Dest.BroadcastId: "OnAgentMissionChange" }).ToArray();
            Assert.Equal(["offered", "declined"], firstMissionNotices
                .Select(packet => NotificationText(NotificationArguments(packet).Items[0])));

            Assert.True(await dispatcher.DispatchAsync(Request(15, null, new PyStr(oid!), "GetReplayTimestamp"), session));
            Assert.Equal(new PyLong(declinedMission.DeclineUntil!.Value.ToFileTime()), ResponseResult(sink.Packets[^1]));

            Assert.True(await dispatcher.DispatchAsync(Request(16, null, new PyStr(oid!), "DoAction", PyNone.Instance), session));
            Assert.Contains(DialogueActions(sink.Packets[^1]), action => action.Items[0] is PyInt id && id.Value == AgentDialogueToken.Request);
            Assert.True(await dispatcher.DispatchAsync(Request(17, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.Request)), session));
            var reofferedMission = await WaitForMissionStateAsync(store, declinedPilot, Agent, AgentMissionState.Offered);
            Assert.Equal(declinedMission.DeclineUntil, reofferedMission.DeclineUntil);
            Assert.True(await dispatcher.DispatchAsync(Request(18, null, new PyStr(oid!), "GetMissionBriefingInfo"), session));
            var briefing = Assert.IsType<PyDict>(ResponseResult(sink.Packets[^1]));
            Assert.IsType<PyLong>(DictionaryField(briefing, "Decline Time"));

            Assert.Equal(0, await relations.ValueAsync(RelationKinds.Standing.Name, Agent, declinedPilot));
            Assert.True(await dispatcher.DispatchAsync(Request(19, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.Request)), session));
            Assert.Contains(DialogueActions(sink.Packets[^1]), action => action.Items[0] is PyInt id && id.Value == AgentDialogueToken.Decline);
            Assert.True(await dispatcher.DispatchAsync(Request(20, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.Decline)), session));
            var secondConfirmation = MachoPacket.Decode(sink.Packets.Last(packet => MachoPacket.Decode(packet)?.Type == MachoMessage.CallReq))!;
            var secondConfirmationBody = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(
                Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(secondConfirmation.Payload).Items[0]).Items[1]).Decoded);
            Assert.Equal("YesNo", Assert.IsType<PyStr>(secondConfirmationBody.Items[1]).Value);
            Assert.True(await dispatcher.DispatchAsync(ClientResponse(Assert.IsType<PyInt>(secondConfirmation.CallId).Value, PyBool.True), session));
            await WaitForMissionStateAsync(store, declinedPilot, Agent, AgentMissionState.Declined);
            Assert.Equal(-0.1, await relations.ValueAsync(RelationKinds.Standing.Name, Agent, declinedPilot), 10);
            Assert.Equal(-0.01, await relations.ValueAsync(RelationKinds.Standing.Name, Corporation, declinedPilot), 10);
            var notifications = sink.Packets.Where(packet => MachoPacket.Decode(packet)?.Type == MachoMessage.Notification).ToArray();
            Assert.Equal(["OnAgentMissionChange", "OnAgentMissionChange", "OnAgentMissionChange", "OnStandingsModified", "OnAgentMissionChange"],
                notifications.Select(NotificationName));
            Assert.Equal(["offered", "declined", "offered", "declined"], notifications
                .Where(packet => NotificationName(packet) == "OnAgentMissionChange")
                .Select(packet => NotificationText(NotificationArguments(packet).Items[0])));
            var standingRows = Assert.IsType<PyList>(Assert.Single(NotificationArguments(notifications[3]).Items));
            var corporationChange = Assert.IsType<PyList>(standingRows.Items[0]);
            Assert.Equal(new PyInt(Corporation), corporationChange.Items[0]);
            Assert.Equal(new PyInt(declinedPilot), corporationChange.Items[1]);
            Assert.Equal(new PyReal(-0.001), corporationChange.Items[2]);
            var agentChange = Assert.IsType<PyList>(standingRows.Items[1]);
            Assert.Equal(new PyInt(Agent), agentChange.Items[0]);
            Assert.Equal(new PyInt(declinedPilot), agentChange.Items[1]);
            Assert.Equal(new PyReal(-0.01), agentChange.Items[2]);
            var secondDeclineNotice = NotificationArguments(notifications[4]);
            Assert.Equal("declined", NotificationText(secondDeclineNotice.Items[0]));
            Assert.Equal(new PyInt(Agent), secondDeclineNotice.Items[1]);

            var secondDeadline = (await store.ReadAsync(declinedPilot, Agent))!.DeclineUntil!.Value;
            dialogueNow = secondDeadline;
            Assert.True(await dispatcher.DispatchAsync(Request(21, null, new PyStr(oid!), "DoAction", PyNone.Instance), session));
            Assert.Contains(DialogueActions(sink.Packets[^1]), action => action.Items[0] is PyInt id && id.Value == AgentDialogueToken.Request);
            Assert.True(await dispatcher.DispatchAsync(Request(22, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.Request)), session));
            Assert.Equal(AgentMissionState.Offered,
                (await WaitForMissionStateAsync(store, declinedPilot, Agent, AgentMissionState.Offered)).State);
            Assert.True(await dispatcher.DispatchAsync(Request(23, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.Decline)), session));
            var thirdConfirmation = MachoPacket.Decode(sink.Packets.Last(packet => MachoPacket.Decode(packet)?.Type == MachoMessage.CallReq))!;
            Assert.True(await dispatcher.DispatchAsync(ClientResponse(Assert.IsType<PyInt>(thirdConfirmation.CallId).Value, PyBool.True), session));
            var thirdDeadline = secondDeadline + AgentMissionStore.DeclineReplayDelay;
            var thirdDecline = await WaitForDeclineDeadlineAsync(store, declinedPilot, Agent, thirdDeadline);
            Assert.Equal(thirdDeadline, thirdDecline.DeclineUntil);
            Assert.Equal(-0.1, await relations.ValueAsync(RelationKinds.Standing.Name, Agent, declinedPilot), 10);
            Assert.Equal(-0.01, await relations.ValueAsync(RelationKinds.Standing.Name, Corporation, declinedPilot), 10);
            var allNotifications = sink.Packets.Where(packet => MachoPacket.Decode(packet)?.Type == MachoMessage.Notification).ToArray();
            Assert.Single(allNotifications, packet => NotificationName(packet) == "OnStandingsModified");
            Assert.Equal(["offered", "declined", "offered", "declined", "offered", "declined"], allNotifications
                .Where(packet => NotificationName(packet) == "OnAgentMissionChange")
                .Select(packet => NotificationText(NotificationArguments(packet).Items[0])));

            // The second decline penalty also applies to important mission agents. EveJS derives the raw amount
            // from the agent level for these offers too; the content's important bit only changes completion rewards.
            var importantPilot = Pilot + 2;
            agent = AgentData() with
            {
                AgentTypeId = AgentMissionKindRules.TypeStorylineMissionAgent,
                ImportantMission = true,
                MissionKind = "encounter",
                MissionTypeLabel = "encounter",
            };
            offerContent = offerContent with { Title = "Encounter", MissionTypeLabel = "encounter", Important = true };
            dialogueNow = DateTimeOffset.UtcNow;
            session.CharacterId = importantPilot;
            session.Set("charid", new PyInt(importantPilot));
            characterSessions.Enter(importantPilot, session);

            Assert.True(await dispatcher.DispatchAsync(Request(24, null, new PyStr(oid!), "DoAction", PyNone.Instance), session));
            Assert.True(await dispatcher.DispatchAsync(Request(25, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.Request)), session));
            Assert.True(await dispatcher.DispatchAsync(Request(26, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.Decline)), session));
            var importantFirstConfirmation = MachoPacket.Decode(sink.Packets.Last(packet => MachoPacket.Decode(packet)?.Type == MachoMessage.CallReq))!;
            Assert.True(await dispatcher.DispatchAsync(ClientResponse(Assert.IsType<PyInt>(importantFirstConfirmation.CallId).Value, PyBool.True), session));
            var importantFirstDecline = await WaitForMissionStateAsync(store, importantPilot, Agent, AgentMissionState.Declined);
            Assert.Equal(0, await relations.ValueAsync(RelationKinds.Standing.Name, Agent, importantPilot));

            dialogueNow = importantFirstDecline.DeclineUntil!.Value.AddHours(-3);
            Assert.True(await dispatcher.DispatchAsync(Request(27, null, new PyStr(oid!), "DoAction", PyNone.Instance), session));
            Assert.True(await dispatcher.DispatchAsync(Request(28, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.Request)), session));
            Assert.True(await dispatcher.DispatchAsync(Request(29, null, new PyStr(oid!), "DoAction",
                new PyInt(AgentDialogueToken.Decline)), session));
            var importantSecondConfirmation = MachoPacket.Decode(sink.Packets.Last(packet => MachoPacket.Decode(packet)?.Type == MachoMessage.CallReq))!;
            Assert.True(await dispatcher.DispatchAsync(ClientResponse(Assert.IsType<PyInt>(importantSecondConfirmation.CallId).Value, PyBool.True), session));
            var importantSecondDecline = await WaitForMissionStateAsync(store, importantPilot, Agent, AgentMissionState.Declined);
            Assert.Equal(importantFirstDecline.DeclineUntil!.Value.AddHours(-3) + AgentMissionStore.DeclineReplayDelay,
                importantSecondDecline.DeclineUntil);
            Assert.Equal(-0.1, await relations.ValueAsync(RelationKinds.Standing.Name, Agent, importantPilot), 10);
            Assert.Equal(-0.01, await relations.ValueAsync(RelationKinds.Standing.Name, Corporation, importantPilot), 10);

            var replayReference = $"decline:{importantPilot}:{Agent}:{dialogueNow.ToUnixTimeMilliseconds()}:{importantFirstDecline.DeclineUntil.Value.ToUnixTimeMilliseconds()}";
            Assert.Null(await CharacterServicesComposition.DeclineInTransactionAsync(source, store, relations,
                importantPilot, agent, dialogueNow, replayReference, default));
            await using var history = source.CreateCommand($"""
                SELECT subject, (reason::jsonb->>'eventTypeID')::int, (reason::jsonb->>'modification')::double precision,
                    reason::jsonb->>'msg'
                FROM {relationsSchema}.relation_history WHERE kind = $1 AND object = $2 ORDER BY id
                """);
            history.Parameters.AddWithValue(RelationKinds.Standing.Name);
            history.Parameters.AddWithValue(importantPilot);
            var standingHistory = new List<(long Subject, int EventType, double Modification, string Message)>();
            await using (var reader = await history.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    standingHistory.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetDouble(2), reader.GetString(3)));
            }
            Assert.Equal(new[] { (Corporation, 75, -0.001, "Encounter"), (Agent, 75, -0.01, "Encounter") }, standingHistory);
        }
        finally
        {
            announcer?.Dispose();
            if (scheduler is not null) await scheduler.DisposeAsync();
            await using (var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
                await drop.ExecuteNonQueryAsync();
            await using (var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schedulerSchema} CASCADE; DROP SCHEMA IF EXISTS {eventsSchema} CASCADE; DROP SCHEMA IF EXISTS {relationsSchema} CASCADE"))
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

    private static PyValue ClientResponse(long callId, PyValue answer)
    {
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(42));
        var destination = new MachoAddress(AddressKind.Node, Service: "agents", CallId: new PyInt(callId),
            NodeId: new PyInt(MachoMessage.ProxyNodeId));
        return new PyObject(new PyStr(ResponseBuilder.CallRspClass), new PyTuple([
            new PyInt(MachoMessage.CallRsp), source.Encode(), destination.Encode(), new PyInt(7),
            new PyTuple([new PySubStream(answer)]), new PyDict([]), PyNone.Instance,
            .. Enumerable.Repeat<PyValue>(PyNone.Instance, 7),
        ]));
    }

    private static async Task<AgentMissionRecord> WaitForMissionStateAsync(
        AgentMissionStore store, long characterId, long agentId, AgentMissionState state)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await store.ReadAsync(characterId, agentId) is { State: var actual } record && actual == state)
                return record;
            await Task.Delay(10);
        }
        Assert.Fail($"mission for character {characterId} did not reach {state}");
        throw new InvalidOperationException("unreachable");
    }

    private static async Task<AgentMissionRecord> WaitForDeclineDeadlineAsync(
        AgentMissionStore store, long characterId, long agentId, DateTimeOffset deadline)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await store.ReadAsync(characterId, agentId) is { State: AgentMissionState.Declined } record &&
                record.DeclineUntil == deadline)
                return record;
            await Task.Delay(10);
        }
        Assert.Fail($"mission for character {characterId} did not reach decline deadline {deadline:O}");
        throw new InvalidOperationException("unreachable");
    }

    private static PyValue ResponseResult(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, ((PyInt)packet[0]).Value);
        var payload = Assert.IsType<PyTuple>(packet[4]);
        return Assert.IsType<PySubStream>(payload.Items[0]).Decoded!;
    }

    private static PyValue DictionaryField(PyDict dictionary, string name) => dictionary.Entries.Single(entry =>
        entry.Key is PyStr key && key.Value == name ||
        entry.Key is PyBytes bytes && System.Text.Encoding.UTF8.GetString(bytes.Value.Span) == name).Value;

    private static string NotificationName(PyValue packet) =>
        MachoPacket.Decode(packet) is { Type: MachoMessage.Notification, Dest.BroadcastId: { } name }
            ? name
            : throw new Xunit.Sdk.XunitException("expected a client notification packet");

    private static PyTuple NotificationArguments(PyValue packet)
    {
        var decoded = MachoPacket.Decode(packet) ?? throw new Xunit.Sdk.XunitException("expected a Macho packet");
        var outer = Assert.IsType<PyTuple>(decoded.Payload);
        var notification = Assert.IsType<PyTuple>(Assert.Single(outer.Items));
        var body = Assert.IsType<PyTuple>(MarshalCodec.Decode(Assert.IsType<PyBuffer>(notification.Items[1]).Value));
        var payload = Assert.IsType<PyTuple>(body.Items[1]);
        return Assert.IsType<PyTuple>(payload.Items[1]);
    }

    private static string NotificationText(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
        _ => throw new Xunit.Sdk.XunitException("expected notification text"),
    };

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
