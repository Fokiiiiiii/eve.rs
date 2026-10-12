using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Relations.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set"; }
}

internal sealed class Rig : IAsyncDisposable
{
    private readonly string[] _schemas;

    private Rig(NpgsqlDataSource source, string[] schemas)
    {
        Source = source;
        _schemas = schemas;
        Scheduler = new DurableScheduler(source, () => Now, new SchedulerOptions { Concurrency = 1 }, schemas[0]);
        Bus = new EventBus(source, Scheduler, null, schemas[1]);
        Relations = new RelationStore(source, Scheduler, Bus, schemas[2]);
    }

    public NpgsqlDataSource Source { get; }
    public DurableScheduler Scheduler { get; }
    public EventBus Bus { get; }
    public RelationStore Relations { get; }
    public long Now { get; set; } = 1_000_000;

    public static async Task<Rig> Create()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var id = Guid.NewGuid().ToString("N")[..12];
        string[] schemas = ["sch_" + id, "evt_" + id, "rel_" + id];
        await DurableScheduler.MigrateAsync(source, schemas[0]);
        await EventBus.MigrateAsync(source, schemas[1]);
        await RelationStore.MigrateAsync(source, schemas[2]);
        return new Rig(source, schemas);
    }

    public async Task<T> Commit<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await work(connection, transaction);
        await transaction.CommitAsync();
        return result;
    }

    public Task<RelationChange?> Set(string kind, long subject, long obj, double value, string? reason = null, object? attrs = null, long? until = null) =>
        Commit((c, t) => Relations.SetAsync(c, t, kind, subject, obj, value, reason, attrs, until));

    public async ValueTask DisposeAsync()
    {
        await using (var connection = await Source.OpenConnectionAsync())
        {
            foreach (var schema in _schemas)
            {
                await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
            }
        }
        await Source.DisposeAsync();
    }
}
