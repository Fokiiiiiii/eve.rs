using Eve.Content;
using Eve.Data;
using Eve.Host;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Dungeons;
using Eve.Dungeons.Incursions;
using Eve.Events;
using Eve.Npc;
using Eve.Scheduler;
using Eve.Services.Space.Incursions;
using Eve.Services.Space.Waves;
using Eve.Universe;
using Npgsql;
using System.Text.Json.Nodes;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The Sansha incursions on the durable runtime (B-L1, B-S1): the capacity of each band, the phases in time, the respawn window, the staging sites as dungeons of the wave
/// runtime, a restart over the same records, and the rows the map and the rewards read. The clock is the test's and the NPCs are the test's fake spawner's.
/// </summary>
public sealed class IncursionDirectorTests
{
    private const long Start = 1_700_000_000_000;
    private static readonly TimeSpan Mobilized = TimeSpan.FromHours(48);
    private static readonly TimeSpan Established = TimeSpan.FromDays(1);
    private static readonly TimeSpan Withdrawing = TimeSpan.FromHours(24);

    [PgFact]
    public async Task Each_band_is_filled_to_its_capacity_with_a_staging_incursion_and_its_five_staging_sites()
    {
        await using var rig = await IncursionRig.CreateAsync();
        await rig.RunDueAsync();

        var rows = await rig.ReadActiveAsync();
        Assert.Equal(5, rows.Count);
        Assert.Equal(1, rows.Count(row => rig.BandOf(row) == IncursionBand.HighSec));
        Assert.Equal(1, rows.Count(row => rig.BandOf(row) == IncursionBand.LowSec));
        Assert.Equal(3, rows.Count(row => rig.BandOf(row) == IncursionBand.NullSec));
        // the constellations of the five incursions are distinct: no two share a system
        var systems = rows.SelectMany(row => row["incursedSystems"]!.AsArray().Select(system => system!.GetValue<long>())).ToList();
        Assert.Equal(systems.Count, systems.Distinct().Count());
        Assert.All(rows, row =>
        {
            Assert.Equal((long)IncursionRules.StateMobilizing, row["state"]!.GetValue<long>());
            Assert.Equal(1.0, row["influence"]!.GetValue<double>());
            Assert.Equal(IncursionRules.StagingRewardGroupId, row["rewardGroupID"]!.GetValue<long>());
        });

        var sites = await rig.Content.ActiveByKindAsync(DungeonWaveDirector.Kind);
        Assert.Equal(25, sites.Count);
        Assert.All(sites, site => Assert.Equal(DungeonWaveDirector.NoPilotOwner, site.Owner));
        Assert.All(sites, site => Assert.Contains(site.Definition.Replace("dungeon.", ""), IncursionRules.StagingDungeonIds.Select(id => id.ToString())));
        // the on-load wave of every staging site is handed to the simulation (one batch per site, all from the pool of the owner data)
        Assert.Equal(25, rig.Spawner.Batches.Count);
        // the selection the simulation was asked for is the authority pool the owner data names (sanshas_deadspace_pve)
        Assert.All(rig.Spawner.Batches, batch => Assert.Equal("Sansha PVE Deadspace Hostiles", batch.Query));
    }

    [PgFact]
    public async Task Phases_last_48_hours_then_a_day_then_24_hours_and_the_incursion_is_gone_after_its_withdrawal()
    {
        await using var rig = await IncursionRig.CreateAsync();
        await rig.RunDueAsync();

        rig.Advance(Mobilized - TimeSpan.FromMinutes(1));
        await rig.RunDueAsync();
        Assert.All(await rig.ReadActiveAsync(), row => Assert.Equal((long)IncursionRules.StateMobilizing, row["state"]!.GetValue<long>()));

        rig.Advance(TimeSpan.FromMinutes(1));
        await rig.RunDueAsync();
        Assert.All(await rig.ReadActiveAsync(), row => Assert.Equal((long)IncursionRules.StateEstablished, row["state"]!.GetValue<long>()));

        rig.Advance(Established);
        await rig.RunDueAsync();
        Assert.All(await rig.ReadActiveAsync(), row => Assert.Equal((long)IncursionRules.StateWithdrawing, row["state"]!.GetValue<long>()));

        rig.Advance(Withdrawing - TimeSpan.FromMinutes(1));
        await rig.RunDueAsync();
        Assert.Equal(5, (await rig.ReadActiveAsync()).Count);

        rig.Advance(TimeSpan.FromMinutes(1));
        await rig.RunDueAsync();
        await rig.SweepAsync();
        Assert.Empty(await rig.ReadActiveAsync());
        Assert.Empty(await rig.Content.ActiveByKindAsync(DungeonWaveDirector.Kind));
        // the NPCs of the staging sites were made, and every one is taken out once the sites are ended
        Assert.Equal(rig.Spawner.Batches.Sum(batch => batch.Ships.Count), rig.Spawner.Removed.Count);
        Assert.Equal(rig.Spawner.Removed.Distinct().Count(), rig.Spawner.Removed.Count);
    }

