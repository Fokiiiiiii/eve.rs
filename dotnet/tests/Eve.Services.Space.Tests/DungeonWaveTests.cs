using Eve.Content;
using Eve.Data;
using Eve.Dungeons;
using Eve.Events;
using Eve.Npc;
using Eve.Scheduler;
using Eve.Services.Space.Waves;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>The spawn queries of the catalog against the NPC authority: a query is an exact pool or profile name or it is refused, never guessed.</summary>
public sealed class WaveCompositionTests
{
    private static readonly NpcSelector Selector = new(NpcAuthority.Load());

    [Theory]
    [InlineData("npc_deadspace_hostiles")]
    [InlineData("sanshas_deadspace_pve")]
    [InlineData("npc_hostiles")]
    public void A_pool_named_exactly_resolves_to_its_selection(string query)
    {
        var (selection, refusal) = WaveComposition.Resolve(Selector, query);

        Assert.Null(refusal);
        Assert.Equal(NpcSelection.PoolKind, selection!.Kind);
        Assert.NotEmpty(selection.Entries);
    }

    [Theory]
    [InlineData("parity_drifter_dungeon_family")]
    [InlineData("no such rat at all")]
    [InlineData("dead")]
    public void A_name_that_is_no_pool_or_profile_exactly_is_refused_with_the_name(string query)
    {
        var (selection, refusal) = WaveComposition.Resolve(Selector, query);

        Assert.Null(selection);
        Assert.Contains($"'{query}'", refusal);
    }

    [Fact]
    public void The_catalog_spawn_queries_resolve_exactly_or_are_listed_as_unresolved()
    {
        var catalog = DungeonCatalog.LoadEmbedded();
        var queries = catalog.Dungeons.SelectMany(dungeon => dungeon.Waves).Select(wave => wave.SpawnQuery ?? "").Distinct().ToArray();

        var unresolved = queries.Where(query => WaveComposition.Resolve(Selector, query).Refusal is not null).OrderBy(query => query).ToArray();

        Assert.Equal(["parity_drifter_dungeon_family"], unresolved);
        Assert.Contains("npc_deadspace_hostiles", queries);
    }
}

/// <summary>
/// The dungeon waves over PostgreSQL with a clock the test moves and an NPC spawner the test plays: the waves in order, the triggers, the kills, the restart, the pilot who leaves
/// and the expiry. What the spawner would make is recorded in order, so a test asserts the counts, the order and that each wave came once.
/// </summary>
public sealed class DungeonWaveTests
{
    private const int SystemId = 30_000_001;
    private const long Pilot = 1001;
    private static readonly double[] Origin = [0, 0, 0];
    private static readonly Lazy<DungeonCatalog> Catalog = new(DungeonCatalog.LoadEmbedded);
    private static readonly Lazy<WaveRules> Rules = new(WaveRules.LoadEmbedded);

    [PgFact]
    public async Task The_Breeding_Facility_timer_scenario_spawns_each_wave_once_in_order_and_completes_once()
    {
        await using var rig = await WaveRig.CreateAsync();
        var start = await rig.Director.StartAsync(Pilot, SystemId, Origin, 2158);
        Assert.True(start.Started, start.Refusal);

        await rig.RunDueAndDeliverAsync();
        Assert.Empty(rig.Spawner.Batches);   // the timer of the first wave has not run out
        rig.Advance(TimeSpan.FromSeconds(239));
        await rig.RunDueAndDeliverAsync();
        Assert.Empty(rig.Spawner.Batches);

        rig.Advance(TimeSpan.FromSeconds(1));
        await rig.RunDueAndDeliverAsync();
        await rig.RunDueAndDeliverAsync();   // a second run of the due jobs makes nothing more
        Assert.Equal(new[] { 7 }, rig.Spawner.Batches.Select(batch => batch.Amount).ToArray());

        for (var wave = 1; wave <= 4; wave++)
        {
            var ships = rig.Spawner.Batches[wave - 1].Ships;
            await rig.KillAsync(ships.Take(ships.Count - 1));
            Assert.Equal(wave, rig.Spawner.Batches.Count);   // all but one killed: the wave is not cleared
            Assert.Empty(rig.Completions);
            await rig.KillAsync([ships[^1]]);
            if (wave < 4)
            {
                Assert.Equal(wave + 1, rig.Spawner.Batches.Count);
            }
        }

        Assert.Equal(new[] { 7, 8, 9, 10 }, rig.Spawner.Batches.Select(batch => batch.Amount).ToArray());
        Assert.Equal(new[] { "content.dungeon.completed" }, rig.Completions.Select(completion => completion.Topic).ToArray());
        Assert.Empty(rig.Spawner.Removed);
    }

