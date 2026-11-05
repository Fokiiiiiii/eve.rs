using System.Collections;
using System.Reflection;

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
    private static readonly SkillType CapRank16 = new(4001, "Rank 16 cap filler", 257, "Spaceship Command", true, 16, 167, 168);
    private static readonly SkillType CapRank3 = new(4002, "Rank 3 cap filler", 257, "Spaceship Command", true, 3, 167, 168);
    private static readonly SkillType CapRank15 = new(4003, "Rank 15 cap filler", 257, "Spaceship Command", true, 15, 167, 168);
    private static readonly SkillType CapRank2 = new(4004, "Rank 2 cap filler", 257, "Spaceship Command", true, 2, 167, 168);

    private readonly PgSchema _schema;

    public long ClockMs = ClockStart;

    public List<TrainingUpdate> Settlements { get; } = [];

    private TrainingRig(PgSchema schema, TrainingSettings settings)
    {
        _schema = schema;
        Catalog = new SkillCatalog([Turret, Command, Rank5, CapRank16, CapRank3, CapRank15, CapRank2],
            new Dictionary<int, IReadOnlyList<StartingSkill>>(),
            alphaMaxLevelsByRace: new Dictionary<int, IReadOnlyDictionary<int, int>> { [1] = new Dictionary<int, int> { [3301] = 5 } });
        Store = new PgTrainingStore(schema.Source, schema.Name);
        Training = new CharacterTraining(schema.Source, Catalog, schema.Store, Store, new TrainingSettings
        {
            Speed = settings.Speed,
            HasOmega = settings.HasOmega,
            AdditionalTrainingSlots = settings.AdditionalTrainingSlots,
            Attribute = schema.Store.LearningAttribute,
            LearningBonus = settings.LearningBonus,
            RefreshLearningBonus = settings.RefreshLearningBonus,
        }, () => ClockMs);
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

    public Task GiveAlphaCapSkillPointsAsync(long character, long totalSkillPoints)
    {
        var rank15Points = totalSkillPoints - 9_500 - SkillPoints.ForTraining(16, 5) - SkillPoints.ForTraining(3, 5) - SkillPoints.ForTraining(2, 1);
        return _schema.GrantAsync(character,
        [
            new CharacterSkill(4001, 5, SkillPoints.ForTraining(16, 5)),
            new CharacterSkill(4002, 5, SkillPoints.ForTraining(3, 5)),
            new CharacterSkill(4003, 3, rank15Points),
            new CharacterSkill(4004, 1, SkillPoints.ForTraining(2, 1)),
        ]);
    }

    public void Wait(TimeSpan time) => ClockMs += (long)time.TotalMilliseconds;

    public static IReadOnlyList<long> Alone => [Character];

    public Task<SaveOutcome?> StartAsync(int type, int level = 0, long character = Character, IReadOnlyList<long>? account = null) =>
        Training.StartAsync(character, Account, type, level, account ?? [character]);

    public Task<QueueSaved> SavedAsync(SaveOutcome? outcome) => Task.FromResult(Assert.IsType<QueueSaved>(outcome));

    public Task<SaveOutcome> SaveAsync(bool activate, params (int Type, int Level)[] entries) =>
        Training.SaveQueueAsync(Character, Account, [.. entries.Select(e => new QueueEntry(e.Type, e.Level))], activate, Alone);

    public Task<SaveOutcome> SaveAlphaAsync(bool activate, params (int Type, int Level)[] entries) =>
        Training.SaveQueueAsync(Character, Account, [.. entries.Select(e => new QueueEntry(e.Type, e.Level))], activate, Alone, isAlphaClone: true, raceId: 1);

    public async Task<long> PersistedSkillPointsAsync(long character)
    {
        await using var connection = await _schema.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        return await _schema.Store.TotalPointsAsync(connection, transaction, character);
    }

    public async ValueTask DisposeAsync() => await _schema.DisposeAsync();
}

