namespace Eve.FactionWarfare.Tests;

public sealed class FwKillTests
{
    private const long Character = 93_000_001;
    private const long Corporation = 98_700_001;
    private const long Now = FwRig.Epoch;

    [PgFact]
    public async Task An_enemy_faction_npc_kill_in_a_warzone_counts_once_for_the_pilot_and_its_corporation()
    {
        await using var rig = await FwRig.CreateAsync();
        var system = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0];
        Assert.True(await rig.Service.RecordNpcKillAsync(Character, Corporation, FwFactions.AmarrEmpire, FwFactions.AngelCartel, system, Now));
        var expected = new FwStatsWindow(0, 0, 1, 0, 0, 0);
        Assert.Equal(expected, await rig.Service.PilotStatsAsync(Character, Now));
        Assert.Equal(expected, await rig.Service.CorporationStatsAsync(Corporation, Now));
    }

    [PgFact]
    public async Task A_kill_that_is_not_an_enemy_kill_or_outside_a_warzone_is_not_counted()
    {
        await using var rig = await FwRig.CreateAsync();
        var system = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0];
        Assert.False(await rig.Service.RecordNpcKillAsync(Character, Corporation, FwFactions.AmarrEmpire, FwFactions.GuristasPirates, system, Now));
        Assert.False(await rig.Service.RecordNpcKillAsync(Character, Corporation, FwFactions.AmarrEmpire, FwFactions.AngelCartel, 30000142, Now));
        Assert.False(await rig.Service.RecordNpcKillAsync(Character, Corporation, 0, FwFactions.AngelCartel, system, Now));
        Assert.Equal(new FwStatsWindow(0, 0, 0, 0, 0, 0), await rig.Service.PilotStatsAsync(Character, Now));
    }
}