    [PgFact]
    public async Task A_kill_count_wave_is_cleared_by_its_last_npc_once_and_a_repeated_or_foreign_kill_counts_nothing()
    {
        await using var rig = await WaveRig.CreateAsync();
        await rig.Director.StartAsync(Pilot, SystemId, Origin, 2173);
        await rig.RunDueAndDeliverAsync();
        var ships = Assert.Single(rig.Spawner.Batches).Ships;
        Assert.Equal(8, ships.Count);

        await rig.KillAsync(ships.Take(7));
        Assert.Empty(rig.Completions);
        await rig.KillAsync([ships[0], 4_299_999_999_999_999]);   // the same ship again, and an NPC of nobody's dungeon
        Assert.Empty(rig.Completions);

        await rig.KillAsync([ships[7]]);
        await rig.KillAsync([ships[7]]);
        Assert.Equal(new[] { "content.dungeon.completed" }, rig.Completions.Select(completion => completion.Topic).ToArray());
        Assert.Single(rig.Spawner.Batches);
    }

    [PgFact]
    public async Task A_restart_keeps_the_wave_in_progress_and_does_not_spawn_it_again()
    {
        await using var rig = await WaveRig.CreateAsync();
        await rig.Director.StartAsync(Pilot, SystemId, Origin, 2158);
        rig.Advance(TimeSpan.FromSeconds(240));
        await rig.RunDueAndDeliverAsync();
        var first = Assert.Single(rig.Spawner.Batches).Ships;

        await rig.RestartAsync();
        await rig.RunDueAndDeliverAsync();
        Assert.Single(rig.Spawner.Batches);   // the first wave came once, before the restart

        await rig.KillAsync(first.Take(3));
        await rig.RestartAsync();
        await rig.KillAsync(first.Skip(3));
        await rig.RunDueAndDeliverAsync();
        Assert.Equal(new[] { 7, 8 }, rig.Spawner.Batches.Select(batch => batch.Amount).ToArray());
        Assert.Empty(rig.Completions);
    }

    [PgFact]
    public async Task A_pilot_who_leaves_the_system_ends_the_dungeon_and_no_wave_comes_after()
    {
        await using var rig = await WaveRig.CreateAsync();
        await rig.Director.StartAsync(Pilot, SystemId, Origin, 2158);
        rig.Advance(TimeSpan.FromSeconds(10));
        await rig.RunDueAndDeliverAsync();
        rig.Spawner.Present = false;

        // out for ten seconds, then past the absence grace: the dungeon ends before its first wave (240 s) comes
        rig.Advance(TimeSpan.FromSeconds(10));
        await rig.RunDueAndDeliverAsync();
        rig.Advance(DungeonWaveDirector.PilotAbsenceGrace);
        await rig.RunDueAndDeliverAsync();
        rig.Advance(TimeSpan.FromMinutes(10));
        await rig.RunDueAndDeliverAsync();

        Assert.Equal(new[] { "content.dungeon.abandoned" }, rig.Completions.Select(completion => completion.Topic).ToArray());
        Assert.Empty(rig.Spawner.Batches);
        Assert.Empty(rig.Spawner.Removed);
        Assert.Empty(await rig.Content.ActiveByKindAsync(DungeonWaveDirector.Kind));
    }

