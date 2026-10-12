using System.Text.RegularExpressions;
using Eve.Content;
using Eve.Services;
using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Eve.Services.Space.Scanning;
using Eve.Services.Space.Sites;
using Eve.Universe;
using Eve.Wormholes;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>The cosmic sites' rules that need no database: the codes, the places and the templates.</summary>
public sealed class CosmicSiteRulesTests
{
    private static readonly Regex CodeForm = new("^[A-Z]{3}-[0-9]{3}$");

    [Fact]
    public void A_seed_gives_the_same_code_and_place_on_every_run_and_another_seed_another_code()
    {
        var anchors = new[] { new SiteAnchor(1, [1e11, 0, 0]) };
        var first = SitePlacement.Place(anchors, "anomaly:combat:30000001:0:0");
        var again = SitePlacement.Place(anchors, "anomaly:combat:30000001:0:0");
        Assert.Equal(first.Position, again.Position);
        Assert.Equal(SiteSeed.Code("x:1"), SiteSeed.Code("x:1"));
        Assert.NotEqual(SiteSeed.Code("x:1"), SiteSeed.Code("x:2"));
    }

    [Fact]
    public void A_code_is_three_letters_a_dash_and_three_digits()
    {
        for (var i = 0; i < 300; i++)
        {
            Assert.True(CodeForm.IsMatch(SiteSeed.Code($"seed:{i}")), SiteSeed.Code($"seed:{i}"));
        }
    }

    [Fact]
    public void A_site_lies_between_half_an_au_and_four_au_from_its_anchor_within_the_pitch_band()
    {
        var anchor = new SiteAnchor(7, [1.0e11, -2.0e10, 3.0e10]);
        for (var i = 0; i < 300; i++)
        {
            var (placed, position) = SitePlacement.Place([anchor], $"seed:{i}");
            var offset = new[] { position[0] - anchor.Position[0], position[1] - anchor.Position[1], position[2] - anchor.Position[2] };
            var distance = Math.Sqrt(offset.Sum(v => v * v));
            Assert.Equal(7, placed.Id);
            Assert.InRange(distance / ProbeScanMath.AuMeters, SitePlacement.MinimumAu, SitePlacement.MaximumAu);
            Assert.InRange(offset[1] / distance, -Math.Sin(0.14) - 1e-9, Math.Sin(0.14) + 1e-9);
        }
    }

    [Fact]
    public void The_site_table_has_a_site_for_every_slot_in_each_band_where_the_slot_occurs()
    {
        var templates = SiteTemplates.Load();
        Assert.NotEmpty(templates.All);
        Assert.Equal(templates.All.Count, templates.All.Select(template => template.Id).Distinct().Count());
        foreach (var band in new[] { "highsec", "lowsec", "nullsec" })
        {
            Assert.NotEmpty(templates.For(SiteTemplate.Anomaly, "combat", band));
            Assert.NotEmpty(templates.For(SiteTemplate.Signature, "combat", band));
            Assert.NotEmpty(templates.For(SiteTemplate.Signature, "data", band));
        }
        Assert.NotEmpty(templates.For(SiteTemplate.Signature, "relic", "nullsec"));
    }

    [Theory]
    [InlineData(1, 42)]
    [InlineData(3, 42)]
    [InlineData(4, 72)]
    [InlineData(5, 104)]
    public void A_signature_needs_the_probe_strength_of_its_level(int level, double strength) =>
        Assert.Equal(strength, ProbeScanMath.SignatureStrength(level));
}

/// <summary>The probe result of a cosmic signature by its level (the wiki: a probe that is weaker than the signature finds it only in part).</summary>
public sealed class SignatureScanResultTests
{
    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(3, 1.0)]
    [InlineData(4, 42.0 / 72.0)]
    [InlineData(5, 42.0 / 104.0)]
    public void A_probe_of_strength_42_covers_a_signature_of_each_level_by_its_strength_over_the_level(int level, double expected) =>
        Assert.Equal(expected, ProbeScanMath.Coverage(0, 1e6, 42, ProbeScanMath.SignatureStrength(level)), 9);

    [Fact]
    public void A_weaker_probe_finds_a_signature_in_part_and_a_probe_that_does_not_reach_it_leaves_it_alone()
    {
        var signature = new ScanTargetSite(77, 19728, 502, 3, [0, 0, 0], 42, true, Code: "ABC-123");

        var partial = ProbeScanEngine.Compute([new ScanProbe(1, [0, 0, 0], 1e9, 21, 1e7, false)], [signature], new Random(7));
        Assert.Empty(partial.AbsentTargets);
        var row = Assert.Single(partial.Results);
        Assert.False(row.Perfect);
        Assert.InRange(row.Certainty, 0, 0.249);
        Assert.Equal("ABC-123", row.Key);

        var none = ProbeScanEngine.Compute([new ScanProbe(1, [5e9, 0, 0], 1e6, 42, 1e4, false)], [signature], new Random(7));
        Assert.Empty(none.Results);
        Assert.Empty(none.AbsentTargets);
    }
}

