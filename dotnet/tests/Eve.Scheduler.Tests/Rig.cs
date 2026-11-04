using Eve.Data;
using Npgsql;

namespace Eve.Scheduler.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set"; }
}

/// <summary>A scratch schema with a scheduler on a clock the test moves.</summary>
internal sealed class Rig : IAsyncDisposable
{
    private Rig(NpgsqlDataSource source, string schema, SchedulerOptions options)
    {
        Source = source;
        Schema = schema;
        Options = options;
        Scheduler = Make();
    }

    public NpgsqlDataSource Source { get; }
    public string Schema { get; }
    public SchedulerOptions Options { get; }
    public DurableScheduler Scheduler { get; }
    public long Now { get; set; } = 1_000_000;
    public List<string> Log { get; } = [];

    /// <summary>Another runner on the same jobs (another process, as far as the database can tell).</summary>
    public DurableScheduler Make() => new(Source, () => Now, Options, Schema, Log.Add);

    public static async Task<Rig> Create(SchedulerOptions? options = null)
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "sched_" + Guid.NewGuid().ToString("N")[..12];
        await DurableScheduler.MigrateAsync(source, schema);
        return new Rig(source, schema, options ?? new SchedulerOptions { Concurrency = 1, BatchSize = 8 });
    }

    public async Task<long> Count(string where = "true")
    {
        await using var connection = await Source.OpenConnectionAsync();
        return (long)(await Sql.ScalarAsync(connection, null, $"SELECT count(*) FROM {Schema}.jobs WHERE {where}", CancellationToken.None))!;
    }

    public async ValueTask DisposeAsync()
    {
        await Scheduler.StopAsync();
        await using (var connection = await Source.OpenConnectionAsync())
        {
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {Schema} CASCADE", CancellationToken.None);
        }
        await Source.DisposeAsync();
    }
}