    [PgFact]
    public async Task A_pilot_who_returns_within_the_absence_grace_keeps_the_dungeon_running()
    {
        await using var rig = await WaveRig.CreateAsync();
        await rig.Director.StartAsync(Pilot, SystemId, Origin, 2173);
        await rig.RunDueAndDeliverAsync();
        var ships = Assert.Single(rig.Spawner.Batches).Ships;

        // a logoff in space: the pilot is out of the system for two sweeps, then logs back in
        rig.Spawner.Present = false;
        rig.Advance(TimeSpan.FromSeconds(10));
        await rig.RunDueAndDeliverAsync();
        rig.Advance(TimeSpan.FromSeconds(10));
        await rig.RunDueAndDeliverAsync();
        rig.Spawner.Present = true;
        rig.Advance(TimeSpan.FromSeconds(10));
        await rig.RunDueAndDeliverAsync();

        Assert.Empty(rig.Completions);
        Assert.Empty(rig.Spawner.Removed);
        Assert.Single(await rig.Content.ActiveByKindAsync(DungeonWaveDirector.Kind));
        Assert.NotEmpty(ships);
    }

    [PgFact]
    public async Task A_wave_that_comes_due_while_its_pilot_is_away_waits_and_spawns_when_the_pilot_is_back()
    {
        await using var rig = await WaveRig.CreateAsync();
        await rig.Director.StartAsync(Pilot, SystemId, Origin, 2173);

        // the first wave comes due while the pilot is out of the system: it is not refused, failed or spawned yet
        rig.Spawner.Present = false;
        await rig.RunDueAndDeliverAsync();
        Assert.Empty(rig.Spawner.Batches);
        Assert.Single(await rig.Content.ActiveByKindAsync(DungeonWaveDirector.Kind));
        Assert.Empty(rig.Completions);

        // back in the system: the wave is retried and spawns its NPCs once
        rig.Spawner.Present = true;
        rig.Advance(TimeSpan.FromSeconds(15));
        await rig.RunDueAndDeliverAsync();
        Assert.Single(rig.Spawner.Batches);
        Assert.NotEmpty(Assert.Single(rig.Spawner.Batches).Ships);
        Assert.Single(await rig.Content.ActiveByKindAsync(DungeonWaveDirector.Kind));
    }

    [PgFact]
    public async Task A_pilot_who_leaves_after_a_wave_came_has_that_wave_taken_out_by_the_sweep()
    {
        await using var rig = await WaveRig.CreateAsync();
        await rig.Director.StartAsync(Pilot, SystemId, Origin, 2173);
        await rig.RunDueAndDeliverAsync();
        var ships = Assert.Single(rig.Spawner.Batches).Ships;

        // out of the system past the absence grace: the first sweep notes the absence, a later one ends the dungeon
        rig.Spawner.Present = false;
        rig.Advance(TimeSpan.FromSeconds(10));
        await rig.RunDueAndDeliverAsync();
        rig.Advance(DungeonWaveDirector.PilotAbsenceGrace);
        await rig.RunDueAndDeliverAsync();

        Assert.Equal(ships.OrderBy(id => id), rig.Spawner.Removed.OrderBy(id => id));
        Assert.Equal(new[] { "content.dungeon.abandoned" }, rig.Completions.Select(completion => completion.Topic).ToArray());
    }

    [PgFact]
    public async Task A_dungeon_that_runs_out_of_time_expires_and_its_NPCs_are_taken_out_by_the_sweep()
    {
        await using var rig = await WaveRig.CreateAsync();
        await rig.Director.StartAsync(Pilot, SystemId, Origin, 2173);
        await rig.RunDueAndDeliverAsync();
        var ships = Assert.Single(rig.Spawner.Batches).Ships;

        rig.Advance(DungeonWaveDirector.Lifetime + TimeSpan.FromSeconds(1));
        await rig.RunDueAndDeliverAsync();
        rig.Advance(DungeonWaveDirector.Sweep);
        await rig.RunDueAndDeliverAsync();

        Assert.Equal(new[] { "content.dungeon.expired" }, rig.Completions.Select(completion => completion.Topic).ToArray());
        Assert.Equal(ships.OrderBy(id => id), rig.Spawner.Removed.OrderBy(id => id));
    }

