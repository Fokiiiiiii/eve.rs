using Eve.Data;
using Eve.Events;
using Eve.Inventory;
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
    public string ItemsSchema => "sitc_i_" + _id;

    public static async Task<SiteContainerRig> CreateAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var rig = new SiteContainerRig(source);
        await DurableScheduler.MigrateAsync(source, "sch_" + rig._id);
        await EventBus.MigrateAsync(source, "evt_" + rig._id);
        await SiteContainerStore.MigrateAsync(source, rig.SitesSchema);
        await PgItemStore.MigrateAsync(source, rig.ItemsSchema);
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
        // the container clears when its loot is taken: the move is announced in its transaction, and the container's hacks hear it
        Bus.Subscribe("test.site-container.emptied", [Eve.Inventory.PgItemStore.ItemMovedTopic], async (context, token) =>
        {
            if (context.Event.Json().TryGetProperty("previousLocationId", out var previous) && previous.TryGetInt64(out var previousLocationId))
                await Hacks().OnItemLeftAsync(context.Connection, context.Transaction, previousLocationId, token);
        });
        Bus.Subscribe("test.site-container", [SiteContainerTopics.Cleared], (context, _) =>
        {
            var payload = context.Event.Json();
            Cleared.Add((payload.GetProperty("solarSystemId").GetInt64(), payload.GetProperty("site").GetInt64(), payload.GetProperty("container").GetInt64()));
            return Task.CompletedTask;
        });
    }

    /// <summary>Pumps until nothing is delivered: an event a handler publishes is past the pump's horizon, so it is delivered by the next pump.</summary>
    public async Task DrainAsync()
    {
        while ((await Bus.PumpAsync()).Delivered > 0)
        {
        }
    }

    public SiteContainerStore Store => new(Source, SitesSchema);

    public SiteContainerHacks Hacks() => new(Source, Store, new PgItemStore(Source, ItemsSchema), Bus, () => Now, id => Removed.Add(id));

    public async Task InsertAsync(params SiteContainerRecord[] records)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Store.InsertAsync(connection, transaction, records);
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var schema in new[] { "sch_" + _id, "evt_" + _id, SitesSchema, ItemsSchema })
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

public sealed class SiteContainerTests
{
    private const long Site = 5_300_000_000_500L;
    private const long Container = 7_000_000_000_000_501L;
    private const long Character = 99_001;

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

        await hacks.EndedAsync(target, won: false, Character, CancellationToken.None);
        Assert.Equal(1, (await rig.Store.GetAsync(Container))!.Failures);
        Assert.Equal("secure", (await rig.Store.GetAsync(Container))!.State);
        Assert.Empty(rig.Removed);

        await hacks.EndedAsync(target, won: false, Character, CancellationToken.None);
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
        await rig.Hacks().EndedAsync(target, won: false, Character, CancellationToken.None);

        // a new process: a new bus, scheduler and hacks, on the same rows
        rig.Start();
        Assert.Equal(1, (await rig.Store.GetAsync(Container))!.Failures);
        await rig.Hacks().EndedAsync(target, won: false, Character, CancellationToken.None);

