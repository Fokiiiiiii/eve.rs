using Eve.Agents;
using Eve.Characters;
using Eve.Data;
using Eve.Events;
using Eve.Macho;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Core;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class AgentLocatorConcurrencyHostWireTests
{
    private const long Pilot = 140000021;
    private const long Agent = 3009400;
    private const long Corporation = 1000001;
    private const long HomeSystem = 30000001;
    private const long TargetSystem = 30000142;
    private const long HomeStation = 60000001;

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class DeliveryGate(IAgentLocatorPort inner) : IAgentLocatorPort
    {
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public LocateStore Locates => inner.Locates;
        public ValueTask<long?> FindCharacterAsync(string name, CancellationToken token) => inner.FindCharacterAsync(name, token);
        public ValueTask<LocateTarget?> TargetAsync(long id, CancellationToken token) => inner.TargetAsync(id, token);
        public ValueTask<LocatePlace?> PlaceAsync(long id, CancellationToken token) => inner.PlaceAsync(id, token);

        public async ValueTask<LocateDeliveryResult> DeliverAsync(
            long characterId, long agentId, long targetCharacterId, long fee, int cooldownMinutes,
            System.Text.Json.Nodes.JsonObject result, CancellationToken token)
        {
            if (Interlocked.Increment(ref _arrived) == 2)
                _bothArrived.TrySetResult();
            await _bothArrived.Task.WaitAsync(token);
            return await inner.DeliverAsync(characterId, agentId, targetCharacterId, fee, cooldownMinutes, result, token);
        }
    }

    private static AgentMissionAgentData AgentData() => new(
        AgentId: Agent, AgentTypeId: 2, DivisionId: 18, Level: 1, StationId: HomeStation,
        CorporationId: Corporation, IsLocatorAgent: true, IsInSpace: false, CareerId: null, ImportantMission: false,
        MissionKind: "locator", MissionTypeLabel: "locator", SpecialityId: null, StationTypeId: null, BloodlineId: null,
        SolarSystemId: HomeSystem, DungeonId: null, SpawnPointId: null, TypeId: null);

    [PgFact]
    public async Task Concurrent_wire_accepts_for_one_open_request_charge_once_and_publish_one_result()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var agentSchema = "loc_agent_" + suffix;
        var walletSchema = "loc_wallet_" + suffix;
        var characterSchema = "loc_char_" + suffix;
        var eventSchema = "loc_event_" + suffix;
        var schedulerSchema = "loc_sched_" + suffix;
        try
        {
            await AgentMissionStore.MigrateAsync(source, agentSchema);
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await PgCharacterStore.MigrateAsync(source, characterSchema);
            await EventBus.MigrateAsync(source, eventSchema);

            var nowMs = DateTimeOffset.Parse("2026-10-11T12:00:00Z").ToUnixTimeMilliseconds();
            var nowCounter = 0L;
            var scheduler = new DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            var events = new EventBus(source, scheduler, schema: eventSchema);
            var characters = new PgCharacterStore(source, characterSchema);
            var sessions = new CharacterSessions();
            var pilotSink = new Sink();
            var pilotSession = new ClientSession(pilotSink) { UserId = 7, CharacterId = Pilot };
            pilotSession.Set("charid", new PyInt(Pilot));
            pilotSession.Set("stationid", new PyInt(HomeStation));
            pilotSession.Set("solarsystemid2", new PyInt(HomeSystem));
            sessions.Enter(Pilot, pilotSession);
            var targetSession = new ClientSession(new Sink()) { UserId = 8 };
            targetSession.Set("stationid", new PyInt(HomeStation));
            targetSession.Set("solarsystemid2", new PyInt(TargetSystem));

            long targetId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                targetId = await characters.InsertAsync(connection, transaction,
                    new NewCharacter(8, "Ada Pilot", 1, 1, 1, 1, 1, 1, Corporation, null, HomeStation,
                        (int)TargetSystem, 20000001, 10000001, null, null));
                await transaction.CommitAsync();
            }
            targetSession.CharacterId = targetId;
            sessions.Enter(targetId, targetSession);

            var wallets = new WalletTransactions(source, walletSchema);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await new PgWalletStore(source, walletSchema).OpenAsync(connection, transaction, Pilot, 1_000_000m, 0);
                await transaction.CommitAsync();
            }

            var locates = new LocateStore(source, agentSchema);
            await locates.SetTargetAsync(Pilot, Agent, targetId);
            var systems = new SolarSystemCatalog([
                new SolarSystemInfo((int)HomeSystem, "Home", 20000001, 10000001, 0.9),
                new SolarSystemInfo((int)TargetSystem, "Target", 20000001, 10000001, 0.8),
            ]);
            var persistence = new Eve.Host.PersistenceAgentLocator(source, locates, characters, sessions, systems,
                wallets, events, () => nowMs + Interlocked.Increment(ref nowCounter));
            var locator = new DeliveryGate(persistence);
            var missions = new AgentMissionStore(source, agentSchema);
            var dialogue = new AgentDialogueSources(
                missions,
                (_, _, _, _, _) => ValueTask.FromResult(new AgentStandings(10, 10, 10)),
                (_, _, _) => ValueTask.FromResult(0),
                _ => 500_003,
                (_, _, _) => ValueTask.FromResult<AgentMissionContent?>(null),
                () => DateTimeOffset.FromUnixTimeMilliseconds(nowMs),
                Locator: locator);
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
            var dispatcher = new PacketDispatcher(registry, () => nowMs);

            Assert.True(await dispatcher.DispatchAsync(Request(1, "agentMgr", new PyInt(1), "MachoBindObject",
                new PyTuple([new PyInt(Agent)]), PyNone.Instance), pilotSession));
            var binding = Assert.IsType<PyTuple>(ResponseResult(pilotSink.Packets[^1]));
            var boundAgent = Assert.IsType<PySubStruct>(binding.Items[0]);
            var oid = BoundObjects.ExtractName(boundAgent)!;

            var first = dispatcher.DispatchAsync(Request(2, null, new PyStr(oid), "DoAction", new PyInt(AgentDialogueButton.LocateAccept)), pilotSession);
            var second = dispatcher.DispatchAsync(Request(3, null, new PyStr(oid), "DoAction", new PyInt(AgentDialogueButton.LocateAccept)), pilotSession);
            await Task.WhenAll(first, second);

            Assert.Equal(995_000m, await wallets.AccountBalanceAsync(new WalletAccount(Pilot)));
            Assert.Single(await wallets.JournalAsync(Pilot));
            await using var countEvents = source.CreateCommand($"SELECT count(*) FROM {eventSchema}.outbox WHERE topic = $1");
            countEvents.Parameters.AddWithValue(LocateDelivered.Topic);
            Assert.Equal(1L, (long)(await countEvents.ExecuteScalarAsync())!);
            var final = await locates.ReadAsync(Pilot, Agent);
            Assert.Null(final!.TargetCharacterId);
            Assert.InRange(final.LocatedAt!.Value.ToUnixTimeMilliseconds(), nowMs + 1, nowMs + 2);
            Assert.All(pilotSink.Packets.Skip(1), packet => Assert.Empty(DialogueActionsFromResponse(packet)));

            await using (var emptyWallet = source.CreateCommand($"UPDATE {walletSchema}.balances SET isk = 0 WHERE owner_id = $1 AND account_key = 1000"))
            {
                emptyWallet.Parameters.AddWithValue(Pilot);
                Assert.Equal(1, await emptyWallet.ExecuteNonQueryAsync());
            }
            nowMs += 6 * 60 * 1000L;
            await locates.SetTargetAsync(Pilot, Agent, targetId);
            Assert.True(await dispatcher.DispatchAsync(Request(4, null, new PyStr(oid), "DoAction",
                new PyInt(AgentDialogueButton.LocateAccept)), pilotSession));
            Assert.Contains(DialogueActionsFromResponse(pilotSink.Packets[^1]),
                action => ((PyInt)action.Items[0]).Value == AgentDialogueButton.LocateAccept);
            Assert.Equal(0m, await wallets.AccountBalanceAsync(new WalletAccount(Pilot)));
            var unpaid = await locates.ReadAsync(Pilot, Agent);
            Assert.Equal(targetId, unpaid!.TargetCharacterId);
            Assert.Equal(final.LocatedAt, unpaid.LocatedAt);
            Assert.Single(await wallets.JournalAsync(Pilot));
            Assert.Equal(1L, (long)(await countEvents.ExecuteScalarAsync())!);
        }
        finally
        {
            foreach (var schema in new[] { agentSchema, walletSchema, characterSchema, eventSchema, schedulerSchema })
                await using (var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
                    await drop.ExecuteNonQueryAsync();
            await source.DisposeAsync();
        }
    }

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
        var payload = Assert.IsType<PyTuple>(packet[4]);
        return Assert.IsType<PySubStream>(payload.Items[0]).Decoded!;
    }

    private static IReadOnlyList<PyTuple> DialogueActionsFromResponse(PyValue response)
    {
        var root = Assert.IsType<PyTuple>(ResponseResult(response));
        var displayed = Assert.IsType<PyTuple>(root.Items[0]);
        return Assert.IsType<PyList>(displayed.Items[1]).Items.Select(item => Assert.IsType<PyTuple>(item)).ToArray();
    }

}
