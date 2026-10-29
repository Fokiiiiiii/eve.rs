using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Sde;
using Eve.Ships;
using Eve.Structures;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class JumpCloneSettlementHostWireTests : IAsyncLifetime
{
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA",
        "EVE_WALLET_SCHEMA", "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA",
        "EVE_CORPORATIONS_SCHEMA", "EVE_MARKET_SCHEMA", "EVE_TRADE_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA",
        "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA", "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA",
    ];

    private readonly string prefix = "jcw" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> saved = [];
    private readonly List<string> schemas = [];
    private HostRuntime? host;

    private string Schema(string variable) => $"{prefix}_{variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant()}";

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = Schema(variable);
            schemas.Add(schema);
            Environment.SetEnvironmentVariable(variable, schema);
        }
        foreach (var (name, value) in new[] { ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip"), ("EVE_WORMHOLES", "off") })
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (host is not null)
        {
            await host.ShutdownAsync();
            host = null;
        }
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is { } url)
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            foreach (var schema in schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (name, value) in saved)
            Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static ServiceCall Call(ClientSession session, string method, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args };

    private async Task<long> CharacterAsync(NpgsqlDataSource source, Station station, long corporationId, decimal balance)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
        var character = await characters.InsertAsync(connection, transaction,
            new NewCharacter(1, $"Clone Fee {corporationId} {Guid.NewGuid():N}", 0, 1, 1, 1, 11, 1376, corporationId, null,
                station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
        await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(connection, transaction, character, balance, 0);
        var capsule = await new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA")).CreateAsync(connection, transaction,
            new NewItem(CharacterShips.CapsuleTypeId, character, character, ItemFlags.Hangar, 1, Singleton: true));
        await characters.SetShipAsync(connection, transaction, character, capsule);
        await using (var membership = new NpgsqlCommand($"""
            INSERT INTO {Schema("EVE_RELATIONS_SCHEMA")}.relations
                (kind, subject, object, value, attrs, since_ms, until_ms, updated_ms)
            VALUES ($1, $2, $3, 1, jsonb_build_object(), $4, NULL, $4)
            """, connection, transaction))
        {
            membership.Parameters.AddWithValue(Eve.Relations.RelationKinds.CorporationMember.Name);
            membership.Parameters.AddWithValue(character);
            membership.Parameters.AddWithValue(corporationId);
            membership.Parameters.AddWithValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await membership.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return character;
    }

    private async Task<long> CloneAtAsync(NpgsqlDataSource source, long characterId, long locationId)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var cloneId = await new PgJumpCloneStore(source, Schema("EVE_CHARACTERS_SCHEMA"))
            .InsertAsync(connection, transaction, characterId, locationId, "station");
        await transaction.CommitAsync();
        return cloneId;
    }

    private async Task<(int Reference, long Counterparty, long? Location)> LastJournalAsync(NpgsqlDataSource source, long ownerId)
    {
        await using var command = source.CreateCommand(
            $"SELECT ref_type_id, counterparty_id, reference_id FROM {Schema("EVE_WALLET_SCHEMA")}.journal WHERE owner_id = $1 AND account_key = 1000 ORDER BY id DESC LIMIT 1");
        command.Parameters.AddWithValue(ownerId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    [PgFact]
    public async Task Registered_clone_jump_quotes_and_settles_the_origin_fee_and_refuses_without_funds()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var service = host.Registry.Lookup("jumpCloneSvc") ?? throw new InvalidOperationException("jumpCloneSvc is not registered");
        var stations = await StationCatalog.LoadAsync(new SdeReader(source));
        var station = stations.All.First();
        var targetStation = stations.All.First(candidate => candidate.Id != station.Id);
        const long ownerCorporationId = 99_000_000_001;
        const long structureId = 9_000_000_000_000_101;
        var character = await CharacterAsync(source, station, ownerCorporationId, 5_000_000m);
        var cloneId = await CloneAtAsync(source, character, targetStation.Id);
        var profilesService = host.Registry.Lookup("structureProfiles") ?? throw new InvalidOperationException("structureProfiles is not registered");
        var profileSession = new ClientSession(new Sink()) { CharacterId = character, SessionId = 904 };
        profileSession.Set("corpid", new PyLong(ownerCorporationId));
        profileSession.Set("corprole", new PyLong(2048));
        var profiles = Assert.IsType<PyList>(await profilesService.CallAsync(Call(profileSession, "GetProfiles")));
        var defaultProfile = Assert.IsType<PyObjectEx>(Assert.Single(profiles.Items));
        var profileId = Assert.IsType<PyInt>(defaultProfile.Dict.Single(entry => Assert.IsType<PyStr>(entry.Key).Value == "profileID").Value).Value;

        var structures = new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA"));
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await structures.CreateAsync(connection, transaction,
                new NewStructure(structureId, 35832, station.SolarSystemId, 1, 2, 3, 0, ownerCorporationId, 0, "Clone Fee Structure", "", profileId)));
            await structures.SaveAsync(connection, transaction, new StructureSnapshot
            {
                StructureId = structureId,
                State = StructureState.ShieldVulnerable,
                Upkeep = UpkeepState.FullPower,
                CoreInstalled = true,
            });
            await structures.SetServiceAsync(connection, transaction, structureId, StructureServiceIds.Docking, ServiceState.Online, 1);
            await structures.SetServiceAsync(connection, transaction, structureId, StructureServiceIds.JumpClone, ServiceState.Online, 1);
            await transaction.CommitAsync();
        }
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(connection, transaction, ownerCorporationId, 0, 0);
            await transaction.CommitAsync();
        }

        var sink = new Sink();
        var session = new ClientSession(sink) { CharacterId = character, SessionId = 901 };
        session.Set("charid", new PyLong(character));
        session.Set("stationid", new PyLong(station.Id));
        session.Set("solarsystemid2", new PyInt(station.SolarSystemId));

        async Task<PyValue> BoundCall(ClientSession current, long locationId, int solarSystemId, string method, params PyValue[] args)
        {
            var inner = Py.Tuple(new PyStr(method), new PyTuple(args), new PyDict([]));
            var result = await service.CallAsync(Call(current, "MachoBindObject",
                Py.Tuple(new PyLong(locationId), new PyInt(solarSystemId), new PyInt(6)), inner));
            return Assert.IsType<PyTuple>(result).Items[1];
        }

        var stationQuote = Assert.IsType<PyReal>(await BoundCall(session, station.Id, station.SolarSystemId, "GetPriceForClone"));
        Assert.Equal(900_000, stationQuote.Value);
        var stationJump = await BoundCall(session, station.Id, station.SolarSystemId, "CloneJump",
            new PyLong(targetStation.Id), new PyLong(cloneId), stationQuote, PyBool.False);
        Assert.Same(PyNone.Instance, stationJump);
        var wallet = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
        Assert.Equal(4_100_000m, (await wallet.GetAsync(character))!.Isk);
        Assert.Equal(-900_000m, await wallet.LastIskChangeAsync(character));
        Assert.Equal((128, 0L, (long?)null), await LastJournalAsync(source, character));
        var stationJumpClones = await new PgJumpCloneStore(source, Schema("EVE_CHARACTERS_SCHEMA")).ReadAsync(character);
        Assert.Contains(stationJumpClones.Clones, clone => clone.LocationId == station.Id && clone.LocationKind == "station");

        var structureCharacter = await CharacterAsync(source, station, ownerCorporationId, 5_000_000m);
        var structureCloneId = await CloneAtAsync(source, structureCharacter, targetStation.Id);
        var structureWallet = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
        var structureSession = new ClientSession(new Sink()) { CharacterId = structureCharacter, SessionId = 902 };
        structureSession.Set("charid", new PyLong(structureCharacter));
        structureSession.Set("structureid", new PyLong(structureId));
        structureSession.Set("solarsystemid2", new PyInt(station.SolarSystemId));
        var structureQuote = Assert.IsType<PyReal>(await BoundCall(structureSession, structureId, station.SolarSystemId, "GetPriceForClone"));
        Assert.Equal(stationQuote, structureQuote);
        Assert.Same(PyNone.Instance, await BoundCall(structureSession, structureId, station.SolarSystemId, "CloneJump",
            new PyLong(targetStation.Id), new PyLong(structureCloneId), structureQuote, PyBool.False));
        Assert.Equal(4_100_000m, (await structureWallet.GetAsync(structureCharacter))!.Isk);
        Assert.Equal(900_000m, (await structureWallet.GetAsync(ownerCorporationId))!.Isk);
        Assert.Equal(-900_000m, await structureWallet.LastIskChangeAsync(structureCharacter));
        Assert.Equal(900_000m, await structureWallet.LastIskChangeAsync(ownerCorporationId));
        Assert.Equal((128, ownerCorporationId, (long?)targetStation.Id), await LastJournalAsync(source, structureCharacter));
        Assert.Equal((128, structureCharacter, (long?)targetStation.Id), await LastJournalAsync(source, ownerCorporationId));
        var structureJumpClones = await new PgJumpCloneStore(source, Schema("EVE_CHARACTERS_SCHEMA")).ReadAsync(structureCharacter);
        Assert.Contains(structureJumpClones.Clones, clone => clone.LocationId == structureId && clone.LocationKind == "structure");

        var poorCharacter = await CharacterAsync(source, station, ownerCorporationId + 2, 0m);
        var poorCloneId = await CloneAtAsync(source, poorCharacter, targetStation.Id);
        var poorSession = new ClientSession(new Sink()) { CharacterId = poorCharacter, SessionId = 903 };
        poorSession.Set("charid", new PyLong(poorCharacter));
        poorSession.Set("stationid", new PyLong(station.Id));
        poorSession.Set("solarsystemid2", new PyInt(station.SolarSystemId));
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            BoundCall(poorSession, station.Id, station.SolarSystemId, "CloneJump",
                new PyLong(targetStation.Id), new PyLong(poorCloneId), stationQuote, PyBool.False));
        Assert.Equal("NotEnoughMoney", error.Error.UserErrorMessage);
        Assert.Equal(0m, (await wallet.GetAsync(poorCharacter))!.Isk);
        Assert.Single((await new PgJumpCloneStore(source, Schema("EVE_CHARACTERS_SCHEMA")).ReadAsync(poorCharacter)).Clones);
    }
}