/// <summary>A warp to a scan result: a cosmic site is named by its code (any case), a ship or other target by its number, and only in the system it was scanned in.</summary>
public sealed class ScanResultWarpTests
{
    private const int System = 30_000_001;
    private static readonly double[] SitePlace = [1.0e9, 2.0e9, 3.0e9];

    [Fact]
    public void A_site_is_warped_to_by_its_code_in_any_case_and_only_in_the_system_it_was_scanned_in()
    {
        var authority = new ProbeScanWarpAuthority();
        var session = new ClientSession();
        var site = new ScanResultRow(9001, 3, 502, 19728, 0, 0, 1.0, SitePlace, 0, true, Code: "ABC-123");
        authority.Record(session, System, [site]);

        Assert.Equal(SitePlace, authority.Resolve(session, System, "abc-123"));
        Assert.Equal(SitePlace, authority.Resolve(session, System, " ABC-123 "));
        Assert.Null(authority.Resolve(session, System, "ABC-124"));
        Assert.Null(authority.Resolve(session, System + 1, "ABC-123"));
    }

    [Fact]
    public void A_ship_result_is_still_warped_to_by_its_number_and_a_result_below_100_percent_is_not()
    {
        var authority = new ProbeScanWarpAuthority();
        var session = new ClientSession();
        var ship = new ScanResultRow(55, 1, 6, 606, 0, 55, 1.0, SitePlace, 0, true);
        var partial = new ScanResultRow(56, 3, 502, 19728, 0, 0, 0.6, SitePlace, 4_000, false, Code: "DEF-456");
        authority.Record(session, System, [ship, partial]);

        Assert.Equal(SitePlace, authority.Resolve(session, System, 55L));
        Assert.Null(authority.Resolve(session, System, "DEF-456"));
    }
}

/// <summary>The director and the probe source over PostgreSQL (the content runtime, the scheduler and the bus of their own schemas).</summary>
public sealed class CosmicSiteDirectorTests
{
    private const int NullSystem = 30_000_001;

    [PgFact]
    public async Task A_system_gets_one_site_per_slot_once_and_a_second_fill_adds_none_and_a_restart_keeps_them()
    {
        await using var rig = await SiteRig.CreateAsync();
        await rig.FillAsync(NullSystem);
        var first = await rig.Director.ActiveAsync(NullSystem);

        Assert.Equal(new[] { 0, 1, 2, 3 }, first.Select(site => site.Slot).OrderBy(slot => slot).ToArray());
        Assert.All(first, site => Assert.True(Regex.IsMatch(site.Code, "^[A-Z]{3}-[0-9]{3}$"), site.Code));
        Assert.Equal(4, first.Select(site => site.Code).Distinct().Count());
        Assert.All(first, site => Assert.True(SiteRig.NearAnAnchor(site.Position), "a site lies beyond 4 AU of its anchor"));

        await rig.FillAsync(NullSystem);
        var ids = first.Select(site => site.InstanceId).OrderBy(id => id).ToArray();
        Assert.Equal(ids, (await rig.Director.ActiveAsync(NullSystem)).Select(site => site.InstanceId).OrderBy(id => id).ToArray());

        var restarted = rig.NewDirector();
        Assert.Equal(ids, (await restarted.ActiveAsync(NullSystem)).Select(site => site.InstanceId).OrderBy(id => id).ToArray());
    }

    [PgFact]
    public async Task An_expired_site_is_replaced_at_once_in_its_slot_and_its_replacement_is_elsewhere()
    {
        await using var rig = await SiteRig.CreateAsync();
        await rig.FillAsync(NullSystem);
        var before = await rig.Director.ActiveAsync(NullSystem);

        rig.Advance(CosmicSiteDirector.Lifetime + TimeSpan.FromMinutes(1));
        await rig.RunDueAndDeliverAsync();

        var after = await rig.Director.ActiveAsync(NullSystem);
        Assert.Equal(new[] { 0, 1, 2, 3 }, after.Select(site => site.Slot).OrderBy(slot => slot).ToArray());
        Assert.Empty(after.Select(site => site.InstanceId).Intersect(before.Select(site => site.InstanceId)));
        Assert.Equal(4, after.Select(site => site.Code).Distinct().Count());
        Assert.Empty(after.Select(site => site.Code).Intersect(before.Select(site => site.Code)));
    }