    [PgFact]
    public async Task A_dungeon_whose_wave_names_no_pool_or_profile_of_the_authority_is_refused_and_nothing_starts()
    {
        await using var rig = await WaveRig.CreateAsync();

        var start = await rig.Director.StartAsync(Pilot, SystemId, Origin, 5610);

        Assert.False(start.Started);
        Assert.Contains("wave 1: no NPC pool or profile is named 'parity_drifter_dungeon_family' exactly", start.Refusal);
        Assert.Empty(await rig.Content.ActiveByKindAsync(DungeonWaveDirector.Kind));
    }

    [PgFact]
    public async Task A_pilot_who_is_not_in_space_in_the_system_cannot_start_a_dungeon_and_a_second_start_is_the_same_instance()
    {
        await using var rig = await WaveRig.CreateAsync();
        rig.Spawner.Present = false;
        Assert.Equal("the pilot is not in space in this system", (await rig.Director.StartAsync(Pilot, SystemId, Origin, 2173)).Refusal);

        rig.Spawner.Present = true;
        Assert.True((await rig.Director.StartAsync(Pilot, SystemId, Origin, 2173)).Started);
        var again = await rig.Director.StartAsync(Pilot, SystemId, Origin, 2173);
        Assert.False(again.Started);
        Assert.Single(await rig.Content.ActiveByKindAsync(DungeonWaveDirector.Kind));
    }

    [PgFact]
    public async Task A_simulation_that_makes_fewer_NPCs_than_a_wave_asks_for_fails_the_dungeon_and_the_sweep_takes_out_those_made()
    {
        await using var rig = await WaveRig.CreateAsync();
        rig.Spawner.MakeAtMost = 5;
        await rig.Director.StartAsync(Pilot, SystemId, Origin, 2173);

        await rig.RunDueAndDeliverAsync();
        rig.Advance(TimeSpan.FromSeconds(10));
        await rig.RunDueAndDeliverAsync();

        var failed = Assert.Single(rig.Completions);
        Assert.Equal("content.dungeon.failed", failed.Topic);
        Assert.Contains("wave 1: 5 of 8 NPCs were made", failed.PayloadJson);
        Assert.Equal(5, rig.Spawner.Removed.Count);
    }

    /// <summary>What the dungeon topics announce, in order.</summary>
    private sealed record Completion(string Topic, string PayloadJson);

    /// <summary>The NPC spawner the test plays: it records each batch the director asks for and makes ids of its own.</summary>
    private sealed class FakeSpawner : IDungeonNpcSpawner
    {
        private readonly object _gate = new();
        private long _next = 4_300_000_000_000_000;

        public List<(int Amount, string Query, IReadOnlyList<long> Ships)> Batches { get; } = [];

        public List<long> Removed { get; } = [];

        public bool Present { get; set; } = true;

        /// <summary>When set, each wave gets at most this many NPCs from the simulation.</summary>
        public int? MakeAtMost { get; set; }

        public Task<DungeonSpawnResult> SpawnAsync(int solarSystemId, double[] origin, NpcSelection selection, int amount, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var count = MakeAtMost is { } limit ? Math.Min(limit, amount) : amount;
                var ships = Enumerable.Range(0, count).Select(_ => ++_next).ToList();
                Batches.Add((amount, selection.Name, ships));
                return Task.FromResult(new DungeonSpawnResult(ships, count < amount ? "the simulation refused the rest" : null));
            }
        }

        public bool IsPilotInSystem(long characterId, int solarSystemId) => Present;

