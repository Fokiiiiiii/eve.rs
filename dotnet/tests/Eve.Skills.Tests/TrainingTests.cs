namespace Eve.Skills.Tests;

public class TrainingPointsTests
{
    [Theory]
    [InlineData(1, 1, 250)]
    [InlineData(1, 2, 1415)]      // 1414.2 rounded up, as the reference's queue has it (a starting skill has 1414)
    [InlineData(1, 3, 8000)]
    [InlineData(1, 4, 45255)]
    [InlineData(1, 5, 256000)]
    [InlineData(2, 2, 2829)]
    [InlineData(4, 1, 1000)]
    [InlineData(5, 2, 7072)]      // recorded from the reference: a rank 5 skill going to level II
    [InlineData(1, 0, 0)]
    [InlineData(1, 9, 256000)]    // beyond the top counts as the top
    [InlineData(1, -1, 0)]
    public void A_level_costs_its_points_rounded_up(int rank, int level, long expected) => Assert.Equal(expected, SkillPoints.ForTraining(rank, level));

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(1, 249, 0)]
    [InlineData(1, 250, 1)]
    [InlineData(1, 1414, 1)]      // one point short of level II
    [InlineData(1, 1415, 2)]
    [InlineData(1, 255999, 4)]
    [InlineData(1, 256000, 5)]
    [InlineData(1, 999999, 5)]
    public void The_level_of_some_points_is_the_highest_they_reach(int rank, long points, int level) => Assert.Equal(level, SkillPoints.LevelAt(rank, points));
}

public class TrainingMathTests
{
    private const long Minute = Training.TicksPerMinute;

    [Fact]
    public void Points_a_minute_are_the_primary_attribute_and_half_the_secondary_times_what_scales_them()
    {
        var skill = new SkillType(1, "s", 1, "g", true, 1, PrimaryAttribute: 167, SecondaryAttribute: 168);
        double Attribute(int id) => id == 167 ? 20 : id == 168 ? 10 : 0;
        Assert.Equal(25, Training.PointsPerMinute(skill, Attribute, 1));
        Assert.Equal(12.5, Training.PointsPerMinute(skill, Attribute, 0.5));
        Assert.Equal(1_500_000, Training.PointsPerMinute(skill, Attribute, 60_000));
    }

    [Fact]
    public void A_duration_is_the_points_over_the_rate_in_ticks_rounded_down()
    {
        Assert.Equal(23_300_000_000, Training.DurationTicks(1165, 30));   // 38 minutes 50 seconds, recorded from the reference
        Assert.Equal(131_700_000_000, Training.DurationTicks(6585, 30));
        Assert.Equal(745_100_000_000, Training.DurationTicks(37255, 30));
        Assert.Equal(0, Training.DurationTicks(0, 30));
        Assert.Equal(0, Training.DurationTicks(100, 0));
        Assert.Equal(388_333, Training.DurationTicks(1165, 1_800_000));   // rounded down: 388,333.3
    }

    [Theory]
    [InlineData(0, 250)]              // not begun
    [InlineData(-5, 250)]             // before it began
    [InlineData(Minute, 280)]         // a minute at 30 a minute
    [InlineData(2 * Minute + 1, 310)]
    [InlineData(100 * Minute, 1415)]  // never past the destination
    public void The_points_gained_grow_with_time_and_stop_at_the_destination(long sinceStart, long expected) =>
        Assert.Equal(expected, Training.PointsAt(250, 1415, 1_000_000, 1_000_000 + sinceStart, 30));

    [Fact]
    public void Nothing_is_gained_without_a_rate()
    {
        Assert.Equal(250, Training.PointsAt(250, 1415, 0, 100 * Minute, 0));
    }
}

public class TrainingQueueTests
{
    private const long Start = 134_351_385_044_230_000;
    private const long Minute = Training.TicksPerMinute;

    private static readonly SkillType Turret = new(3301, "Small Hybrid Turret", 255, "Gunnery", true, 1, 167, 168);
    private static readonly SkillType Command = new(3327, "Spaceship Command", 257, "Spaceship Command", true, 1, 167, 168);
    private static readonly SkillType Strike = new(3315, "Surgical Strike", 255, "Gunnery", true, 4, 167, 168, [new SkillRequirement(3300, 4)]);
    private static readonly SkillType Gunnery = new(3300, "Gunnery", 255, "Gunnery", true, 1, 167, 168);

    private static readonly SkillCatalog Catalog = new([Turret, Command, Strike, Gunnery], new Dictionary<int, IReadOnlyList<StartingSkill>>());

