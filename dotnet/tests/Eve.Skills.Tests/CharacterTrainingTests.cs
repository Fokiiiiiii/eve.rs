namespace Eve.Skills.Tests;

/// <summary>Training against PostgreSQL with a clock the test moves.</summary>
internal sealed class TrainingRig : IAsyncDisposable
{
    public const long Character = 1001;
    public const long Account = 7;
    public const long ClockStart = 1_700_000_000_000;

    private static readonly SkillType Turret = new(3301, "Small Hybrid Turret", 255, "Gunnery", true, 1, 167, 168);
    private static readonly SkillType Command = new(3327, "Spaceship Command", 257, "Spaceship Command", true, 1, 167, 168);
    private static readonly SkillType Rank5 = new(3317, "Rank Five", 257, "Spaceship Command", true, 5, 167, 168);

    private readonly PgSchema _schema;

    public long ClockMs = ClockStart;

    public List<TrainingUpdate> Settlements { get; } = [];

    private TrainingRig(PgSchema schema, TrainingSettings settings)
    {
        _schema = schema;
        Catalog = new SkillCatalog([Turret, Command, Rank5], new Dictionary<int, IReadOnlyList<StartingSkill>>());
        Store = new PgTrainingStore(schema.Source, schema.Name);
        Training = new CharacterTraining(schema.Source, Catalog, schema.Store, Store, settings, () => ClockMs);
        Training.Settled += update =>
        {
            Settlements.Add(update);
            return Task.CompletedTask;
        };
    }

    public SkillCatalog Catalog { get; }

    public PgTrainingStore Store { get; }

    public CharacterTraining Training { get; }

    public PgSkillStore Skills => _schema.Store;

    public static async Task<TrainingRig> CreateAsync(TrainingSettings? settings = null, long character = Character)
    {
        var rig = new TrainingRig(await PgSchema.CreateAsync(), settings ?? new TrainingSettings());
        await rig.GiveAsync(character);
        return rig;
    }

    /// <summary>A new character's skills: Small Hybrid Turret I, Spaceship Command III and Rank Five I.</summary>
    public Task GiveAsync(long character) => _schema.GrantAsync(character, [new CharacterSkill(3301, 1, 250), new CharacterSkill(3327, 3, 8000), new CharacterSkill(3317, 1, 1250)]);

    public void Wait(TimeSpan time) => ClockMs += (long)time.TotalMilliseconds;

    public static IReadOnlyList<long> Alone => [Character];

    public Task<SaveOutcome?> StartAsync(int type, int level = 0, long character = Character, IReadOnlyList<long>? account = null) =>
        Training.StartAsync(character, Account, type, level, account ?? [character]);

    public Task<QueueSaved> SavedAsync(SaveOutcome? outcome) => Task.FromResult(Assert.IsType<QueueSaved>(outcome));

    public Task<SaveOutcome> SaveAsync(bool activate, params (int Type, int Level)[] entries) =>
        Training.SaveQueueAsync(Character, Account, [.. entries.Select(e => new QueueEntry(e.Type, e.Level))], activate, Alone);

    public async ValueTask DisposeAsync() => await _schema.DisposeAsync();
}

