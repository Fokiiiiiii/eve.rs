using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.GameStore;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Sde;
using Eve.Structures;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// Corporation offices at an Upwell structure through the real host composition (ST-12), on scratch PostgreSQL schemas: the Corporation Offices service is online
/// with no module or fuel (EVE University wiki); the rent of the profile is quoted and charged once to the renting corporation and credited to the owner; an office
/// the corporation cannot pay for is refused; the rent falls due as a durable job that renews a paid office (auto-pay) or gives an unpaid one up (its items to
/// asset safety), and a Director gives an office up by hand.
/// </summary>
[Collection("host-composition")]
public sealed class StructureOfficeTests : IAsyncLifetime
{
    private const long Corporation = 98_000_001, Outsider = 98_000_002, StructureId = 1_001;
    private const long Jita = 30_000_142, AccessGroup = 500;
    private const int Astrahus = 35_832, OfficesService = StructureServiceIds.Offices, RentalBillType = 2;
    private const long OwnerRent = 25_000, OutsiderRent = 40_000, Balance = 1_000_000;
    private const int OfficeFolderType = 26, Veldspar = 1230, CompressedVeldspar = 62516, CorpSag1 = 115;

    /// <summary>The environment the host reads: every schema on its own scratch name, plus the no-simulation switch.</summary>
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA",
        "EVE_TRADE_SCHEMA", "EVE_BOOKMARKS_SCHEMA", "EVE_ESS_SCHEMA",
    ];

    private readonly string _prefix = "so" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    private string Schema(string variable) => variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant() is var name ? $"{_prefix}_{name}" : "";

    private string Url => Environment.GetEnvironmentVariable("EVE_PG_URL")!;

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            _saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = Schema(variable);
            _schemas.Add(schema);
            Environment.SetEnvironmentVariable(variable, schema);
        }
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

    /// <summary>A pilot's session at the structure, in the corporation the test names.</summary>
    private static ClientSession PilotAtStructure(long characterId, long corporationId)
    {
        var session = new ClientSession(new Sink()) { SessionId = 402 };
        session.Set("charid", new PyInt(characterId));
        session.Set("corpid", new PyInt(corporationId));
        session.Set("corporationID", new PyInt(corporationId));
        session.Set("structureid", new PyInt(StructureId));
        return session;
    }

    private static ServiceCall Call(ClientSession session, string method, long id, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(id) };

    /// <summary>A structure in the game store's legacy shape: an Astrahus of the corporation in Jita, shield stage, full power.</summary>
    private static JsonObject SeededStructure() => new()
    {
        ["structureID"] = StructureId,
        ["typeID"] = Astrahus,
        ["solarSystemID"] = Jita,
        ["ownerCorpID"] = Corporation,
        ["itemName"] = "Office Structure",
        ["position"] = new JsonObject { ["x"] = 1.0, ["y"] = 2.0, ["z"] = 3.0 },
        ["state"] = StructureState.ShieldVulnerable,
        ["upkeepState"] = UpkeepState.FullPower,
        ["stateStartedAt"] = 0,
        ["stateEndsAt"] = 0,
        ["hasQuantumCore"] = true,
    };

    /// <summary>
    /// The structure, and the owner's default profile: Corporation Office Rent (20) is <see cref="OwnerRent"/> for the owner and <see cref="OutsiderRent"/> for the
    /// members of the access group that names the Outsider corporation.
    /// </summary>
    private async Task SeedAsync(NpgsqlDataSource source)
    {
        await using var store = new PersistenceStore(source, Schema("EVE_GAMESTORE_SCHEMA"));
        await store.EnsureSchemaAsync();
        await using var owner = await OwnerSession.StartAsync(store, PersistenceRoles.World, "structure-office-" + _prefix);
        await using var cache = new GameStoreCache(store, owner,
            new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });
        await cache.RecoverAsync();
        await cache.LoadAsync("structures");
        Assert.True(cache.Write("structures", "/" + StructureId, SeededStructure()).Success);
        await cache.FlushTableAsync("structures");
        await cache.LoadAsync("accessGroups");
        Assert.True(cache.Write("accessGroups", "/" + AccessGroup, new JsonObject
        {
            ["groupID"] = AccessGroup,
            ["creatorID"] = Corporation,
            ["members"] = new JsonArray(new JsonObject { ["memberID"] = Outsider, ["membershipType"] = 1 }),
        }).Success);
        await cache.FlushTableAsync("accessGroups");
        await cache.LoadAsync("structureProfiles");
        Assert.True(cache.Write("structureProfiles", "/", new JsonObject
        {
            ["meta"] = new JsonObject { ["version"] = 1 },
            ["nextProfileID"] = 8,
            ["profilesByID"] = new JsonObject
            {
                ["7"] = new JsonObject
                {
                    ["profileID"] = 7, ["corporationID"] = Corporation, ["name"] = "Default Profile", ["description"] = "", ["isDefault"] = true,
                    ["settingsBySettingID"] = new JsonObject
                    {
                        ["20"] = new JsonArray(
                            new JsonObject { ["groupID"] = 0, ["value"] = OwnerRent },
                            new JsonObject { ["groupID"] = AccessGroup, ["value"] = OutsiderRent }),
                    },
                },
            },
        }).Success);
        await cache.FlushTableAsync("structureProfiles");
    }

    /// <summary>A pilot in the characters schema (the host reads its corporation from there).</summary>
    private async Task<long> SeedPilotAsync(string name, long corporationId)
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        await PgCharacterStore.MigrateAsync(source, Schema("EVE_CHARACTERS_SCHEMA"));
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var characterId = await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).InsertAsync(connection, transaction,
            new NewCharacter(1, name, 0, 1, 1, 1, 11, 1376, corporationId, null, station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
        await transaction.CommitAsync();
        return characterId;
    }

    /// <summary>A corporation of the test: its row, its member and the member's roles, and its wallet's main account funded with <paramref name="isk"/>.</summary>
    private async Task SeedCorporationAsync(NpgsqlDataSource source, long corporationId, long memberId, long roles, decimal isk)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Exec(connection, transaction,
            $"INSERT INTO {Schema("EVE_CORPORATIONS_SCHEMA")}.owner_names (owner_id, name_key, ticker_key, is_npc) VALUES ($1, $2, $3, false) ON CONFLICT DO NOTHING",
            corporationId, $"office test corporation {corporationId}", $"OT{corporationId}");
        await Exec(connection, transaction,
            $"INSERT INTO {Schema("EVE_CORPORATIONS_SCHEMA")}.corporations (id, name, ticker, ceo_id, creator_id, station_id, solar_system_id, tax_rate, lp_tax_rate, description, url, member_limit, is_npc, created_ms) " +
            "VALUES ($1, $2, $3, $4, $4, 0, 0, 0, 0, '', '', 100, false, 0) ON CONFLICT DO NOTHING",
            corporationId, $"office test corporation {corporationId}", $"OT{corporationId}", memberId);
        await Exec(connection, transaction,
            $"INSERT INTO {Schema("EVE_CORPORATIONS_SCHEMA")}.members (corporation_id, character_id, roles, started_ms) VALUES ($1, $2, $3, 0) ON CONFLICT DO NOTHING",
            corporationId, memberId, roles);
        await Exec(connection, transaction,
            $"INSERT INTO {Schema("EVE_WALLET_SCHEMA")}.balances (owner_id, account_key, isk, plex) VALUES ($1, 1000, $2, 0) ON CONFLICT DO NOTHING",
            corporationId, isk);
        await transaction.CommitAsync();
    }

    private static async Task Exec(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T?> ScalarAsync<T>(string sql, params object[] values)
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, typeof(T));
    }

    private async Task<decimal> BalanceAsync(long owner) => await ScalarAsync<decimal>(
        $"SELECT isk FROM {Schema("EVE_WALLET_SCHEMA")}.balances WHERE owner_id = $1 AND account_key = 1000", owner);

    private async Task<bool> OfficeActiveAsync(long corporationId) => await ScalarAsync<bool>(
        $"SELECT count(*) > 0 FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.office_rentals WHERE corporation_id = $1 AND station_id = $2 AND active",
        corporationId, StructureId);

    private async Task<long> WrapCountAsync(long corporationId) => await ScalarAsync<long>(
        $"SELECT count(*) FROM {Schema("EVE_ASSET_SAFETY_SCHEMA")}.asset_safety_wraps WHERE scope = 2 AND corporation_id = $1 AND source_structure_id = $2",
        corporationId, StructureId);

    private async Task SetRentDueAsync(long corporationId)
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        await using (var bills = source.CreateCommand(
            $"UPDATE {Schema("EVE_CORPORATIONS_SCHEMA")}.office_rent_bills SET issued_at_ms = 0, due_at_ms = 1 WHERE corporation_id = $1 AND status = 0"))
        {
            bills.Parameters.Add(new NpgsqlParameter { Value = corporationId });
            await bills.ExecuteNonQueryAsync();
        }
        await using var jobs = source.CreateCommand(
            $"UPDATE {Schema("EVE_SCHEDULER_SCHEMA")}.jobs SET run_at_ms = 0 WHERE kind = 'office.rent_due' AND state = 'pending'");
        await jobs.ExecuteNonQueryAsync();
    }

    private async Task SetAutoPayAsync(NpgsqlDataSource source, long corporationId, bool enabled)
    {
        await using var command = source.CreateCommand(
            $"INSERT INTO {Schema("EVE_CORPORATIONS_SCHEMA")}.corporation_bill_auto_pay_settings (owner_id, division_id, enabled_bill_types) VALUES ($1, 1000, $2::jsonb) " +
            "ON CONFLICT (owner_id) DO UPDATE SET enabled_bill_types = EXCLUDED.enabled_bill_types");
        command.Parameters.Add(new NpgsqlParameter { Value = corporationId });
        command.Parameters.Add(new NpgsqlParameter { Value = enabled ? $"{{\"{RentalBillType}\": true}}" : "{}" });
        await command.ExecuteNonQueryAsync();
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

    private async Task SeedWorldAsync(NpgsqlDataSource source, long renterMember, long renterRoles, decimal renterIsk)
    {
        // the schemas the corporation rows go to exist before the host starts (the host migrates them again, which does nothing)
        await PgWalletStore.MigrateAsync(source, Schema("EVE_WALLET_SCHEMA"));
        await CorporationStore.MigrateAsync(source, Schema("EVE_CORPORATIONS_SCHEMA"));
        await CorporationOfficeStore.MigrateAsync(source, Schema("EVE_CORPORATIONS_SCHEMA"));
        await SeedAsync(source);
        await SeedCorporationAsync(source, Corporation, await SeedPilotAsync("Owner CEO", Corporation), 0, 0m);
        await SeedCorporationAsync(source, Outsider, renterMember, renterRoles, renterIsk);
    }

    [PgFact]
    public async Task The_office_service_is_online_with_no_module_and_the_structure_is_an_office_site()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        await SeedAsync(source);
        var member = await SeedPilotAsync("Office Pilot", Corporation);
        var outsider = await SeedPilotAsync("Outside Pilot", Outsider);

        var host = await HostComposition.BuildAsync(Settings());
        var offices = host.Registry.Lookup("officeManager") ?? throw new InvalidOperationException("officeManager is not registered");

        // the structure binds (it is not an NPC station); the owner's member is quoted the owner's rent, a group member the group's rent
        Assert.IsType<PyTuple>(await offices.CallAsync(Call(PilotAtStructure(member, Corporation), "MachoBindObject", 1, new PyInt(StructureId))));
        Assert.Equal(new PyReal(OwnerRent), await offices.CallAsync(Call(PilotAtStructure(member, Corporation), "GetPriceQuote", 2, new PyInt(Corporation))));
        Assert.Equal(new PyReal(OutsiderRent), await offices.CallAsync(Call(PilotAtStructure(outsider, Outsider), "GetPriceQuote", 3, new PyInt(Outsider))));
        await host.ShutdownAsync();

        // a restart keeps the profile rent; the office service stays online in the structures schema
        var restarted = await HostComposition.BuildAsync(Settings());
        var again = restarted.Registry.Lookup("officeManager") ?? throw new InvalidOperationException("officeManager is not registered after the restart");
        Assert.Equal(new PyReal(OutsiderRent), await again.CallAsync(Call(PilotAtStructure(outsider, Outsider), "GetPriceQuote", 4, new PyInt(Outsider))));
        await restarted.ShutdownAsync();

        var legacy = (await new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA")).ReadLegacyTableAsync())[StructureId.ToString()]!.AsObject();
        Assert.Equal(ServiceState.Online, legacy["serviceStates"]![OfficesService.ToString()]!.GetValue<int>());
    }

    [PgFact]
    public async Task Renting_an_office_charges_the_renter_once_and_credits_the_owner()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        await SeedWorldAsync(source, await SeedPilotAsync("Renter", Outsider), CorporationRoles.RentOffice, Balance);
        var renter = await ScalarAsync<long>($"SELECT character_id FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.members WHERE corporation_id = $1", Outsider);

        var host = await HostComposition.BuildAsync(Settings());
        var offices = host.Registry.Lookup("officeManager") ?? throw new InvalidOperationException("officeManager is not registered");
        await offices.CallAsync(Call(PilotAtStructure(renter, Outsider), "RentOffice", 1));
        // a second rent of the same office is answered without a charge
        await offices.CallAsync(Call(PilotAtStructure(renter, Outsider), "RentOffice", 2));
        await host.ShutdownAsync();

        Assert.True(await OfficeActiveAsync(Outsider));
        Assert.Equal(Balance - OutsiderRent, await BalanceAsync(Outsider));
        Assert.Equal(OutsiderRent, await BalanceAsync(Corporation));
        // the office folder is the corporation's item at the structure, and the first due job is scheduled
        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT count(*) FROM {Schema("EVE_INVENTORY_SCHEMA")}.items WHERE owner_id = $1 AND location_id = $2 AND type_id = $3",
            Outsider, StructureId, OfficeFolderType));
        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT count(*) FROM {Schema("EVE_SCHEDULER_SCHEMA")}.jobs WHERE kind = 'office.rent_due' AND state = 'pending'"));
    }

    [PgFact]
    public async Task A_renter_who_cannot_pay_is_refused_and_nothing_is_written()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        await SeedWorldAsync(source, await SeedPilotAsync("Broke Renter", Outsider), CorporationRoles.RentOffice, 100m);
        var renter = await ScalarAsync<long>($"SELECT character_id FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.members WHERE corporation_id = $1", Outsider);

        var host = await HostComposition.BuildAsync(Settings());
        var offices = host.Registry.Lookup("officeManager") ?? throw new InvalidOperationException("officeManager is not registered");
        await Assert.ThrowsAnyAsync<Exception>(async () => await offices.CallAsync(Call(PilotAtStructure(renter, Outsider), "RentOffice", 1)));
        await host.ShutdownAsync();

        Assert.False(await OfficeActiveAsync(Outsider));
        Assert.Equal(100m, await BalanceAsync(Outsider));
        Assert.Equal(0m, await BalanceAsync(Corporation));
        Assert.Equal(0L, await ScalarAsync<long>(
            $"SELECT count(*) FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.office_rent_bills WHERE corporation_id = $1", Outsider));
    }

    [PgFact]
    public async Task A_rent_paid_before_a_restart_is_not_charged_again_after_it()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        await SeedWorldAsync(source, await SeedPilotAsync("Renter Again", Outsider), CorporationRoles.RentOffice, Balance);
        var renter = await ScalarAsync<long>($"SELECT character_id FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.members WHERE corporation_id = $1", Outsider);

        var first = await HostComposition.BuildAsync(Settings());
        await (first.Registry.Lookup("officeManager") ?? throw new InvalidOperationException("officeManager is not registered"))
            .CallAsync(Call(PilotAtStructure(renter, Outsider), "RentOffice", 1));
        await first.ShutdownAsync();

        var restarted = await HostComposition.BuildAsync(Settings());
        await (restarted.Registry.Lookup("officeManager") ?? throw new InvalidOperationException("officeManager is not registered after the restart"))
            .CallAsync(Call(PilotAtStructure(renter, Outsider), "RentOffice", 2));
        await restarted.ShutdownAsync();

        Assert.Equal(Balance - OutsiderRent, await BalanceAsync(Outsider));
        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT count(*) FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.office_rent_bills WHERE corporation_id = $1 AND status = 0", Outsider));
    }

    [PgFact]
    public async Task A_due_rent_is_paid_by_auto_pay_and_renews_the_office_once()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        await SeedWorldAsync(source, await SeedPilotAsync("Auto Renter", Outsider), CorporationRoles.RentOffice, Balance);
        var renter = await ScalarAsync<long>($"SELECT character_id FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.members WHERE corporation_id = $1", Outsider);
        await SetAutoPayAsync(source, Outsider, enabled: true);

        var host = await HostComposition.BuildAsync(Settings());
        await (host.Registry.Lookup("officeManager") ?? throw new InvalidOperationException("officeManager is not registered"))
            .CallAsync(Call(PilotAtStructure(renter, Outsider), "RentOffice", 1));
        await SetRentDueAsync(Outsider);

        // the job pays the due bill from the division and opens the next one; the office stays
        Assert.True(await WaitForAsync(async () => await ScalarAsync<long>(
            $"SELECT count(*) FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.office_rent_bills WHERE corporation_id = $1 AND status = 1", Outsider) == 1, TimeSpan.FromSeconds(60)));
        await host.ShutdownAsync();

        Assert.True(await OfficeActiveAsync(Outsider));
        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT count(*) FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.office_rent_bills WHERE corporation_id = $1 AND status = 0", Outsider));
        Assert.Equal(Balance - 2 * OutsiderRent, await BalanceAsync(Outsider));
        Assert.Equal(2 * OutsiderRent, await BalanceAsync(Corporation));
        // the renewed bill's job waits for its own due time
        Assert.True(await ScalarAsync<bool>(
            $"SELECT count(*) > 0 FROM {Schema("EVE_SCHEDULER_SCHEMA")}.jobs WHERE kind = 'office.rent_due' AND state = 'pending' AND run_at_ms > $1",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    [PgFact]
    public async Task An_unpaid_office_is_given_up_when_its_rent_falls_due_and_its_items_go_to_asset_safety_after_a_restart()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        await SeedWorldAsync(source, await SeedPilotAsync("Late Renter", Outsider), CorporationRoles.RentOffice, Balance);
        var renter = await ScalarAsync<long>($"SELECT character_id FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.members WHERE corporation_id = $1", Outsider);

        var first = await HostComposition.BuildAsync(Settings());
        await (first.Registry.Lookup("officeManager") ?? throw new InvalidOperationException("officeManager is not registered"))
            .CallAsync(Call(PilotAtStructure(renter, Outsider), "RentOffice", 1));
        await first.ShutdownAsync();

        // the job waits in the scheduler across the restart; when the rent falls due the office is given up
        await SetRentDueAsync(Outsider);
        var restarted = await HostComposition.BuildAsync(Settings());
        Assert.True(await WaitForAsync(async () => !await OfficeActiveAsync(Outsider), TimeSpan.FromSeconds(60)));
        await restarted.ShutdownAsync();

        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT count(*) FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.office_rent_bills WHERE corporation_id = $1 AND status = 2", Outsider));
        Assert.Equal(1L, await WrapCountAsync(Outsider));
        Assert.Equal(Balance - OutsiderRent, await BalanceAsync(Outsider));
    }

    [PgFact]
    public async Task A_director_gives_an_office_up_and_its_items_go_to_asset_safety_while_a_member_cannot()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        await SeedWorldAsync(source, await SeedPilotAsync("Office Director", Outsider), CorporationRoles.RentOffice | CorporationRoles.Director, Balance);
        var director = await ScalarAsync<long>($"SELECT character_id FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.members WHERE corporation_id = $1", Outsider);
        var member = await SeedPilotAsync("Plain Member", Outsider);
        await using (var connection = await source.OpenConnectionAsync())
            await Exec(connection, null, $"INSERT INTO {Schema("EVE_CORPORATIONS_SCHEMA")}.members (corporation_id, character_id, roles, started_ms) VALUES ($1, $2, 0, 0)",
                Outsider, member);

        var host = await HostComposition.BuildAsync(Settings());
        var offices = host.Registry.Lookup("officeManager") ?? throw new InvalidOperationException("officeManager is not registered");
        await offices.CallAsync(Call(PilotAtStructure(director, Outsider), "RentOffice", 1));
        await Assert.ThrowsAnyAsync<Exception>(async () => await offices.CallAsync(Call(PilotAtStructure(member, Outsider), "UnrentOffice", 2)));
        Assert.True(await OfficeActiveAsync(Outsider));
        await offices.CallAsync(Call(PilotAtStructure(director, Outsider), "UnrentOffice", 3));
        await host.ShutdownAsync();

        Assert.False(await OfficeActiveAsync(Outsider));
        Assert.Equal(1L, await WrapCountAsync(Outsider));
    }

    /// <summary>The office hangar (CorpSAG1, flag 115) of the corporation at the structure, the reprocessing plant online, and the compression RPC of the host.</summary>
    [PgFact]
    public async Task A_renewal_reprices_the_office_rent_from_the_structure_profile_not_the_signing_quote()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        await SeedWorldAsync(source, await SeedPilotAsync("Repriced Renter", Outsider), CorporationRoles.RentOffice, Balance);
        var renter = await ScalarAsync<long>($"SELECT character_id FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.members WHERE corporation_id = $1", Outsider);
        await SetAutoPayAsync(source, Outsider, enabled: true);

        var host = await HostComposition.BuildAsync(Settings());
        await (host.Registry.Lookup("officeManager") ?? throw new InvalidOperationException("officeManager is not registered"))
            .CallAsync(Call(PilotAtStructure(renter, Outsider), "RentOffice", 1));
        await SetRentDueAsync(Outsider);

        // the renter signed at 40 000 ISK; the renewal bill takes the profile's group 0 value (25 000 ISK) at the moment it is opened
        Assert.True(await WaitForAsync(async () => await ScalarAsync<decimal>(
            $"SELECT COALESCE((SELECT amount_isk FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.office_rent_bills WHERE corporation_id = $1 AND status = 0), -1)", Outsider) == OwnerRent,
            TimeSpan.FromSeconds(60)));
        await host.ShutdownAsync();

        Assert.True(await OfficeActiveAsync(Outsider));
        Assert.Equal(Balance - 2 * OutsiderRent, await BalanceAsync(Outsider));
    }

    [PgFact]
    public async Task A_destroyed_structure_closes_its_rented_office_cancels_the_bill_and_its_items_go_to_asset_safety()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        await SeedWorldAsync(source, await SeedPilotAsync("Renter Of A Lost Structure", Outsider), CorporationRoles.RentOffice, Balance);
        var renter = await ScalarAsync<long>($"SELECT character_id FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.members WHERE corporation_id = $1", Outsider);

        var host = await HostComposition.BuildAsync(Settings());
        await (host.Registry.Lookup("officeManager") ?? throw new InvalidOperationException("officeManager is not registered"))
            .CallAsync(Call(PilotAtStructure(renter, Outsider), "RentOffice", 1));

        // the unanchoring ended long ago: the structure's next timer removes it (unanchored), as the scheduler would deliver it
        await using (var unanchor = source.CreateCommand($"UPDATE {Schema("EVE_STRUCTURES_SCHEMA")}.structures SET unanchor_ends_ms = 1 WHERE structure_id = $1"))
        {
            unanchor.Parameters.Add(new NpgsqlParameter { Value = StructureId });
            await unanchor.ExecuteNonQueryAsync();
        }
        var timers = new StructureTimers(new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA")), host.Scheduler!, host.Events!, new OfficeTestRules());
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var removal = await timers.ApplyAsync(connection, transaction, StructureId, new TimerDue());
            Assert.Equal(StructureRemoval.Unanchored, removal?.Removal);
            await transaction.CommitAsync();
        }
        Assert.True(await WaitForAsync(async () => !await OfficeActiveAsync(Outsider), TimeSpan.FromSeconds(60)));
        await host.ShutdownAsync();

        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT count(*) FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.office_rent_bills WHERE corporation_id = $1 AND status = 2", Outsider));
        Assert.Equal(1L, await WrapCountAsync(Outsider));
        Assert.Equal(Balance - OutsiderRent, await BalanceAsync(Outsider));
    }

    private sealed class OfficeTestRules : IStructureRulesSource
    {
        public StructureRules RulesOf(int typeId, int solarSystemId) => StructureRules.Of(StructureSize.Medium, SecurityBand.HighSec);
    }

    [PgFact]
    public async Task A_rented_office_hangar_at_a_structure_is_the_corporation_compression_source_through_the_host()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        await SeedWorldAsync(source, await SeedPilotAsync("Office Director", Outsider), CorporationRoles.RentOffice | CorporationRoles.Director, Balance);
        var director = await ScalarAsync<long>($"SELECT character_id FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.members WHERE corporation_id = $1", Outsider);

        var host = await HostComposition.BuildAsync(Settings());
        var offices = host.Registry.Lookup("officeManager") ?? throw new InvalidOperationException("officeManager is not registered");
        var compression = host.Registry.Lookup("structureCompressionMgr") ?? throw new InvalidOperationException("structureCompressionMgr is not registered");

        // the plant (the Reprocessing service) is online, the Veldspar sits in the corporation's hangar at the structure
        await using (var connection = await source.OpenConnectionAsync())
            await Exec(connection, null,
                $"INSERT INTO {Schema("EVE_STRUCTURES_SCHEMA")}.structure_services (structure_id, service_id, state, online_since_ms) VALUES ($1, 4, 1, 0)",
                StructureId);
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        long veldspar;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            veldspar = await items.CreateAsync(connection, transaction,
                new NewItem(Veldspar, Outsider, StructureId, CorpSag1, 500));
            await transaction.CommitAsync();
        }

        // no office: the compression is refused and the ore stays
        await Assert.ThrowsAnyAsync<Exception>(async () => await compression.CallAsync(Call(PilotAtStructure(director, Outsider), "CompressItemInStructure", 1, new PyLong(veldspar))));
        Assert.Equal(500L, await ScalarAsync<long>($"SELECT quantity FROM {Schema("EVE_INVENTORY_SCHEMA")}.items WHERE id = $1", veldspar));

        // the office is rented: the same call compresses the ore in the office hangar, and the members who may take are told (the result is the compressed stack)
        await offices.CallAsync(Call(PilotAtStructure(director, Outsider), "RentOffice", 2));
        await compression.CallAsync(Call(PilotAtStructure(director, Outsider), "CompressItemInStructure", 3, new PyLong(veldspar)));
        await host.ShutdownAsync();

        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT count(*) FROM {Schema("EVE_INVENTORY_SCHEMA")}.items WHERE owner_id = $1 AND location_id = $2 AND type_id = $3 AND quantity = 500",
            Outsider, StructureId, CompressedVeldspar));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM {Schema("EVE_INVENTORY_SCHEMA")}.items WHERE id = $1", veldspar));
    }
}
