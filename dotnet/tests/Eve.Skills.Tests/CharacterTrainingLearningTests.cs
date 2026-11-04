namespace Eve.Skills.Tests;

/// <summary>What an implant or a booster adds to the learning attributes changes the rate, and the running skill is rebased at the rate it had.</summary>
public class CharacterTrainingLearningTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [PgFact]
    public async Task A_learning_bonus_raises_the_rate_from_the_change_and_the_running_skill_keeps_its_points_before_it()
    {
        // The bonus is read through a cache that every call refreshes first (as the host's learning bonus does): an implant plugged in
        // after the rebase changes the cache at the next read. Small Hybrid Turret is perception and willpower: 20 + 20/2 = 30 a minute.
        var pending = 0d;
        var applied = 0d;
        await using var rig = await TrainingRig.CreateAsync(new TrainingSettings
        {
            LearningBonus = (_, _) => applied,
            RefreshLearningBonus = (_, _) =>
            {
                applied = pending;
                return Task.CompletedTask;
            },
        });
        await rig.SavedAsync(await rig.StartAsync(3301));
        rig.Wait(10 * Minute);
        Assert.Equal(550, (await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account)).Skills.Single(s => s.TypeId == 3301).SkillPoints);

        // the change: the running skill is rebased at the rate it has (the implant is not in yet), then the implant of +5 on every learning
        // attribute is plugged in: from now on the rate is (25 + 25/2) = 37.5 a minute
        var update = Assert.IsType<TrainingUpdate>(await rig.Training.RebaseRunningAsync(TrainingRig.Character, TrainingRig.Account));
        Assert.True(update.RebaseChanged);
        Assert.Equal(550, update.Rebased!.SkillPoints);
        Assert.Equal(3301, update.View.InTraining);
        pending = 5;

        rig.Wait(10 * Minute);
        Assert.Equal(550 + 375, (await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account)).Skills.Single(s => s.TypeId == 3301).SkillPoints);
    }

    [PgFact]
    public async Task Rebasing_with_nothing_running_changes_nothing()
    {
        await using var rig = await TrainingRig.CreateAsync(new TrainingSettings { LearningBonus = (_, _) => 5 });
        Assert.Null(await rig.Training.RebaseRunningAsync(TrainingRig.Character, TrainingRig.Account));
        Assert.Empty((await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account)).Queue);
    }

    [PgFact]
    public async Task The_refresh_of_the_bonus_runs_before_a_rate_is_read()
    {
        var refreshed = new List<long>();
        await using var rig = await TrainingRig.CreateAsync(new TrainingSettings
        {
            RefreshLearningBonus = (character, _) =>
            {
                refreshed.Add(character);
                return Task.CompletedTask;
            },
        });
        await rig.Training.ViewAsync(TrainingRig.Character, TrainingRig.Account);
        Assert.Contains(TrainingRig.Character, refreshed);
    }
}