public class CharacterTrainingTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [PgFact]
    public async Task A_new_character_has_an_empty_queue_and_its_skills_as_stored()
    {
        await using var rig = await TrainingRig.CreateAsync();
        var view = await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account);
        Assert.Empty(view.Queue);
        Assert.False(view.State.Running);
        Assert.Equal(250 + 8000 + 1250, view.TotalPoints);
        Assert.Null(view.InTraining);
    }

    [PgFact]
    public async Task Starting_a_skill_runs_the_next_level_and_the_history_says_so()
    {
        await using var rig = await TrainingRig.CreateAsync();
        var saved = await rig.SavedAsync(await rig.StartAsync(3301));
        Assert.False(saved.Paused);
        var line = Assert.Single(saved.Update.View.Queue);
        Assert.Equal((3301, 2, 250L, 1415L), (line.TypeId, line.ToLevel, line.StartPoints, line.DestinationPoints));
        Assert.Equal(line.Start + 23_300_000_000, line.End); // 1,165 points at 30 a minute
        Assert.Equal(3301, saved.Update.View.InTraining);

        var stored = await rig.Store.LoadAsync(TrainingRig.Character);
        Assert.True(stored.Active);
        Assert.Equal([new QueueEntry(3301, 2)], stored.Entries);
        Assert.Equal([new HistoryEntry(rig.Training.Now, SkillEvents.TrainingStarted, 3301, 250, 2)], await rig.Training.HistoryAsync(TrainingRig.Character));
    }

    [PgFact]
    public async Task What_is_not_a_skill_starts_nothing()
    {
        await using var rig = await TrainingRig.CreateAsync();
        Assert.Null(await rig.StartAsync(99));
        Assert.Empty((await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account)).Queue);
    }

    [PgFact]
    public async Task A_skill_the_character_does_not_have_cannot_be_started_and_a_level_out_of_order_is_refused()
    {
        await using var rig = await TrainingRig.CreateAsync();
        Assert.Equal(QueueError.SkillNotUploaded, Assert.IsType<QueueRefused>(await rig.SaveAsync(true, (3301, 2), (3300, 1))).Error);
        Assert.Equal(QueueError.OutOfOrder, Assert.IsType<QueueRefused>(await rig.StartAsync(3301, 3)).Error);
        // nothing was kept
        Assert.Empty((await rig.Store.LoadAsync(TrainingRig.Character)).Entries);
        Assert.Empty(await rig.Training.HistoryAsync(TrainingRig.Character));
    }

    [PgFact]
    public async Task The_running_skill_has_the_points_it_has_gained_by_now()
    {
        await using var rig = await TrainingRig.CreateAsync();
        await rig.StartAsync(3301);
        rig.Wait(10 * Minute);
        var view = await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account);
        Assert.Equal(new CharacterSkill(3301, 1, 550), view.Skills.Single(s => s.TypeId == 3301));  // ten minutes at 30 a minute
        Assert.Equal(550 + 8000 + 1250, view.TotalPoints);
        Assert.Equal(250, (await rig.Skills.ForCharacterAsync(TrainingRig.Character)).Single(s => s.TypeId == 3301).SkillPoints); // stored until it is carried
    }

    [PgFact]
    public async Task A_level_that_finishes_is_settled_once_and_the_clock_tells_whoever_listens()
    {
        await using var rig = await TrainingRig.CreateAsync();
        await rig.StartAsync(3301);
        rig.Wait(TimeSpan.FromSeconds(2330) + TimeSpan.FromMilliseconds(1));

        var view = await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account);
        Assert.Empty(view.Queue);
        Assert.False(view.State.Active);
        Assert.Equal(new CharacterSkill(3301, 2, 1415), (await rig.Skills.ForCharacterAsync(TrainingRig.Character)).Single(s => s.TypeId == 3301));
        var update = Assert.Single(rig.Settlements);
        Assert.Equal([new CharacterSkill(3301, 2, 1415)], update.Finished);
        Assert.Equal(new CharacterSkill(3301, 1, 250), update.SkillsBefore.Single(s => s.TypeId == 3301));
        Assert.Equal([SkillEvents.TrainingComplete, SkillEvents.TrainingStarted], (await rig.Training.HistoryAsync(TrainingRig.Character)).Select(h => h.EventType));

        await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account); // settled already: nothing more to tell
        Assert.Single(rig.Settlements);
    }

    [PgFact]
    public async Task The_next_line_begins_where_the_last_ended_even_when_nobody_asked_in_between()
    {
        await using var rig = await TrainingRig.CreateAsync();
        var saved = await rig.SavedAsync(await rig.SaveAsync(true, (3301, 2), (3301, 3)));
        var first = saved.Update.View.Queue[0];
        rig.Wait(TimeSpan.FromMinutes(100)); // the first line takes 38.8 minutes, the second 219.5

        var view = await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account);
        var line = Assert.Single(view.Queue);
        Assert.Equal((3301, 3, first.End), (line.TypeId, line.ToLevel, line.Start));
        var live = view.Skills.Single(s => s.TypeId == 3301);
        Assert.Equal((2, 1415 + (long)Math.Floor((100 * 600_000_000d - 23_300_000_000d) / 600_000_000d * 30)), (live.Level, live.SkillPoints)); // level II done, and level III gaining
        Assert.Equal([SkillEvents.QueueTrainingCompleted], (await rig.Training.HistoryAsync(TrainingRig.Character)).Where(h => h.EventType != SkillEvents.TrainingStarted).Select(h => h.EventType));
    }

    [PgFact]
    public async Task Stopping_keeps_the_points_gained_and_the_queue_and_stopping_again_does_nothing()
    {
        await using var rig = await TrainingRig.CreateAsync();
        await rig.SaveAsync(true, (3301, 2), (3327, 4));
        rig.Wait(10 * Minute);

        var stopped = await rig.Training.AbortAsync(TrainingRig.Character, TrainingRig.Account);
        Assert.NotNull(stopped);
        Assert.Equal((new CharacterSkill(3301, 1, 550), true), (stopped.Rebased, stopped.RebaseChanged));
        Assert.Equal(new CharacterSkill(3301, 1, 550), (await rig.Skills.ForCharacterAsync(TrainingRig.Character)).Single(s => s.TypeId == 3301));
        var after = await rig.Store.LoadAsync(TrainingRig.Character);
        Assert.Equal((false, null, 2), (after.Active, after.ActiveStart, after.Entries.Count));
        Assert.All(stopped.View.Queue, line => Assert.Equal((null, null), (line.Start, line.End)));
        Assert.Equal((550L, 1415L), (stopped.View.Queue[0].StartPoints, stopped.View.Queue[0].DestinationPoints)); // it resumes from where it was
        Assert.Equal(new HistoryEntry(rig.Training.Now, SkillEvents.TrainingCancelled, 3301, 550, 2), (await rig.Training.HistoryAsync(TrainingRig.Character))[0]);

        Assert.Null(await rig.Training.AbortAsync(TrainingRig.Character, TrainingRig.Account));
        rig.Wait(500 * Minute);
        Assert.Equal(550, (await rig.Skills.ForCharacterAsync(TrainingRig.Character)).Single(s => s.TypeId == 3301).SkillPoints); // a stopped queue does not progress
    }

    [PgFact]
    public async Task Saving_a_queue_with_another_first_line_carries_the_points_of_the_line_that_gave_way()
    {
        await using var rig = await TrainingRig.CreateAsync();
        await rig.SaveAsync(true, (3301, 2));
        rig.Wait(10 * Minute);

        var saved = await rig.SavedAsync(await rig.SaveAsync(true, (3327, 4), (3301, 2)));
        Assert.Equal((new CharacterSkill(3301, 1, 550), true), (saved.Update.Rebased, saved.Update.RebaseChanged));
        Assert.Equal(3327, saved.Update.View.InTraining);
        Assert.Equal(rig.Training.Now, saved.Update.View.State.ActiveStart);
        Assert.Equal(550L, saved.Update.View.Queue[1].StartPoints); // the skill that gave way starts again from what it has
        Assert.Equal([SkillEvents.TrainingStarted, SkillEvents.TrainingStarted], (await rig.Training.HistoryAsync(TrainingRig.Character)).Select(h => h.EventType));
    }

    [PgFact]
    public async Task Saving_the_same_first_line_keeps_its_start_and_starts_no_new_history()
    {
        await using var rig = await TrainingRig.CreateAsync();
        var first = await rig.SavedAsync(await rig.SaveAsync(true, (3301, 2)));
        rig.Wait(10 * Minute);
        var again = await rig.SavedAsync(await rig.SaveAsync(true, (3301, 2), (3301, 3)));
        Assert.Equal(first.Update.View.State.ActiveStart, again.Update.View.State.ActiveStart);
        Assert.Null(again.Update.Rebased);
        Assert.Single(await rig.Training.HistoryAsync(TrainingRig.Character));
        Assert.Equal(2, again.Update.View.Queue.Count);
    }

    [PgFact]
    public async Task Saving_a_queue_without_running_it_pauses_it_and_running_it_again_is_a_fresh_start()
    {
        await using var rig = await TrainingRig.CreateAsync();
        await rig.SaveAsync(true, (3301, 2));
        var paused = await rig.SavedAsync(await rig.SaveAsync(false, (3301, 2), (3301, 3)));
        Assert.True(paused.Paused);
        Assert.All(paused.Update.View.Queue, line => Assert.Null(line.Start));
        rig.Wait(5 * Minute);
        var running = await rig.SavedAsync(await rig.SaveAsync(true, (3301, 2), (3301, 3)));
        Assert.Equal(rig.Training.Now, running.Update.View.State.ActiveStart);
        Assert.Equal([SkillEvents.TrainingStarted, SkillEvents.TrainingStarted], (await rig.Training.HistoryAsync(TrainingRig.Character)).Select(h => h.EventType));
        var empty = await rig.SavedAsync(await rig.SaveAsync(true));
        Assert.True(empty.Paused);
        Assert.Empty(empty.Update.View.Queue);
    }

    [PgFact]
    public async Task An_account_trains_one_character_at_a_time()
    {
        await using var rig = await TrainingRig.CreateAsync();
        await rig.GiveAsync(1002);
        IReadOnlyList<long> account = [TrainingRig.Character, 1002];
        Assert.IsType<QueueSaved>(await rig.StartAsync(3301, account: account));
        var busy = Assert.IsType<TrainingBusy>(await rig.StartAsync(3327, character: 1002, account: account));
        Assert.Equal(1, busy.Training);
        // the one that trains may change what it trains
        Assert.IsType<QueueSaved>(await rig.StartAsync(3327, account: account));
        // when it stops the other may start
        await rig.Training.AbortAsync(TrainingRig.Character, TrainingRig.Account);
        Assert.IsType<QueueSaved>(await rig.StartAsync(3327, character: 1002, account: account));
        // paused queues do not count
        Assert.IsType<QueueSaved>(await rig.Training.SaveQueueAsync(TrainingRig.Character, TrainingRig.Account, [new(3301, 2)], false, account));
    }

    [PgFact]
    public async Task A_lapsed_subscription_trains_at_half_speed_and_the_server_speed_multiplies()
    {
        await using var half = await TrainingRig.CreateAsync(new TrainingSettings { HasOmega = _ => false });
        Assert.Equal(46_600_000_000, (await half.SavedAsync(await half.StartAsync(3301))).Update.View.Queue[0].End - (await half.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account)).Queue[0].Start);

        await using var fast = await TrainingRig.CreateAsync(new TrainingSettings { Speed = 60_000 });
        var line = (await fast.SavedAsync(await fast.StartAsync(3301))).Update.View.Queue[0];
        Assert.Equal(388_333, line.End - line.Start); // what the reference gave at that speed
    }

    [PgFact]
    public async Task A_rank_five_skill_takes_five_times_the_points()
    {
        await using var rig = await TrainingRig.CreateAsync();
        var line = (await rig.SavedAsync(await rig.StartAsync(3317))).Update.View.Queue[0];
        Assert.Equal((1250L, 7072L), (line.StartPoints, line.DestinationPoints));
        rig.Wait(TimeSpan.FromMinutes(1000));
        await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account);
        Assert.Equal(new CharacterSkill(3317, 2, 7072), (await rig.Skills.ForCharacterAsync(TrainingRig.Character)).Single(s => s.TypeId == 3317));
    }

    [PgFact]
    public async Task Several_calls_that_find_the_same_level_finished_settle_it_once()
    {
        await using var rig = await TrainingRig.CreateAsync();
        await rig.StartAsync(3301);
        rig.Wait(TimeSpan.FromMinutes(60));
        var runs = Enumerable.Range(0, 8).Select(_ => rig.Training.SettleAsync(TrainingRig.Character, TrainingRig.Account)).ToArray();
        var updates = await Task.WhenAll(runs);
        Assert.Single(updates, u => u is not null);
        Assert.Single(rig.Settlements);
        Assert.Equal([SkillEvents.TrainingComplete, SkillEvents.TrainingStarted], (await rig.Training.HistoryAsync(TrainingRig.Character)).Select(h => h.EventType));
    }

    [PgFact]
    public async Task A_handler_that_fails_does_not_undo_what_the_clock_settled()
    {
        await using var rig = await TrainingRig.CreateAsync();
        rig.Training.Settled += _ => throw new InvalidOperationException("the client is gone");
        await rig.StartAsync(3301);
        rig.Wait(TimeSpan.FromMinutes(60));
        var view = await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account);
        Assert.Empty(view.Queue);
        Assert.Equal(2, (await rig.Skills.ForCharacterAsync(TrainingRig.Character)).Single(s => s.TypeId == 3301).Level);
    }

    [PgFact]
    public async Task Changes_to_one_characters_queue_happen_one_after_the_other()
    {
        await using var rig = await TrainingRig.CreateAsync();
        var runs = Enumerable.Range(0, 8).Select(i => rig.SaveAsync(true, i % 2 == 0 ? [(3301, 2)] : [(3327, 4)])).ToArray();
        await Task.WhenAll(runs);
        var state = await rig.Store.LoadAsync(TrainingRig.Character);
        Assert.Single(state.Entries); // whichever came last: never a mixture
        Assert.True(state.Active);
        Assert.True((await rig.Training.HistoryAsync(TrainingRig.Character)).Count(h => h.EventType == SkillEvents.TrainingStarted) >= 1);
    }
}
