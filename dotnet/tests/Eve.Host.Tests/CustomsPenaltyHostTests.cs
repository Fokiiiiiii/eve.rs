using Eve.Characters;
using Eve.Data;
using Eve.Identity;
using Eve.Inventory;
using Eve.Scheduler;
using Eve.Services.Space;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// The customs penalty through the real host composition (ST-18): the host registers the contraband.penalty-settle job with its own scheduler and runs it. A penalty
/// scheduled on that scheduler is settled by the host's runner once: the contraband leaves the hold, the standing toward the faction falls, and the record is written.
/// </summary>
[Collection("host-composition")]
public sealed class CustomsPenaltyHostTests : IAsyncLifetime
{
    private const long Pilot = 96_000_001;
    private const int SystemId = 30_000_142, Faction = 500_003, ShipType = 96_000_010, ContrabandType = 96_000_011;

    /// <summary>The environment the host reads: every schema on its own scratch name, plus the no-simulation switch.</summary>
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA",
        "EVE_TRADE_SCHEMA", "EVE_BOOKMARKS_SCHEMA", "EVE_ESS_SCHEMA", "EVE_CONTRABAND_SCHEMA",
    ];

    private readonly string _prefix = "cp" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    private string Schema(string variable) => $"{_prefix}_{variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant()}";

    private static string Url => Environment.GetEnvironmentVariable("EVE_PG_URL")!;

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

    [PgFact]
    public async Task A_penalty_scheduled_on_the_host_scheduler_is_settled_once_by_the_host_runner()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));

        var host = await HostComposition.BuildAsync(Settings());
        var ship = (await CreateItemsAsync(source, items, new NewItem(ShipType, Pilot, SystemId, 0, Singleton: true)))[0];
        var cargo = (await CreateItemsAsync(source, items, new NewItem(ContrabandType, Pilot, ship, ItemFlags.Cargo)))[0];

        // the inspection's settlement, scheduled on the shared scheduler (its own process would do the same): one contraband item, fined and standing lost
        var scheduler = new DurableScheduler(source, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, Schema("EVE_SCHEDULER_SCHEMA"));
        var operation = Guid.NewGuid().ToString("N");
        await scheduler.ScheduleAsync(new JobSpec(UndockContraband.SettleJobKind, scheduler.Now)
        {
            Key = "contraband-penalty:" + operation,
            Payload = new ContrabandSettlement(operation, Pilot, ship, SystemId, Faction,
                [new ContrabandSettlementLine(cargo, ContrabandType, 1, 10m, 0.02)]),
        });

        // the host's runner (started with the host) claims it when due: a bounded wait for its commit
        var settled = 0L;
        for (var attempt = 0; attempt < 100 && settled == 0; attempt++)
        {
            await Task.Delay(100);
            settled = await ScalarAsync<long>(source, $"SELECT count(*) FROM {Schema("EVE_CONTRABAND_SCHEMA")}.contraband_penalties WHERE operation_id = '{operation}'");
        }
        await host.ShutdownAsync();

        Assert.Equal(1L, settled);
        Assert.Null(await items.GetAsync(cargo));
        Assert.Equal(-0.2, await ScalarAsync<double>(source,
            $"SELECT value FROM {Schema("EVE_RELATIONS_SCHEMA")}.relations WHERE kind = 'standing' AND subject = {Pilot} AND object = {Faction}"));
        Assert.Equal(0L, await ScalarAsync<long>(source, $"SELECT count(*) FROM {Schema("EVE_SCHEDULER_SCHEMA")}.jobs WHERE key = 'contraband-penalty:{operation}'"));
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long[]> CreateItemsAsync(NpgsqlDataSource source, PgItemStore items, params NewItem[] newItems)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var ids = new long[newItems.Length];
        for (var index = 0; index < newItems.Length; index++)
        {
            ids[index] = await items.CreateAsync(connection, transaction, newItems[index]);
        }
        await transaction.CommitAsync();
        return ids;
    }
}
