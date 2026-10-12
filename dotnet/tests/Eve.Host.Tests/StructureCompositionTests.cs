using System.Text.Json.Nodes;
using Eve.Data;
using Eve.GameStore;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Characters;
using Eve.Inventory;
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
        "EVE_TRADE_SCHEMA", "EVE_BOOKMARKS_SCHEMA", "EVE_ESS_SCHEMA",
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
        foreach (var (name, value) in new[] { ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip"), ("EVE_WORMHOLES", "off") })
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
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
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
    private async Task<long> SeedManagerAsync(string name = "Structure Manager")
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        await PgCharacterStore.MigrateAsync(source, Schema("EVE_CHARACTERS_SCHEMA"));
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var characterId = await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).InsertAsync(connection, transaction,
            new NewCharacter(1, name, 0, 1, 1, 1, 11, 1376, Corporation, null, station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
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
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
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
    public async Task Restart_clears_stale_structure_controller_records_without_removing_the_structure()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        await SeedGameStoreAsync(source, SeededRow(StructureId, StructureState.ShieldVulnerable, 0, 0, UpkeepState.FullPower));
        var host = await HostComposition.BuildAsync(Settings());
        var structures = new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA"));
        var pilots = new StructurePilotStore(structures);
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var result = await pilots.AcquireAsync(connection, transaction,
                new StructurePilotRequest(StructureId, 90000001, 1001, 10000001, StructureId, false), true);
            Assert.Equal(StructurePilotAcquireStatus.Acquired, result.Status);
            await transaction.CommitAsync();
        }
        await host.ShutdownAsync();

        var restarted = await HostComposition.BuildAsync(Settings());
        try
        {
            await using var connection = await source.OpenConnectionAsync();
            Assert.Null(await pilots.GetByStructureAsync(connection, null, StructureId));
            Assert.NotNull(await structures.GetAsync(StructureId));
        }
        finally { await restarted.ShutdownAsync(); }
    }

    [PgFact]
    public async Task Registered_delivery_moves_items_and_persists_one_inbox_notification_across_retry_and_restart()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        await SeedGameStoreAsync(source, SeededRow(StructureId, StructureState.ShieldVulnerable, 0, 0, UpkeepState.FullPower));
        var sender = await SeedManagerAsync();
        var recipient = await SeedManagerAsync("Structure Recipient");
        var session = ManagerSession(sender);
        session.Set("structureid", new PyLong(StructureId));
        session.Set("locationid", new PyLong(StructureId));
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            long itemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                itemId = await items.CreateAsync(connection, transaction, new NewItem(34, sender, StructureId, ItemFlags.Hangar, 25, false));
                await transaction.CommitAsync();
            }
            var service = host.Registry.Lookup("structureDeliveries") ?? throw new InvalidOperationException("structureDeliveries is not registered");
            var args = new PyValue[] { new PyLong(StructureId), new PyLong(recipient), new PyList([new PyLong(itemId), new PyLong(itemId)]) };
            Assert.IsType<PyNone>(await service.CallAsync(Call(session, "Deliver", 1, args)));
            Assert.IsType<PyNone>(await service.CallAsync(Call(session, "Deliver", 2, args)));
            var moved = await items.GetAsync(itemId);
            Assert.Equal(recipient, moved!.OwnerId);
            Assert.Equal(173, moved.Flag);
            await using var persisted = new PersistenceStore(source, Schema("EVE_GAMESTORE_SCHEMA"));
            var table = await persisted.LoadTableAsync("notifications");
            var box = table["boxes"]![recipient.ToString()]!["byID"]!.AsObject();
            var notification = Assert.Single(box).Value!.AsObject();
            Assert.Equal(199, notification["typeID"]!.GetValue<int>());
            Assert.Equal(sender, notification["senderID"]!.GetValue<long>());
            Assert.Equal(25, notification["data"]!["listOfTypesAndQty"]![0]![0]!.GetValue<long>());
        }
        finally { await host.ShutdownAsync(); }
        var restarted = await HostComposition.BuildAsync(Settings());
        try
        {
            await using var persisted = new PersistenceStore(source, Schema("EVE_GAMESTORE_SCHEMA"));
            var table = await persisted.LoadTableAsync("notifications");
            Assert.Single(table["boxes"]![recipient.ToString()]!["byID"]!.AsObject());
        }
        finally { await restarted.ShutdownAsync(); }
    }

    [PgFact]
    public async Task An_imported_structure_with_an_elapsed_deadline_advances_once_after_start_and_a_replay_changes_nothing()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
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

    private static ClientSession RoleSession(long characterId, long corporationId, int roles)
    {
        var session = new ClientSession(new Sink()) { SessionId = 402 };
        session.Set("charid", new PyInt(characterId));
        session.Set("corpid", new PyInt(corporationId));
        session.Set("corprole", new PyInt(roles));
        return session;
    }

    /// <summary>The user error a call was refused with: its name, and the notice it carries when it is a CustomNotify.</summary>
    private static (string Name, string? Notice) Refusal(Exception exception)
    {
        var error = Assert.IsType<MachoWrappedException>(exception).Error;
        if (error.Payload is not PyObjectEx payload || payload.Header is not PyTuple header || header.Items.Count < 2 || header.Items[1] is not PyTuple arguments
            || arguments.Items.Count < 2 || arguments.Items[1] is not PyDict dict)
        {
            return (error.UserErrorMessage, null);
        }
        var notice = dict.Entries.SingleOrDefault(e => JsSemantics.StringValue(e.Key) == "notify").Value is PyStr text ? text.Value : null;
        return (error.UserErrorMessage, notice);
    }

    [PgFact]
    public async Task A_reinforcement_time_is_pending_for_thirty_days_and_a_request_for_the_current_time_clears_it()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        await SeedGameStoreAsync(source, SeededRow(StructureId, StructureState.ShieldVulnerable, 0, 0, UpkeepState.FullPower));
        var session = ManagerSession(await SeedManagerAsync());
        var store = new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA"));

        var host = await HostComposition.BuildAsync(Settings());
        var vulnerability = host.Registry.Lookup("structureVulnerability") ?? throw new InvalidOperationException("structureVulnerability is not registered");
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await vulnerability.CallAsync(Call(session, "UpdateReinforceTimesFromClient", 1, new PyList([new PyInt(StructureId)]), new PyInt(2), new PyInt(10)));
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var pending = (await store.GetAsync(StructureId))!.Snapshot;
        Assert.Equal((5, 18), (pending.ReinforceWeekday, pending.ReinforceHour));
        Assert.Equal((2, 10), (pending.PendingWeekday, pending.PendingHour));
        Assert.InRange(pending.PendingEffectiveMs, before + StructureTimes.ReinforceChangeDelayMs, after + StructureTimes.ReinforceChangeDelayMs);

        // the structure's own time asked for again: the pending change is withdrawn
        await vulnerability.CallAsync(Call(session, "UpdateReinforceTimesFromClient", 2, new PyList([new PyInt(StructureId)]), new PyInt(5), new PyInt(18)));
        var cleared = (await store.GetAsync(StructureId))!.Snapshot;
        Assert.Equal((-1, -1, 0L), (cleared.PendingWeekday, cleared.PendingHour, cleared.PendingEffectiveMs));
        Assert.Equal((5, 18), (cleared.ReinforceWeekday, cleared.ReinforceHour));
        await host.ShutdownAsync();
    }

    [PgFact]
    public async Task A_reinforcement_request_is_refused_for_another_corporation_a_pilot_without_a_role_or_an_unknown_structure_and_changes_nothing()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        await SeedGameStoreAsync(source, SeededRow(StructureId, StructureState.ShieldVulnerable, 0, 0, UpkeepState.FullPower));
        var characterId = await SeedManagerAsync();
        var store = new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA"));

        var host = await HostComposition.BuildAsync(Settings());
        var vulnerability = host.Registry.Lookup("structureVulnerability") ?? throw new InvalidOperationException("structureVulnerability is not registered");
        // a Director may ask (the role set is Station Manager, Director or CEO)
        await vulnerability.CallAsync(Call(RoleSession(characterId, Corporation, 1), "UpdateReinforceTimesFromClient", 1, new PyList([new PyInt(StructureId)]), new PyInt(2), new PyInt(10)));
        Assert.Equal(2, (await store.GetAsync(StructureId))!.Snapshot.PendingWeekday);

        var stranger = await Assert.ThrowsAsync<MachoWrappedException>(async () => await vulnerability.CallAsync(
            Call(RoleSession(characterId, Corporation + 1, 2048), "UpdateReinforceTimesFromClient", 2, new PyList([new PyInt(StructureId)]), new PyInt(3), new PyInt(11))));
        Assert.Equal("CrpAccessDenied", Refusal(stranger).Name);
        var roleless = await Assert.ThrowsAsync<MachoWrappedException>(async () => await vulnerability.CallAsync(
            Call(RoleSession(characterId, Corporation, 0), "UpdateReinforceTimesFromClient", 3, new PyList([new PyInt(StructureId)]), new PyInt(3), new PyInt(11))));
        Assert.Equal("CrpAccessDenied", Refusal(roleless).Name);
        var unknown = await Assert.ThrowsAsync<MachoWrappedException>(async () => await vulnerability.CallAsync(
            Call(RoleSession(characterId, Corporation, 2048), "UpdateReinforceTimesFromClient", 4, new PyList([new PyInt(StructureId), new PyInt(StructureId + 9)]), new PyInt(3), new PyInt(11))));
        Assert.Equal("TargetingAttemptCancelled", Refusal(unknown).Name);

        var unchanged = (await store.GetAsync(StructureId))!.Snapshot;
        Assert.Equal((2, 10), (unchanged.PendingWeekday, unchanged.PendingHour));
        await host.ShutdownAsync();
    }

    [PgFact]
    public async Task Anchor_outside_space_is_refused_by_name_through_the_registered_service()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        await SeedGameStoreAsync(source, SeededRow(StructureId, StructureState.ShieldVulnerable, 0, 0, UpkeepState.FullPower));
        var characterId = await SeedManagerAsync();
        var session = ManagerSession(characterId);

        var host = await HostComposition.BuildAsync(Settings());
        var deployment = host.Registry.Lookup("structureDeployment") ?? throw new InvalidOperationException("structureDeployment is not registered");
        // no simulation in this host: the pilot is not in space, so the anchor is refused by name before any item is read
        var args = new PyValue[] { new PyInt(9_001), PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, new PyStr("Anchored"), new PyStr(""), PyNone.Instance, PyNone.Instance, PyNone.Instance };
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(async () => await deployment.CallAsync(Call(session, "Anchor", 1, args)));
        var error = refused.Error;
        Assert.Equal("CustomNotify", error.UserErrorMessage);
        Assert.Equal("You must be in space to deploy a structure.", Assert.IsType<PyStr>(((PyDict)((PyTuple)((PyTuple)((PyObjectEx)error.Payload!).Header).Items[1]).Items[1]).Entries.Single(e => JsSemantics.StringValue(e.Key) == "notify").Value).Value);
        await host.ShutdownAsync();
    }

    [PgFact]
    public async Task Unanchor_starts_a_seven_day_timer_for_a_director_and_cancelling_stops_it()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        await SeedGameStoreAsync(source, SeededRow(StructureId, StructureState.ShieldVulnerable, 0, 0, UpkeepState.FullPower));
        var characterId = await SeedManagerAsync();
        var store = new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA"));

        var host = await HostComposition.BuildAsync(Settings());
        var deployment = host.Registry.Lookup("structureDeployment") ?? throw new InvalidOperationException("structureDeployment is not registered");
        // a Station Manager may manage the structure but not unanchor it (EVE.js requireStructureDecommissionSession: Director or CEO)
        var manager = await Assert.ThrowsAsync<MachoWrappedException>(async () => await deployment.CallAsync(Call(RoleSession(characterId, Corporation, 2048), "Unanchor", 1, new PyInt(StructureId))));
        Assert.Equal("CrpAccessDenied", Refusal(manager).Name);

        var director = RoleSession(characterId, Corporation, 1);
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await deployment.CallAsync(Call(director, "Unanchor", 2, new PyInt(StructureId)));
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var unanchoring = (await store.GetAsync(StructureId))!.Snapshot.UnanchorEndsMs;
        Assert.InRange(unanchoring, before + StructureTimes.UnanchoringMs, after + StructureTimes.UnanchoringMs);

        // asking again while it runs changes nothing (EVE.js answers success)
        await deployment.CallAsync(Call(director, "Unanchor", 3, new PyInt(StructureId)));
        Assert.Equal(unanchoring, (await store.GetAsync(StructureId))!.Snapshot.UnanchorEndsMs);

        await deployment.CallAsync(Call(director, "CancelUnanchor", 4, new PyInt(StructureId)));
        Assert.Equal(0, (await store.GetAsync(StructureId))!.Snapshot.UnanchorEndsMs);
        // nothing to cancel is not an error
        await deployment.CallAsync(Call(director, "CancelUnanchor", 5, new PyInt(StructureId)));
        await host.ShutdownAsync();
    }

    [PgFact]
    public async Task Unanchor_is_refused_by_name_in_a_state_that_cancels_it_and_changes_nothing()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        await SeedGameStoreAsync(source, SeededRow(StructureId, StructureState.ArmorReinforce, 0, 0, UpkeepState.FullPower));
        var characterId = await SeedManagerAsync();
        var store = new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA"));

        var host = await HostComposition.BuildAsync(Settings());
        var deployment = host.Registry.Lookup("structureDeployment") ?? throw new InvalidOperationException("structureDeployment is not registered");
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(async () => await deployment.CallAsync(Call(RoleSession(characterId, Corporation, 1), "Unanchor", 1, new PyInt(StructureId))));
        Assert.Equal(("CustomNotify", "Failed to unanchor structure: STRUCTURE_CANNOT_UNANCHOR_IN_STATE."), Refusal(refused));
        Assert.Equal(0, (await store.GetAsync(StructureId))!.Snapshot.UnanchorEndsMs);
        await host.ShutdownAsync();
    }

    [PgFact]
    public async Task Rename_normalizes_the_name_the_directory_shows_refuses_a_short_name_and_survives_a_restart()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        await SeedGameStoreAsync(source, SeededRow(StructureId, StructureState.ShieldVulnerable, 0, 0, UpkeepState.FullPower));
        var characterId = await SeedManagerAsync();
        var session = ManagerSession(characterId);
        var store = new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA"));

        var host = await HostComposition.BuildAsync(Settings());
        var deployment = host.Registry.Lookup("structureDeployment") ?? throw new InvalidOperationException("structureDeployment is not registered");
        await deployment.CallAsync(Call(session, "RenameStructure", 1, new PyInt(StructureId), new PyStr("  Fort   Knox\n Two  ")));
        Assert.Equal("Fort Knox Two", (await store.GetAsync(StructureId))!.Name);
        Assert.Equal("Fort Knox Two", (await store.ReadLegacyTableAsync())[StructureId.ToString()]!["itemName"]!.GetValue<string>());

        var tooShort = await Assert.ThrowsAsync<MachoWrappedException>(async () => await deployment.CallAsync(Call(session, "RenameStructure", 2, new PyInt(StructureId), new PyStr(" ab "))));
        Assert.Equal("CharNameTooShort", Refusal(tooShort).Name);
        var stranger = await Assert.ThrowsAsync<MachoWrappedException>(async () => await deployment.CallAsync(Call(RoleSession(characterId, Corporation + 1, 2048), "RenameStructure", 3, new PyInt(StructureId), new PyStr("Stolen name"))));
        Assert.Equal("CrpAccessDenied", Refusal(stranger).Name);
        await host.ShutdownAsync();

        var restarted = await HostComposition.BuildAsync(Settings());
        Assert.Equal("Fort Knox Two", (await new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA")).GetAsync(StructureId))!.Name);
        await restarted.ShutdownAsync();
    }
}