    private static double Rate(SkillType type) => Training.PointsPerMinute(type, _ => 20, 1); // 30 a minute

    private static Dictionary<int, CharacterSkill> Owned(params CharacterSkill[] skills) => skills.ToDictionary(s => s.TypeId);

    private static Dictionary<int, CharacterSkill> Rookie() => Owned(
        new CharacterSkill(3301, 1, 250), new CharacterSkill(3327, 3, 8000), new CharacterSkill(3300, 4, 45255), new CharacterSkill(3315, 0, 0));

    [Fact]
    public void A_running_queue_is_worked_out_line_after_line_from_where_the_running_line_began()
    {
        var state = new QueueState([new(3301, 2), new(3301, 3), new(3327, 4)], true, Start);
        var projection = Training.Project(state, Rookie(), Catalog, Rate, Start + (5 * Minute));

        // the numbers the reference gave for this queue
        Assert.Equal(
            [
                (0, 3301, 2, 250L, 1415L, Start, Start + 23_300_000_000),
                (1, 3301, 3, 1415L, 8000L, Start + 23_300_000_000, Start + 23_300_000_000 + 131_700_000_000),
                (2, 3327, 4, 8000L, 45255L, Start + 23_300_000_000 + 131_700_000_000, Start + 23_300_000_000 + 131_700_000_000 + 745_100_000_000),
            ],
            projection.Entries.Select(e => (e.Position, e.TypeId, e.ToLevel, e.StartPoints, e.DestinationPoints, e.Start!.Value, e.End!.Value)));
        Assert.Equal(projection.Entries[2].End, projection.EndTime);
        Assert.Equal(new CharacterSkill(3301, 3, 8000), projection.Skills[3301]); // the skills as they will be when it has run
        Assert.Equal(new CharacterSkill(3327, 4, 45255), projection.Skills[3327]);
    }

    [Fact]
    public void A_queue_that_is_not_running_has_no_times_but_the_same_points()
    {
        var state = new QueueState([new(3301, 2), new(3301, 3)], false, null);
        var projection = Training.Project(state, Rookie(), Catalog, Rate, Start);
        Assert.All(projection.Entries, e => Assert.Equal((null, null), (e.Start, e.End)));
        Assert.Equal([(250L, 1415L), (1415L, 8000L)], projection.Entries.Select(e => (e.StartPoints, e.DestinationPoints)));
        Assert.Null(projection.EndTime);
    }

    [Fact]
    public void A_skill_part_way_through_a_level_starts_from_the_points_it_has()
    {
        var owned = Owned(new CharacterSkill(3301, 1, 700));
        var projection = Training.Project(new QueueState([new(3301, 2)], true, Start), owned, Catalog, Rate, Start);
        Assert.Equal((700L, 1415L), (projection.Entries[0].StartPoints, projection.Entries[0].DestinationPoints));
        // a level that is not the next starts from the level before it
        var far = Training.Project(new QueueState([new(3301, 4)], true, Start), owned, Catalog, Rate, Start);
        Assert.Equal(8000L, far.Entries[0].StartPoints);
    }

    [Fact]
    public void A_running_queue_without_a_start_begins_now_and_a_line_of_a_skill_the_character_lacks_is_left_out()
    {
        var state = new QueueState([new(9999, 1), new(3301, 2)], true, null);
        var projection = Training.Project(state, Rookie(), Catalog, Rate, Start);
        var only = Assert.Single(projection.Entries);
        Assert.Equal((1, 3301, Start), (only.Position, only.TypeId, only.Start!.Value)); // its position in the queue is kept
    }

    [Theory]
    [InlineData(new[] { 3301, 2, 3301, 3 }, null)]
    [InlineData(new[] { 3301, 3 }, QueueError.OutOfOrder)]                 // level II is not trained yet
    [InlineData(new[] { 3301, 1 }, QueueError.PreviouslyTrained)]
    [InlineData(new[] { 3301, 2, 3301, 2 }, QueueError.PreviouslyTrained)]  // the same level twice
    [InlineData(new[] { 3327, 4, 3327, 5 }, null)]
    [InlineData(new[] { 3327, 6 }, QueueError.PastMaximumLevel)]
    [InlineData(new[] { 9999, 1 }, QueueError.SkillNotUploaded)]
    [InlineData(new[] { 3315, 1 }, null)]                                   // Gunnery IV is trained: Surgical Strike may start
    public void A_queue_is_refused_for_the_first_rule_it_breaks(int[] pairs, QueueError? expected)
    {
        var entries = Enumerable.Range(0, pairs.Length / 2).Select(i => new QueueEntry(pairs[2 * i], pairs[(2 * i) + 1])).ToList();
        Assert.Equal(expected, Training.Validate(entries, Rookie(), Catalog, Rate, Start));
    }