public class CharacterTrainingTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static int SettlementGateCount(CharacterTraining training)
    {
        var gates = typeof(CharacterTraining).GetField("settleGates", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((IDictionary)gates.GetValue(training)!).Count;
    }

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
    public async Task Starting_a_skill_runs_the_next_level_without_adding_a_history_row()
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
        Assert.Empty(await rig.Training.HistoryAsync(TrainingRig.Character));
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
    public async Task Neural_remap_settles_old_rate_then_rebases_the_running_queue_at_the_new_rate()
    {
        await using var rig = await TrainingRig.CreateAsync();
        await rig.GiveAsync(TrainingRig.Character);
        await rig.StartAsync(3301, 2);
        rig.Wait(10 * Minute);

        var update = await rig.Training.RespecAsync(
            TrainingRig.Character, TrainingRig.Account,
            new Dictionary<int, int> { [164] = 17, [165] = 17, [166] = 17, [167] = 27, [168] = 21 });

        Assert.NotNull(update);
        Assert.Equal(550, update.View.Skills.Single(skill => skill.TypeId == 3301).SkillPoints);
        Assert.Equal(37.5, update.View.Queue[0].PointsPerMinute);
        Assert.Equal(rig.Training.Now, update.View.Queue[0].Start);

        rig.Wait(10 * Minute);
        var later = await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account);
        Assert.Equal(925, later.Skills.Single(skill => skill.TypeId == 3301).SkillPoints);
    }

    [PgFact]
    public async Task A_level_that_finishes_is_settled_once_and_the_clock_tells_whoever_listens()
    {
        await using var rig = await TrainingRig.CreateAsync();
        var started = await rig.SavedAsync(await rig.StartAsync(3301));
        var line = Assert.Single(started.Update.View.Queue);
        rig.Wait(TimeSpan.FromSeconds(2330) + TimeSpan.FromMilliseconds(1));

        var view = await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account);
        Assert.Empty(view.Queue);
        Assert.False(view.State.Active);
        Assert.Equal(new CharacterSkill(3301, 2, 1415), (await rig.Skills.ForCharacterAsync(TrainingRig.Character)).Single(s => s.TypeId == 3301));
        var update = Assert.Single(rig.Settlements);
        Assert.Equal([new CharacterSkill(3301, 2, 1415)], update.Finished);
        Assert.Equal(new CharacterSkill(3301, 1, 250), update.SkillsBefore.Single(s => s.TypeId == 3301));
        Assert.Equal([new HistoryEntry(line.End!.Value, SkillEvents.TrainingComplete, 3301, 1415, 2)], await rig.Training.HistoryAsync(TrainingRig.Character));

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
        Assert.Equal([new HistoryEntry(first.End!.Value, SkillEvents.QueueTrainingCompleted, 3301, 1415, 2)], await rig.Training.HistoryAsync(TrainingRig.Character));
    }

    [PgFact]
    public async Task An_offline_settlement_records_each_completion_at_its_scheduled_end_newest_first()
    {
        await using var rig = await TrainingRig.CreateAsync();
        var saved = await rig.SavedAsync(await rig.SaveAsync(true, (3301, 2), (3301, 3)));
        var first = saved.Update.View.Queue[0];
        var second = saved.Update.View.Queue[1];
        Assert.Empty(await rig.Training.HistoryAsync(TrainingRig.Character));

        rig.Wait(10_000 * Minute);
        await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account);

        Assert.Equal(
        [
            new HistoryEntry(second.End!.Value, SkillEvents.TrainingComplete, 3301, 8000, 3),
            new HistoryEntry(first.End!.Value, SkillEvents.QueueTrainingCompleted, 3301, 1415, 2),
        ], await rig.Training.HistoryAsync(TrainingRig.Character));
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
        Assert.Empty(await rig.Training.HistoryAsync(TrainingRig.Character));
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
        Assert.Empty(await rig.Training.HistoryAsync(TrainingRig.Character));
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
        Assert.Empty(await rig.Training.HistoryAsync(TrainingRig.Character));
        var empty = await rig.SavedAsync(await rig.SaveAsync(true));
        Assert.True(empty.Paused);
        Assert.Empty(empty.Update.View.Queue);
    }

    [PgFact]
    public async Task Saving_an_alpha_queue_with_an_unchanged_running_head_counts_elapsed_points_once()
    {
        await using var rig = await TrainingRig.CreateAsync();
        var started = await rig.SavedAsync(await rig.SaveAsync(true, (3301, 2)));
        var activeStart = started.Update.View.State.ActiveStart;
        rig.Wait(22 * Minute);

        const long persistedPoints = 4_999_000;
        await rig.GiveAlphaCapSkillPointsAsync(TrainingRig.Character, persistedPoints);
        var view = await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account);
        var head = Assert.Single(view.Queue);
        Assert.Equal((3301, 2), (head.TypeId, head.ToLevel));
        Assert.Equal(activeStart, view.State.ActiveStart);

        var elapsed = Training.PointsAt(head.StartPoints, head.DestinationPoints, head.Start!.Value, rig.Training.Now, head.PointsPerMinute) - head.StartPoints;
        var fullQueueIncrement = view.Queue.Sum(line => line.DestinationPoints - line.StartPoints);
        Assert.Equal(persistedPoints, await rig.PersistedSkillPointsAsync(TrainingRig.Character));
        Assert.Equal(1_165, fullQueueIncrement);
        var overCap = persistedPoints + fullQueueIncrement - Training.AlphaMaxTrainingSkillPoints;
        Assert.True(overCap > 0 && overCap < elapsed);

        var refused = Assert.IsType<QueueRefused>(await rig.SaveAlphaAsync(true, (3301, 2)));
        Assert.Equal(QueueError.OverAlphaSpTrainingSize, refused.Error);

        foreach (var total in new[] { Training.AlphaMaxTrainingSkillPoints - fullQueueIncrement - 1, Training.AlphaMaxTrainingSkillPoints - fullQueueIncrement })
        {
            await rig.GiveAlphaCapSkillPointsAsync(TrainingRig.Character, total);
            Assert.Equal(total, await rig.PersistedSkillPointsAsync(TrainingRig.Character));
            var saved = Assert.IsType<QueueSaved>(await rig.SaveAlphaAsync(true, (3301, 2)));
            Assert.False(saved.Paused);
            Assert.Equal(activeStart, saved.Update.View.State.ActiveStart);
        }

        await rig.GiveAlphaCapSkillPointsAsync(TrainingRig.Character, persistedPoints);
        var paused = Assert.IsType<QueueSaved>(await rig.SaveAlphaAsync(false, (3301, 2)));
        Assert.True(paused.Paused);
        Assert.False(paused.Update.View.State.Running);
        Assert.Equal(head.StartPoints + elapsed, paused.Update.Rebased!.SkillPoints);
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
    public async Task Concurrent_starts_for_different_characters_cannot_exceed_the_account_training_slots()
    {
        await using var rig = await TrainingRig.CreateAsync();
        const long second = 1002;
        await rig.GiveAsync(second);
        IReadOnlyList<long> account = [TrainingRig.Character, second];

        var outcomes = await Task.WhenAll(
            rig.Training.SaveQueueAsync(TrainingRig.Character, TrainingRig.Account, [new(3301, 2)], true, account),
            rig.Training.SaveQueueAsync(second, TrainingRig.Account, [new(3301, 2)], true, account));

        Assert.Single(outcomes, outcome => outcome is QueueSaved);
        Assert.Single(outcomes, outcome => outcome is TrainingBusy { Training: 1 });
        Assert.Equal(1, await rig.Store.RunningCountAsync(account));
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
    public async Task Idle_settlement_gates_are_evicted_after_high_cardinality_churn()
    {
        await using var rig = await TrainingRig.CreateAsync();
        for (var characterId = 10_000L; characterId < 10_128; characterId++)
        {
            await rig.GiveAsync(characterId);
            Assert.Null(await rig.Training.SettleAsync(characterId, TrainingRig.Account));
        }

        Assert.Equal(0, SettlementGateCount(rig.Training));
    }

    [PgFact]
    public async Task Concurrent_settlements_for_same_character_share_a_gate_and_settle_it_once()
    {
        var holdFirstRefresh = 0;
        var held = 0;
        var activeRefreshes = 0;
        var overlappingRefreshes = 0;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await TrainingRig.CreateAsync(new TrainingSettings
        {
            RefreshLearningBonus = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref activeRefreshes) > 1)
                    Interlocked.Exchange(ref overlappingRefreshes, 1);
                try
                {
                    if (Volatile.Read(ref holdFirstRefresh) != 0 && Interlocked.CompareExchange(ref held, 1, 0) == 0)
                    {
                        entered.TrySetResult(true);
                        await release.Task.WaitAsync(cancellationToken);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref activeRefreshes);
                }
            },
        });
        var started = await rig.SavedAsync(await rig.StartAsync(3301));
        var scheduledEnd = started.Update.View.Queue.Single().End!.Value;
        rig.Wait(TimeSpan.FromMinutes(60));
        Volatile.Write(ref holdFirstRefresh, 1);

        using var firstCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        var first = rig.Training.SettleAsync(TrainingRig.Character, TrainingRig.Account, firstCancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancelledWaiter = rig.Training.SettleAsync(TrainingRig.Character, TrainingRig.Account, queuedCancellation.Token);
            var waiter = rig.Training.SettleAsync(TrainingRig.Character, TrainingRig.Account);

            queuedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelledWaiter);
            firstCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);

            Assert.NotNull(await waiter.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(0, overlappingRefreshes);
            Assert.Single(rig.Settlements);
            Assert.Equal([new HistoryEntry(scheduledEnd, SkillEvents.TrainingComplete, 3301, 1415, 2)], await rig.Training.HistoryAsync(TrainingRig.Character));
        }
        finally
        {
            firstCancellation.Cancel();
            queuedCancellation.Cancel();
            release.TrySetResult(true);
        }
    }

    [PgFact]
    public async Task A_failed_settlement_releases_its_gate()
    {
        var failFirstRefresh = 1;
        await using var rig = await TrainingRig.CreateAsync(new TrainingSettings
        {
            RefreshLearningBonus = (_, _) => Interlocked.Exchange(ref failFirstRefresh, 0) == 1
                ? Task.FromException(new InvalidOperationException("refresh failed"))
                : Task.CompletedTask,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Training.SettleAsync(TrainingRig.Character, TrainingRig.Account));
        Assert.Null(await rig.Training.SettleAsync(TrainingRig.Character, TrainingRig.Account).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(0, SettlementGateCount(rig.Training));
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
        Assert.Empty(await rig.Training.HistoryAsync(TrainingRig.Character));
    }

    [PgFact]
    public async Task An_account_trains_one_character_at_a_time_and_each_running_extra_queue_lets_one_more_train()
    {
        var running = 0;
        await using var rig = await TrainingRig.CreateAsync(new TrainingSettings { AdditionalTrainingSlots = _ => running });
        const long second = 1002;
        const long third = 1003;
        await rig.GiveAsync(second);
        await rig.GiveAsync(third);
        IReadOnlyList<long> account = [TrainingRig.Character, second, third];

        Assert.IsType<QueueSaved>(await rig.Training.StartAsync(TrainingRig.Character, TrainingRig.Account, 3301, 2, account));
        Assert.IsType<TrainingBusy>(await rig.Training.StartAsync(second, TrainingRig.Account, 3327, 4, account));

        running = 1;
        Assert.IsType<QueueSaved>(await rig.Training.StartAsync(second, TrainingRig.Account, 3327, 4, account));
        Assert.IsType<TrainingBusy>(await rig.Training.StartAsync(third, TrainingRig.Account, 3317, 2, account));

        running = 2;
        Assert.IsType<QueueSaved>(await rig.Training.StartAsync(third, TrainingRig.Account, 3317, 2, account));
    }
}
