using System.Text.Json.Nodes;
using Eve.Events;
using static Eve.Structures.MoonExtractions;

namespace Eve.Structures.Tests;

/// <summary>
/// The drill's fracture and the cancel on PostgreSQL with the fake clock (ST-6a d; EVE.js FractureChunkWithStructure and CancelExtraction): a chunk fires only once it is
/// ready, the fracture completes 30 s later as fractured and leaves its ore field for 48 hours, a fire on a fracture in progress changes nothing, and a cancel ends the chunk.
/// </summary>
public class MoonExtractionFractureTests
{
    private const long Refinery = 1001;
    private const long Duration = 518_460;
    private const long FieldLifetimeMs = 172_800_000;

    private sealed class FakeMoonRules : IMoonRulesSource
    {
        public long FieldDecayMs { get; set; } = FieldLifetimeMs;

        public MoonRules RulesOf(int typeId) => new(10_800_000, FieldDecayMs);
    }

    private sealed class Watch
    {
        public required MoonExtractionStore Store { get; init; }
        public required MoonExtractions Moons { get; init; }
        public required FakeMoonRules Rules { get; init; }
        public List<MoonExtractionEnded> Ended { get; } = [];
        public List<MoonFractureStarted> FractureStarted { get; } = [];
    }

    private static async Task<Watch> Wire(Rig rig, string name)
    {
        var store = new MoonExtractionStore(rig.Source, rig.StructuresSchema);
        var rules = new FakeMoonRules();
        var moons = new MoonExtractions(rig.Store, store, rig.Scheduler, rig.Bus, rules);
        moons.Register();
        moons.Subscribe();
        var watch = new Watch { Store = store, Moons = moons, Rules = rules };
        rig.Bus.Subscribe($"test.fracture.ended.{name}", [EndedTopic], (context, _) =>
        {
            watch.Ended.Add(context.Event.Payload<MoonExtractionEnded>()!);
            return Task.CompletedTask;
        });
        rig.Bus.Subscribe($"test.fracture.started.{name}", [MoonExtractions.FractureStartedTopic], (context, _) =>
        {
            watch.FractureStarted.Add(context.Event.Payload<MoonFractureStarted>()!);
            return Task.CompletedTask;
        });
        await rig.Bus.PumpAsync();
        return watch;
    }

    /// <summary>A working refinery with its drill online and the moon's resources, and a started chunk.</summary>
    private static async Task<(Rig Rig, Watch Watch, MoonExtraction Chunk)> StartedChunk(Rig rig, string name)
    {
        await rig.Working(Refinery);
        await rig.Commit((c, t) => rig.Timers.SetServiceAsync(c, t, Refinery, StructureServiceIds.MoonMining, true));
        var watch = await Wire(rig, name);
        await rig.Commit((c, t) => watch.Store.ImportAsync(c, t, new JsonObject
        {
            ["resourcesByStructureID"] = new JsonObject
            {
                [Refinery.ToString()] = new JsonObject { ["moonID"] = 40_000_001, ["solarSystemID"] = 30_000_142, ["ownerID"] = 98_000_001, ["resources"] = new JsonObject { ["34"] = 1.0 } },
            },
        }));
        var result = await rig.Commit((c, t) => watch.Moons.StartAsync(c, t, Refinery, Duration, false));
        return (rig, watch, result.Extraction!);
    }

    private static async Task<FractureOutcome> Fire(Rig rig, Watch watch, long actorCharacterId = 0) =>
        await rig.Commit((c, t) => watch.Moons.FractureAsync(c, t, Refinery, actorCharacterId: actorCharacterId));

    private static async Task<MoonExtraction?> Row(Rig rig, Watch watch, long id) =>
        await rig.Commit((c, t) => watch.Store.GetAsync(c, t, id, false));

    [PgFact]
    public async Task A_chunk_that_is_not_ready_cannot_be_fired_on_and_changes_nothing()
    {
        await using var rig = await Rig.Create();
        var (_, watch, chunk) = await StartedChunk(rig, "early");

        Assert.Equal(FractureOutcome.NotReady, await Fire(rig, watch));

        Assert.Equal("active", (await Row(rig, watch, chunk.ExtractionId))!.State);
        Assert.Empty(watch.Ended);
    }

    [PgFact]
    public async Task A_ready_chunk_fires_and_completes_as_fractured_after_thirty_seconds_and_its_field_decays_after_48_hours()
    {
        await using var rig = await Rig.Create();
        var (_, watch, chunk) = await StartedChunk(rig, "fired");
        rig.Now = chunk.ChunkAvailableMs;
        await rig.Tick();

        Assert.Equal(FractureOutcome.Fired, await Fire(rig, watch));

        var firing = (await Row(rig, watch, chunk.ExtractionId))!;
        Assert.Equal(("fracturing", rig.Now + FractureMs), (firing.State, firing.FractureCompleteMs));
        var fractureJob = await rig.Scheduler.FindAsync(FractureJobKind, $"extraction:{chunk.ExtractionId}");
        Assert.Equal(rig.Now + FractureMs, fractureJob!.RunAtMs);

        rig.Now += FractureMs;
        await rig.Tick();
        await rig.Tick();

        var fractured = (await Row(rig, watch, chunk.ExtractionId))!;
        Assert.Equal(("completed", Fractured), (fractured.State, fractured.CompletedReason));
        Assert.Equal(Fractured, Assert.Single(watch.Ended).Reason);
        var field = await rig.Scheduler.FindAsync(FieldDecayJobKind, $"extraction:{chunk.ExtractionId}");
        Assert.Equal(rig.Now + FieldLifetimeMs, field!.RunAtMs);
    }


