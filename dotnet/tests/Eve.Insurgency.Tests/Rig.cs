using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Insurgency.Tests;

/// <summary>A fact that needs PostgreSQL: skipped (not passed) when EVE_PG_URL is not set.</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null)
            Skip = "EVE_PG_URL is not set";
    }
}

/// <summary>One campaign store with its own schemas, a scheduler and an event bus on a fake clock.</summary>
public sealed class Rig : IAsyncDisposable
{
    public const long Epoch = 1_700_006_400_000;
    public const long Hour = 3_600_000;

    private readonly string _id;

    private Rig(NpgsqlDataSource source, string id)
    {
        Source = source;
        _id = id;
        SchedulerSchema = "sch_" + id;
        EventsSchema = "evt_" + id;
        InsurgencySchema = "ins_" + id;
        Start();
    }

    public NpgsqlDataSource Source { get; }
    public string SchedulerSchema { get; }
    public string EventsSchema { get; }
    public string InsurgencySchema { get; }
    public long Now { get; set; } = Epoch;
    public DurableScheduler Scheduler { get; private set; } = null!;
    public EventBus Bus { get; private set; } = null!;
    public InsurgencyStore Store { get; private set; } = null!;
    public InsurgencyCampaigns Campaigns { get; private set; } = null!;

    public static async Task<Rig> CreateAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var id = Guid.NewGuid().ToString("N")[..12];
        await DurableScheduler.MigrateAsync(source, "sch_" + id);
        await EventBus.MigrateAsync(source, "evt_" + id);
        await InsurgencyStore.MigrateAsync(source, "ins_" + id);
        return new Rig(source, id);
    }

    /// <summary>A new process on the same database: a new scheduler, bus and campaign service (the old ones are dropped; their rows stay).</summary>
    public void Start()
    {
        Scheduler = new DurableScheduler(Source, () => Now, schema: SchedulerSchema);
        Bus = new EventBus(Source, Scheduler, schema: EventsSchema);
        Store = new InsurgencyStore(Source, InsurgencySchema);
        Campaigns = new InsurgencyCampaigns(Store, Scheduler, Bus);
        Campaigns.Register();
    }

    public async Task<T> Commit<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await work(connection, transaction);
        await transaction.CommitAsync();
        return result;
    }

    /// <summary>Moves the clock to <paramref name="at"/> and runs the jobs that are due then.</summary>
    public async Task RunAt(long at)
    {
        Now = at;
        await Scheduler.RunDueAsync();
    }

    public async Task<Campaign?> Get(long campaignId)
    {
        await using var connection = await Source.OpenConnectionAsync();
        return await Store.GetAsync(connection, null, campaignId, false);
    }

    public async Task<JobInfo?> Job(long campaignId) => await Scheduler.FindAsync(InsurgencyCampaigns.JobKind, $"campaign:{campaignId}");

    public async ValueTask DisposeAsync()
    {
        await Scheduler.StopAsync();
        foreach (var schema in new[] { SchedulerSchema, EventsSchema, InsurgencySchema })
        {
            try
            {
                await using var drop = Source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
            catch (NpgsqlException)
            {
                // Leaving a scratch schema behind is better than failing the test.
            }
        }
        await Source.DisposeAsync();
    }
}
