using Npgsql;
using System.Text.Json.Nodes;
using static Eve.Structures.MoonExtractions;

namespace Eve.Structures.Tests;

/// <summary>
/// The moon drill's start on PostgreSQL (ST-6a b; EVE.js moonExtractionsService StartNewExtraction): the refusals of the drill and of the moon, the duration clamped to
/// 6 to 56 days, the chunk at 720 000 m3 a day (30 000 m3 an hour) split by the moon's composition, the running extraction answered as it is, and the calendar event id.
/// </summary>
public class MoonExtractionStartTests
{
    private const long Refinery = 1001, Other = 1002;

    private sealed class FakeMoonRules : IMoonRulesSource
    {
        private readonly MoonRules _rules;

        public FakeMoonRules(long autoFractureDelayMs = 10_800_000, long fieldDecayMs = 172_800_000) =>
            _rules = new MoonRules(autoFractureDelayMs, fieldDecayMs);

        public MoonRules RulesOf(int typeId) => _rules;
    }

    private static JsonObject Profile(params long[] structures) => new()
    {
        ["resourcesByStructureID"] = new JsonObject(structures.Select(id => new KeyValuePair<string, JsonNode?>(id.ToString(), new JsonObject
        {
            ["moonID"] = 40_000_001,
            ["solarSystemID"] = 30_000_142,
            ["ownerID"] = 98_000_001,
            ["resources"] = new JsonObject { ["34"] = 0.75, ["20"] = 0.25 },
        }))),
    };

    private static async Task<(MoonExtractionStore Store, MoonExtractions Moons)> Services(
        Rig rig, Func<NpgsqlConnection, NpgsqlTransaction, long, CancellationToken, Task<MoonRules>>? effectiveRules = null)
    {
        var store = new MoonExtractionStore(rig.Source, rig.StructuresSchema);
        return (store, new MoonExtractions(rig.Store, store, rig.Scheduler, rig.Bus, new FakeMoonRules(), effectiveRules));
    }

    /// <summary>A working structure whose moon drill is online (service 14, the Standup Moon Drill I).</summary>
    private static async Task<long> Refinery_with_drill(Rig rig, long id)
    {
        await rig.Working(id);
        await rig.Commit((c, t) => rig.Timers.SetServiceAsync(c, t, id, StructureServiceIds.MoonMining, true));
        return id;
    }

    private static async Task<StartResult> Start(Rig rig, MoonExtractions moons, long structureId, long durationSeconds, bool addToCalendar = false, long actorCharacterId = 0) =>
        await rig.Commit((c, t) => moons.StartAsync(c, t, structureId, durationSeconds, addToCalendar, actorCharacterId: actorCharacterId));

    [PgFact]
    public async Task Without_an_online_drill_the_start_is_refused_and_writes_nothing()
    {
        await using var rig = await Rig.Create();
        var (store, moons) = await Services(rig);
        await rig.Working(Refinery);
        await rig.Commit((c, t) => store.ImportAsync(c, t, Profile(Refinery)));

        var result = await Start(rig, moons, Refinery, 518_460);

        Assert.Equal(StartOutcome.DrillOffline, result.Outcome);
        Assert.Null(result.Extraction);
        Assert.Null(await rig.Commit((c, t) => store.ActiveForStructureAsync(c, t, Refinery)));
    }

    [PgFact]
    public async Task Without_the_moons_resources_the_start_is_refused()
    {
        await using var rig = await Rig.Create();
        var (_, moons) = await Services(rig);
        await Refinery_with_drill(rig, Refinery);

        Assert.Equal(StartOutcome.NoMoonResources, (await Start(rig, moons, Refinery, 518_460)).Outcome);
    }

    [PgFact]
    public async Task An_unknown_structure_is_refused()
    {
        await using var rig = await Rig.Create();
        var (_, moons) = await Services(rig);

        Assert.Equal(StartOutcome.NoStructure, (await Start(rig, moons, 9_999, 518_460)).Outcome);
    }

