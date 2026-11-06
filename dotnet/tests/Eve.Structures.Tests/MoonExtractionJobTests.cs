using System.Text.Json.Nodes;
using Eve.Events;
using Eve.Scheduler;

namespace Eve.Structures.Tests;

/// <summary>
/// The moon extraction's deadlines on PostgreSQL with the fake clock (ST-6a c): the chunk turns ready at its arrival, a chunk nobody fires on decays at its natural time,
/// the ore field lasts 48 hours, a drill that goes offline or a destroyed structure ends an extraction, and the deadlines survive a restart and are re-armed after an import.
/// </summary>
public class MoonExtractionJobTests
{
    private const long Refinery = 1001;
    private const long Duration = 518_460;
    private const long FieldLifetimeMs = 172_800_000;

    private sealed class FakeMoonRules : IMoonRulesSource
    {
        public MoonRules RulesOf(int typeId) => new(10_800_000, FieldLifetimeMs);
    }

    private sealed class Moon
    {
        public required MoonExtractionStore Store { get; init; }
        public required MoonExtractions Moons { get; init; }
        public List<MoonChunkArrived> Arrived { get; } = [];
        public List<MoonExtractionEnded> Ended { get; } = [];
        public List<MoonFieldDecayed> Decayed { get; } = [];
    }

    /// <summary>The domain on the rig's scheduler and bus, with the three job handlers, the reactions and the collectors of its events.</summary>
    private static async Task<Moon> Wire(Rig rig, string name)
    {
        var store = new MoonExtractionStore(rig.Source, rig.StructuresSchema);
        var domain = new MoonExtractions(rig.Store, store, rig.Scheduler, rig.Bus, new FakeMoonRules());
        domain.Register();
        domain.Subscribe();
        var collected = new Moon { Store = store, Moons = domain };
        rig.Bus.Subscribe($"test.moon.arrived.{name}", [MoonExtractions.ChunkArrivedTopic], (context, _) =>
        {
            collected.Arrived.Add(context.Event.Payload<MoonChunkArrived>()!);
            return Task.CompletedTask;
        });
        rig.Bus.Subscribe($"test.moon.ended.{name}", [MoonExtractions.EndedTopic], (context, _) =>
        {
            collected.Ended.Add(context.Event.Payload<MoonExtractionEnded>()!);
            return Task.CompletedTask;
        });
        rig.Bus.Subscribe($"test.moon.decayed.{name}", [MoonExtractions.FieldDecayedTopic], (context, _) =>
        {
            collected.Decayed.Add(context.Event.Payload<MoonFieldDecayed>()!);
            return Task.CompletedTask;
        });
        // the first pump makes the new subscribers start "now"
        await rig.Bus.PumpAsync();
        return collected;
    }

    private static async Task<Rig> WithDrillAndMoon(Rig rig, long id = Refinery)
    {
        await rig.Working(id);
        await rig.Commit((c, t) => rig.Timers.SetServiceAsync(c, t, id, StructureServiceIds.MoonMining, true));
        await rig.Commit((c, t) => new MoonExtractionStore(rig.Source, rig.StructuresSchema).ImportAsync(c, t, new JsonObject
        {
            ["resourcesByStructureID"] = new JsonObject
            {
                [id.ToString()] = new JsonObject { ["moonID"] = 40_000_001, ["solarSystemID"] = 30_000_142, ["ownerID"] = 98_000_001, ["resources"] = new JsonObject { ["34"] = 0.75, ["20"] = 0.25 } },
            },
        }));
        return rig;
    }

    private static async Task<MoonExtraction> Start(Rig rig, Moon moon, long id = Refinery)
    {
        var result = await rig.Commit((c, t) => moon.Moons.StartAsync(c, t, id, Duration, false));
        Assert.Equal(MoonExtractions.StartOutcome.Started, result.Outcome);
        await rig.Tick();
        return result.Extraction!;
    }

    private static async Task<MoonExtraction?> Open(Rig rig, Moon moon, long id = Refinery) =>
        await rig.Commit((c, t) => moon.Store.ActiveForStructureAsync(c, t, id));

    [PgFact]
    public async Task The_chunk_turns_ready_at_its_arrival_and_the_arrival_is_announced_once()
    {
        await using var rig = await Rig.Create();
        await WithDrillAndMoon(rig);
        var moon = await Wire(rig, "arrival");
        var started = await Start(rig, moon);
        var arrival = await rig.Scheduler.FindAsync(MoonExtractions.ArrivalJobKind, $"extraction:{started.ExtractionId}");
        Assert.Equal(started.ChunkAvailableMs, arrival!.RunAtMs);

        rig.Now = started.ChunkAvailableMs;
        await rig.Tick();
        await rig.Tick();

        var ready = await Open(rig, moon);
        Assert.Equal(rig.Now, ready!.ReadyNotifiedMs);
        var announced = Assert.Single(moon.Arrived);
        Assert.Equal((started.ExtractionId, Refinery), (announced.ExtractionId, announced.StructureId));
    }

    [PgFact]
    public async Task A_chunk_nobody_fires_on_decays_at_its_natural_time_and_the_field_decays_after_48_hours()
    {
        await using var rig = await Rig.Create();
        await WithDrillAndMoon(rig);
        var moon = await Wire(rig, "decay");
        var started = await Start(rig, moon);

        rig.Now = started.NaturalDecayMs;
        await rig.Tick();

        Assert.Null(await Open(rig, moon));
        var ended = Assert.Single(moon.Ended);
        Assert.Equal((started.ExtractionId, MoonExtractions.Decayed), (ended.ExtractionId, ended.Reason));
        Assert.Null(await rig.Scheduler.FindAsync(MoonExtractions.ArrivalJobKind, $"extraction:{started.ExtractionId}"));
        var field = await rig.Scheduler.FindAsync(MoonExtractions.FieldDecayJobKind, $"extraction:{started.ExtractionId}");
        Assert.Equal(rig.Now + FieldLifetimeMs, field!.RunAtMs);

        rig.Now += FieldLifetimeMs;
        await rig.Tick();

        Assert.Single(moon.Decayed);
        var row = await rig.Commit((c, t) => moon.Store.GetAsync(c, t, started.ExtractionId, false));
        Assert.Equal(rig.Now, row!.FieldDecayedMs);
    }

