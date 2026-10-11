using System.Text.Json;
using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Structures.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set"; }
}

internal sealed class FakeRules : IStructureRulesSource
{
    public Dictionary<int, StructureRules> ByType { get; } = [];
    public StructureRules Default { get; set; } = StructureRules.Of(StructureSize.Medium, SecurityBand.HighSec);
    public StructureRules RulesOf(int typeId, int solarSystemId) => ByType.GetValueOrDefault(typeId, Default);
}

/// <summary>Scratch schemas for the scheduler, the bus and the structures, a clock the test moves, and a "process" that can be replaced to test a restart.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public const int Medium = 35832;

    private Rig(NpgsqlDataSource source, string id)
    {
        Source = source;
        SchedulerSchema = "sch_" + id;
        EventsSchema = "evt_" + id;
        StructuresSchema = "str_" + id;
        Store = new StructureStore(source, StructuresSchema);
        Start();
    }

    public NpgsqlDataSource Source { get; }
    public string SchedulerSchema { get; }
    public string EventsSchema { get; }
    public string StructuresSchema { get; }
    public StructureStore Store { get; }
    public FakeRules Rules { get; } = new();
    public long Now { get; set; } = 1_700_006_400_000;
    public List<string> Log { get; } = [];
    public List<StructureStateChanged> Changes { get; } = [];
    public DurableScheduler Scheduler { get; private set; } = null!;
    public EventBus Bus { get; private set; } = null!;
    public StructureTimers Timers { get; private set; } = null!;

    /// <summary>Every roll the timers draw; a test sets <see cref="Roll"/> to place exits.</summary>
    public double Roll { get; set; } = 0.5;

    public static async Task<Rig> Create()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var id = Guid.NewGuid().ToString("N")[..12];
        await DurableScheduler.MigrateAsync(source, "sch_" + id);
        await EventBus.MigrateAsync(source, "evt_" + id);
        await StructureStore.MigrateAsync(source, "str_" + id);
        var rig = new Rig(source, id);
        // the first pump makes the subscriber start "now": events published before it would be skipped
        await rig.Bus.PumpAsync();
        return rig;
    }

    /// <summary>A new process on the same database: a new scheduler, bus and timers (the old ones are dropped, their jobs and events stay).</summary>
    public void Start()
    {
        Scheduler = new DurableScheduler(Source, () => Now, new SchedulerOptions { Concurrency = 1 }, SchedulerSchema, Log.Add);
        Bus = new EventBus(Source, Scheduler, new EventBusOptions { MaxAttempts = 3 }, EventsSchema, Log.Add);
        Timers = new StructureTimers(Store, Scheduler, Bus, Rules, () => Roll);
        Timers.Register();
        Bus.Subscribe("test.collector", [StructureTimers.StateChangedTopic], (context, _) =>
        {
            Changes.Add(context.Event.Payload<StructureStateChanged>()!);
            return Task.CompletedTask;
        });
    }

    public async Task Restart()
    {
        await Scheduler.StopAsync();
        Start();
        await Bus.PumpAsync();
    }

    /// <summary>Runs every job that is due on the clock and delivers the events they published.</summary>
    public async Task Tick()
    {
        await Scheduler.RunDueAsync();
        await Bus.PumpAsync();
    }

    public async Task<T> Commit<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await work(connection, transaction);
        await transaction.CommitAsync();
        return result;
    }

    public async Task<StructureTransition?> Apply(long id, StructureEvent e) => await Commit((c, t) => Timers.ApplyAsync(c, t, id, e));

    public async Task<StructureTransition?> Anchor(long id, int typeId = Medium, int weekday = 5, int hour = 18) =>
        await Commit((c, t) => Timers.AnchorAsync(c, t, new NewStructure(id, typeId, 30_000_142, 1, 2, 3, 0.5, 98_000_001, 99_000_001, "Test " + id, "", ReinforceWeekday: weekday, ReinforceHour: hour)));

    public async Task<StructureSnapshot> Snapshot(long id) => (await Store.GetAsync(id))!.Snapshot;

    public async Task<JobInfo?> Job(long id) => await Scheduler.FindAsync(StructureTimers.JobKind, $"structure:{id}");

    public async Task<long> EventCount()
    {
        await using var connection = await Source.OpenConnectionAsync();
        return (long)(await Sql.ScalarAsync(connection, null, $"SELECT count(*) FROM {EventsSchema}.outbox WHERE topic = 'structure.state_changed'", CancellationToken.None))!;
    }

    /// <summary>Walks a freshly anchored structure to a working one: anchoring, onlining with a core and one service.</summary>
    public async Task<long> Working(long id, int typeId = Medium)
    {
        await Anchor(id, typeId);
        Now += StructureTimes.RepairMs;
        await Tick();
        Now += StructureTimes.AnchoringMs;
        await Tick();
        await Apply(id, new CoreInserted());
        Now += StructureTimes.RepairMs;
        await Tick();
        await Commit((c, t) => Timers.SetServiceAsync(c, t, id, 1, true));
        return id;
    }

    public async ValueTask DisposeAsync()
    {
        await Scheduler.StopAsync();
        await using (var connection = await Source.OpenConnectionAsync())
        {
            foreach (var schema in new[] { SchedulerSchema, EventsSchema, StructuresSchema })
            {
                await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
            }
        }
        await Source.DisposeAsync();
    }
}
