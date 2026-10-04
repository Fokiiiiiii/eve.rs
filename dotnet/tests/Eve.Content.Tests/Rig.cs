using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Content.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set"; }
}

internal sealed class Rig : IAsyncDisposable
{
    private readonly string[] _schemas;
    public string SchedulerSchema => _schemas[0];
    public string EventsSchema => _schemas[1];
    public string ContentSchema => _schemas[2];

    private Rig(NpgsqlDataSource source, string[] schemas)
    {
        Source = source;
        _schemas = schemas;
        Scheduler = new DurableScheduler(source, () => Now, new SchedulerOptions { Concurrency = 1 }, schemas[0]);
        Bus = new EventBus(source, Scheduler, null, schemas[1]);
        Content = new ContentRuntime(source, Scheduler, Bus, schemas[2]);
    }

    public NpgsqlDataSource Source { get; }
    public DurableScheduler Scheduler { get; }
    public EventBus Bus { get; }
    public ContentRuntime Content { get; }
    public long Now { get; set; } = 1_000_000;
    public List<string> Seen { get; } = [];

    public static async Task<Rig> Create()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var id = Guid.NewGuid().ToString("N")[..12];
        string[] schemas = ["sch_" + id, "evt_" + id, "cnt_" + id];
        await DurableScheduler.MigrateAsync(source, schemas[0]);
        await EventBus.MigrateAsync(source, schemas[1]);
        await ContentRuntime.MigrateAsync(source, schemas[2]);
        return new Rig(source, schemas);
    }

    /// <summary>Attaches the runtime, records every <c>content.*</c> event in <see cref="Seen"/>, and makes the subscribers start reading from here.</summary>
    public async Task Ready()
    {
        await Content.AttachAsync();
        Bus.Subscribe("test.watch", ["content.*"], (ctx, _) => { Seen.Add(ctx.Event.Topic); return Task.CompletedTask; });
        await Bus.PumpAsync();
    }

    /// <summary>Delivers events until nothing is left (a handler's own events included).</summary>
    public async Task Drain()
    {
        for (var i = 0; i < 20 && (await Bus.PumpAsync()).Delivered > 0; i++)
        {
        }
    }

    public async Task<T> Commit<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await work(connection, transaction);
        await transaction.CommitAsync();
        return result;
    }

    public Task<StartResult> Start(string definition, long owner, object? data = null, string? uniqueKey = null) =>
        Commit((c, t) => Content.StartAsync(c, t, definition, owner, data, uniqueKey));

    public async Task Publish(string topic, object payload)
    {
        await Bus.PublishAsync(topic, payload);
        await Drain();
    }

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