    [PgFact]
    public async Task The_chunk_grows_at_30000_m3_an_hour_split_by_the_moon_and_its_deadlines_follow_it()
    {
        await using var rig = await Rig.Create();
        var (store, moons) = await Services(rig);
        await Refinery_with_drill(rig, Refinery);
        await rig.Commit((c, t) => store.ImportAsync(c, t, Profile(Refinery)));
        const long duration = 518_460;

        var result = await Start(rig, moons, Refinery, duration);

        Assert.Equal(StartOutcome.Started, result.Outcome);
        var e = result.Extraction!;
        Assert.Equal("active", e.State);
        Assert.Equal(duration, e.DurationSeconds);
        Assert.Equal(rig.Now + duration * 1000, e.ChunkAvailableMs);
        Assert.Equal(e.ChunkAvailableMs + 10_800_000, e.NaturalDecayMs);
        Assert.Equal(98_000_001, e.OwnerCorporationId);
        Assert.Equal(30_000_142, e.SolarSystemId);
        var total = e.OreVolumeM3.Values.Sum();
        Assert.Equal(720_000.0 * duration / 86_400, total, 6);
        Assert.Equal(30_000.0, total / (duration / 3_600.0), 6);
        Assert.Equal(0.75 * total, e.OreVolumeM3[34], 6);
        Assert.Equal(0.25 * total, e.OreVolumeM3[20], 6);
        Assert.Equal(0, e.CalendarEventId);
    }

    [PgFact]
    public async Task The_duration_is_clamped_to_six_to_fifty_six_days()
    {
        await using var rig = await Rig.Create();
        var (store, moons) = await Services(rig);
        await Refinery_with_drill(rig, Refinery);
        await Refinery_with_drill(rig, Other);
        await rig.Commit((c, t) => store.ImportAsync(c, t, Profile(Refinery, Other)));

        Assert.Equal(518_460, (await Start(rig, moons, Refinery, 1)).Extraction!.DurationSeconds);
        Assert.Equal(4_838_400, (await Start(rig, moons, Other, 99_999_999)).Extraction!.DurationSeconds);
    }

    [PgFact]
    public async Task A_second_start_on_the_same_structure_answers_the_running_extraction()
    {
        await using var rig = await Rig.Create();
        var (store, moons) = await Services(rig);
        await Refinery_with_drill(rig, Refinery);
        await rig.Commit((c, t) => store.ImportAsync(c, t, Profile(Refinery)));
        var first = (await Start(rig, moons, Refinery, 518_460)).Extraction!;

        var second = await Start(rig, moons, Refinery, 4_838_400);

        Assert.Equal(StartOutcome.Existing, second.Outcome);
        Assert.Equal(first.ExtractionId, second.Extraction!.ExtractionId);
        Assert.Equal(518_460, second.Extraction.DurationSeconds);
    }

    [PgFact]
    public async Task Calendar_event_ids_are_one_past_the_highest_and_only_for_extractions_added_to_the_calendar()
    {
        await using var rig = await Rig.Create();
        var (store, moons) = await Services(rig);
        await Refinery_with_drill(rig, Refinery);
        await Refinery_with_drill(rig, Other);
        await rig.Commit((c, t) => store.ImportAsync(c, t, Profile(Refinery, Other)));

        var plain = (await Start(rig, moons, Refinery, 518_460, addToCalendar: false)).Extraction!;
        var booked = (await Start(rig, moons, Other, 518_460, addToCalendar: true)).Extraction!;

        Assert.Equal(0, plain.CalendarEventId);
        Assert.Equal(1, booked.CalendarEventId);
        Assert.True(booked.ExtractionId > plain.ExtractionId);
    }


