using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>The site containers in PostgreSQL: each scratch schema of its own, a clock the test moves, and a bus that delivers what the win publishes.</summary>
internal sealed class SiteContainerRig : IAsyncDisposable
{
    public const int SolarSystemId = 30_000_142;

    private readonly string _id = Guid.NewGuid().ToString("N")[..12];

    private SiteContainerRig(NpgsqlDataSource source)
    {
        Source = source;
    }

    public NpgsqlDataSource Source { get; }
    public long Now { get; set; } = 1_700_006_400_000;
    public List<long> Removed { get; } = [];
    public List<(long SolarSystemId, long Site, long Container)> Cleared { get; } = [];
    public DurableScheduler Scheduler { get; private set; } = null!;
    public EventBus Bus { get; private set; } = null!;
    public string SitesSchema => "sitc_" + _id;

    public static async Task<SiteContainerRig> CreateAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var rig = new SiteContainerRig(source);
        await DurableScheduler.MigrateAsync(source, "sch_" + rig._id);
        await EventBus.MigrateAsync(source, "evt_" + rig._id);
        await SiteContainerStore.MigrateAsync(source, rig.SitesSchema);
        rig.Start();
        // the first pump makes the subscriber start now (events published before it are not delivered)
        await rig.Bus.PumpAsync();
        return rig;
    }

    /// <summary>A new process on the same database: a new scheduler and bus; the rows stay.</summary>
    public void Start()
    {
        Scheduler = new DurableScheduler(Source, () => Now, new SchedulerOptions { Concurrency = 1 }, "sch_" + _id, _ => { });
        Bus = new EventBus(Source, Scheduler, new EventBusOptions { MaxAttempts = 3 }, "evt_" + _id, _ => { });
        Bus.Subscribe("test.site-container", [SiteContainerTopics.Cleared], (context, _) =>
        {
            var payload = context.Event.Json();
            Cleared.Add((payload.GetProperty("solarSystemId").GetInt64(), payload.GetProperty("site").GetInt64(), payload.GetProperty("container").GetInt64()));
            return Task.CompletedTask;
        });
    }

    public SiteContainerStore Store => new(Source, SitesSchema);

    public SiteContainerHacks Hacks() => new(Source, Store, Bus, () => Now, id => Removed.Add(id));

    public async Task InsertAsync(params SiteContainerRecord[] records)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Store.InsertAsync(connection, transaction, records);
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync() => await Source.DisposeAsync();
}

public sealed class SiteContainerTests
{
    private const long Site = 5_300_000_000_500L;
    private const long Container = 7_000_000_000_000_501L;

    private static SiteContainerRecord Secure(long expiresMs, long container = Container, bool relic = false) => new(
        container, Site, SiteContainerRig.SolarSystemId, relic ? "research_laboratory" : "containment_facility", relic, 17, 306,
        [1_000, 2_000, 3_000], 50, 4, 0, "secure", expiresMs);

    [PgFact]
    public async Task A_container_is_hackable_until_two_failures_then_it_is_destroyed_and_leaves_the_scene()
    {
        await using var rig = await SiteContainerRig.CreateAsync();
        await rig.InsertAsync(Secure(rig.Now + 86_400_000));
        var hacks = rig.Hacks();

        var target = await hacks.FindAsync(Container, CancellationToken.None);
        Assert.NotNull(target);
        Assert.Equal((Container, SiteContainerRig.SolarSystemId, 4, false), (target.ItemId, target.SolarSystemId, target.Difficulty, target.Relic));

        await hacks.EndedAsync(target, won: false, CancellationToken.None);
        Assert.Equal(1, (await rig.Store.GetAsync(Container))!.Failures);
        Assert.Equal("secure", (await rig.Store.GetAsync(Container))!.State);
        Assert.Empty(rig.Removed);

        await hacks.EndedAsync(target, won: false, CancellationToken.None);
        Assert.Equal("destroyed", (await rig.Store.GetAsync(Container))!.State);
        Assert.Equal([Container], rig.Removed);
        Assert.Null(await hacks.FindAsync(Container, CancellationToken.None));
    }

    [PgFact]
    public async Task The_failure_count_survives_a_restart()
    {
        await using var rig = await SiteContainerRig.CreateAsync();
        await rig.InsertAsync(Secure(rig.Now + 86_400_000));
        var target = (await rig.Hacks().FindAsync(Container, CancellationToken.None))!;
        await rig.Hacks().EndedAsync(target, won: false, CancellationToken.None);

        // a new process: a new bus, scheduler and hacks, on the same rows
        rig.Start();
        Assert.Equal(1, (await rig.Store.GetAsync(Container))!.Failures);
        await rig.Hacks().EndedAsync(target, won: false, CancellationToken.None);

        Assert.Equal("destroyed", (await rig.Store.GetAsync(Container))!.State);
        Assert.Equal([Container], rig.Removed);
    }

    [PgFact]
    public async Task A_win_clears_the_container_and_announces_it_with_its_site_for_the_site_objective()
    {
        await using var rig = await SiteContainerRig.CreateAsync();
        await rig.InsertAsync(Secure(rig.Now + 86_400_000, relic: true));
        var target = (await rig.Hacks().FindAsync(Container, CancellationToken.None))!;
        Assert.True(target.Relic);

        await rig.Hacks().EndedAsync(target, won: true, CancellationToken.None);
        await rig.Bus.PumpAsync();

        Assert.Equal("cleared", (await rig.Store.GetAsync(Container))!.State);
        Assert.Equal([(SiteContainerRig.SolarSystemId, Site, Container)], rig.Cleared);
        Assert.Null(await rig.Hacks().FindAsync(Container, CancellationToken.None));
    }

    [PgFact]
    public async Task A_container_of_a_site_that_has_ended_is_not_hackable()
    {
        await using var rig = await SiteContainerRig.CreateAsync();
        await rig.InsertAsync(Secure(rig.Now - 1));

        Assert.Null(await rig.Hacks().FindAsync(Container, CancellationToken.None));
    }
}
