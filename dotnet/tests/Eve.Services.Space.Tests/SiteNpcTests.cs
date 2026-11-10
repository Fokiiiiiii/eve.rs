using System.Text.Json;
using Eve.Content;
using Eve.Marshal;
using Eve.Npc;
using Eve.Services;
using Eve.Services.Space.Sites;
using Eve.Sde;
using Eve.Space;
using Eve.Universe;

namespace Eve.Services.Space.Tests;

/// <summary>The NPC waves of the cosmic sites through the host's own NPC path: the spawner, the simulation's kills and the content runtime.</summary>
public sealed class SiteNpcTests
{
    // a wave is spread by the spawner's formation: a base of 5 km, a window of 2 km and 1.25 km for each NPC of the batch, at most 12 km more
    private const double NearMeters = 20_000;

    /// <summary>Every wave query of the site table names an NPC profile or pool that the selector can roll.</summary>
    [Fact]
    public void Every_wave_of_the_site_table_names_an_npc_profile_or_pool()
    {
        var selector = new NpcSelector(NpcAuthority.Load());
        var queries = SiteTemplates.Load().All.SelectMany(template => template.Waves).Select(wave => wave.Query).Distinct().ToArray();
        Assert.NotEmpty(queries);
        Assert.All(queries, query => Assert.NotNull(selector.Select(query).Selection));
    }

    [PgFact]
    public async Task A_pilot_in_a_system_brings_the_first_wave_of_each_site_to_its_place_once()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;
        var host = await SiteHost.CreateAsync(rig);
        var system = (int)session.GetInt("solarsystemid")!.Value;
        await host.FillAsync(system);
        var sites = await host.Director.ActiveAsync(system);
        var first = sites.Sum(site => FirstWave(host, site));

        await host.Waves.SpawnForSystemAsync(system);
        var spawns = SiteSpawns(rig, sites);
        Assert.Equal(first, spawns.Length);

