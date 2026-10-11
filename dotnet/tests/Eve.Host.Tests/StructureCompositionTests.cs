using System.Text.Json.Nodes;
using Eve.Data;
using Eve.GameStore;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Characters;
using Eve.Services;
using Eve.Sde;
using Eve.Structures;
using Eve.Universe;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>
/// The structures schema through the real host composition (HostComposition.BuildAsync, the path eve-host runs), on scratch PostgreSQL schemas. The game
/// store's table is seeded before the host starts, so the start-up copy (ST-0) is what the host reads; the calls go through the registry the dispatcher uses.
/// </summary>
[Collection("host-composition")]
public sealed class StructureCompositionTests : IAsyncLifetime
{
    private const long Corporation = 98_000_001;
    private const long StructureId = 1_001;
    private const long Jita = 30_000_142;
    private const int Astrahus = 35_832;

    /// <summary>The environment the host reads: every schema on its own scratch name, plus the no-simulation switch.</summary>
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA",
    ];

    private readonly string _prefix = "sc" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    private string Schema(string variable) => variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant() is var name ? $"{_prefix}_{name}" : "";

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            _saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = Schema(variable);
            _schemas.Add(schema);
            Environment.SetEnvironmentVariable(variable, schema);
        }
        // no simulation, and no market seed from ESI: the host would fetch the live market when it starts (as MarketHostTests skips it)
        foreach (var (name, value) in new[] { ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip") })
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL");
        if (url is not null)
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
            foreach (var schema in _schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static ServiceCall Call(ClientSession session, string method, long id, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(id) };

    /// <summary>A station manager of the corporation, the role the description and the profile calls ask for (2048).</summary>
    private static ClientSession ManagerSession(long characterId)
    {
        var session = new ClientSession(new Sink()) { SessionId = 401 };
        session.Set("charid", new PyInt(characterId));
        session.Set("corpid", new PyInt(Corporation));
        session.Set("corprole", new PyInt(2048));
        return session;
    }

    /// <summary>A structure in the game store's legacy shape: a working Astrahus in Jita, shield stage, full power.</summary>
    private static JsonObject SeededRow(long structureId, int state, long stateStartedMs, long stateEndsMs, int upkeep) => new()
    {
        ["structureID"] = structureId,
        ["typeID"] = Astrahus,
        ["solarSystemID"] = Jita,
        ["ownerCorpID"] = Corporation,
        ["itemName"] = "Seeded " + structureId,
        ["position"] = new JsonObject { ["x"] = 1.0, ["y"] = 2.0, ["z"] = 3.0 },
        ["state"] = state,
        ["upkeepState"] = upkeep,
        ["stateStartedAt"] = stateStartedMs,
        ["stateEndsAt"] = stateEndsMs,
        ["hasQuantumCore"] = true,
    };

    /// <summary>Writes the game store's structures and structure profile tables (the legacy tables the host reads), then lets the writer go so the host can own them.</summary>
    private async Task SeedGameStoreAsync(NpgsqlDataSource source, params JsonObject[] rows)
    {
        await using var store = new PersistenceStore(source, Schema("EVE_GAMESTORE_SCHEMA"));
        await store.EnsureSchemaAsync();
        await using var owner = await OwnerSession.StartAsync(store, PersistenceRoles.World, "structure-composition-" + _prefix);
        await using var cache = new GameStoreCache(store, owner,
            new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });
        await cache.RecoverAsync();
        await cache.LoadAsync("structures");
        foreach (var row in rows)
        {
            Assert.True(cache.Write("structures", "/" + row["structureID"]!.GetValue<long>(), row).Success);
        }
        await cache.FlushTableAsync("structures");
        // the corporation already has its default profile (7): the change reuses it and writes no profile root (a root write to a table the cache did not load is refused, TABLE_NOT_FOUND, see TODO)
        await cache.LoadAsync("structureProfiles");
        Assert.True(cache.Write("structureProfiles", "/", new JsonObject
        {
            ["meta"] = new JsonObject { ["version"] = 1 },
            ["nextProfileID"] = 8,
            ["profilesByID"] = new JsonObject
            {
                ["7"] = new JsonObject { ["profileID"] = 7, ["corporationID"] = Corporation, ["name"] = "Default Profile", ["description"] = "", ["isDefault"] = true },
            },
        }).Success);
        await cache.FlushTableAsync("structureProfiles");
    }

    /// <summary>A character of the corporation, in the characters schema: the host reconciles corporation membership from it at start-up.</summary>
    private async Task<long> SeedManagerAsync()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        await PgCharacterStore.MigrateAsync(source, Schema("EVE_CHARACTERS_SCHEMA"));
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var characterId = await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).InsertAsync(connection, transaction,
            new NewCharacter(1, "Structure Manager", 0, 1, 1, 1, 11, 1376, Corporation, null, station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
        await transaction.CommitAsync();
        return characterId;
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return true;
            await Task.Delay(200);
        }
        return await condition();
    }

    [PgFact]
    public async Task A_description_and_a_profile_set_through_the_composition_are_read_back_after_a_restart()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        await SeedGameStoreAsync(source, SeededRow(StructureId, StructureState.ShieldVulnerable, 0, 0, UpkeepState.FullPower));
        var session = ManagerSession(await SeedManagerAsync());

        var host = await HostComposition.BuildAsync(Settings());
        var directory = host.Registry.Lookup("structureDirectory") ?? throw new InvalidOperationException("structureDirectory is not registered");
        var profiles = host.Registry.Lookup("structureProfiles") ?? throw new InvalidOperationException("structureProfiles is not registered");
        await directory.CallAsync(Call(session, "SetStructureDescription", 1, new PyInt(StructureId), new PyStr("a new description")));
        await profiles.CallAsync(Call(session, "ChangeProfiles", 2, new PyList([new PyInt(StructureId)]), new PyInt(0)));
        await host.ShutdownAsync();

        // a new host reads the schema: the description comes back through the directory, the profile is in the row
        var restarted = await HostComposition.BuildAsync(Settings());
        var again = restarted.Registry.Lookup("structureDirectory") ?? throw new InvalidOperationException("structureDirectory is not registered after the restart");
        Assert.Equal("a new description", Assert.IsType<PyStr>(await again.CallAsync(Call(session, "GetStructureDescription", 3, new PyInt(StructureId)))).Value);
        var row = (await new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA")).ReadLegacyTableAsync())[StructureId.ToString()]!.AsObject();
        Assert.Equal(7, row["profileID"]!.GetValue<long>());
        await restarted.ShutdownAsync();
    }

    [PgFact]
    public async Task An_imported_structure_with_an_elapsed_deadline_advances_once_after_start_and_a_replay_changes_nothing()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - StructureTimes.Hour;
        // the anchoring repair ended an hour ago: the timeline's next step is the anchoring, a day from that deadline
        await SeedGameStoreAsync(source, SeededRow(StructureId, StructureState.AnchorVulnerable, deadline - StructureTimes.RepairMs, deadline, UpkeepState.LowPower));
        var store = new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA"));

        var host = await HostComposition.BuildAsync(Settings());
        Assert.True(await WaitForAsync(async () => (await store.GetAsync(StructureId))!.Snapshot.State == StructureState.Anchoring, TimeSpan.FromSeconds(30)));
        var advanced = (await store.GetAsync(StructureId))!;
        Assert.Equal(deadline + StructureTimes.AnchoringMs, advanced.Snapshot.StateEndsMs);
        Assert.Equal(2, advanced.Version); // the import is version 1: exactly one step was applied
        await Task.Delay(TimeSpan.FromSeconds(2));
        await host.ShutdownAsync();

        // a restart copies nothing new and replays nothing: the step is not applied again
        var restarted = await HostComposition.BuildAsync(Settings());
        await Task.Delay(TimeSpan.FromSeconds(2));
        await restarted.ShutdownAsync();
        var again = (await store.GetAsync(StructureId))!;
        Assert.Equal(StructureState.Anchoring, again.Snapshot.State);
        Assert.Equal(deadline + StructureTimes.AnchoringMs, again.Snapshot.StateEndsMs);
        Assert.Equal(2, again.Version);
    }
}
