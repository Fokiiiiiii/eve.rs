using Eve.Characters;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Universe;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class HomeStationHostTests
{
    [PgFact]
    public async Task Host_composition_persists_local_and_remote_home_station_changes()
    {
        await using var rig = await HomeStationPgRig.CreateAsync();
        await rig.SeedOfficeCandidatesAsync();
        var character = await rig.CreateCharacterAsync();
        var session = new ClientSession { CharacterId = character.Id, Sink = new RecordingPacketSink() };
        session.Set("stationid", new PyInt(rig.LocalOfficeStation.Id));
        session.Set("homeStationID", new PyInt(rig.InitialHomeStation.Id));
        session.Set("cloneStationID", new PyInt(rig.InitialHomeStation.Id));

        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("home_station"));
        Assert.True(service.HasMethod("get_home_station"));
        Assert.True(service.HasMethod("set_home_station"));
        Assert.True(service.HasMethod("get_next_remote_change_time"));
        var candidates = Assert.IsType<PyList>(await CallAsync(service, "get_home_station_candidates", session));
        Assert.Single(candidates.Items, candidate => HomeStationId(candidate).Value == rig.LocalOfficeStation.Id);
        Assert.Single(candidates.Items, candidate => HomeStationId(candidate).Value == rig.RemoteOfficeStation.Id);
        Assert.Equal(new PyInt(rig.InitialHomeStation.Id), HomeStationId(await CallAsync(service, "get_home_station", session)));
        Assert.IsType<PyNone>(await CallAsync(service, "get_next_remote_change_time", session));

        Assert.IsType<PyNone>(await CallAsync(
            service, "set_home_station", session, new PyInt(rig.LocalOfficeStation.Id), PyBool.False));

        var afterLocal = Assert.IsType<Character>(await rig.Characters.GetAsync(character.Id));
        Assert.Equal(rig.LocalOfficeStation.Id, afterLocal.HomeStationId);
        Assert.Equal(0L, afterLocal.NextRemoteHomeStationChangeFileTime);
        Assert.Equal(new PyInt(rig.LocalOfficeStation.Id), session.Get("homeStationID"));
        Assert.Equal(new PyInt(rig.LocalOfficeStation.Id), session.Get("cloneStationID"));
        Assert.Equal(new PyInt(rig.LocalOfficeStation.Id), HomeStationId(await CallAsync(service, "get_home_station", session)));
        Assert.IsType<PyNone>(await CallAsync(service, "get_next_remote_change_time", session));
        AssertNotification(Assert.Single(Notifications((RecordingPacketSink)session.Sink!)), rig.LocalOfficeStation.Id);

        Assert.IsType<PyNone>(await CallAsync(
            service, "set_home_station", session, new PyInt(rig.RemoteOfficeStation.Id), PyBool.True));

        var afterRemote = Assert.IsType<Character>(await rig.Characters.GetAsync(character.Id));
        Assert.Equal(rig.RemoteOfficeStation.Id, afterRemote.HomeStationId);
        Assert.True(afterRemote.NextRemoteHomeStationChangeFileTime > DateTime.UtcNow.ToFileTimeUtc());
        Assert.Equal(new PyInt(rig.RemoteOfficeStation.Id), session.Get("homeStationID"));
        Assert.Equal(new PyInt(rig.RemoteOfficeStation.Id), session.Get("cloneStationID"));
        Assert.Equal(new PyInt(rig.RemoteOfficeStation.Id), HomeStationId(await CallAsync(service, "get_home_station", session)));
        Assert.IsType<PyCPickled>(await CallAsync(service, "get_next_remote_change_time", session));

        var sink = (RecordingPacketSink)session.Sink!;
        Assert.Collection(
            Notifications(sink),
            notification => AssertNotification(notification, rig.LocalOfficeStation.Id),
            notification => AssertNotification(notification, rig.RemoteOfficeStation.Id));

        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
        {
            await CallAsync(service, "set_home_station", session, new PyLong(9_999_999_999L), PyBool.False);
        });

        var afterInvalid = Assert.IsType<Character>(await rig.Characters.GetAsync(character.Id));
        Assert.Equal(afterRemote.HomeStationId, afterInvalid.HomeStationId);
        Assert.Equal(afterRemote.NextRemoteHomeStationChangeFileTime, afterInvalid.NextRemoteHomeStationChangeFileTime);
        Assert.Equal(new PyInt(rig.RemoteOfficeStation.Id), session.Get("homeStationID"));
        Assert.Equal(new PyInt(rig.RemoteOfficeStation.Id), session.Get("cloneStationID"));
        Assert.Equal(2, Notifications(sink).Count);

        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
        {
            await CallAsync(service, "reset_remote_change_time", session);
        });
        Assert.Equal(afterRemote.NextRemoteHomeStationChangeFileTime,
            (await rig.Characters.GetAsync(character.Id))!.NextRemoteHomeStationChangeFileTime);
        Assert.IsType<PyCPickled>(await CallAsync(service, "get_next_remote_change_time", session));
        Assert.Equal(2, Notifications(sink).Count);

        session.Set("accountRole", new PyLong(Eve.Services.GameMasterRoles.Qa));
        Assert.IsType<PyNone>(await CallAsync(service, "reset_remote_change_time", session));
        Assert.Equal(0, (await rig.Characters.GetAsync(character.Id))!.NextRemoteHomeStationChangeFileTime);
        Assert.IsType<PyNone>(await CallAsync(service, "get_next_remote_change_time", session));
        Assert.Equal(2, Notifications(sink).Count);

        var structureId = await rig.SeedStructureAsync();
        session.Set("stationid", PyNone.Instance);
        session.Set("structureid", new PyInt(structureId));
        session.Set("shipid", new PyInt(structureId + 1));
        candidates = Assert.IsType<PyList>(await CallAsync(service, "get_home_station_candidates", session));
        Assert.Single(candidates.Items, candidate => HomeStationId(candidate).Value == structureId);
        Assert.IsType<PyNone>(await CallAsync(service, "set_home_station", session, new PyLong(structureId), PyBool.False));
        Assert.Equal(structureId, (await rig.Characters.GetAsync(character.Id))!.HomeStationId);
        Assert.Equal(new PyInt(structureId), HomeStationId(await CallAsync(service, "get_home_station", session)));
        AssertNotification(Notifications(sink)[2], structureId);

        var docking = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("structureDocking"));
        foreach (var method in new[] { "Dock", "Undock" })
        {
            var unavailable = await Assert.ThrowsAsync<MachoWrappedException>(() =>
                CallAsync(docking, method, session, new PyLong(structureId), PyNone.Instance));
            Assert.Equal("CustomInfo", unavailable.Error.UserErrorMessage);
        }
        Assert.Equal(structureId, session.GetInt("structureid"));
        Assert.Equal(3, Notifications(sink).Count);
    }

    [PgFact]
    public async Task Registered_skin_activation_sends_two_scalar_args_only_after_commit_and_none_on_failure()
    {
        // Reuse the isolated Host composition, packet capture and wire decoder below.
        await using var rig = await HomeStationPgRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var otherCharacter = await rig.CreateCharacterAsync();
        var sink = new RecordingPacketSink();
        var otherSink = new RecordingPacketSink();
        var session = new ClientSession(sink) { CharacterId = character.Id, SessionId = character.Id };
        var otherSession = new ClientSession(otherSink) { CharacterId = otherCharacter.Id, SessionId = otherCharacter.Id };
        var sessions = Assert.IsType<CharacterSessions>(rig.Runtime.CharacterSessions);
        sessions.Enter(character.Id, session);
        sessions.Enter(otherCharacter.Id, otherSession);
        var service = Assert.IsType<ShipCosmeticsMgrRpcService>(rig.Registry.Lookup("shipCosmeticsMgr"));
        var sde = new Eve.Sde.SdeReader(rig.Source);
        var catalog = await ShipCosmeticsCatalog.LoadAsync(sde);
        var license = catalog.LicenseTypes.Values.First(entry => catalog.Skins.ContainsKey(entry.SkinId));
        var items = new Eve.Inventory.PgItemStore(rig.Source, rig.InventorySchema);
        long itemId, foreignItemId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            itemId = await items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(license.LicenseTypeId, character.Id, rig.LocalOfficeStation.Id, 4, 2));
            foreignItemId = await items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(license.LicenseTypeId, otherCharacter.Id, rig.LocalOfficeStation.Id, 4, 2));
            await transaction.CommitAsync();
        }

        Assert.IsType<PyNone>(await CallAsync(service, "ActivateSkinLicense", session, PyBool.False));
        foreach (var invalidId in new[] { long.MaxValue, foreignItemId })
            await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(service,
                "ActivateSkinLicense", session, new PyList([new PyLong(invalidId)])));
        Assert.Empty(Notifications(sink));
        Assert.Empty(Notifications(otherSink));

        foreach (var cancel in new[] { true, false })
        {
            // Hold the real item row, so activation cannot commit or consume it yet.
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            Assert.NotNull(await items.LockAndGetAsync(connection, transaction, itemId));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var activation = service.CallAsync(new ServiceCall
            {
                Method = "ActivateSkinLicense", Session = session,
                Args = [new PyList([new PyLong(itemId)])], Cancellation = cancellation.Token,
            }).AsTask();
            try
            {
                var waiting = false;
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!waiting && !activation.IsCompleted && DateTime.UtcNow < deadline)
                {
                    await using var command = rig.Source.CreateCommand(
                        "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE wait_event_type='Lock' " +
                        "AND query LIKE $1 AND pid <> pg_backend_pid())");
                    command.Parameters.AddWithValue($"%FROM {rig.InventorySchema}.items%FOR UPDATE%");
                    waiting = (bool)(await command.ExecuteScalarAsync())!;
                    if (!waiting) await Task.Delay(10);
                }
                Assert.True(waiting, "Activation must reach the blocked item lock before notification assertions.");
                Assert.False(activation.IsCompleted);
                Assert.Empty(Notifications(sink));
                Assert.Empty(Notifications(otherSink));
                Assert.Equal(2L, (await items.GetAsync(itemId))!.Quantity);

                if (cancel)
                {
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await activation);
                    Assert.Empty(Notifications(sink));
                    Assert.Empty(Notifications(otherSink));
                    Assert.Equal(2L, (await items.GetAsync(itemId))!.Quantity);
                }
                else
                {
                    await transaction.CommitAsync();
                    Assert.IsType<PyNone>(await activation);
                }
            }
            finally
            {
                cancellation.Cancel();
                // Drain a failed assertion's pending call before disposing the Host fixture.
                try { await activation; } catch (OperationCanceledException) { }
            }
        }

        Assert.Equal(1L, (await items.GetAsync(itemId))!.Quantity);
        var foreignItem = await items.GetAsync(foreignItemId);
        Assert.Equal(otherCharacter.Id, foreignItem!.OwnerId);
        Assert.Equal(2L, foreignItem.Quantity);
        Assert.Collection(Notifications(sink),
            notification => Assert.Equal("OnItemChange", notification.Name),
            notification =>
            {
                Assert.Equal("OnSkinLicenseActivated", notification.Name);
                Assert.Collection(notification.Args,
                    value => Assert.Equal(new PyInt(license.SkinId), Assert.IsType<PyInt>(value)),
                    value => Assert.Equal(new PyInt(character.Id), Assert.IsType<PyInt>(value)));
            });
        Assert.Empty(Notifications(otherSink));
        // Retrying the already active license must not consume the remaining item or emit a packet.
        await Assert.ThrowsAsync<MachoWrappedException>(() => CallAsync(service,
            "ActivateSkinLicense", session, new PyList([new PyLong(itemId)])));
        Assert.Equal(1L, (await items.GetAsync(itemId))!.Quantity);
        Assert.Equal(2, Notifications(sink).Count);
        Assert.Empty(Notifications(otherSink));
    }

    private static async Task<PyValue> CallAsync(IService service, string method, ClientSession session, params PyValue[] args) =>
        await service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    private static PyInt HomeStationId(PyValue response)
    {
        var value = Assert.IsType<PyObjectEx>(response);
        var header = Assert.IsType<PyTuple>(value.Header);
        var fields = Assert.IsType<PyDict>(header.Items[1]).Entries
            .ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);
        return Assert.IsType<PyInt>(fields["id"]);
    }

    private static List<(string Name, PyValue[] Args)> Notifications(RecordingPacketSink sink)
    {
        var events = new List<(string Name, PyValue[] Args)>();
        foreach (var packet in sink.Sent.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification })
                continue;
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
            var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
            var args = ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items;
            events.Add((((PyStr)address[1]).Value, [.. args]));
        }
        return events;
    }

    private static void AssertNotification((string Name, PyValue[] Args) notification, long stationId)
    {
        Assert.Equal("OnHomeStationChanged", notification.Name);
        Assert.Equal(InventoryWire.Id(stationId), Assert.Single(notification.Args));
    }

    private sealed class RecordingPacketSink : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];

        public void Send(PyValue packet) => Sent.Add(packet);
    }

    private sealed class HomeStationPgRig : IAsyncDisposable
    {
        private static readonly string[] SchemaVariables =
        [
            "EVE_IDENTITY_SCHEMA",
            "EVE_GLOBAL_CONFIG_SCHEMA",
            "EVE_CHARACTERS_SCHEMA",
            "EVE_INVENTORY_SCHEMA",
            "EVE_SKILLS_SCHEMA",
            "EVE_WALLET_SCHEMA",
            "EVE_FITTING_SCHEMA",
            "EVE_INDUSTRY_SCHEMA",
            "EVE_NPC_SCHEMA",
            "EVE_SPACE_SCHEMA",
            "EVE_ASSET_SAFETY_SCHEMA",
            "EVE_CRIMEWATCH_SCHEMA",
            "EVE_CORPORATIONS_SCHEMA",
            "EVE_MARKET_SCHEMA",
            "EVE_FLEET_SCHEMA",
            "EVE_GAMESTORE_SCHEMA",
            "EVE_SCHEDULER_SCHEMA",
            "EVE_EVENTS_SCHEMA",
            "EVE_RELATIONS_SCHEMA",
            "EVE_CONTENT_SCHEMA",
            "EVE_STRUCTURES_SCHEMA",
            "EVE_AGENTS_SCHEMA",
        ];

        private const long CorporationId = 900_001_001;
        private readonly NpgsqlDataSource _source;
        private readonly string[] _schemasToDrop;
        private readonly Func<Task> _shutdown;

        private HomeStationPgRig(
            NpgsqlDataSource source,
            IReadOnlyDictionary<string, string> schemas,
            HostRuntime runtime,
            Func<Task> shutdown,
            Station initialHomeStation,
            Station localOfficeStation,
            Station remoteOfficeStation)
        {
            _source = source;
            _schemasToDrop = schemas.Values.ToArray();
            _shutdown = shutdown;
            Runtime = runtime;
            Characters = new PgCharacterStore(source, schemas["EVE_CHARACTERS_SCHEMA"]);
            InventorySchema = schemas["EVE_INVENTORY_SCHEMA"];
            CorporationSchema = schemas["EVE_CORPORATIONS_SCHEMA"];
            StructureSchema = schemas["EVE_STRUCTURES_SCHEMA"];
            InitialHomeStation = initialHomeStation;
            LocalOfficeStation = localOfficeStation;
            RemoteOfficeStation = remoteOfficeStation;
        }

        public HostRuntime Runtime { get; }
        public ServiceRegistry Registry => Runtime.Registry;
        public NpgsqlDataSource Source => _source;
        public string InventorySchema { get; }
        public PgCharacterStore Characters { get; }
        public string CorporationSchema { get; }
        public string StructureSchema { get; }
        public Station InitialHomeStation { get; }
        public Station LocalOfficeStation { get; }
        public Station RemoteOfficeStation { get; }

        public static async Task<HomeStationPgRig> CreateAsync()
        {
            var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL")
                ?? throw new InvalidOperationException("EVE_PG_URL is not set.");
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var schemas = SchemaVariables.ToDictionary(
                variable => variable,
                variable => $"hs_{suffix}_{variable[4..^7].ToLowerInvariant()}");
            var previous = SchemaVariables.Concat(["EVE_MARKET_SEED", "EVE_SIM", "EVE_WORMHOLES"])
                .ToDictionary(variable => variable, variable => Environment.GetEnvironmentVariable(variable));
            var connectionBuilder = new NpgsqlConnectionStringBuilder(Eve.Data.PgConnection.ForTest(pgUrl));
            var source = new NpgsqlDataSourceBuilder(connectionBuilder.ConnectionString).Build();
            Func<Task>? shutdown = null;

            try
            {
                foreach (var (variable, schema) in schemas)
                    Environment.SetEnvironmentVariable(variable, schema);

                Environment.SetEnvironmentVariable("EVE_MARKET_SEED", "skip");
                Environment.SetEnvironmentVariable("EVE_SIM", "off");
                Environment.SetEnvironmentVariable("EVE_WORMHOLES", "off");
                // shortcut: CalendarStore has no schema override, so EVE_PG_URL must target an isolated test database.
                var host = await HostComposition.BuildAsync(HostConfiguration.Read());
                shutdown = host.ShutdownAsync;
                var stations = await ReadStationsAsync(new Eve.Sde.SdeReader(source));
                return new HomeStationPgRig(
                    source, schemas, host, shutdown,
                    stations.InitialHome, stations.LocalOffice, stations.RemoteOffice);
            }
            catch
            {
                try
                {
                    if (shutdown is not null)
                        await shutdown();
                }
                finally
                {
                    try
                    {
                        await DropSchemasAsync(source, schemas.Values);
                    }
                    finally
                    {
                        await source.DisposeAsync();
                    }
                }
                throw;
            }
            finally
            {
                foreach (var (variable, value) in previous)
                    Environment.SetEnvironmentVariable(variable, value);
            }
        }

        public async Task SeedOfficeCandidatesAsync()
        {
            await using var connection = await _source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var (folderId, station) in new[]
            {
                (900_001_101L, LocalOfficeStation),
                (900_001_102L, RemoteOfficeStation),
            })
            {
                await using var command = new NpgsqlCommand(
                    $"INSERT INTO {CorporationSchema}.office_rentals " +
                    "(office_folder_id, corporation_id, station_id, solar_system_id, rent_isk, rented_at_ms, expires_at_ms, active) " +
                    "VALUES ($1, $2, $3, $4, $5, $6, $7, true)",
                    connection,
                    transaction)
                {
                    Parameters =
                    {
                        new() { Value = folderId },
                        new() { Value = CorporationId },
                        new() { Value = station.Id },
                        new() { Value = station.SolarSystemId },
                        new() { Value = 10_000m },
                        new() { Value = nowMs },
                        new() { Value = nowMs + 90L * 24 * 60 * 60 * 1000 },
                    },
                };
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        public async Task<long> SeedStructureAsync()
        {
            const long id = 1_000_000_000_051;
            var row = new System.Text.Json.Nodes.JsonObject
            {
                ["structureID"] = id,
                ["typeID"] = 35832,
                ["solarSystemID"] = LocalOfficeStation.SolarSystemId,
                ["ownerCorpID"] = CorporationId,
                ["itemName"] = "Home Station Structure",
                ["state"] = (int)Eve.Structures.StructureState.ShieldVulnerable,
                ["upkeepState"] = (int)Eve.Structures.UpkeepState.FullPower,
                ["hasQuantumCore"] = true,
            };
            await using var connection = await _source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var structures = new Eve.Structures.StructureStore(_source, StructureSchema);
            var imported = await structures.ImportAsync(connection, transaction,
                new() { [id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = row },
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.Equal(id, Assert.Single(imported));
            await transaction.CommitAsync();
            return id;
        }

        public async Task<Character> CreateCharacterAsync()
        {
            var character = new NewCharacter(
                AccountId: 1,
                Name: $"HomePilot{Guid.NewGuid():N}"[..18],
                Gender: 0,
                RaceId: 1,
                BloodlineId: 1,
                AncestryId: 1,
                SchoolId: 0,
                TypeId: 1373,
                CorporationId: CorporationId,
                FactionId: null,
                StationId: LocalOfficeStation.Id,
                SolarSystemId: LocalOfficeStation.SolarSystemId,
                ConstellationId: LocalOfficeStation.ConstellationId,
                RegionId: LocalOfficeStation.RegionId,
                DollInfo: null,
                PortraitInfo: null);

            long characterId;
            await using (var connection = await _source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                characterId = await Characters.InsertAsync(connection, transaction, character);
                await transaction.CommitAsync();
            }

            var movedToPreviousHome = await Characters.TryChangeHomeStationAsync(
                characterId, InitialHomeStation.Id, false, DateTime.UtcNow.ToFileTimeUtc());
            if (movedToPreviousHome != HomeStationChangeResult.Changed)
                throw new InvalidOperationException($"Could not seed the initial home station: {movedToPreviousHome}.");

            return await Characters.GetAsync(characterId)
                ?? throw new InvalidOperationException($"Character {characterId} was not persisted.");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _shutdown();
            }
            finally
            {
                try
                {
                    await DropSchemasAsync(_source, _schemasToDrop);
                }
                finally
                {
                    await _source.DisposeAsync();
                }
            }
        }

        private static async Task<(Station InitialHome, Station LocalOffice, Station RemoteOffice)> ReadStationsAsync(
            Eve.Sde.SdeReader sde)
        {
            var stations = await StationCatalog.LoadAsync(sde);
            var regions = (await sde.ReadAsync("mapRegions")).ToDictionary(
                row => checked((int)row.Key),
                row => (
                    Name: Eve.Sde.SdeReader.English(row.Doc.GetProperty("name")),
                    Wormhole: row.Doc.TryGetProperty("wormholeClassID", out var classId)
                        && classId.ValueKind == System.Text.Json.JsonValueKind.Number
                        && classId.GetInt32() > 0));
            var systems = (await sde.ReadAsync("mapSolarSystems")).ToDictionary(
                row => checked((int)row.Key),
                row => row.Doc.GetProperty("regionID").GetInt32());
            var eligible = stations.All.Where(station =>
            {
                if (!systems.TryGetValue(station.SolarSystemId, out var system)
                    || !regions.TryGetValue(system, out var region))
                    return false;
                return !region.Wormhole && !region.Name.Equals("Pochven", StringComparison.OrdinalIgnoreCase);
            }).OrderBy(station => station.Id).ToArray();
            var localSystem = eligible.GroupBy(station => station.SolarSystemId)
                .Where(group => group.Count() >= 2)
                .OrderBy(group => group.Key)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("The SDE has no non-special solar system with two stations.");
            var localStations = localSystem.OrderBy(station => station.Id).ToArray();
            var remoteStation = eligible.FirstOrDefault(station => station.SolarSystemId != localSystem.Key)
                ?? throw new InvalidOperationException("The SDE has no station outside the selected solar system.");

            return (localStations[1], localStations[0], remoteStation);
        }

        private static async Task DropSchemasAsync(NpgsqlDataSource source, IEnumerable<string> schemas)
        {
            foreach (var schema in schemas)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}