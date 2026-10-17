namespace Eve.FactionWarfare.Tests;

/// <summary>The per-owner windows behind the top, all, militia and alliance statistics (PostgreSQL: the daily rows of two corporations and a pilot).</summary>
public sealed class FwOwnerWindowTests
{
    [Fact]
    public void Sum_and_max_are_taken_per_value()
    {
        var a = new FwStatsWindow(1, 2, 3, 4, 5, 6);
        var b = new FwStatsWindow(10, 0, 30, 40, 0, 60);
        Assert.Equal(new FwStatsWindow(11, 2, 33, 44, 5, 66), FwStore.Sum([a, b]));
        Assert.Equal(new FwStatsWindow(10, 2, 30, 40, 5, 60), FwStore.Max([a, b]));
        Assert.Equal(new FwStatsWindow(0, 0, 0, 0, 0, 0), FwStore.Sum([]));
    }

    [PgFact]
    public async Task Owner_windows_hold_each_corporation_and_pilot_on_its_own_row()
    {
        await using var rig = await FwRig.CreateAsync();
        // yesterday (the day before today) for two corporations and one pilot, and an older row that only counts in the total
        await rig.Service.RecordActivityAsync(1001, 2001, 3, 30, rig.Now - 86_400_000L, CancellationToken.None);
        await rig.Service.RecordActivityAsync(1002, 2002, 1, 10, rig.Now - 86_400_000L, CancellationToken.None);
        await rig.Service.RecordActivityAsync(1002, 2002, 2, 5, rig.Now - 10 * 86_400_000L, CancellationToken.None);
        var corporations = (await rig.Service.OwnerWindowsAsync(true, rig.Now)).ToDictionary(row => row.OwnerId, row => row.Window);
        Assert.Equal(new FwStatsWindow(3, 3, 3, 30, 30, 30), corporations[2001]);
        Assert.Equal(new FwStatsWindow(1, 1, 3, 10, 10, 15), corporations[2002]);
        var pilots = await rig.Service.OwnerWindowsAsync(false, rig.Now);
        Assert.Equal(2, pilots.Count);
        Assert.Equal(1001, pilots.Single(row => row.OwnerId == 1001).OwnerId);
    }
}
