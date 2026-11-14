using System.Reflection;
using System.Text;
using Eve.Data;
using Eve.Dogma;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;
using Eve.Universe;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private const long TestShipcasterLauncherId = 1_204_300_002_001;
    private const long TestStarterShipcasterId = 1_204_300_002_002;
    private const long TestShipcasterLandingPadId = 1_204_300_002_101;
    private const long TestStarterLandingPadId = 1_204_300_002_102;
    private const int TestShipcasterFactionId = 500_001;

    [PgFact]
    public async Task Registered_shipcaster_queries_serialize_the_persisted_network_snapshot()
    {
        await using var fixture = await ShipcasterFixture.CreateAsync();
        await fixture.PublishAsync();
        Start(configure: registry =>
        {
            foreach (var service in fixture.Services())
                registry.Register(service);
        });
        var (front, open) = await OpenFrontAsync();

        var all = Assert.IsType<PyList>(await HostRpcAsync(front, open, 1,
            "shipCasterLandingPadMgr", "GetAllLandingPads"));
        var byFaction = Assert.IsType<PyList>(await HostRpcAsync(front, open, 2,
            "shipCasterLandingPadMgr", "GetFactionLandingPads", new PyInt(TestShipcasterFactionId)));
        var otherFaction = Assert.IsType<PyList>(await HostRpcAsync(front, open, 3,
            "shipCasterLandingPadMgr", "GetFactionLandingPads", new PyInt(500_003)));
        var factions = Assert.IsType<PyList>(await HostRpcAsync(front, open, 4,
            "shipCasterLauncherMgr", "GetFactionsWithShipcaster"));

        Assert.Equal(2, all.Items.Count);
        Assert.Equal(all.Items, byFaction.Items);
        Assert.Empty(otherFaction.Items);
        Assert.Equal([new PyInt(TestShipcasterFactionId)], factions.Items);
        AssertLandingPad(all.Items[0], TestShipcasterLandingPadId, fixture.DestinationSystemId,
            built: true, linkTimestamp: 1234, disrupted: false);
        AssertLandingPad(all.Items[1], TestStarterLandingPadId, fixture.DestinationSystemId,
            built: true, linkTimestamp: null, disrupted: false);
    }

    [PgFact]
    public async Task Registered_shipcaster_queries_reject_an_unpublished_network_instead_of_faking_empty_success()
    {
        await using var fixture = await ShipcasterFixture.CreateAsync();
        Start(configure: registry =>
        {
            foreach (var service in fixture.Services())
                registry.Register(service);
        });
        var (front, open) = await OpenFrontAsync();

        var all = await CallErrorAsync(front, open, 1, "shipCasterLandingPadMgr", "GetAllLandingPads");
        AssertShipcasterNotify(all, "Shipcaster network data is not available on this server.");
        var factions = await CallErrorAsync(front, open, 2, "shipCasterLauncherMgr", "GetFactionsWithShipcaster");
        AssertShipcasterNotify(factions, "Shipcaster network data is not available on this server.");
    }

    [PgFact]
    public async Task Registered_shipcaster_jump_checks_the_published_target_and_persists_arrival()
    {
        await using var fixture = await ShipcasterFixture.CreateAsync();
        await fixture.PublishAsync();
        Start(configure: registry =>
        {
            foreach (var service in fixture.Services())
                registry.Register(service);
        }, sessionOpened: fixture.OpenSession);
        var (front, open) = await OpenFrontAsync();

        var (result, notifications) = await CallTravelAsync(front, open, 1,
            "shipcasterTravelMgr", "CmdJumpThroughShipcaster",
            new PyLong(TestShipcasterLauncherId), new PyInt(fixture.DestinationSystemId), new PyLong(TestShipcasterLandingPadId));
        AssertSessionChange(result);
        Assert.Equal(["OnJumpQueueUpdate"], notifications);
        Assert.Single(fixture.Simulation.Commands, command => command.Command == SimCommands.StructureJump);

        var replay = Assert.IsType<PyTuple>(await HostRpcAsync(front, open, 2,
            "shipcasterTravelMgr", "CmdJumpThroughShipcaster",
            new PyLong(TestShipcasterLauncherId), new PyInt(fixture.DestinationSystemId), new PyLong(TestShipcasterLandingPadId)));
        Assert.Equal(result, replay);
        Assert.Single(fixture.Simulation.Commands, command => command.Command == SimCommands.StructureJump);

        fixture.Simulation.Deliver(new SimJumped((ulong)fixture.Session(0).SessionId, fixture.OriginSystemId,
            TestShipcasterLauncherId, fixture.DestinationSystemId, TestShipcasterLandingPadId));
        await WaitUntilAsync(async () =>
        {
            var persistedCharacter = await fixture.Rig.Characters.GetAsync(fixture.CharacterId);
            var persistedShip = await fixture.Rig.Items.GetAsync(fixture.ShipId);
            return fixture.Rig.Space.SolarSystemOf(fixture.Session(0)) == fixture.DestinationSystemId &&
                persistedCharacter?.SolarSystemId == fixture.DestinationSystemId &&
                persistedShip?.LocationId == fixture.DestinationSystemId;
        });
    }

    [PgFact]
    public async Task Concurrent_shipcaster_retries_submit_one_jump_and_replay_the_session_change()
    {
        await using var fixture = await ShipcasterFixture.CreateAsync();
        await fixture.PublishAsync();
        Start(configure: registry =>
        {
            foreach (var service in fixture.Services())
                registry.Register(service);
        }, sessionOpened: fixture.OpenSession);
        var (front, open) = await OpenFrontAsync();
        var first = MarshalCodec.Encode(Packets.CallWithKwargs(open, 10,
            "shipcasterTravelMgr", "CmdJumpThroughShipcaster",
            Py.Tuple(new PyLong(TestShipcasterLauncherId), new PyInt(fixture.DestinationSystemId),
                new PyLong(TestShipcasterLandingPadId)), TransportKwargs()));
        var second = MarshalCodec.Encode(Packets.CallWithKwargs(open, 11,
            "shipcasterTravelMgr", "CmdJumpThroughShipcaster",
            Py.Tuple(new PyLong(TestShipcasterLauncherId), new PyInt(fixture.DestinationSystemId),
                new PyLong(TestShipcasterLandingPadId)), TransportKwargs()));
        await Task.WhenAll(
            front.SendAsync(new Packet(open.SessionId, first)),
            front.SendAsync(new Packet(open.SessionId, second)));

        var responses = new List<PyValue>();
        var notifications = new List<string>();
        while (responses.Count < 2)
        {
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(send.Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            if (packet.Type == MachoMessage.Notification)
            {
                notifications.Add(Assert.IsType<string>(packet.Dest.BroadcastId));
                continue;
            }
            Assert.Equal(MachoMessage.CallRsp, packet.Type);
            responses.Add(Result(decoded)!);
        }

        AssertSessionChange(responses[0]);
        Assert.Equal(responses[0], responses[1]);
        Assert.Equal(["OnJumpQueueUpdate"], notifications);
        Assert.Single(fixture.Simulation.Commands, command => command.Command == SimCommands.StructureJump);
    }

    [PgFact]
    public async Task Registered_shipcaster_rejects_a_stale_target_without_moving()
    {
        await using var stale = await ShipcasterFixture.CreateAsync();
        await stale.PublishAsync(targetActive: false);
        Start(configure: registry =>
        {
            foreach (var service in stale.Services())
                registry.Register(service);
        }, sessionOpened: stale.OpenSession);
        var (front, open) = await OpenFrontAsync();

        var staleReply = await CallErrorAsync(front, open, 1, "shipcasterTravelMgr", "CmdJumpThroughShipcaster",
            new PyLong(TestShipcasterLauncherId), new PyInt(stale.DestinationSystemId), new PyLong(TestShipcasterLandingPadId));
        AssertShipcasterNotify(staleReply, "No active shipcaster destination is available.");
        Assert.DoesNotContain(stale.Simulation.Commands, command => command.Command == SimCommands.StructureJump);
    }

    [PgFact]
    public async Task Registered_shipcaster_rejects_a_target_at_its_exact_start_time()
    {
        await using var fixture = await ShipcasterFixture.CreateAsync();
        await fixture.PublishAsync(targetStartsAtNow: true);
        Start(configure: registry =>
        {
            foreach (var service in fixture.Services())
                registry.Register(service);
        }, sessionOpened: fixture.OpenSession);
        var (front, open) = await OpenFrontAsync();

        var failure = await CallErrorAsync(front, open, 1, "shipcasterTravelMgr", "CmdJumpThroughShipcaster",
            new PyLong(TestShipcasterLauncherId), new PyInt(fixture.DestinationSystemId), new PyLong(TestShipcasterLandingPadId));
        AssertShipcasterNotify(failure, "No active shipcaster destination is available.");
        Assert.DoesNotContain(fixture.Simulation.Commands, command => command.Command == SimCommands.StructureJump);
    }

    [PgFact]
    public async Task Registered_shipcaster_rejects_a_client_destination_that_differs_from_the_active_schedule()
    {
        await using var fixture = await ShipcasterFixture.CreateAsync();
        await fixture.PublishAsync();
        Start(configure: registry =>
        {
            foreach (var service in fixture.Services())
                registry.Register(service);
        }, sessionOpened: fixture.OpenSession);
        var (front, open) = await OpenFrontAsync();

        var failure = await CallErrorAsync(front, open, 1, "shipcasterTravelMgr", "CmdJumpThroughShipcaster",
            new PyLong(TestShipcasterLauncherId), new PyInt(fixture.DestinationSystemId + 1), new PyLong(TestShipcasterLandingPadId));
        AssertShipcasterNotify(failure, "No active shipcaster destination is available.");
        Assert.DoesNotContain(fixture.Simulation.Commands, command => command.Command == SimCommands.StructureJump);
    }

    [PgFact]
    public async Task Registered_shipcaster_rejects_a_target_that_does_not_match_the_pilot_war_faction()
    {
        await using var fixture = await ShipcasterFixture.CreateAsync();
        await fixture.PublishAsync();
        Start(configure: registry =>
        {
            foreach (var service in fixture.Services(500_003))
                registry.Register(service);
        }, sessionOpened: fixture.OpenSession);
        var (front, open) = await OpenFrontAsync();

        var failure = await CallErrorAsync(front, open, 1, "shipcasterTravelMgr", "CmdJumpThroughShipcaster",
            new PyLong(TestShipcasterLauncherId), new PyInt(fixture.DestinationSystemId), new PyLong(TestShipcasterLandingPadId));
        AssertShipcasterNotify(failure, "No active shipcaster destination is available.");
        Assert.DoesNotContain(fixture.Simulation.Commands, command => command.Command == SimCommands.StructureJump);
    }

    [PgFact]
    public async Task Registered_shipcaster_rejects_a_launcher_outside_the_client_interaction_range()
    {
        await using var fixture = await ShipcasterFixture.CreateAsync();
        await fixture.PublishAsync();
        fixture.Simulation.SetPosition(TestShipcasterLauncherId, fixture.OriginSystemId, 250, 10_000, 0, 0);
        Start(configure: registry =>
        {
            foreach (var service in fixture.Services())
                registry.Register(service);
        }, sessionOpened: fixture.OpenSession);
        var (front, open) = await OpenFrontAsync();

        var failure = await CallErrorAsync(front, open, 1, "shipcasterTravelMgr", "CmdJumpThroughShipcaster",
            new PyLong(TestShipcasterLauncherId), new PyInt(fixture.DestinationSystemId), new PyLong(TestShipcasterLandingPadId));
        AssertShipcasterNotify(failure, "No active shipcaster destination is available.");
        Assert.DoesNotContain(fixture.Simulation.Commands, command => command.Command == SimCommands.StructureJump);
    }

    [PgFact]
    public async Task Registered_starter_shipcaster_is_one_time_and_keeps_eligibility_persisted()
    {
        await using var fixture = await ShipcasterFixture.CreateAsync();
        await fixture.PublishAsync();
        Start(configure: registry =>
        {
            foreach (var service in fixture.Services())
                registry.Register(service);
        }, sessionOpened: fixture.OpenSession);
        var (front, open) = await OpenFrontAsync();

        Assert.Equal(PyBool.True, await HostRpcAsync(front, open, 1,
            "starterShipcasterTravelMgr", "CanCharacterJump"));
        var invalidLauncher = await CallErrorAsync(front, open, 2, "starterShipcasterTravelMgr",
            "CmdJumpThroughStarterShipcaster", new PyLong(TestStarterShipcasterId + 1));
        AssertShipcasterNotify(invalidLauncher, "No starter shipcaster destination is available.");
        Assert.False(await fixture.Store.StarterUsedAsync(fixture.CharacterId));

        fixture.Simulation.StructureJumpRefusal = "TOO_FAR_FROM_STRUCTURE";
        var refused = await CallErrorAsync(front, open, 3, "starterShipcasterTravelMgr",
            "CmdJumpThroughStarterShipcaster", new PyLong(TestStarterShipcasterId));
        Assert.Equal("CustomNotify", UserErrorKey(refused));
        Assert.False(await fixture.Store.StarterUsedAsync(fixture.CharacterId));
        Assert.Equal(PyBool.True, await HostRpcAsync(front, open, 4,
            "starterShipcasterTravelMgr", "CanCharacterJump"));

        fixture.Simulation.StructureJumpRefusal = null;
        var (result, notifications) = await CallTravelAsync(front, open, 5, "starterShipcasterTravelMgr",
            "CmdJumpThroughStarterShipcaster", new PyLong(TestStarterShipcasterId));
        AssertSessionChange(result);
        Assert.Equal(["OnJumpQueueUpdate"], notifications);
        var replay = Assert.IsType<PyTuple>(await HostRpcAsync(front, open, 6,
            "starterShipcasterTravelMgr", "CmdJumpThroughStarterShipcaster", new PyLong(TestStarterShipcasterId)));
        Assert.Equal(result, replay);
        Assert.True(await fixture.Store.StarterUsedAsync(fixture.CharacterId));
        Assert.False(await fixture.Store.HasUnusedStarterAtAsync(fixture.CharacterId, fixture.OriginSystemId));
        Assert.Equal(PyBool.False, await HostRpcAsync(front, open, 7,
            "starterShipcasterTravelMgr", "CanCharacterJump"));

        var restartedStore = new PgShipcasterStore(fixture.Rig.Source, fixture.Schema);
        Assert.True(await restartedStore.StarterUsedAsync(fixture.CharacterId));
        Assert.False(await restartedStore.HasUnusedStarterAtAsync(fixture.CharacterId, fixture.OriginSystemId));
        Assert.Equal(2, fixture.Simulation.Commands.Count(command => command.Command == SimCommands.StructureJump));
    }

    private async Task<(PyValue Result, IReadOnlyList<string> Notifications)> CallTravelAsync(
        TestFront front,
        SessionOpen open,
        uint callId,
        string service,
        string method,
        params PyValue[] args)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, callId, service, method, new PyTuple(args), TransportKwargs()))));
        var notifications = new List<string>();
        while (true)
        {
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(send.Payload);
            var envelope = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            if (envelope.Type == MachoMessage.Notification)
            {
                notifications.Add(Assert.IsType<string>(envelope.Dest.BroadcastId));
                continue;
            }
            Assert.Equal(MachoMessage.CallRsp, envelope.Type);
            return (Result(decoded)!, notifications);
        }
    }

    private async Task<Send> CallErrorAsync(TestFront front, SessionOpen open, uint callId,
        string service, string method, params PyValue[] args)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, callId, service, method, new PyTuple(args), TransportKwargs()))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.ErrorResponse,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(reply.Payload))).Type);
        return reply;
    }

    private static void AssertShipcasterNotify(Send reply, string message)
    {
        Assert.Equal("CustomNotify", UserErrorKey(reply));
        Assert.Contains(message, Encoding.ASCII.GetString(reply.Payload.Span));
    }

    private static void AssertSessionChange(PyValue value)
    {
        var tuple = Assert.IsType<PyTuple>(value);
        Assert.Equal(2, tuple.Items.Count);
        Assert.Same(PyNone.Instance, tuple.Items[0]);
        Assert.IsType<PyLong>(tuple.Items[1]);
    }

    private static void AssertLandingPad(PyValue value, long itemId, int systemId,
        bool built, long? linkTimestamp, bool disrupted)
    {
        var row = Assert.IsType<PyObjectEx>(value);
        var header = Assert.IsType<PyTuple>(row.Header).Items;
        Assert.Equal(new PyToken("shipcaster.landingPadData.LandingPadData"), header[0]);
        Assert.Equal(new PyList([
            new PyLong(itemId), new PyInt(systemId), new PyInt(TestShipcasterFactionId),
            built ? PyBool.True : PyBool.False,
            linkTimestamp is { } timestamp ? new PyLong(timestamp) : PyNone.Instance,
            disrupted ? PyBool.True : PyBool.False,
        ]), header[1]);
    }

    private sealed class ShipcasterFixture : IAsyncDisposable
    {
        private const int Origin = 30_000_142;
        private readonly int _shipTypeId;
        private readonly long _corporationId;
        private readonly ClientSession?[] _sessions = new ClientSession?[1];
        private int _nextSession;

        private ShipcasterFixture(AbyssalHostFlowTests.Rig rig, PgShipcasterStore store, string schema,
            SolarSystemCatalog systems, JumpBridgeSimulation simulation, long characterId, long corporationId,
            long shipId, int shipTypeId, int destinationSystemId)
        {
            Rig = rig;
            Store = store;
            Schema = schema;
            Systems = systems;
            Simulation = simulation;
            CharacterId = characterId;
            _corporationId = corporationId;
            ShipId = shipId;
            _shipTypeId = shipTypeId;
            DestinationSystemId = destinationSystemId;
        }

        public AbyssalHostFlowTests.Rig Rig { get; }
        public PgShipcasterStore Store { get; }
        public string Schema { get; }
        public SolarSystemCatalog Systems { get; }
        public JumpBridgeSimulation Simulation { get; }
        public long CharacterId { get; }
        public long ShipId { get; }
        public int OriginSystemId => Origin;
        public int DestinationSystemId { get; }
        public ClientSession Session(int index) => _sessions[index] ?? throw new InvalidOperationException("Host session is not open");

        public static async Task<ShipcasterFixture> CreateAsync()
        {
            var simulationContract = DispatchProxy.Create<ISimulation, JumpBridgeSimulation>();
            var simulation = (JumpBridgeSimulation)(object)simulationContract;
            var rig = await AbyssalHostFlowTests.Rig.Create(simulationContract);
            var schema = "shipcaster_hf_" + Guid.NewGuid().ToString("N")[..16];
            try
            {
                var run = await rig.CreateRun(enter: false);
                var character = await rig.Characters.GetAsync(run.Data.CreatorCharacterId)
                    ?? throw new InvalidOperationException("test character was not created");
                var shipId = character.ShipId ?? throw new InvalidOperationException("test ship was not created");
                var ship = await rig.Items.GetAsync(shipId) ?? throw new InvalidOperationException("test ship item was not created");
                var systems = await SolarSystemCatalog.LoadAsync(rig.Sde);
                var destination = systems.Systems
                    .Select(system => (System: system, Distance: systems.LightYearsBetween(Origin, system.Id)))
                    .Where(entry => entry.System.Id != Origin && entry.Distance is > 0)
                    .OrderBy(entry => entry.Distance)
                    .FirstOrDefault();
                if (destination.System is null)
                    throw new InvalidOperationException("pinned SDE has no destination system for a shipcaster test");
                await PgShipcasterStore.MigrateAsync(rig.Source, schema);
                var store = new PgShipcasterStore(rig.Source, schema);
                var dogma = await DogmaStatic.LoadAsync(rig.Sde);
                await dogma.EnsureTypesAsync([ship.TypeId]);
                var shipRadius = dogma.Type(ship.TypeId)?.Attributes.GetValueOrDefault(162) ?? 50;
                simulation.SetPosition(shipId, Origin, shipRadius, 0, 0, 0);
                simulation.SetPosition(TestShipcasterLauncherId, Origin, 250, 1_000, 0, 0);
                simulation.SetPosition(TestStarterShipcasterId, Origin, 250, 2_000, 0, 0);
                simulation.DestinationSystemId = destination.System.Id;
                simulation.DestinationStructureId = TestShipcasterLandingPadId;
                return new ShipcasterFixture(rig, store, schema, systems, simulation,
                    character.Id, character.CorporationId, shipId, ship.TypeId, destination.System.Id);
            }
            catch
            {
                await using var command = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
                await rig.DisposeAsync();
                throw;
            }
        }

        public async Task PublishAsync(bool targetActive = true, bool targetStartsAtNow = false)
        {
            var nowFileTime = Py.FileTimeFromUnixMs(Rig.Now);
            var target = new ShipcasterScheduledTarget(DestinationSystemId, TestShipcasterLandingPadId,
                TestShipcasterFactionId, targetStartsAtNow ? nowFileTime : nowFileTime - 10_000,
                targetActive ? nowFileTime + 10_000 : nowFileTime - 1);
            await Store.PublishSnapshotAsync(new ShipcasterNetworkSnapshot(
                [
                    new ShipcasterLandingPadState(TestShipcasterLandingPadId, DestinationSystemId,
                        TestShipcasterFactionId, true, 1234, false),
                    new ShipcasterLandingPadState(TestStarterLandingPadId, DestinationSystemId,
                        TestShipcasterFactionId, true, null, false),
                ],
                [
                    new ShipcasterLauncherState(TestShipcasterLauncherId, Origin, TestShipcasterFactionId, [], false),
                    new ShipcasterLauncherState(TestStarterShipcasterId, Origin, TestShipcasterFactionId, [], true,
                        DestinationSystemId, TestStarterLandingPadId),
                ],
                new Dictionary<long, IReadOnlyList<ShipcasterScheduledTarget>>
                {
                    [TestShipcasterLauncherId] = [target],
                }), Rig.Now);
        }

        public IService[] Services(long characterWarFactionId = TestShipcasterFactionId) =>
            CharacterServicesComposition.CreateShipcasterServices(Systems, Store, Rig.Space,
                (_, _) => ValueTask.FromResult(characterWarFactionId), () => Rig.Now);

        public void OpenSession(ClientSession session)
        {
            var index = Interlocked.Increment(ref _nextSession) - 1;
            if (index >= _sessions.Length)
                throw new InvalidOperationException("unexpected additional shipcaster test session");
            _sessions[index] = session;
            session.CharacterId = CharacterId;
            session.Set("charid", new PyLong(CharacterId));
            session.Set("corpid", new PyLong(_corporationId));
            session.Set("shipid", new PyLong(ShipId));
            session.Set("solarsystemid", new PyInt(Origin));
            session.Set("solarsystemid2", new PyInt(Origin));
            session.Set("locationid", new PyInt(Origin));
            Rig.Sessions.Enter(CharacterId, session);
            Rig.Space.EnterPilotForTest(session, CharacterId, ShipId, _shipTypeId, Origin);
        }

        public async ValueTask DisposeAsync()
        {
            await Rig.Space.QuiesceAsync();
            await using (var command = Rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {Schema} CASCADE"))
                await command.ExecuteNonQueryAsync();
            await Rig.DisposeAsync();
        }
    }
}
