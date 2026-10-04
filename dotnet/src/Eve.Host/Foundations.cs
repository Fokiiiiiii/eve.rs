using Eve.Content;
using Eve.Data;
using Eve.Events;
using Eve.ReadSide;
using Eve.Relations;
using Eve.Scheduler;
using Npgsql;

/// <summary>
/// The shared foundations every feature builds on (see <c>docs/FOUNDATIONS.md</c>): the durable scheduler, the outbox event bus, the relation store, the content runtime and the
/// rowset read side, each in a PostgreSQL schema of its own. A feature takes the ones it needs by constructor; it does not start a timer, keep an event list or a table of its running
/// content for itself. Create them, register handlers and subscribers, then <see cref="StartAsync"/>.
/// </summary>
internal sealed class Foundations : IAsyncDisposable
{
    private Foundations(DurableScheduler scheduler, EventBus events, RelationStore relations, ContentRuntime content, RowsetReaderHolder reads)
    {
        Scheduler = scheduler;
        Events = events;
        Relations = relations;
        Content = content;
        Reads = reads.Reader;
    }

    public DurableScheduler Scheduler { get; }
    public EventBus Events { get; }
    public RelationStore Relations { get; }
    public ContentRuntime Content { get; }
    public Eve.ReadSide.ReadSide Reads { get; }

    /// <summary>Only to name the read side's type once (the type and its namespace share a name).</summary>
    internal sealed record RowsetReaderHolder(Eve.ReadSide.ReadSide Reader);

    /// <summary>Migrates the schemas and builds the foundations. Nothing runs yet.</summary>
    public static async Task<Foundations> CreateAsync(NpgsqlDataSource source, Func<long> now, Func<string, string, string> env, Action<string> log)
    {
        var schedulerSchema = env("EVE_SCHEDULER_SCHEMA", DurableScheduler.DefaultSchema);
        var eventsSchema = env("EVE_EVENTS_SCHEMA", EventBus.DefaultSchema);
        var relationsSchema = env("EVE_RELATIONS_SCHEMA", RelationStore.DefaultSchema);
        var contentSchema = env("EVE_CONTENT_SCHEMA", ContentRuntime.DefaultSchema);
        await DurableScheduler.MigrateAsync(source, schedulerSchema);
        await EventBus.MigrateAsync(source, eventsSchema);
        await RelationStore.MigrateAsync(source, relationsSchema);
        await ContentRuntime.MigrateAsync(source, contentSchema);
        var scheduler = new DurableScheduler(source, now, null, schedulerSchema, message => log($"scheduler: {message}"));
        var events = new EventBus(source, scheduler, null, eventsSchema, message => log($"events: {message}"));
        var relations = new RelationStore(source, scheduler, events, relationsSchema);
        var content = new ContentRuntime(source, scheduler, events, contentSchema);
        log($"foundations in PostgreSQL, schemas {schedulerSchema}, {eventsSchema}, {relationsSchema} and {contentSchema}");
        return new Foundations(scheduler, events, relations, content, new RowsetReaderHolder(new Eve.ReadSide.ReadSide(source, new Dictionary<string, string> { ["relations"] = relationsSchema })));
    }

    /// <summary>
    /// Starts delivering: subscribes the content runtime to the events its definitions listen to, makes sure the event bus's own jobs exist, and starts the scheduler's runner. Call it once,
    /// after every feature has registered its job handlers, subscribers and content.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Content.AttachAsync(cancellationToken);
        await Events.StartAsync(cancellationToken);
        Scheduler.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await Events.StopAsync();
        await Scheduler.DisposeAsync();
    }
}
