using Eve.Data;
using Eve.Scheduler;
using Eve.Universe;
using Npgsql;

namespace Eve.Wormholes.Tests;

/// <summary>A fact that needs PostgreSQL (<c>EVE_PG_URL</c>); skipped, not passed, without it.</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set"; }
}

/// <summary>A fact that needs PostgreSQL and the V24.01 client data (<c>EVE_CLIENT_SDE_DIR</c>); skipped without either.</summary>
public sealed class RealDataFactAttribute : FactAttribute
{
    public RealDataFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EVE_CLIENT_SDE_DIR"))) Skip = "EVE_CLIENT_SDE_DIR is not set";
    }
}

/// <summary>
/// A world in its own scratch schemas (wormhole and scheduler), dropped at the end. Restart() makes a new scheduler and store on the
/// same schemas, as after a restart: the durable jobs must still run.
/// </summary>
internal sealed class Rig : IAsyncDisposable
{
    private readonly string _wormholeSchema;
    private readonly string _schedulerSchema;
    private readonly IReadOnlyDictionary<int, WormholeType> _types;
    private readonly IReadOnlyDictionary<int, int> _classes;

    public NpgsqlDataSource Source { get; }
    public long Now { get; set; } = 1_000_000;
    public DurableScheduler Scheduler { get; private set; } = null!;
    public WormholeStore Store { get; private set; } = null!;
    public WormholeJobs Jobs { get; private set; } = null!;

    private Rig(NpgsqlDataSource source, string suffix, IReadOnlyDictionary<int, WormholeType> types, IReadOnlyDictionary<int, int> classes)
    {
        Source = source;
        _wormholeSchema = "wh_" + suffix;
        _schedulerSchema = "sch_" + suffix;
        _types = types;
        _classes = classes;
        Start();
    }

    public static async Task<Rig> CreateAsync(IReadOnlyDictionary<int, WormholeType> types, IReadOnlyDictionary<int, int> classes)
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        await WormholeStore.MigrateAsync(source, "wh_" + suffix);
        await DurableScheduler.MigrateAsync(source, "sch_" + suffix);
        return new Rig(source, suffix, types, classes);
    }

    /// <summary>A new scheduler and store on the same schemas, as after a restart.</summary>
    public void Restart() => Start();

    private void Start()
    {
        Scheduler = new DurableScheduler(Source, () => Now, new SchedulerOptions { Concurrency = 1 }, _schedulerSchema);
        Store = new WormholeStore(Source, _wormholeSchema);
        Jobs = new WormholeJobs(Scheduler, Store, _types, _classes);
        Jobs.Register();
    }

    /// <summary>The active connections leaving a system.</summary>
    public async Task<IReadOnlyList<StoredConnection>> ActiveFromAsync(int systemId)
    {
        await using var connection = await Source.OpenConnectionAsync();
        return await Store.ActiveFromAsync(connection, null, systemId);
    }

    /// <summary>Opens one connection in its own transaction and returns its ID.</summary>
    public async Task<long> OpenAsync(PlannedConnection planned, bool isStatic, long? nowMs = null)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await Jobs.OpenAsync(connection, transaction, planned, isStatic, nowMs ?? Now);
        await transaction.CommitAsync();
        return id;
    }

    /// <summary>One transit in its own transaction.</summary>
    public async Task<TransitResult> TransitAsync(long connectionId, double shipMassKg)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await Jobs.TransitAsync(connection, transaction, connectionId, shipMassKg, Now);
        await transaction.CommitAsync();
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await using (var connection = await Source.OpenConnectionAsync())
        {
            foreach (var schema in new[] { _wormholeSchema, _schedulerSchema })
            {
                await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA IF EXISTS {schema} CASCADE", CancellationToken.None);
            }
        }
        await Source.DisposeAsync();
    }
}