    [PgFact]
    public async Task Start_uses_rig_rules_once_and_publishes_the_actor_with_the_extraction_snapshot()
    {
        await using var rig = await Rig.Create();
        var calls = 0;
        const long fieldDecayMs = 86_400_000;
        var (store, moons) = await Services(rig, (_, _, structureId, _) =>
        {
            Assert.Equal(Refinery, structureId);
            calls++;
            return Task.FromResult(new MoonRules(7_200_000, fieldDecayMs, 1.25));
        });
        await Refinery_with_drill(rig, Refinery);
        var profile = Profile(Refinery);
        ((JsonObject)profile["resourcesByStructureID"]![Refinery.ToString()]!)["yieldMultiplier"] = 1.2;
        await rig.Commit((c, t) => store.ImportAsync(c, t, profile));
        var started = new List<MoonExtractionStarted>();
        rig.Bus.Subscribe("test.moon.started.rig-rules", [MoonExtractions.StartedTopic], (context, _) =>
        {
            started.Add(context.Event.Payload<MoonExtractionStarted>()!);
            return Task.CompletedTask;
        });
        await rig.Bus.PumpAsync();
        const long duration = 518_460;

        var first = await Start(rig, moons, Refinery, duration, actorCharacterId: 9_001);
        await rig.Bus.PumpAsync();
        var retry = await Start(rig, moons, Refinery, duration, actorCharacterId: 9_002);
        await rig.Bus.PumpAsync();

        Assert.Equal(StartOutcome.Started, first.Outcome);
        Assert.Equal(StartOutcome.Existing, retry.Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(1.5, first.Extraction!.YieldMultiplier, 6);
        Assert.Equal(fieldDecayMs, first.Extraction.FieldDecayMs);
        Assert.Equal(first.Extraction.ChunkAvailableMs + 7_200_000, first.Extraction.NaturalDecayMs);
        Assert.Single(started);
        Assert.Equal(9_001, started[0].ActorCharacterId);
        Assert.Equal(first.Extraction.ExtractionId, started[0].ExtractionId);
    }


    [PgFact]
    public async Task Concurrent_starts_on_one_structure_create_one_extraction_and_one_start_event()
    {
        await using var rig = await Rig.Create();
        var (store, moons) = await Services(rig);
        await Refinery_with_drill(rig, Refinery);
        await rig.Commit((c, t) => store.ImportAsync(c, t, Profile(Refinery)));
        var started = new List<MoonExtractionStarted>();
        rig.Bus.Subscribe("test.moon.started.concurrent", [MoonExtractions.StartedTopic], (context, _) =>
        {
            started.Add(context.Event.Payload<MoonExtractionStarted>()!);
            return Task.CompletedTask;
        });
        await rig.Bus.PumpAsync();

        var results = await Task.WhenAll(
            Start(rig, moons, Refinery, 518_460, actorCharacterId: 9_011),
            Start(rig, moons, Refinery, 518_460, actorCharacterId: 9_012));
        await rig.Bus.PumpAsync();

        Assert.Equal(1, results.Count(result => result.Outcome == StartOutcome.Started));
        Assert.Equal(1, results.Count(result => result.Outcome == StartOutcome.Existing));
        Assert.NotNull(await rig.Commit((c, t) => store.ActiveForStructureAsync(c, t, Refinery)));
        Assert.Single(started);
    }


    [PgFact]
    public async Task Failed_effective_rules_leave_no_extraction()
    {
        await using var rig = await Rig.Create();
        var (store, moons) = await Services(rig, (_, _, _, _) =>
            Task.FromException<MoonRules>(new InvalidOperationException("rig rules unavailable")));
        await Refinery_with_drill(rig, Refinery);
        await rig.Commit((c, t) => store.ImportAsync(c, t, Profile(Refinery)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Start(rig, moons, Refinery, 518_460));

        Assert.Equal("rig rules unavailable", error.Message);
        Assert.Null(await rig.Commit((c, t) => store.ActiveForStructureAsync(c, t, Refinery)));
    }

    [PgFact]
    public async Task Rehydrating_extractions_reschedules_the_persisted_deadline_and_field_decay_after_rules_change()
    {
        await using var rig = await Rig.Create();
        var (store, moons) = await Services(rig);
        await Refinery_with_drill(rig, Refinery);
        await rig.Commit((c, t) => store.ImportAsync(c, t, Profile(Refinery)));
        var extraction = (await Start(rig, moons, Refinery, 518_460)).Extraction!;

        await rig.Restart();
        var restarted = new MoonExtractions(rig.Store, store, rig.Scheduler, rig.Bus, new FakeMoonRules(99_000_000, 999_000_000));
        Assert.Equal(1, await rig.Commit((c, t) => restarted.EnsureScheduledAsync(c, t)));

        var persisted = await rig.Commit((c, t) => store.GetAsync(c, t, extraction.ExtractionId, false));
        var fracture = await rig.Scheduler.FindAsync(FractureJobKind, $"extraction:{extraction.ExtractionId}");
        Assert.Equal(extraction.NaturalDecayMs, fracture!.RunAtMs);
        Assert.Equal(extraction.FieldDecayMs, persisted!.FieldDecayMs);
    }
}