        Assert.Equal("destroyed", (await rig.Store.GetAsync(Container))!.State);
        Assert.Equal([Container], rig.Removed);
    }

    [PgFact]
    public async Task A_win_leaves_the_loot_in_the_container_and_the_container_is_not_yet_cleared()
    {
        await using var rig = await SiteContainerRig.CreateAsync();
        await rig.InsertAsync(Secure(rig.Now + 86_400_000, relic: true));
        var target = (await rig.Hacks().FindAsync(Container, CancellationToken.None))!;
        Assert.True(target.Relic);

        await rig.Hacks().EndedAsync(target, won: true, Character, CancellationToken.None);
        await rig.Bus.PumpAsync();

        Assert.Equal("looted", (await rig.Store.GetAsync(Container))!.State);
        Assert.Empty(rig.Cleared);
        Assert.Null(await rig.Hacks().FindAsync(Container, CancellationToken.None));

        var loot = await new PgItemStore(rig.Source, rig.ItemsSchema).InLocationAsync(Container, Character);
        Assert.NotEmpty(loot);
        Assert.All(loot, item => Assert.True(item.Quantity >= 1 && item.Flag == ItemFlags.Hangar));
    }

    [PgFact]
    public async Task The_last_loot_taken_clears_the_container_announces_it_with_its_site_and_removes_its_ball()
    {
        await using var rig = await SiteContainerRig.CreateAsync();
        await rig.InsertAsync(Secure(rig.Now + 86_400_000, relic: true));
        await rig.Hacks().EndedAsync((await rig.Hacks().FindAsync(Container, CancellationToken.None))!, won: true, Character, CancellationToken.None);
        await rig.Bus.PumpAsync();
        var items = new PgItemStore(rig.Source, rig.ItemsSchema) { Events = rig.Bus };
        var loot = (await items.InLocationAsync(Container, Character)).ToList();
        Assert.True(loot.Count >= 2, "the loot has more than one piece, so the first taking does not clear it");
        const long Station = 60_000_001;

        foreach (var (piece, index) in loot.Select((piece, index) => (piece, index)))
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await items.MoveAsync(connection, transaction, piece.Id, Station, ItemFlags.Hangar);
            await transaction.CommitAsync();
            await rig.DrainAsync();
            var last = index == loot.Count - 1;
            Assert.Equal(last ? "cleared" : "looted", (await rig.Store.GetAsync(Container))!.State);
        }

        Assert.Equal([(SiteContainerRig.SolarSystemId, Site, Container)], rig.Cleared);
        Assert.Equal([Container], rig.Removed);
    }

    [PgFact]
    public async Task A_container_of_a_site_that_has_ended_is_not_hackable()
    {
        await using var rig = await SiteContainerRig.CreateAsync();
        await rig.InsertAsync(Secure(rig.Now - 1));

        Assert.Null(await rig.Hacks().FindAsync(Container, CancellationToken.None));
    }
}

public sealed class SiteLootTests
{
    private static readonly Dictionary<string, int> AllNames = new(StringComparer.Ordinal)
    {
        ["Datacore - Electronic Engineering"] = 1, ["Accelerant Decryptor"] = 2, ["Esoteric Data Interface"] = 3,
        ["Burned Logic Circuit"] = 4, ["Alloyed Tritanium Bar"] = 5,
    };

    [Fact]
    public void A_roll_is_deterministic_for_a_seed_and_stays_within_the_profiles_bounds()
    {
        var first = SiteLoot.Roll(SiteLoot.GenericDataProfile, 77, AllNames);
        Assert.Equal(first, SiteLoot.Roll(SiteLoot.GenericDataProfile, 77, AllNames));
        foreach (var (typeId, quantity) in first)
        {
            Assert.Contains(typeId, AllNames.Values);
            Assert.True(quantity >= 1 && quantity <= 4 + 1 + 2, $"quantity {quantity}");
        }
    }

    [Fact]
    public void Only_the_candidates_the_static_data_has_are_rolled()
    {
        var rolled = SiteLoot.Roll(SiteLoot.GenericRelicProfile, 5, new Dictionary<string, int> { ["Burned Logic Circuit"] = 4 });
        Assert.Equal([(4, rolled[0].Quantity)], rolled);
        Assert.InRange(rolled[0].Quantity, 2, 7);
        Assert.Empty(SiteLoot.Roll(SiteLoot.GenericRelicProfile, 5, new Dictionary<string, int>()));
    }

    [Fact]
    public void A_relic_container_has_the_relic_profile_and_a_data_container_the_data_profile()
    {
        Assert.Equal(SiteLoot.GenericRelicProfile, SiteLoot.ProfileOf(relic: true));
        Assert.Equal(SiteLoot.GenericDataProfile, SiteLoot.ProfileOf(relic: false));
    }
}