    [PgFact]
    public async Task A_band_respawns_in_the_same_band_12_to_36_hours_after_its_incursion_ended()
    {
        await using var rig = await IncursionRig.CreateAsync(unit: 0.5);
        await rig.RunDueAsync();
        rig.Advance(Mobilized + Established + Withdrawing);
        await rig.RunDueAsync();
        Assert.Empty(await rig.ReadActiveAsync());

        // 0.5 of the window is 24 hours after the end
        rig.Advance(TimeSpan.FromHours(23));
        await rig.RunDueAsync();
        Assert.Empty(await rig.ReadActiveAsync());

        rig.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await rig.RunDueAsync();
        var rows = await rig.ReadActiveAsync();
        Assert.Equal(5, rows.Count);
        Assert.Equal(1, rows.Count(row => rig.BandOf(row) == IncursionBand.HighSec));
        Assert.Equal(3, rows.Count(row => rig.BandOf(row) == IncursionBand.NullSec));
    }

    [PgFact]
    public async Task A_respawn_at_the_shortest_delay_comes_at_12_hours_and_not_before()
    {
        await using var rig = await IncursionRig.CreateAsync(unit: 0.0);
        await rig.RunDueAsync();
        rig.Advance(Mobilized + Established + Withdrawing);
        await rig.RunDueAsync();

        rig.Advance(TimeSpan.FromHours(11) + TimeSpan.FromMinutes(59));
        await rig.RunDueAsync();
        Assert.Empty(await rig.ReadActiveAsync());

        rig.Advance(TimeSpan.FromMinutes(1));
        await rig.RunDueAsync();
        Assert.Equal(5, (await rig.ReadActiveAsync()).Count);
    }

    [PgFact]
    public async Task A_restart_resumes_the_phases_where_they_were_and_adds_no_incursion()
    {
        await using var rig = await IncursionRig.CreateAsync();
        await rig.RunDueAsync();
        rig.Advance(TimeSpan.FromHours(24));
        await rig.RunDueAsync();

        await rig.RestartAsync();
        rig.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        await rig.RunDueAsync();

        var rows = await rig.ReadActiveAsync();
        Assert.Equal(5, rows.Count);
        Assert.All(rows, row => Assert.Equal((long)IncursionRules.StateEstablished, row["state"]!.GetValue<long>()));
        Assert.Equal(1, rows.Count(row => rig.BandOf(row) == IncursionBand.HighSec));
        Assert.Equal(3, rows.Count(row => rig.BandOf(row) == IncursionBand.NullSec));
        Assert.Equal(25, await rig.CountSitesAsync());
    }

    [PgFact]
    public async Task A_restart_during_a_respawn_gap_keeps_the_gap_and_the_band_comes_back_when_it_is_due()
    {
        await using var rig = await IncursionRig.CreateAsync(unit: 0.0);
        await rig.RunDueAsync();
        rig.Advance(Mobilized + Established + Withdrawing);
        await rig.RunDueAsync();
        Assert.Empty(await rig.ReadActiveAsync());

        rig.Advance(TimeSpan.FromHours(6));
        await rig.RestartAsync();
        await rig.RunDueAsync();
        Assert.Empty(await rig.ReadActiveAsync());

        rig.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        await rig.RunDueAsync();
        Assert.Equal(5, (await rig.ReadActiveAsync()).Count);
    }

    [PgFact]
    public async Task A_pending_loyalty_point_answer_names_the_groups_of_the_live_incursions_and_none_after_they_are_gone()
    {
        await using var rig = await IncursionRig.CreateAsync();
        await rig.RunDueAsync();

        var live = await rig.Director.PendingLoyaltyPointsAsync([IncursionRules.StagingRewardGroupId, 99]);
        var group = Assert.Single(live);
        Assert.Equal(IncursionRules.StagingRewardGroupId, group.Key);
        Assert.Equal(0, group.Value);

        rig.Advance(Mobilized + Established + Withdrawing);
        await rig.RunDueAsync();
        Assert.Empty(await rig.Director.PendingLoyaltyPointsAsync([IncursionRules.StagingRewardGroupId]));
    }