    [PgFact]
    public async Task A_system_nobody_is_in_gets_no_site_and_the_first_pilot_brings_them()
    {
        await using var rig = await SiteRig.CreateAsync();
        var empty = rig.NewDirector(_ => false);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await empty.FillAsync(connection, transaction, NullSystem);
            await transaction.CommitAsync();
        }
        Assert.Empty(await empty.ActiveAsync(NullSystem));

        await rig.FillAsync(NullSystem, rig.NewDirector(_ => true));
        Assert.Equal(4, (await rig.Director.ActiveAsync(NullSystem)).Count);
    }

    [PgFact]
    public async Task A_wormhole_system_gets_the_sites_of_its_plan_and_its_probe_targets_need_no_strength()
    {
        var system = JSpaceSystems.Load().First(j => j.WormholeClass == 3);
        await using var rig = await SiteRig.CreateAsync();
        var director = rig.NewDirector(_ => true, id => id == system.Id ? 3 : null);
        await rig.FillAsync(system.Id, director);

        var plan = WormholeSites.Plan(3, drifter: false, (ulong)system.Id);
        var sites = await rig.Director.ActiveAsync(system.Id);
        // the gas and ore of the plan are not placed (no site of their kinds yet); the combat, data and relic ones are
        var placed = plan.Select((signature, index) => (signature, index)).Where(item => item.signature.Category is not ("gas" or "ore")).ToList();
        Assert.Equal(placed.Count, sites.Count);
        foreach (var (signature, index) in placed)
        {
            var site = sites.Single(candidate => candidate.Slot == index);
            Assert.Equal($"wormhole-{signature.DungeonKey}", site.TemplateId);
            Assert.Equal(signature.Category == "combat", site.IsAnomaly);
        }

        var targets = await new ContentCosmicSites(director).SitesInAsync(system.Id);
        Assert.Equal(placed.Count(item => item.signature.Category != "combat"), targets.Count);
        Assert.All(targets, target => Assert.Equal(WormholeSites.ScanStrength, target.RequiredStrength));
    }

    [PgFact]
    public async Task The_probe_source_lists_each_signature_of_the_system_by_its_code_with_the_strength_of_its_level()
    {
        await using var rig = await SiteRig.CreateAsync();
        var targets = await new ContentCosmicSites(rig.Director).SitesInAsync(NullSystem);
        var signatures = (await rig.Director.ActiveAsync(NullSystem)).Where(site => !site.IsAnomaly).Select(site => site.Code).OrderBy(code => code).ToArray();

        Assert.Equal(3, targets.Count);
        Assert.Equal(signatures, targets.Select(target => target.Code!).OrderBy(code => code).ToArray());
        Assert.All(targets, target =>
        {
            Assert.True(target.IsSignature);
            Assert.Contains(target.RequiredStrength, new double[] { 42, 72, 104 });
        });
    }

    [PgFact]
    public async Task A_site_cleared_by_its_npcs_is_replaced_at_once_elsewhere_and_a_restart_keeps_the_progress()
    {
        await using var rig = await SiteRig.CreateAsync();
        await rig.FillAsync(NullSystem);
        var before = await rig.Director.ActiveAsync(NullSystem);
        var anomaly = before.Single(site => site.IsAnomaly);
        var template = rig.Director.Templates.ById(anomaly.TemplateId)!;
        var half = template.NpcCount / 2;

        await rig.KillAsync(anomaly.InstanceId, NullSystem, half);
        await rig.RestartAsync();
        await rig.KillAsync(anomaly.InstanceId, NullSystem, template.NpcCount - half);
        for (var i = 0; i < template.ContainerCount; i++)
        {
            await rig.Bus.PublishAsync(CosmicSiteDirector.ContainerClearedTopic, new { site = anomaly.InstanceId, solarSystemId = NullSystem });
        }
        await rig.RunDueAndDeliverAsync();

        var after = await rig.Director.ActiveAsync(NullSystem);
        Assert.DoesNotContain(after, site => site.InstanceId == anomaly.InstanceId);
        Assert.Equal(new[] { 0, 1, 2, 3 }, after.Select(site => site.Slot).OrderBy(slot => slot).ToArray());
        var replacement = after.Single(site => site.IsAnomaly);
        Assert.NotEqual(anomaly.Code, replacement.Code);
        Assert.Equal(4, after.Select(site => site.Code).Distinct().Count());
    }

    /// <summary>The site of the system test: a null-sec system with an anchor pair (the director takes the anchors from the static data, here from a fixture).</summary>
    private sealed class FixedAnchors : ISiteAnchors
    {
        public Task<IReadOnlyList<SiteAnchor>> OfAsync(int solarSystemId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SiteAnchor>>(SiteRig.Anchors);
    }

    /// <summary>A content runtime, scheduler and bus over their own schemas, with the director attached and a clock the test moves.</summary>
    private sealed class SiteRig : IAsyncDisposable
    {
        public static readonly SiteAnchor[] Anchors = [new SiteAnchor(40_000_001, [0, 0, 0]), new SiteAnchor(40_000_002, [5.0e11, 0, 0])];

        private long _now = 1_700_000_000_000;

        private SiteRig(NpgsqlDataSource source, string[] schemas)
        {
            Source = source;
            _schemas = schemas;
            Scheduler = new DurableScheduler(source, () => _now, null, schemas[0]);
            Bus = new EventBus(source, Scheduler, null, schemas[1]);
            Content = new ContentRuntime(source, Scheduler, Bus, schemas[2]);
            Systems = new SolarSystemCatalog([new SolarSystemInfo(NullSystem, "Test Null", 1, 1, -0.5)]);
            Director = NewDirector();
        }

        private readonly string[] _schemas;

        public NpgsqlDataSource Source { get; }
        public DurableScheduler Scheduler { get; private set; }
        public EventBus Bus { get; private set; }
        public ContentRuntime Content { get; private set; }
        public SolarSystemCatalog Systems { get; }
        public CosmicSiteDirector Director { get; private set; }

        public static async Task<SiteRig> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            var id = Guid.NewGuid().ToString("N")[..12];
            string[] schemas = ["sit_sch_" + id, "sit_evt_" + id, "sit_cnt_" + id];
            await DurableScheduler.MigrateAsync(source, schemas[0]);
            await EventBus.MigrateAsync(source, schemas[1]);
            await ContentRuntime.MigrateAsync(source, schemas[2]);
            var rig = new SiteRig(source, schemas);
            rig.Director.Attach(rig.Content, rig.Bus);
            await rig.Content.AttachAsync();
            await rig.Bus.PumpAsync();
            return rig;
        }

        /// <summary>A director over the same stores (what a restarted host makes).</summary>
        public CosmicSiteDirector NewDirector(Func<int, bool>? occupied = null, Func<int, int?>? wormholeClassOf = null) =>
            new(Source, Content, Scheduler, SiteTemplates.Load(), new FixedAnchors(), Systems, occupied: occupied, wormholeClassOf: wormholeClassOf);

        public void Advance(TimeSpan span) => _now += (long)span.TotalMilliseconds;

        /// <summary>A host restarted over the same schemas: new runtime objects, the same records.</summary>
        public async Task RestartAsync()
        {
            Scheduler = new DurableScheduler(Source, () => _now, null, _schemas[0]);
            Bus = new EventBus(Source, Scheduler, null, _schemas[1]);
            Content = new ContentRuntime(Source, Scheduler, Bus, _schemas[2]);
            Director = NewDirector();
            Director.Attach(Content, Bus);
            await Content.AttachAsync();
            await Bus.PumpAsync();
        }

        /// <summary>A site's NPCs are killed: the kills are announced as the simulation announces them, with the site they belong to.</summary>
        public async Task KillAsync(long site, int solarSystemId, int count)
        {
            for (var i = 0; i < count; i++)
            {
                await Bus.PublishAsync("npc.killed", new { site, solarSystemId, characterId = 1, typeId = 0 });
            }
            await RunDueAndDeliverAsync();
        }

        public async Task FillAsync(int solarSystemId, CosmicSiteDirector? director = null)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await (director ?? Director).FillAsync(connection, transaction, solarSystemId);
            await transaction.CommitAsync();
        }

        /// <summary>Runs the due jobs (the content's deadlines) and delivers what they publish, until nothing is left.</summary>
        public async Task RunDueAndDeliverAsync()
        {
            for (var round = 0; round < 5; round++)
            {
                var ran = await Scheduler.RunDueAsync();
                for (var i = 0; i < 20 && (await Bus.PumpAsync()).Delivered > 0; i++)
                {
                }
                if (ran == 0)
                {
                    break;
                }
            }
        }

        public static bool NearAnAnchor(double[] position) => Anchors.Any(anchor =>
        {
            var distance = Math.Sqrt(Enumerable.Range(0, 3).Sum(k => Math.Pow(position[k] - anchor.Position[k], 2)));
            return distance >= SitePlacement.MinimumAu * ProbeScanMath.AuMeters - 1 && distance <= SitePlacement.MaximumAu * ProbeScanMath.AuMeters + 1;
        });

        public async ValueTask DisposeAsync()
        {
            foreach (var schema in _schemas)
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
}