    [PgFact]
    public async Task Field_decay_uses_the_start_snapshot_after_restart_and_rig_rules_change()
    {
        await using var rig = await Rig.Create();
        var (_, watch, chunk) = await StartedChunk(rig, "field-snapshot");
        Assert.Equal(FieldLifetimeMs, chunk.FieldDecayMs);
        await rig.Restart();
        watch = await Wire(rig, "field-snapshot-restarted");
        watch.Rules.FieldDecayMs = FieldLifetimeMs * 2;
        rig.Now = chunk.ChunkAvailableMs;
        await rig.Tick();

        Assert.Equal(FractureOutcome.Fired, await Fire(rig, watch));
        rig.Now += FractureMs;
        await rig.Tick();

        var field = await rig.Scheduler.FindAsync(FieldDecayJobKind, $"extraction:{chunk.ExtractionId}");
        Assert.Equal(rig.Now + chunk.FieldDecayMs, field!.RunAtMs);
    }

    [PgFact]
    public async Task A_fire_on_a_fracture_in_progress_changes_nothing()
    {
        await using var rig = await Rig.Create();
        var (_, watch, chunk) = await StartedChunk(rig, "twice");
        rig.Now = chunk.ChunkAvailableMs;
        await rig.Tick();
        Assert.Equal(FractureOutcome.Fired, await Fire(rig, watch, 9_002));
        await rig.Bus.PumpAsync();
        Assert.Equal(9_002, Assert.Single(watch.FractureStarted).ActorCharacterId);
        var before = await Row(rig, watch, chunk.ExtractionId);

        rig.Now += 5_000;
        Assert.Equal(FractureOutcome.Firing, await Fire(rig, watch, 9_003));
        await rig.Bus.PumpAsync();

        var after = await Row(rig, watch, chunk.ExtractionId);
        Assert.Equal((before!.FractureStartedMs, before.FractureCompleteMs), (after!.FractureStartedMs, after.FractureCompleteMs));
        Assert.Single(watch.FractureStarted);
    }

    [PgFact]
    public async Task A_fire_with_no_extraction_answers_nothing_to_do()
    {
        await using var rig = await Rig.Create();
        var (_, watch, _) = await StartedChunk(rig, "none");

        Assert.Equal(FractureOutcome.NoExtraction, await rig.Commit((c, t) => watch.Moons.FractureAsync(c, t, Refinery + 1)));
    }

    [PgFact]
    public async Task A_cancel_ends_the_chunk_as_cancelled_and_its_deadlines_go_with_it()
    {
        await using var rig = await Rig.Create();
        var (_, watch, chunk) = await StartedChunk(rig, "cancel");

        Assert.True(await rig.Commit((c, t) => watch.Moons.CancelAsync(c, t, Refinery, actorCharacterId: 9_004)));
        await rig.Tick();

        var row = (await Row(rig, watch, chunk.ExtractionId))!;
        Assert.Equal(("completed", Cancelled), (row.State, row.CompletedReason));
        Assert.Equal(Cancelled, Assert.Single(watch.Ended).Reason);
        Assert.Equal(9_004, watch.Ended[0].ActorCharacterId);
        Assert.Null(await rig.Scheduler.FindAsync(ArrivalJobKind, $"extraction:{chunk.ExtractionId}"));
        Assert.Null(await rig.Scheduler.FindAsync(FractureJobKind, $"extraction:{chunk.ExtractionId}"));
        Assert.Null(await rig.Scheduler.FindAsync(FieldDecayJobKind, $"extraction:{chunk.ExtractionId}"));
        Assert.False(await rig.Commit((c, t) => watch.Moons.CancelAsync(c, t, Refinery)));
    }

    [PgFact]
    public async Task A_cancel_of_a_fracture_in_progress_stops_the_fracture()
    {
        await using var rig = await Rig.Create();
        var (_, watch, chunk) = await StartedChunk(rig, "cancel-firing");
        rig.Now = chunk.ChunkAvailableMs;
        await rig.Tick();
        await Fire(rig, watch);

        await rig.Commit((c, t) => watch.Moons.CancelAsync(c, t, Refinery));
        rig.Now += FractureMs;
        await rig.Tick();
        await rig.Tick();

        Assert.Equal(Cancelled, (await Row(rig, watch, chunk.ExtractionId))!.CompletedReason);
        Assert.Equal(Cancelled, Assert.Single(watch.Ended).Reason);
    }
}