        await host.Waves.SpawnForSystemAsync(system);
        Assert.Equal(first, SiteSpawns(rig, sites).Length);
    }

    [PgFact]
    public async Task Core_citadel_spawns_its_documented_sleeper_types_and_restores_the_remaining_types_after_a_scene_rebuild()
    {
        var (rig, character, session) = await InSpaceAsync();
        await using var _ = rig;
        var host = await SiteHost.CreateAsync(rig);
        var system = (int)session.GetInt("solarsystemid")!.Value;
        var site = await host.StartCoreCitadelAsync(system);

        await host.Waves.SpawnForSystemAsync(system);
        var firstWave = SiteSpawns(rig, [site]);
        Assert.Equal([30196, 30197, 30198], firstWave.Select(spawn => spawn.Ship.TypeId).Order().ToArray());

        var keeper = firstWave.Single(spawn => spawn.Ship.TypeId == 30197);
        rig.Sim.Say(Destroyed(keeper, character.ShipId));
        await WaitForAsync(rig, async () =>
            (await host.Content.ObjectivesOfAsync(site.InstanceId)).Single(objective => objective.Index == 0).Progress == 1);
        using (var state = JsonDocument.Parse((await host.Content.GetAsync(site.InstanceId))!.DataJson))
        {
            Assert.Equal(1, state.RootElement.GetProperty("npcTypeKills").GetProperty("30197").GetInt32());
        }

        rig.Space.ForgetNpcsOf(system);
        var beforeRebuild = SiteSpawns(rig, [site]).Length;
        await host.Waves.SpawnForSystemAsync(system);
        var restored = SiteSpawns(rig, [site]).Skip(beforeRebuild).ToArray();
        Assert.Equal([30196, 30198], restored.Select(spawn => spawn.Ship.TypeId).Order().ToArray());
        Assert.Equal(1, SiteSpawns(rig, [site]).Count(spawn => spawn.Ship.TypeId == 30197));

        foreach (var spawn in restored)
        {
            rig.Sim.Say(Destroyed(spawn, character.ShipId));
        }
        var template = host.Templates.ById(site.TemplateId)!;
        await WaitForAsync(rig, () => Task.FromResult(SiteSpawns(rig, [site]).Length == firstWave.Length + restored.Length + template.Waves[1].Amount));
        var secondWave = SiteSpawns(rig, [site]).Skip(firstWave.Length + restored.Length).ToArray();
        Assert.Equal(8, secondWave.Count(spawn => spawn.Ship.TypeId == 30216));
        Assert.Equal(3, secondWave.Count(spawn => spawn.Ship.TypeId == 30196));
        Assert.Equal(1, secondWave.Count(spawn => spawn.Ship.TypeId == 30198));

        foreach (var spawn in secondWave)
        {
            rig.Sim.Say(Destroyed(spawn, character.ShipId));
        }
        var thirdWaveEnd = firstWave.Length + restored.Length + secondWave.Length + template.Waves[2].Amount;
        await WaitForAsync(rig, () => Task.FromResult(SiteSpawns(rig, [site]).Length == thirdWaveEnd));
        var thirdWave = SiteSpawns(rig, [site]).Skip(firstWave.Length + restored.Length + secondWave.Length).ToArray();
        Assert.Equal(8, thirdWave.Count(spawn => spawn.Ship.TypeId == 30216));
        Assert.Equal(3, thirdWave.Count(spawn => spawn.Ship.TypeId == 30197));
        Assert.Equal(3, thirdWave.Count(spawn => spawn.Ship.TypeId == 30198));

        foreach (var spawn in thirdWave)
        {
            rig.Sim.Say(Destroyed(spawn, character.ShipId));
        }
        var fourthWaveEnd = thirdWaveEnd + template.Waves[3].Amount;
        await WaitForAsync(rig, () => Task.FromResult(SiteSpawns(rig, [site]).Length == fourthWaveEnd));
        var fourthWave = SiteSpawns(rig, [site]).Skip(thirdWaveEnd).ToArray();
        Assert.Equal(8, fourthWave.Count(spawn => spawn.Ship.TypeId == 30216));
        Assert.Equal(2, fourthWave.Count(spawn => spawn.Ship.TypeId == 30199));
    }

    [PgFact]
    public async Task A_kill_of_a_site_npc_counts_for_its_wave_and_the_next_wave_is_made_at_the_site()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var host = await SiteHost.CreateAsync(rig);
        var system = (int)session.GetInt("solarsystemid")!.Value;
        await host.FillAsync(system);
        await host.Waves.SpawnForSystemAsync(system);
        var anomaly = (await host.Director.ActiveAsync(system)).Single(site => site.IsAnomaly);
        var waves = host.Templates.ById(anomaly.TemplateId)!.Waves;
        var anomalySpawns = rig.Sim.Heard.OfType<SimSpawn>().Where(spawn => Near(spawn.Position, anomaly.Position)).ToArray();
        Assert.Equal(waves[0].Amount, anomalySpawns.Length);

        var announced = new List<JsonElement>();
        rig.Bus!.Subscribe("test.site.kills", ["npc.killed"], (context, _) =>
        {
            announced.Add(context.Event.Json());
            return Task.CompletedTask;
        });
        // a subscriber starts at the bus's horizon when it is first pumped: pump it before the kills, or the kills published before it are never delivered to it
        await rig.Bus.PumpAsync();
        var spawnsBefore = SiteSpawns(rig, await host.Director.ActiveAsync(system)).Length;
        foreach (var spawn in anomalySpawns)
        {
            rig.Sim.Say(Destroyed(spawn, c.ShipId));
        }

        // the kills are announced in the background: wait until the subscriber has every one of them, then the objective they complete
        int AnnouncedKills() => announced.Count(kill => kill.TryGetProperty("site", out var site) && site.GetInt64() == anomaly.InstanceId);
        await WaitForAsync(rig, () => Task.FromResult(AnnouncedKills() == anomalySpawns.Length));
        await WaitForAsync(rig, async () => (await host.Content.ObjectivesOfAsync(anomaly.InstanceId)).Any(objective => objective.Index == 0 && objective.State == "done"));
        var sites = await host.Director.ActiveAsync(system);
        await WaitForAsync(rig, () => Task.FromResult(SiteSpawns(rig, sites).Length == spawnsBefore + (waves.Count > 1 ? waves[1].Amount : 0)));
        var next = SiteSpawns(rig, sites).Skip(spawnsBefore).ToArray();
        Assert.Equal(waves[1].Amount, next.Length);
        Assert.All(next, spawn => Assert.True(Near(spawn.Position, anomaly.Position)));
        Assert.Equal(anomalySpawns.Length, AnnouncedKills());
    }

    [PgFact]
    public async Task A_scene_made_again_brings_back_only_the_kills_still_to_come()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var host = await SiteHost.CreateAsync(rig);
        var system = (int)session.GetInt("solarsystemid")!.Value;
        await host.FillAsync(system);
        await host.Waves.SpawnForSystemAsync(system);
        var sites = await host.Director.ActiveAsync(system);
        var anomaly = sites.Single(site => site.IsAnomaly);
        var anomalyWave = host.Templates.ById(anomaly.TemplateId)!.Waves[0].Amount;
        var half = anomalyWave / 2;
        var anomalySpawns = rig.Sim.Heard.OfType<SimSpawn>().Where(spawn => Near(spawn.Position, anomaly.Position)).Take(half).ToArray();
        foreach (var spawn in anomalySpawns)
        {
            rig.Sim.Say(Destroyed(spawn, c.ShipId));
        }
        await WaitForAsync(rig, async () => (await host.Content.ObjectivesOfAsync(anomaly.InstanceId)).Any(objective => objective.Index == 0 && objective.Progress == half));

        // the scene is made again (everybody left the system and a pilot came back): the host forgets the NPCs it had of it
        rig.Space.ForgetNpcsOf(system);
        var before = SiteSpawns(rig, sites).Length;
        await host.Waves.SpawnForSystemAsync(system);

        var expected = sites.Sum(site => FirstWave(host, site)) - half;
        var made = SiteSpawns(rig, sites).Skip(before).ToArray();
        Assert.Equal(expected, made.Length);
        Assert.Equal(anomalyWave - half, made.Count(spawn => Near(spawn.Position, anomaly.Position)));
    }

    [PgFact]
    public async Task Without_a_pilot_in_the_system_no_wave_is_made()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.UseBusAsync();
        var host = await SiteHost.CreateAsync(rig);
        const int jita = 30000142;
        await host.FillAsync(jita);
        Assert.NotEmpty(await host.Director.ActiveAsync(jita));

        await host.Waves.SpawnForSystemAsync(jita);
        Assert.Empty(SiteSpawns(rig, await host.Director.ActiveAsync(jita)));
    }

    /// <summary>The NPCs of a site's first objective: nothing when the site starts with its containers (a data or relic site may hold no NPCs).</summary>
    private static int FirstWave(SiteHost host, SiteRecord site) =>
        host.Templates.ById(site.TemplateId)!.Waves is { Count: > 0 } waves ? waves[0].Amount : 0;

    /// <summary>The spawns made near one of the sites (the belt mechanism makes its own rats at the belts, which no site is near).</summary>
    private static SimSpawn[] SiteSpawns(Rig rig, IReadOnlyList<SiteRecord> sites) =>
        rig.Sim.Heard.OfType<SimSpawn>().Where(spawn => sites.Any(site => Near(spawn.Position, site.Position))).ToArray();

    private static bool Near(IReadOnlyList<double> position, double[] site) =>
        Math.Sqrt(Enumerable.Range(0, 3).Sum(k => Math.Pow(position[k] - site[k], 2))) <= NearMeters;

    private static SimDestroyed Destroyed(SimSpawn spawn, long killerShip) =>
        new(1UL << 62, spawn.Ship.ShipId, 4_300_000_000_000_001, [0, 0, 0], [1, 0, 0], 47, killerShip);

    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> InSpaceAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        await rig.UseBusAsync();
        Events.Of(session);
        return (rig, character, session);
    }

    /// <summary>Delivers the bus until the condition holds (the kills are announced in the background).</summary>
    private static async Task WaitForAsync(Rig rig, Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            await rig.Bus!.PumpAsync();
            if (await condition())
            {
                return;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("the site's NPC wave did not come in time");
    }

    /// <summary>A host's cosmic sites over the rig: the content runtime, the director and the NPC waves on the rig's own bus and scheduler.</summary>
    private sealed class SiteHost
    {
        private SiteHost(Rig rig, CosmicSiteDirector director, ContentRuntime content, SiteNpcs waves, SiteTemplates templates)
        {
            Rig = rig;
            Director = director;
            Content = content;
            Waves = waves;
            Templates = templates;
        }

        public Rig Rig { get; }
        public CosmicSiteDirector Director { get; }
        public ContentRuntime Content { get; }
        public SiteNpcs Waves { get; }
        public SiteTemplates Templates { get; }

        public static async Task<SiteHost> CreateAsync(Rig rig)
        {
            var schema = "site_npc_" + Guid.NewGuid().ToString("N")[..12];
            rig.KeepSchemas(schema);
            await ContentRuntime.MigrateAsync(rig.Source, schema);
            var content = new ContentRuntime(rig.Source, rig.Scheduler, rig.Bus!, schema);
            var templates = SiteTemplates.Load();
            var director = new CosmicSiteDirector(rig.Source, content, rig.Scheduler, templates, new SdeSiteAnchors(new SdeReader(rig.Source)), rig.Systems);
            director.Attach(content, rig.Bus!);
            await content.AttachAsync();
            var waves = new SiteNpcs(director, content, rig.Space);
            waves.Register(rig.Bus!);
            await rig.Bus!.PumpAsync();
            return new SiteHost(rig, director, content, waves, templates);
        }

        public async Task FillAsync(int solarSystemId)
        {
            await using var connection = await Rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Director.FillAsync(connection, transaction, solarSystemId);
            await transaction.CommitAsync();
        }

        public async Task<SiteRecord> StartCoreCitadelAsync(int solarSystemId)
        {
            var template = Templates.ById("wormhole-3704")!;
            await using var connection = await Rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var started = await Content.StartAsync(connection, transaction, template.DefinitionId, solarSystemId, new
            {
                siteID = CosmicSiteDirector.SiteIdOf(solarSystemId, 0),
                solarSystemID = solarSystemId,
                template = template.Id,
                family = template.Family,
                slot = 0,
                targetID = "test-core-citadel",
                position = new[] { 12_000d, 24_000d, 36_000d },
                anchorID = 1,
                difficulty = template.Difficulty,
                dungeonID = template.DungeonId,
                entryObjectTypeID = template.EntryObjectTypeId,
            });
            await transaction.CommitAsync();
            return (await Director.ActiveAsync(solarSystemId)).Single(site => site.InstanceId == started.Id);
        }
    }
}