    [PgFact]
    public async Task The_map_reports_the_live_incursions_through_the_host_read_model_and_nothing_once_they_are_gone()
    {
        await using var rig = await IncursionRig.CreateAsync();
        await rig.RunDueAsync();
        var readModel = new PersistenceIncursionAnomalyReadModel(
            (_, _, _) => Task.FromResult<IReadOnlyList<ContentInstance>>([]),
            (_, _) => Task.FromResult(new JsonObject()),
            () => Start,
            rig.Director.ReadActiveAsync);
        var map = new IncursionAnomalyService("map", readModel.ReadIncursionsAsync, readModel.ReadAnomaliesAsync);

        var report = Assert.IsType<PyList>(await map.CallAsync(WireCall("GetIncursionGlobalReport")));
        Assert.Equal(5, report.Items.Count);
        Assert.All(report.Items, item =>
        {
            var fields = FieldsOf(item);
            Assert.Equal((long)IncursionRules.StateMobilizing, Int(fields["state"]));
            Assert.Equal(IncursionRules.StagingRewardGroupId, Int(fields["rewardGroupID"]));
        });
        var rowset = Assert.IsType<PyObject>(await map.CallAsync(WireCall("GetSystemsInIncursions")));
        var lines = Assert.IsType<PyList>(FieldsOf(rowset)["lines"]).Items;
        Assert.Equal(5, lines.Count);
        Assert.All(lines, line => Assert.Equal((long)IncursionRules.StagingSceneType, Int(Assert.IsType<PyList>(line).Items[1])));

        rig.Advance(IncursionRules.MaximumLifetime + TimeSpan.FromMinutes(1));
        await rig.RunDueAsync();
        await rig.SweepAsync();
        Assert.Empty(Assert.IsType<PyList>(await map.CallAsync(WireCall("GetIncursionGlobalReport"))).Items);
        Assert.Empty(Assert.IsType<PyList>(FieldsOf(await map.CallAsync(WireCall("GetSystemsInIncursions")))["lines"]).Items);
    }

    private static ServiceCall WireCall(string method) => new() { Method = method, Args = [], Session = new ClientSession(new DiscardingSink()) };