    [Fact]
    public void A_skill_may_not_be_lined_up_before_what_it_needs_has_been_trained()
    {
        var owned = Rookie();
        owned[3300] = new CharacterSkill(3300, 3, 8000); // Gunnery only III: Surgical Strike needs IV
        Assert.Equal(QueueError.BeforeRequirements, Training.Validate([new(3315, 1)], owned, Catalog, Rate, Start));
        // lined up behind the level it needs, it is fine
        Assert.Null(Training.Validate([new(3300, 4), new(3315, 1)], owned, Catalog, Rate, Start));
        // and the level after the one it needs is checked too
        Assert.Equal(QueueError.BeforeRequirements, Training.Validate([new(3315, 1), new(3300, 4)], owned, Catalog, Rate, Start));
    }

    [Fact]
    public void A_queue_has_at_most_a_hundred_and_fifty_lines_and_takes_at_most_ten_years()
    {
        var owned = Owned(new CharacterSkill(3327, 0, 0));
        var many = Enumerable.Range(0, 151).Select(_ => new QueueEntry(3327, 1)).ToList();
        Assert.Equal(QueueError.TooManySkills, Training.Validate(many, owned, Catalog, Rate, Start));

        // fifty rank sixteen skills to level V take thirteen years
        var heavy = Enumerable.Range(0, 50).Select(i => new SkillType(20000 + i, $"h{i}", 1, "g", true, 16, 167, 168)).ToList();
        var catalog = new SkillCatalog(heavy, new Dictionary<int, IReadOnlyList<StartingSkill>>());
        var trained = heavy.ToDictionary(t => t.TypeId, t => new CharacterSkill(t.TypeId, 4, SkillPoints.ForTraining(16, 4)));
        var queue = heavy.Select(t => new QueueEntry(t.TypeId, 5)).ToList();
        Assert.Equal(QueueError.TooLong, Training.Validate(queue, trained, catalog, Rate, Start));
        Assert.Null(Training.Validate(queue.Take(10).ToList(), trained, catalog, Rate, Start));
    }

    [Fact]
    public void Nothing_settles_before_the_line_ends()
    {
        var state = new QueueState([new(3301, 2)], true, Start);
        var settlement = Training.Settle(state, Rookie(), Catalog, Rate, Start + 23_299_999_999);
        Assert.False(settlement.QueueChanged);
        Assert.Empty(settlement.Finished);
        Assert.Equal(state, settlement.State);
    }

    [Fact]
    public void A_line_that_has_ended_is_a_trained_level_and_the_next_line_begins_where_it_ended()
    {
        var state = new QueueState([new(3301, 2), new(3301, 3)], true, Start);
        var settlement = Training.Settle(state, Rookie(), Catalog, Rate, Start + 23_300_000_000 + 1);
        Assert.True(settlement.QueueChanged);
        Assert.Equal([new CharacterSkill(3301, 2, 1415)], settlement.Finished);
        Assert.Equal(new QueueState([new(3301, 3)], true, Start + 23_300_000_000), settlement.State);
        Assert.Equal([new HistoryEntry(Start + 23_300_000_001, SkillEvents.QueueTrainingCompleted, 3301, 1415, 2)], settlement.History); // more lines are waiting
    }

    [Fact]
    public void A_line_settles_at_the_moment_it_ends_not_a_tick_later()
    {
        var state = new QueueState([new(3301, 2)], true, Start);
        var settlement = Training.Settle(state, Rookie(), Catalog, Rate, Start + 23_300_000_000);
        Assert.Equal([new CharacterSkill(3301, 2, 1415)], settlement.Finished);
    }

    [Fact]
    public void A_line_whose_skill_already_has_the_points_is_dropped_not_finished()
    {
        var owned = Owned(new CharacterSkill(3301, 1, 1415)); // the points of level II with the level not yet worked out
        var settlement = Training.Settle(new QueueState([new(3301, 2)], true, Start), owned, Catalog, Rate, Start + 1);
        Assert.True(settlement.QueueChanged);
        Assert.Empty(settlement.Finished);
        Assert.Empty(settlement.History);
        Assert.Empty(settlement.State.Entries);
    }