    [PgFact]
    public async Task A_drill_that_goes_offline_before_the_arrival_ends_the_extraction_as_offlined()
    {
        await using var rig = await Rig.Create();
        await WithDrillAndMoon(rig);
        var moon = await Wire(rig, "offline");
        var started = await Start(rig, moon);

        await rig.Commit((c, t) => rig.Timers.SetServiceAsync(c, t, Refinery, StructureServiceIds.MoonMining, false));
        // the reaction publishes the end in its own transaction, which the bus delivers on the next pump
        await rig.Tick();
        await rig.Tick();

        Assert.Null(await Open(rig, moon));
        Assert.Equal(MoonExtractions.Offlined, Assert.Single(moon.Ended).Reason);
        Assert.Null(await rig.Scheduler.FindAsync(MoonExtractions.ArrivalJobKind, $"extraction:{started.ExtractionId}"));
        Assert.Null(await rig.Scheduler.FindAsync(MoonExtractions.FractureJobKind, $"extraction:{started.ExtractionId}"));
    }

    [PgFact]
    public async Task A_drill_that_goes_offline_after_the_chunk_arrived_changes_nothing()
    {
        await using var rig = await Rig.Create();
        await WithDrillAndMoon(rig);
        var moon = await Wire(rig, "late");
        var started = await Start(rig, moon);
        rig.Now = started.ChunkAvailableMs;
        await rig.Tick();

        await rig.Commit((c, t) => rig.Timers.SetServiceAsync(c, t, Refinery, StructureServiceIds.MoonMining, false));
        await rig.Tick();

        Assert.Equal(started.ExtractionId, (await Open(rig, moon))!.ExtractionId);
        Assert.Empty(moon.Ended);
    }

    [PgFact]
    public async Task A_destroyed_structure_ends_its_open_extraction_as_offlined()
    {
        await using var rig = await Rig.Create();
        await WithDrillAndMoon(rig);
        var moon = await Wire(rig, "destroyed");
        var started = await Start(rig, moon);

        // the timeline's own destruction is covered by the timeline tests; this publishes the event the timeline publishes for it
        await rig.Commit((c, t) => rig.Bus.PublishInTransactionAsync(c, t, StructureTimers.StateChangedTopic,
            new StructureStateChanged(Refinery, 30_000_142, StructureState.ShieldVulnerable, UpkeepState.FullPower, new StructureTimerInfo(0, 0, 0), false, "destroyed"), $"structure:{Refinery}"));
        // the reaction publishes the end in its own transaction, which the bus delivers on the next pump
        await rig.Tick();
        await rig.Tick();

        Assert.Null(await Open(rig, moon));
        var ended = Assert.Single(moon.Ended);
        Assert.Equal((started.ExtractionId, MoonExtractions.Offlined), (ended.ExtractionId, ended.Reason));
    }

    [PgFact]
    public async Task The_deadlines_survive_a_restart_and_the_chunk_arrives_once()
    {
        await using var rig = await Rig.Create();
        await WithDrillAndMoon(rig);
        var moon = await Wire(rig, "before");
        var started = await Start(rig, moon);

        await rig.Restart();
        moon = await Wire(rig, "after");
        rig.Now = started.ChunkAvailableMs;
        await rig.Tick();
        await rig.Tick();

        Assert.Single(moon.Arrived);
        Assert.Equal(rig.Now, (await Open(rig, moon))!.ReadyNotifiedMs);
    }

    [PgFact]
    public async Task An_extraction_imported_without_its_jobs_gets_them_back_when_it_is_ensured()
    {
        await using var rig = await Rig.Create();
        await WithDrillAndMoon(rig);
        var moon = await Wire(rig, "ensure");
        var store = moon.Store;
        var legacy = new JsonObject
        {
            ["extractions"] = new JsonObject
            {
                ["77"] = new JsonObject
                {
                    ["extractionID"] = 77, ["structureID"] = Refinery, ["moonID"] = 40_000_001, ["solarSystemID"] = 30_000_142, ["ownerCorpID"] = 98_000_001,
                    ["startMoveTimeMs"] = rig.Now - 1_000, ["chunkAvailableTimeMs"] = rig.Now + 3_600_000, ["naturalDecayTimeMs"] = rig.Now + 14_400_000,
                    ["durationSeconds"] = Duration, ["yieldMultiplier"] = 1, ["calendarEventID"] = 0, ["state"] = "active",
                },
            },
        };
        Assert.Equal(1, await rig.Commit((c, t) => store.ImportAsync(c, t, legacy)));
        Assert.Null(await rig.Scheduler.FindAsync(MoonExtractions.ArrivalJobKind, "extraction:77"));

        Assert.Equal(1, await rig.Commit((c, t) => moon.Moons.EnsureScheduledAsync(c, t)));
        Assert.Equal(rig.Now + 3_600_000, (await rig.Scheduler.FindAsync(MoonExtractions.ArrivalJobKind, "extraction:77"))!.RunAtMs);
        Assert.Equal(rig.Now + 14_400_000, (await rig.Scheduler.FindAsync(MoonExtractions.FractureJobKind, "extraction:77"))!.RunAtMs);
    }
}