        public void Remove(IReadOnlyList<long> shipIds)
        {
            lock (_gate)
            {
                Removed.AddRange(shipIds);
            }
        }
    }

    [PgFact]
    public async Task A_started_dungeon_is_a_deadspace_anchor_at_its_origin_in_its_system()
    {
        await using var rig = await WaveRig.CreateAsync();
        Assert.True((await rig.Director.StartAsync(Pilot, SystemId, Origin, 2173)).Started);

        // a mission pocket is positioned where its origin is: the observatory refuses that system, and no other
        var pocket = Assert.Single(await rig.Director.ActivePocketsAsync());
        Assert.Equal(SystemId, pocket.SolarSystemId);
        Assert.Equal(Origin, pocket.Origin);
    }

    /// <summary>The runtime over its own schemas, the director attached, a clock the test moves; a restart makes new runtime objects over the same records.</summary>
    private sealed class WaveRig : IAsyncDisposable
    {
        private readonly string[] _schemas;
        private readonly List<Completion> _completions = [];
        private long _now = 1_700_000_000_000;

        private WaveRig(NpgsqlDataSource source, string[] schemas)
        {
            Source = source;
            _schemas = schemas;
            Spawner = new FakeSpawner();
            Scheduler = new DurableScheduler(source, () => _now, null, schemas[0]);
            Bus = new EventBus(source, Scheduler, null, schemas[1]);
            Content = new ContentRuntime(source, Scheduler, Bus, schemas[2]);
            Director = NewDirector();
        }

        public NpgsqlDataSource Source { get; }
        public FakeSpawner Spawner { get; }
        public DurableScheduler Scheduler { get; private set; }
        public EventBus Bus { get; private set; }
        public ContentRuntime Content { get; private set; }
        public DungeonWaveDirector Director { get; private set; }

        public IReadOnlyList<Completion> Completions
        {
            get
            {
                lock (_completions)
                {
                    return [.. _completions];
                }
            }
        }

        public static async Task<WaveRig> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            var id = Guid.NewGuid().ToString("N")[..12];
            string[] schemas = ["wav_sch_" + id, "wav_evt_" + id, "wav_cnt_" + id, "wav_spc_" + id];
            await DurableScheduler.MigrateAsync(source, schemas[0]);
            await EventBus.MigrateAsync(source, schemas[1]);
            await ContentRuntime.MigrateAsync(source, schemas[2]);
            await DungeonWaveDirector.MigrateAsync(source, schemas[3]);
            var rig = new WaveRig(source, schemas);
            await rig.AttachAsync();
            return rig;
        }

        private DungeonWaveDirector NewDirector() =>
            new(Source, Content, Scheduler, Bus, Catalog.Value, Rules.Value, new NpcSelector(NpcAuthority.Load()), Spawner, _schemas[3],
                clock: () => DateTimeOffset.FromUnixTimeMilliseconds(_now));

        private async Task AttachAsync()
        {
            await Director.AttachAsync();
            Bus.Subscribe("test.waves.record", ["content.dungeon.completed", "content.dungeon.failed", "content.dungeon.abandoned", "content.dungeon.expired"], (context, _) =>
            {
                lock (_completions)
                {
                    _completions.Add(new Completion(context.Event.Topic, context.Event.PayloadJson));
                }
                return Task.CompletedTask;
            });
            await Content.AttachAsync();
            await Bus.PumpAsync();
        }

        public void Advance(TimeSpan span) => _now += (long)span.TotalMilliseconds;

        /// <summary>A host restarted over the same schemas: new runtime objects, the same records.</summary>
        public async Task RestartAsync()
        {
            Scheduler = new DurableScheduler(Source, () => _now, null, _schemas[0]);
            Bus = new EventBus(Source, Scheduler, null, _schemas[1]);
            Content = new ContentRuntime(Source, Scheduler, Bus, _schemas[2]);
            Director = NewDirector();
            await AttachAsync();
        }

        /// <summary>The NPCs are destroyed: the simulation announces each as <c>npc.killed</c>, with the ship that died.</summary>
        public async Task KillAsync(IEnumerable<long> ships)
        {
            foreach (var ship in ships)
            {
                await Bus.PublishAsync("npc.killed", new { shipId = ship, solarSystemId = SystemId, characterId = Pilot, typeId = 0, profile = "test" });
            }
            await RunDueAndDeliverAsync();
        }

        /// <summary>Runs the due jobs (the content's deadlines, the spawns, the sweep) and delivers what they publish, until nothing is left.</summary>
        public async Task RunDueAndDeliverAsync()
        {
            for (var round = 0; round < 6; round++)
            {
                var ran = await Scheduler.RunDueAsync();
                var delivered = 0;
                for (var i = 0; i < 20; i++)
                {
                    var pumped = (await Bus.PumpAsync()).Delivered;
                    if (pumped == 0)
                    {
                        break;
                    }
                    delivered += pumped;
                }
                if (ran == 0 && delivered == 0)
                {
                    break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var schema in _schemas)
            {
                await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }
}