    [Fact]
    public void A_line_for_a_level_that_is_not_the_next_starts_at_the_level_before_it_whatever_the_skill_has()
    {
        // a skill past the line's level (the line is stale): the line still counts from the level before it
        var owned = Owned(new CharacterSkill(3301, 3, 8000));
        var line = Training.Project(new QueueState([new(3301, 2)], true, Start), owned, Catalog, Rate, Start).Entries[0];
        Assert.Equal((250L, 1415L), (line.StartPoints, line.DestinationPoints));
    }

    [Fact]
    public void A_queue_of_exactly_a_hundred_and_fifty_lines_is_allowed()
    {
        var skills = Enumerable.Range(0, Training.MaxQueueLength).Select(i => new SkillType(30000 + i, $"s{i}", 1, "g", true, 1, 167, 168)).ToList();
        var catalog = new SkillCatalog(skills, new Dictionary<int, IReadOnlyList<StartingSkill>>());
        var owned = skills.ToDictionary(t => t.TypeId, t => new CharacterSkill(t.TypeId, 0, 0));
        var queue = skills.Select(t => new QueueEntry(t.TypeId, 1)).ToList();
        Assert.Null(Training.Validate(queue, owned, catalog, Rate, Start));
    }

    [Fact]
    public void Several_lines_can_end_in_one_settlement_and_the_last_ends_the_queue()
    {
        var state = new QueueState([new(3301, 2), new(3301, 3)], true, Start);
        var settlement = Training.Settle(state, Rookie(), Catalog, Rate, Start + 10_000 * Minute);
        Assert.Equal([new CharacterSkill(3301, 2, 1415), new CharacterSkill(3301, 3, 8000)], settlement.Finished);
        Assert.Equal(new QueueState([], false, null), settlement.State);
        Assert.Equal([SkillEvents.QueueTrainingCompleted, SkillEvents.TrainingComplete], settlement.History.Select(h => h.EventType));
        Assert.Equal(new CharacterSkill(3301, 3, 8000), settlement.Skills[3301]);
    }

    [Fact]
    public void A_timer_settlement_finishes_only_one_overdue_queue_line()
    {
        var state = new QueueState([new(3301, 2), new(3301, 3)], true, Start);
        var firstEnd = Training.Project(state, Rookie(), Catalog, Rate, Start).Entries[0].End;
        var settlement = Training.Settle(state, Rookie(), Catalog, Rate, Start + 10_000 * Minute, maxFinished: 1);
        Assert.Equal([new CharacterSkill(3301, 2, 1415)], settlement.Finished);
        Assert.Equal(new QueueState([new(3301, 3)], true, firstEnd), settlement.State);
        Assert.Equal([SkillEvents.QueueTrainingCompleted], settlement.History.Select(h => h.EventType));
    }

    [Fact]
    public void A_queue_that_is_not_running_does_not_progress_but_lines_already_trained_are_dropped()
    {
        var paused = new QueueState([new(3301, 2)], false, null);
        Assert.False(Training.Settle(paused, Rookie(), Catalog, Rate, Start + 10_000 * Minute).QueueChanged);

        var trained = Rookie();
        trained[3327] = new CharacterSkill(3327, 4, 45255);
        var stale = Training.Settle(new QueueState([new(3327, 4), new(3301, 2)], true, Start), trained, Catalog, Rate, Start + Minute);
        Assert.True(stale.QueueChanged);
        Assert.Empty(stale.Finished);
        Assert.Equal(new QueueState([new(3301, 2)], true, Start + Minute), stale.State); // the next line begins now
    }

    [Fact]
    public void A_running_skill_that_stops_keeps_the_points_it_gained_and_the_level_they_make()
    {
        var state = new QueueState([new(3301, 2)], true, Start);
        var line = Training.Project(state, Rookie(), Catalog, Rate, Start).Entries[0];
        var half = Training.Rebase(line, new CharacterSkill(3301, 1, 250), 1, Start + (10 * Minute));
        Assert.Equal(new CharacterSkill(3301, 1, 550), half);                                            // ten minutes at 30
        Assert.Equal(new CharacterSkill(3301, 1, 250), Training.Rebase(line, new CharacterSkill(3301, 1, 250), 1, Start - 1));
        Assert.Equal(new CharacterSkill(3301, 2, 1415), Training.Rebase(line, new CharacterSkill(3301, 1, 250), 1, Start + (1000 * Minute)));
    }
}
