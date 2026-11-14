using System.Reflection;
using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Services.Wallet;
using Eve.Space;
using Eve.Structures;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using System.Text.Json.Nodes;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private const long JumpBridgeSource = 1_204_300_001_001;
    private const long JumpBridgeDestination = 1_204_300_001_002;
    private const long JumpBridgeProfile = 604_300_001;

    public class JumpBridgeSimulation : DispatchProxy
    {
        private Action<SimMessage>? _received;
        private readonly Dictionary<long, (int SystemId, double[] Position, double Radius)> _positions = [];
        private readonly HashSet<ulong> _structureJumpAccepted = [];
        public List<(ulong SessionId, byte Command, double[] Args)> Commands { get; } = [];
        public int DestinationSystemId { get; set; }
        public long DestinationStructureId { get; set; }
        public string? StructureJumpRefusal { get; set; }

        public void SetPosition(long itemId, int systemId, double radius, params double[] position) =>
            _positions[itemId] = (systemId, position, radius);

        public void Deliver(SimMessage message) => _received?.Invoke(message);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method);
            if (method.Name == "get_IsConnected") return true;
            if (method.Name == "add_Received") { _received += (Action<SimMessage>)args![0]!; return null; }
            if (method.Name == "remove_Received") { _received -= (Action<SimMessage>)args![0]!; return null; }
            if (method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")) return null;
            if (method.Name == nameof(ISimulation.CommandAsync) && args is [ulong sessionId, byte command, double[] values, CancellationToken])
            {
                Commands.Add((sessionId, command, values));
                if (command == SimCommands.PositionOf && values.Length > 0 && _positions.TryGetValue((long)values[0], out var pose))
                {
                    return Task.FromResult(new CommandReply(true, 0, null)
                    {
                        Position = pose.Position,
                        Radius = pose.Radius,
                    });
                }
                if (command == SimCommands.StructureJump)
                {
                    if (StructureJumpRefusal is { } refusal)
                        return Task.FromResult(new CommandReply(false, 0, refusal));
                    if (!_structureJumpAccepted.Add(sessionId))
                        return Task.FromResult(new CommandReply(false, 0, "STARGATE_JUMP_IN_PROGRESS"));
                    return Task.FromResult(new CommandReply(true, 0, null, null,
                        DestinationSystemId, DestinationStructureId));
                }
                return Task.FromResult(new CommandReply(true, 0, null));
            }
            if (method.ReturnType == typeof(Task<string>)) return Task.FromResult<string?>(null);
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (method.ReturnType == typeof(void)) return null;
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    private sealed class JumpBridgeFixture : IAsyncDisposable
    {
        private readonly string _structureSchema;
        private readonly string _walletSchema;
        private readonly DogmaStatic _dogma;
        private readonly SolarSystemCatalog _systems;
        private readonly JumpBridgeSimulation _simulation;
        private readonly PersistenceStructureDirectoryReadModel _directory;
        private readonly StructureStore _structures;
        private readonly WalletTransactions _wallets;
        private readonly long _fuelItemId;
        private readonly long _fuelQuantity;
        private readonly long _characterId;
        private readonly long _characterCorporationId;
        private readonly long _shipId;
        private readonly int _shipTypeId;
        private readonly int _originSystemId;
        private readonly int _destinationSystemId;
        private readonly ClientSession?[] _sessions = new ClientSession?[2];
        private int _nextSession;
        private readonly WalletAnnouncer _announcer;

        private JumpBridgeFixture(
            AbyssalHostFlowTests.Rig rig,
            string structureSchema,
            string walletSchema,
            DogmaStatic dogma,
            SolarSystemCatalog systems,
            JumpBridgeSimulation simulation,
            PersistenceStructureDirectoryReadModel directory,
            StructureStore structures,
            WalletTransactions wallets,
            long fuelItemId,
            long fuelQuantity,
            long characterId,
            long characterCorporationId,
            long shipId,
            int shipTypeId,
            int originSystemId,
            int destinationSystemId)
        {
            Rig = rig;
            _structureSchema = structureSchema;
            _walletSchema = walletSchema;
            _dogma = dogma;
            _systems = systems;
            _simulation = simulation;
            _directory = directory;
            _structures = structures;
            _wallets = wallets;
            _fuelItemId = fuelItemId;
            _fuelQuantity = fuelQuantity;
            _characterId = characterId;
            _characterCorporationId = characterCorporationId;
            _shipId = shipId;
            _shipTypeId = shipTypeId;
            _originSystemId = originSystemId;
            _destinationSystemId = destinationSystemId;
            _announcer = new WalletAnnouncer(wallets, rig.Sessions);
        }

        public AbyssalHostFlowTests.Rig Rig { get; }
        public long CharacterId => _characterId;
        public long CharacterCorporationId => _characterCorporationId;
        public long ShipId => _shipId;
        public ClientSession Session(int index) => _sessions[index] ?? throw new InvalidOperationException("Host session is not open");
        public long FuelItemId => _fuelItemId;
        public long FuelQuantity => _fuelQuantity;
        public int OriginSystemId => _originSystemId;
        public int DestinationSystemId => _destinationSystemId;
        public SolarSystemCatalog Systems => _systems;
        public JumpBridgeSimulation Simulation => _simulation;
        public WalletTransactions Wallets => _wallets;
        public PersistenceStructureDirectoryReadModel Directory => _directory;
        public StructureStore Structures => _structures;

        public static async Task<JumpBridgeFixture> CreateAsync(
            long fuelQuantity = 1_000_000,
            decimal rate = 0.025m,
            decimal characterBalance = 100_000m)
        {
            var simulationContract = DispatchProxy.Create<ISimulation, JumpBridgeSimulation>();
            var simulation = (JumpBridgeSimulation)(object)simulationContract;
            var rig = await AbyssalHostFlowTests.Rig.Create(simulationContract);
            var run = await rig.CreateRun(enter: false);
            var character = await rig.Characters.GetAsync(run.Data.CreatorCharacterId) ?? throw new InvalidOperationException("test character was not created");
            var shipId = character.ShipId ?? throw new InvalidOperationException("test ship was not created");
            var ship = await rig.Items.GetAsync(shipId) ?? throw new InvalidOperationException("test ship item was not created");
            var structureSchema = "jb_travel_s_" + Guid.NewGuid().ToString("N")[..12];
            var walletSchema = "jb_travel_w_" + Guid.NewGuid().ToString("N")[..12];
            try
            {
                await StructureStore.MigrateAsync(rig.Source, structureSchema);
                await PgWalletStore.MigrateAsync(rig.Source, walletSchema);
                var structures = new StructureStore(rig.Source, structureSchema);
                var wallets = new WalletTransactions(rig.Source, walletSchema);
                var walletStore = new PgWalletStore(rig.Source, walletSchema);
                var dogma = await DogmaStatic.LoadAsync(rig.Sde);
                await dogma.EnsureTypesAsync([35_841, 16_273]);
                var systems = await SolarSystemCatalog.LoadAsync(rig.Sde);
                const int origin = 30_000_142;
                var destination = systems.Systems
                    .Select(system => (System: system, Distance: systems.LightYearsBetween(origin, system.Id)))
                    .Where(entry => entry.System.Id != origin && entry.Distance is > 0 and <= 5)
                    .OrderBy(entry => entry.Distance)
                    .FirstOrDefault();
                if (destination.System is null)
                    throw new InvalidOperationException("the pinned SDE has no nearby jump-bridge test system");

                var fuelId = 0L;
                await using (var connection = await rig.Source.OpenConnectionAsync())
                await using (var transaction = await connection.BeginTransactionAsync())
                {
                    foreach (var (id, systemId, name) in new[]
                    {
                        (JumpBridgeSource, origin, "Jump Bridge Test Source"),
                        (JumpBridgeDestination, destination.System.Id, "Jump Bridge Test Destination"),
                    })
                    {
                        await structures.CreateAsync(connection, transaction,
                            new NewStructure(id, 35_841, systemId, 0, 0, 0, 0, character.CorporationId,
                                0, name, "", JumpBridgeProfile));
                        await structures.SaveAsync(connection, transaction, new StructureSnapshot
                        {
                            StructureId = id,
                            State = StructureState.ShieldVulnerable,
                            Upkeep = UpkeepState.FullPower,
                        });
                        await structures.SetServiceAsync(connection, transaction, id, StructureServiceIds.JumpBridge,
                            ServiceState.Online, 1_790_000_000_000);
                    }
                    fuelId = await rig.Items.CreateAsync(connection, transaction,
                        new NewItem(16_273, character.CorporationId, JumpBridgeSource, 172, fuelQuantity));
                    await walletStore.OpenAsync(connection, transaction, character.Id, characterBalance, 0);
                    await walletStore.OpenAsync(connection, transaction, character.CorporationId, 0m, 0);
                    await wallets.OpenCorporationDivisionsAsync(connection, transaction, character.CorporationId);
                    await transaction.CommitAsync();
                }

                var structuresState = new JsonObject
                {
                    [JumpBridgeSource.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["structureID"] = JumpBridgeSource,
                        ["typeID"] = 35_841,
                        ["solarSystemID"] = origin,
                        ["ownerCorpID"] = character.CorporationId,
                        ["profileID"] = JumpBridgeProfile,
                        ["serviceStates"] = new JsonObject { ["15"] = ServiceState.Online },
                        ["devFlags"] = new JsonObject { ["destinationSolarsystemID"] = destination.System.Id },
                    },
                    [JumpBridgeDestination.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["structureID"] = JumpBridgeDestination,
                        ["typeID"] = 35_841,
                        ["solarSystemID"] = destination.System.Id,
                        ["ownerCorpID"] = character.CorporationId,
                        ["profileID"] = JumpBridgeProfile,
                        ["serviceStates"] = new JsonObject { ["15"] = ServiceState.Online },
                        ["devFlags"] = new JsonObject { ["destinationSolarsystemID"] = origin },
                    },
                };
                var profiles = new JsonObject
                {
                    ["profilesByID"] = new JsonObject
                    {
                        [JumpBridgeProfile.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                        {
                            ["corporationID"] = character.CorporationId,
                            ["settingsBySettingID"] = new JsonObject
                            {
                                ["34"] = new JsonArray(new JsonObject { ["groupID"] = 0, ["value"] = rate }),
                            },
                        },
                    },
                };
                Task<JsonObject> ReadTable(string table, CancellationToken _) => Task.FromResult(table switch
                {
                    "structures" => (JsonObject)structuresState.DeepClone(),
                    "structureProfiles" => (JsonObject)profiles.DeepClone(),
                    _ => new JsonObject(),
                });
                Task<Eve.Relations.Affiliation> GetAffiliation(long _, CancellationToken __) =>
                    Task.FromResult(new Eve.Relations.Affiliation(character.CorporationId, null, null));
                var directory = new PersistenceStructureDirectoryReadModel(ReadTable, GetAffiliation);

                var structureTypeRadius = dogma.Type(35_841)?.Attributes.GetValueOrDefault(162) ?? 0;
                simulation.SetPosition(JumpBridgeSource, origin, structureTypeRadius, 0, 0, 0);
                simulation.SetPosition(shipId, origin, 50, 0, 0, 0);
                simulation.DestinationSystemId = destination.System.Id;
                simulation.DestinationStructureId = JumpBridgeDestination;

                return new JumpBridgeFixture(rig, structureSchema, walletSchema, dogma, systems, simulation,
                    directory, structures, wallets, fuelId, fuelQuantity, character.Id, character.CorporationId,
                    shipId, ship.TypeId, origin, destination.System.Id);
            }
            catch
            {
                await using var command = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {structureSchema} CASCADE; DROP SCHEMA IF EXISTS {walletSchema} CASCADE;");
                await command.ExecuteNonQueryAsync();
                await rig.DisposeAsync();
                throw;
            }
        }

        public StructureTravelRpcService CreateProductionService() =>
            CharacterServicesComposition.CreateStructureJumpBridgeTravelService(
                Rig.Source, Structures, Rig.Items, _dogma, Directory,
                Rig.Space, _systems, Wallets, Rig.Foundations.Events, () => Rig.Now);

        public void OpenSession(ClientSession session)
        {
            var index = Interlocked.Increment(ref _nextSession) - 1;
            if (index >= _sessions.Length)
                throw new InvalidOperationException("unexpected additional test front");
            _sessions[index] = session;
            session.CharacterId = _characterId;
            session.Set("charid", new PyLong(_characterId));
            session.Set("corpid", new PyLong(_characterCorporationId));
            session.Set("shipid", new PyLong(_shipId));
            session.Set("solarsystemid", new PyInt(_originSystemId));
            session.Set("solarsystemid2", new PyInt(_originSystemId));
            session.Set("locationid", new PyInt(_originSystemId));
            Rig.Sessions.Enter(_characterId, session);
            Rig.Space.EnterPilotForTest(session, _characterId, _shipId, _shipTypeId, _originSystemId);
        }

        public async Task<long> FuelAfterAsync() => (await Rig.Items.GetAsync(_fuelItemId))?.Quantity ?? 0;

        public async Task SetFuelQuantityAsync(long quantity)
        {
            await using var connection = await Rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Rig.Items.SetQuantityAsync(connection, transaction, _fuelItemId, quantity);
            await transaction.CommitAsync();
        }

        public async Task<decimal> CharacterBalanceAsync() =>
            await Wallets.AccountBalanceAsync(new WalletAccount(_characterId)) ?? 0m;

        public async Task<decimal> CorporationDivisionBalanceAsync() =>
            await Wallets.AccountBalanceAsync(new WalletAccount(_characterCorporationId, 1_001)) ?? 0m;

        public Task<IReadOnlyList<JournalEntry>> CharacterJournalAsync() =>
            Wallets.JournalForAccountAsync(_characterId, 1_000);

        public Task<IReadOnlyList<JournalEntry>> CorporationJournalAsync() =>
            Wallets.JournalForAccountAsync(_characterCorporationId, 1_001);

        public async ValueTask DisposeAsync()
        {
            _announcer.Dispose();
            await Rig.Space.QuiesceAsync();
            foreach (var schema in new[] { _structureSchema, _walletSchema })
            {
                await using var command = Rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
            await Rig.DisposeAsync();
        }
    }

    [PgFact]
    public async Task Registered_jump_bridge_travel_moves_the_ship_and_settles_fuel_fee_and_notifications()
    {
        await using var fixture = await JumpBridgeFixture.CreateAsync();
        Start(configure: registry => registry.Register(fixture.CreateProductionService()),
            sessionOpened: fixture.OpenSession);
        var (front, open) = await OpenFrontAsync();
        var session = fixture.Session(0);
        var mass = await fixture.Rig.Space.ActiveShipMassAsync(session, CancellationToken.None);
        Assert.NotNull(mass);
        var distance = fixture.Systems.LightYearsBetween(fixture.OriginSystemId, fixture.DestinationSystemId);
        Assert.NotNull(distance);
        var ozone = Math.Max(50L, checked((long)Math.Ceiling(mass!.Value * distance!.Value * 0.000003 + 50)));
        var expectedFee = decimal.Round(ozone * 0.025m, 2, MidpointRounding.AwayFromZero);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 1, "structureJumpBridgeMgr", "CmdJumpThroughStructureStargate",
            Py.Tuple(new PyLong(JumpBridgeSource)), TransportKwargs()))));
        var notificationNames = new List<string>();
        PyValue? result = null;
        while (true)
        {
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(send.Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            if (packet.Type == MachoMessage.CallRsp)
            {
                result = Result(decoded);
                break;
            }
            Assert.Equal(MachoMessage.Notification, packet.Type);
            notificationNames.Add(Assert.IsType<string>(packet.Dest.BroadcastId));
        }

        var sessionChange = Assert.IsType<PyTuple>(result);
        Assert.Equal(2, sessionChange.Items.Count);
        Assert.Same(PyNone.Instance, sessionChange.Items[0]);
        Assert.IsType<PyLong>(sessionChange.Items[1]);
        Assert.Equal(["OnAccountChange", "OnJumpQueueUpdate"], notificationNames);
        Assert.Equal(fixture.FuelQuantity - ozone, await fixture.FuelAfterAsync());
        Assert.Equal(100_000m - expectedFee, await fixture.CharacterBalanceAsync());
        Assert.Equal(expectedFee, await fixture.CorporationDivisionBalanceAsync());
        Assert.Contains(await fixture.CharacterJournalAsync(), entry =>
            entry.RefTypeId == 140 && entry.Amount == -expectedFee && entry.ReferenceId == JumpBridgeSource);
        Assert.Contains(await fixture.CorporationJournalAsync(), entry =>
            entry.RefTypeId == 140 && entry.Amount == expectedFee && entry.ReferenceId == JumpBridgeSource);

        var replayResult = Assert.IsType<PyTuple>(await CallJumpSuccessAsync(front, open, 2));
        Assert.Equal(2, replayResult.Items.Count);
        Assert.Equal(sessionChange.Items[0], replayResult.Items[0]);
        Assert.Equal(sessionChange.Items[1], replayResult.Items[1]);
        Assert.Equal(fixture.FuelQuantity - ozone, await fixture.FuelAfterAsync());
        Assert.Equal(100_000m - expectedFee, await fixture.CharacterBalanceAsync());
        Assert.Single(await fixture.CharacterJournalAsync(), entry =>
            entry.RefTypeId == 140 && entry.Amount == -expectedFee && entry.ReferenceId == JumpBridgeSource);
        Assert.Single(await fixture.CorporationJournalAsync(), entry =>
            entry.RefTypeId == 140 && entry.Amount == expectedFee && entry.ReferenceId == JumpBridgeSource);

        fixture.Simulation.Deliver(new SimJumped((ulong)session.SessionId, fixture.OriginSystemId,
            JumpBridgeSource, fixture.DestinationSystemId, JumpBridgeDestination));
        await WaitUntilAsync(async () =>
        {
            var persistedCharacter = await fixture.Rig.Characters.GetAsync(fixture.CharacterId);
            var persistedShip = await fixture.Rig.Items.GetAsync(fixture.ShipId);
            return fixture.Rig.Space.SolarSystemOf(session) == fixture.DestinationSystemId &&
                persistedCharacter?.SolarSystemId == fixture.DestinationSystemId &&
                persistedShip?.LocationId == fixture.DestinationSystemId;
        });
    }

    [PgFact]
    public async Task Concurrent_duplicate_jump_bridge_calls_settle_once_and_return_the_same_session_change()
    {
        await using var fixture = await JumpBridgeFixture.CreateAsync();
        Start(configure: registry => registry.Register(fixture.CreateProductionService()),
            sessionOpened: fixture.OpenSession);
        var (front, open) = await OpenFrontAsync();
        var first = MarshalCodec.Encode(Packets.CallWithKwargs(open, 10,
            "structureJumpBridgeMgr", "CmdJumpThroughStructureStargate",
            Py.Tuple(new PyLong(JumpBridgeSource)), TransportKwargs()));
        var second = MarshalCodec.Encode(Packets.CallWithKwargs(open, 11,
            "structureJumpBridgeMgr", "CmdJumpThroughStructureStargate",
            Py.Tuple(new PyLong(JumpBridgeSource)), TransportKwargs()));
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

        var sessionChanges = responses.Select(value => Assert.IsType<PyTuple>(value)).ToArray();
        Assert.Equal(sessionChanges[0].Items[0], sessionChanges[1].Items[0]);
        Assert.Equal(sessionChanges[0].Items[1], sessionChanges[1].Items[1]);
        Assert.Equal(["OnAccountChange", "OnJumpQueueUpdate"], notifications);
        Assert.Single(fixture.Simulation.Commands, command => command.Command == SimCommands.StructureJump);
        Assert.Equal(100_000m - await ExpectedBridgeFeeAsync(fixture), await fixture.CharacterBalanceAsync());
        Assert.Single(await fixture.CharacterJournalAsync(), entry => entry.RefTypeId == 140 && entry.ReferenceId == JumpBridgeSource);
        Assert.Single(await fixture.CorporationJournalAsync(), entry => entry.RefTypeId == 140 && entry.ReferenceId == JumpBridgeSource);
    }

    [PgFact]
    public async Task Jump_bridge_without_enough_fuel_is_refused_without_charging_or_moving()
    {
        await using var fixture = await JumpBridgeFixture.CreateAsync(fuelQuantity: 1);
        Start(configure: registry => registry.Register(fixture.CreateProductionService()),
            sessionOpened: fixture.OpenSession);
        var (front, open) = await OpenFrontAsync();

        var failure = await CallJumpFailureAsync(front, open, 1);

        Assert.Equal("CustomNotify", UserErrorKey(failure));
        Assert.Equal(1, await fixture.FuelAfterAsync());
        Assert.Equal(100_000m, await fixture.CharacterBalanceAsync());
        Assert.DoesNotContain(fixture.Simulation.Commands, command => command.Command == SimCommands.StructureJump);
    }

    [PgFact]
    public async Task Jump_bridge_with_insufficient_isk_is_refused_before_fuel_or_simulation_changes()
    {
        await using var fixture = await JumpBridgeFixture.CreateAsync(rate: 2_000m, characterBalance: 0m);
        Start(configure: registry => registry.Register(fixture.CreateProductionService()),
            sessionOpened: fixture.OpenSession);
        var (front, open) = await OpenFrontAsync();

        var failure = await CallJumpFailureAsync(front, open, 1);

        Assert.Equal("NotEnoughMoney", UserErrorKey(failure));
        Assert.Equal(fixture.FuelQuantity, await fixture.FuelAfterAsync());
        Assert.Equal(0m, await fixture.CharacterBalanceAsync());
        Assert.DoesNotContain(fixture.Simulation.Commands, command => command.Command == SimCommands.StructureJump);
    }

    [PgFact]
    public async Task Simulation_refusal_keeps_jump_bridge_fuel_and_wallet_unchanged()
    {
        await using var fixture = await JumpBridgeFixture.CreateAsync();
        fixture.Simulation.StructureJumpRefusal = "TOO_FAR_FROM_STRUCTURE";
        Start(configure: registry => registry.Register(fixture.CreateProductionService()),
            sessionOpened: fixture.OpenSession);
        var (front, open) = await OpenFrontAsync();

        var failure = await CallJumpFailureAsync(front, open, 1);

        Assert.Equal("CustomNotify", UserErrorKey(failure));
        Assert.Equal(fixture.FuelQuantity, await fixture.FuelAfterAsync());
        Assert.Equal(100_000m, await fixture.CharacterBalanceAsync());
        Assert.Single(fixture.Simulation.Commands, command => command.Command == SimCommands.StructureJump);
    }

    private async Task<Send> CallJumpFailureAsync(TestFront front, SessionOpen open, uint callId)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, callId, "structureJumpBridgeMgr", "CmdJumpThroughStructureStargate",
            Py.Tuple(new PyLong(JumpBridgeSource)), TransportKwargs()))));
        var failure = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.ErrorResponse,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(failure.Payload))).Type);
        return failure;
    }

    private async Task<PyValue?> CallJumpSuccessAsync(TestFront front, SessionOpen open, uint callId)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, callId, "structureJumpBridgeMgr", "CmdJumpThroughStructureStargate",
            Py.Tuple(new PyLong(JumpBridgeSource)), TransportKwargs()))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(send.Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);
        return Result(decoded);
    }

    private static async Task<decimal> ExpectedBridgeFeeAsync(JumpBridgeFixture fixture)
    {
        var mass = await fixture.Rig.Space.ActiveShipMassAsync(fixture.Session(0), CancellationToken.None);
        Assert.NotNull(mass);
        var distance = fixture.Systems.LightYearsBetween(fixture.OriginSystemId, fixture.DestinationSystemId);
        Assert.NotNull(distance);
        var ozone = Math.Max(50L, checked((long)Math.Ceiling(mass!.Value * distance!.Value * 0.000003 + 50)));
        return decimal.Round(ozone * 0.025m, 2, MidpointRounding.AwayFromZero);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        Assert.True(await condition(), "the persisted jump did not arrive before the deadline");
    }
}
