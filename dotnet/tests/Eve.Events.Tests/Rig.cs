using Eve.Data;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Events.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set"; }
}

/// <summary>Scratch schemas with a scheduler and a bus on a clock the test moves.</summary>
internal sealed class Rig : IAsyncDisposable
{
    private Rig(NpgsqlDataSource source, string id, EventBusOptions options)
    {
        Source = source;
        SchedulerSchema = "sch_" + id;
        EventsSchema = "evt_" + id;
        Options = options;
        (Scheduler, Bus) = Make();
    }

    public NpgsqlDataSource Source { get; }
    public string SchedulerSchema { get; }
    public string EventsSchema { get; }
    public EventBusOptions Options { get; }
    public DurableScheduler Scheduler { get; }
    public EventBus Bus { get; }
    public long Now { get; set; } = 1_000_000;
    public List<string> Log { get; } = [];

    public (DurableScheduler, EventBus) Make()
    {
        var scheduler = new DurableScheduler(Source, () => Now, new SchedulerOptions { Concurrency = 1 }, SchedulerSchema, Log.Add);
        return (scheduler, new EventBus(Source, scheduler, Options, EventsSchema, Log.Add));
    }

    public static async Task<Rig> Create(EventBusOptions? options = null)
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var id = Guid.NewGuid().ToString("N")[..12];
        var rig = new Rig(source, id, options ?? new EventBusOptions { MaxAttempts = 3 });
        await DurableScheduler.MigrateAsync(source, rig.SchedulerSchema);
        await EventBus.MigrateAsync(source, rig.EventsSchema);
        await using var connection = await source.OpenConnectionAsync();
        await Sql.ExecuteAsync(connection, null, $"CREATE TABLE {rig.EventsSchema}.effects (note text)", CancellationToken.None);
        return rig;
    }

    /// <summary>Runs <paramref name="work"/> in a transaction and commits it.</summary>
    public async Task Commit(Func<NpgsqlConnection, NpgsqlTransaction, Task> work)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await work(connection, transaction);
        await transaction.CommitAsync();
    }

    public Task Publish(string topic, object? payload = null, string? key = null) => Commit((c, t) => Bus.PublishAsync(c, t, topic, payload, key));

    public async Task<List<string>> Effects()
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var command = Sql.Command(connection, null, $"SELECT note FROM {EventsSchema}.effects ORDER BY ctid");
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<string>();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    public async Task<long> JobCount()
    {
        await using var connection = await Source.OpenConnectionAsync();
        return (long)(await Sql.ScalarAsync(connection, null, $"SELECT count(*) FROM {SchedulerSchema}.jobs", CancellationToken.None))!;
    }

    public async ValueTask DisposeAsync()
    {
        await Scheduler.StopAsync();
        await using (var connection = await Source.OpenConnectionAsync())
        {
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {EventsSchema} CASCADE", CancellationToken.None);
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {SchedulerSchema} CASCADE", CancellationToken.None);
        }
        await Source.DisposeAsync();
    }
}