    private static Dictionary<string, PyValue> FieldsOf(PyValue entry) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(entry).Args).Entries.ToDictionary(pair => Assert.IsType<PyStr>(pair.Key).Value, pair => pair.Value);

    private static long Int(PyValue value) => Assert.IsType<PyInt>(value).Value;

    private sealed class DiscardingSink : IPacketSink
    {
        public void Send(PyValue packet)
        {
        }
    }

    /// <summary>A synthetic universe: one highsec, one lowsec and four nullsec constellations, so every band has room to respawn in a constellation of its own.</summary>
    private static SolarSystemCatalog Universe() => new(
    [
        new SolarSystemInfo(1000, "High A", 100, 10, 0.9),
        new SolarSystemInfo(1001, "High B", 100, 10, 0.6),
        new SolarSystemInfo(2000, "Low A", 200, 10, 0.3),
        new SolarSystemInfo(2001, "Low B", 200, 10, 0.1),
        new SolarSystemInfo(3010, "Null A", 301, 11, -0.4),
        new SolarSystemInfo(3011, "Null B", 301, 11, -0.2),
        new SolarSystemInfo(3020, "Null C", 302, 11, -0.9),
        new SolarSystemInfo(3030, "Null D", 303, 11, -0.1),
        new SolarSystemInfo(3040, "Null E", 304, 11, 0.0),
    ]);

    /// <summary>The incursion runtime over its own schemas, the director attached, a clock the test moves; a restart makes new runtime objects over the same records.</summary>
    private sealed class IncursionRig : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _source;
        private readonly string[] _schemas;
        private readonly double _unit;
        private readonly Random _random = new(7);
        private long _now = Start;

        private IncursionRig(NpgsqlDataSource source, string[] schemas, double unit)
        {
            _source = source;
            _schemas = schemas;
            _unit = unit;
            Spawner = new FakeSpawner();
            Systems = Universe();
            Scheduler = new DurableScheduler(source, () => _now, null, schemas[0]);
            Bus = new EventBus(source, Scheduler, null, schemas[1]);
            Content = new ContentRuntime(source, Scheduler, Bus, schemas[2]);
            Director = NewDirector();
        }

        public FakeSpawner Spawner { get; }
        public SolarSystemCatalog Systems { get; }
        public DurableScheduler Scheduler { get; private set; }
        public EventBus Bus { get; private set; }
        public ContentRuntime Content { get; private set; }
        public DungeonWaveDirector Waves { get; private set; } = null!;
        public IncursionDirector Director { get; private set; }

        public static async Task<IncursionRig> CreateAsync(double unit = 0.5)
        {
            var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
            var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
            var id = Guid.NewGuid().ToString("N")[..12];
            string[] schemas = ["inc_sch_" + id, "inc_evt_" + id, "inc_cnt_" + id, "inc_wav_" + id, "inc_inc_" + id];
            await DurableScheduler.MigrateAsync(source, schemas[0]);
            await EventBus.MigrateAsync(source, schemas[1]);
            await ContentRuntime.MigrateAsync(source, schemas[2]);
            await DungeonWaveDirector.MigrateAsync(source, schemas[3]);
            await IncursionDirector.MigrateAsync(source, schemas[4]);
            var rig = new IncursionRig(source, schemas, unit);
            await rig.AttachAsync();
            return rig;
        }

        private IncursionDirector NewDirector()
        {
            Waves = new DungeonWaveDirector(_source, Content, Scheduler, Bus, DungeonCatalog.LoadEmbedded(), WaveRules.LoadEmbedded(), new NpcSelector(NpcAuthority.Load()), Spawner, _schemas[3]);
            return new IncursionDirector(_source, Content, Scheduler, Waves, Systems, _schemas[4], () => _unit < 0 ? _random.NextDouble() : _unit);
        }

        private async Task AttachAsync()
        {
            await Waves.AttachAsync();
            await Director.AttachAsync();
            await Content.AttachAsync();
            await Bus.PumpAsync();
        }

        public void Advance(TimeSpan span) => _now += (long)span.TotalMilliseconds;

        /// <summary>A host restarted over the same schemas: new runtime objects, the same records.</summary>
        public async Task RestartAsync()
        {
            Scheduler = new DurableScheduler(_source, () => _now, null, _schemas[0]);
            Bus = new EventBus(_source, Scheduler, null, _schemas[1]);
            Content = new ContentRuntime(_source, Scheduler, Bus, _schemas[2]);
            Director = NewDirector();
            await AttachAsync();
        }

        /// <summary>Runs the due jobs and delivers what they publish, until nothing is left.</summary>
        public async Task RunDueAsync()
        {
            for (var round = 0; round < 10; round++)
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

        /// <summary>The sweep of the dungeon waves takes the NPCs of ended sites out; it runs every ten seconds, so the test moves the clock past one run.</summary>
        public async Task SweepAsync()
        {
            Advance(DungeonWaveDirector.Sweep);
            await RunDueAsync();
        }

        public async Task<IReadOnlyList<JsonObject>> ReadActiveAsync() => await Director.ReadActiveAsync();

        public async Task<int> CountSitesAsync() => (await Content.ActiveByKindAsync(DungeonWaveDirector.Kind)).Count;

        /// <summary>The band of a report row, from its staging system.</summary>
        public IncursionBand BandOf(JsonObject row) =>
            IncursionRules.BandOf(Systems.Get((int)row["stagingSolarSystemID"]!.GetValue<long>())!.Security);

        public async ValueTask DisposeAsync()
        {
            foreach (var schema in _schemas)
            {
                try
                {
                    await using var drop = _source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                    await drop.ExecuteNonQueryAsync();
                }
                catch (NpgsqlException)
                {
                    // Leaving a scratch schema behind is better than failing the test.
                }
            }
            await _source.DisposeAsync();
        }
    }

    /// <summary>The NPC spawner the test plays: it records each batch the director asks for and makes ids of its own.</summary>
    private sealed class FakeSpawner : IDungeonNpcSpawner
    {
        private readonly object _gate = new();
        private long _next = 4_300_000_000_000_000;

        public List<(int Amount, string Query, IReadOnlyList<long> Ships)> Batches { get; } = [];

        public List<long> Removed { get; } = [];

        public Task<DungeonSpawnResult> SpawnAsync(int solarSystemId, double[] origin, NpcSelection selection, int amount, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var ships = Enumerable.Range(0, amount).Select(_ => ++_next).ToList();
                Batches.Add((amount, selection.Name, ships));
                return Task.FromResult(new DungeonSpawnResult(ships, null));
            }
        }

        public bool IsPilotInSystem(long characterId, int solarSystemId) => false;

        public void Remove(IReadOnlyList<long> shipIds)
        {
            lock (_gate)
            {
                Removed.AddRange(shipIds);
            }
        }
    }
}
