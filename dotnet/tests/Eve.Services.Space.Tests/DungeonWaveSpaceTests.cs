using Eve.Content;
using Eve.Dungeons;
using Eve.Events;
using Eve.Npc;
using Eve.Scheduler;
using Eve.Services.Space.Waves;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The dungeon waves in the space of the host: the NPCs of a wave are made by the space sessions (the same roll and spawn as <c>/npc</c>), the simulation says they died, the host
/// announces each kill on the bus, and the wave runtime counts it. Nothing here is faked but the simulation itself.
/// </summary>
public sealed class DungeonWaveSpaceTests
{
    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> InSpaceAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new Eve.Marshal.PyLong(session.GetInt("shipid")!.Value), Eve.Marshal.PyBool.False);
        return (rig, character, session);
    }

    [PgFact]
    public async Task A_system_owned_wave_waits_for_the_real_Host_undock_to_load_its_scene()
    {
        var rig = await Rig.CreateAsync();
        await using var _ = rig;
        await rig.UseBusAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var waves = await WaveRuntime.CreateAsync(rig);
        var systemId = (int)session.GetInt("solarsystemid2")!.Value;

        var start = await waves.Director.StartForSystemAsync(systemId, [0, 0, 0], 2173, "incursion:host-scene", DungeonWaveDirector.Lifetime);
        Assert.True(start.Started, start.Refusal);

        await waves.RunAsync();
        Assert.Empty(rig.Sim.Heard.OfType<SimSpawn>());
        Assert.DoesNotContain(rig.Log, line => line.Contains("NO_SCENE", StringComparison.Ordinal));
        Assert.Single(await waves.Director.ActivePocketsAsync());

        Assert.Same(rig.Ship, rig.Registry.Lookup("ship"));
        var boundShip = await rig.BindShipAsync(session);
        var answer = await rig.RegisteredCallAsync(session, boundShip, "Undock",
            new Eve.Marshal.PyLong(session.GetInt("shipid")!.Value), Eve.Marshal.PyBool.False);
        var undock = Assert.IsType<Eve.Marshal.PyTuple>(answer);
        Assert.Equal(new Eve.Marshal.PyStr(boundShip), undock.Items[0]);
        Assert.IsType<Eve.Marshal.PyLong>(undock.Items[1]);
        Assert.Null(session.GetInt("stationid"));
        Assert.Equal(systemId, session.GetInt("locationid"));
        Assert.True(rig.Space.HasPilotsIn(systemId));

        rig.Now += 15_000;
        await waves.RunAsync();

        var spawns = rig.Sim.Heard.OfType<SimSpawn>().ToList();
        Assert.Equal(8, spawns.Count);
        Assert.All(spawns, spawn => Assert.Equal(SpaceSessions.NpcBehavior, spawn.Behavior));
        Assert.DoesNotContain(rig.Log, line => line.Contains("NO_SCENE", StringComparison.Ordinal));
        Assert.Single(await waves.Director.ActivePocketsAsync());
    }

    [PgFact]
    public async Task A_dungeon_in_space_is_made_by_the_host_and_the_simulations_kills_count_its_waves_once()
    {
        var (rig, character, session) = await InSpaceAsync();
        await using var _ = rig;
        await rig.UseBusAsync();
        var waves = await WaveRuntime.CreateAsync(rig);
        var systemId = (int)session.GetInt("solarsystemid2")!.Value;

        var start = await waves.Director.StartAsync(character.CharacterId, systemId, [0, 0, 0], 2173);
        Assert.True(start.Started, start.Refusal);
        await waves.RunAsync();

        var spawns = rig.Sim.Heard.OfType<SimSpawn>().ToList();
        Assert.Equal(8, spawns.Count);
        Assert.All(spawns, spawn => Assert.Equal(SpaceSessions.NpcBehavior, spawn.Behavior));
        var ships = spawns.Select(spawn => spawn.Ship.ShipId).ToList();
        Assert.Equal(8, ships.Distinct().Count());

        foreach (var ship in ships.Take(7))
        {
            rig.Sim.Report(new SimDestroyed((ulong)session.SessionId, ship, 0, [0, 0, 0], [1, 0, 0], 1, 0));
        }
        await waves.RunAsync();
        Assert.Empty(Completed(rig));

        rig.Sim.Report(new SimDestroyed((ulong)session.SessionId, ships[7], 0, [0, 0, 0], [1, 0, 0], 1, 0));
        await waves.RunAsync();
        Assert.Equal(8, Announced(rig, "npc.killed").Count);
        Assert.Single(Completed(rig));
        Assert.Equal(8, rig.Sim.Heard.OfType<SimSpawn>().Count());
    }

    private static List<(string Topic, string Json)> Announced(Rig rig, string topic)    {
        lock (rig.Announced)
        {
            return rig.Announced.Where(announced => announced.Topic == topic).ToList();
        }
    }

    private static List<(string Topic, string Json)> Completed(Rig rig) => Announced(rig, "content.dungeon.completed");

    /// <summary>The wave runtime over the rig's bus (the one the space announces on), its own scheduler and schemas, and the rig's clock.</summary>
    private sealed class WaveRuntime
    {
        private readonly Rig _rig;
        private readonly DurableScheduler _scheduler;

        private WaveRuntime(Rig rig, DurableScheduler scheduler, DungeonWaveDirector director)
        {
            _rig = rig;
            _scheduler = scheduler;
            Director = director;
        }

        public DungeonWaveDirector Director { get; }

        public static async Task<WaveRuntime> CreateAsync(Rig rig)
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            string[] schemas = ["wsp_sch_" + id, "wsp_cnt_" + id, "wsp_spc_" + id];
            rig.KeepSchemas(schemas);
            await DurableScheduler.MigrateAsync(rig.Source, schemas[0]);
            await ContentRuntime.MigrateAsync(rig.Source, schemas[1]);
            await DungeonWaveDirector.MigrateAsync(rig.Source, schemas[2]);
            var scheduler = new DurableScheduler(rig.Source, () => rig.Now, null, schemas[0]);
            var content = new ContentRuntime(rig.Source, scheduler, rig.Bus!, schemas[1]);
            var director = new DungeonWaveDirector(
                rig.Source, content, scheduler, rig.Bus!, DungeonCatalog.LoadEmbedded(), WaveRules.LoadEmbedded(), new NpcSelector(NpcAuthority.Load()),
                new SpaceDungeonNpcs(rig.Space), schemas[2]);
            await director.AttachAsync();
            await content.AttachAsync();
            await rig.Bus!.PumpAsync();
            return new WaveRuntime(rig, scheduler, director);
        }

        /// <summary>Lets the space's background announcements land, then runs the due jobs and delivers what they publish, until nothing is left.</summary>
        public async Task RunAsync()
        {
            await _rig.Space.QuiesceAsync();
            for (var round = 0; round < 6; round++)
            {
                var ran = await _scheduler.RunDueAsync();
                var delivered = 0;
                for (var i = 0; i < 20; i++)
                {
                    var pumped = (await _rig.Bus!.PumpAsync()).Delivered;
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
    }
}
