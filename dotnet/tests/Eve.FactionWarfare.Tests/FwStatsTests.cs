using Eve.Marshal;

namespace Eve.FactionWarfare.Tests;

public sealed class FwStatsTests
{
    private const long Character = 91_000_001;
    private const long Corporation = 98_500_001;
    private const long Day = 86_400_000L;
    private const long Amarr = FwFactions.AmarrEmpire;

    [PgFact]
    public async Task Kills_and_points_fall_in_the_windows_of_their_days()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.Service.RecordActivityAsync(Character, Corporation, 1, 100, rig.Now - Day);
        await rig.Service.RecordActivityAsync(Character, Corporation, 2, 50, rig.Now - 3 * Day);
        await rig.Service.RecordActivityAsync(Character, Corporation, 4, 10, rig.Now - 20 * Day);
        await rig.Service.RecordActivityAsync(Character, Corporation, 8, 5, rig.Now);
        await rig.Service.RecordActivityAsync(Character, Corporation, 1, 5, rig.Now);

        // yesterday: 1 kill, 100 points; the seven days before today: yesterday and three days ago; all days: everything.
        var expected = new FwStatsWindow(1, 3, 16, 100, 150, 170);
        Assert.Equal(expected, await rig.Service.PilotStatsAsync(Character, rig.Now));
        Assert.Equal(expected, await rig.Service.CorporationStatsAsync(Corporation, rig.Now));
        Assert.Equal(new FwStatsWindow(0, 0, 0, 0, 0, 0), await rig.Service.PilotStatsAsync(Character + 1, rig.Now));
    }

    [PgFact]
    public async Task Victory_points_accumulate_on_an_occupied_system_and_reach_the_vulnerable_state()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, Amarr);
        var system = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0];
        Assert.Equal(1000, await rig.Service.AddVictoryPointsAsync(system, 1000, rig.Now));
        Assert.Equal(ContestState.Contested, FwRules.Contest(1000));
        Assert.Equal(75_000, await rig.Service.AddVictoryPointsAsync(system, 74_000, rig.Now));
        Assert.Equal(ContestState.Vulnerable, FwRules.Contest(75_000));
        Assert.Null(await rig.Service.AddVictoryPointsAsync(30000142, 10, rig.Now));
    }

    [PgFact]
    public async Task Victory_points_must_be_positive()
    {
        await using var rig = await FwRig.CreateAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Service.AddVictoryPointsAsync(1, 0, rig.Now));
    }
}
