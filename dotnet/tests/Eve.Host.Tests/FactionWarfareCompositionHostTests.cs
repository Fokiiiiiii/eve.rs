using Eve.Characters;
using Eve.Data;
using Eve.Events;
using Eve.FactionWarfare;
using Eve.Host;
using Eve.Identity;
using Eve.Services;
using Eve.Services.Space;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>The player-kill producer registered by the production Host composition writes its durable statistics through PostgreSQL.</summary>
[Collection("host-composition")]
public sealed class FactionWarfareCompositionHostTests : IAsyncLifetime
{
    private const long AttackerCorporation = 98_200_111;
    private const long VictimCorporation = 98_200_112;
    private const long LaterCorporation = 98_200_113;
    private const long Now = 1_794_960_000_000;

    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA", "EVE_ACCESS_GROUPS_SCHEMA", "EVE_TRADE_SCHEMA", "EVE_BOOKMARKS_SCHEMA",
        "EVE_ESS_SCHEMA", "EVE_FACTIONWARFARE_SCHEMA", "EVE_INSURGENCY_SCHEMA", "EVE_CONTRABAND_SCHEMA", "EVE_AGENTS_SCHEMA", "EVE_PLANETS_SCHEMA",
        "EVE_PROJECT_DISCOVERY_SCHEMA", "EVE_RAFFLE_SCHEMA", "EVE_SKINR_SCHEMA", "EVE_WORMHOLE_SCHEMA", "EVE_XMPP_SCHEMA",
    ];

    private readonly string _prefix = "fwc" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    private string Schema(string variable)
    {
        var name = variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant();
        return $"{_prefix}_{name}";
    }

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
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is { } url)
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            foreach (var schema in _schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);
    }

    [PgFact]
    public async Task Registered_host_records_hostile_player_kill_statistics_once_across_restart()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        HostRuntime? host = null;
        try
        {
            host = await HostComposition.BuildAsync(Settings(), () => Now);
            var events = host.Events ?? throw new InvalidOperationException("Host event bus is not registered");
            await PumpAsync(events); // establish the Host producer cursor before publishing the kill

            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var attacker = await AddCharacterAsync(source, characters, AttackerCorporation, FwFactions.MinmatarRepublic, "FW host producer attacker");
            var victim = await AddCharacterAsync(source, characters, VictimCorporation, FwFactions.AmarrEmpire, "FW host producer victim");
            var system = checked((int)FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0]);
            var kill = new ShipKillmailEvent(victim, VictimCorporation, 0, 0,
                7_120_001, 587, system, Now, 100, 7_120_002,
                [new ShipKillmailAttacker(attacker, AttackerCorporation, 0, 0, 7_120_002, 587, 0, 100, 0, 1,
                    FwFactions.MinmatarRepublic)], [], VictimWarFactionId: FwFactions.AmarrEmpire);

            await events.PublishAsync(ShipKillmailEvent.Topic, kill);
            await SetCharacterFactionAsync(source, characters, attacker, FwFactions.AmarrEmpire);
            await SetCharacterFactionAsync(source, characters, victim, FwFactions.AmarrEmpire);
            await characters.SetCorporationAsync(attacker, LaterCorporation);
            await PumpAsync(events);

            var store = new FwStore(source, Schema("EVE_FACTIONWARFARE_SCHEMA"));
            var day = FwRules.DayOf(Now);
            Assert.Equal(1, (await PilotStatsAsync(store, source, attacker, day)).KillsTotal);
            Assert.Equal(1, (await CorporationStatsAsync(store, source, AttackerCorporation, day)).KillsTotal);
            Assert.Equal(0, (await CorporationStatsAsync(store, source, LaterCorporation, day)).KillsTotal);

            await host.ShutdownAsync();
            host = await HostComposition.BuildAsync(Settings(), () => Now);
            await PumpAsync(host.Events ?? throw new InvalidOperationException("restarted Host event bus is not registered"));
            Assert.Equal(1, (await PilotStatsAsync(store, source, attacker, day)).KillsTotal);
            Assert.Equal(1, (await CorporationStatsAsync(store, source, AttackerCorporation, day)).KillsTotal);
            Assert.Equal(0, (await CorporationStatsAsync(store, source, LaterCorporation, day)).KillsTotal);
        }
        finally
        {
            if (host is not null)
                await host.ShutdownAsync();
        }
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static async Task<long> AddCharacterAsync(NpgsqlDataSource source, PgCharacterStore characters, long corporation, long faction, string name)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await characters.InsertAsync(connection, transaction,
            new NewCharacter(1, $"{name} {Guid.NewGuid():N}", 0, 1, 1, 1, 11, 1376, corporation, null,
                60_000_610, 30_000_142, 20_000_001, 10_000_001, null, null));
        await characters.SetFactionWarfareStateAsync(connection, transaction, id, faction, 0);
        await transaction.CommitAsync();
        return id;
    }

    private static async Task SetCharacterFactionAsync(NpgsqlDataSource source, PgCharacterStore characters, long characterId, long factionId)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await characters.SetFactionWarfareStateAsync(connection, transaction, characterId, factionId, 0);
        await transaction.CommitAsync();
    }

    private static async Task PumpAsync(EventBus events)
    {
        for (var attempt = 0; attempt < 20; attempt++)
            if ((await events.PumpAsync()).Delivered == 0)
                break;
    }

    private static async Task<FwStatsWindow> PilotStatsAsync(FwStore store, NpgsqlDataSource source, long characterId, int day)
    {
        await using var connection = await source.OpenConnectionAsync();
        return await store.PilotStatsAsync(connection, null, characterId, day);
    }

    private static async Task<FwStatsWindow> CorporationStatsAsync(FwStore store, NpgsqlDataSource source, long corporationId, int day)
    {
        await using var connection = await source.OpenConnectionAsync();
        return await store.CorporationStatsAsync(connection, null, corporationId, day);
    }
}
