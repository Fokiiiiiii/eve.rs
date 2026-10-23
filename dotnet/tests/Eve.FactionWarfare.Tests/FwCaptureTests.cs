namespace Eve.FactionWarfare.Tests;

public sealed class FwCaptureTests
{
    private const long Amarr = FwFactions.AmarrEmpire;
    private const long JitaSystem = 30000142;

    [PgFact]
    public async Task A_vulnerable_system_is_captured_by_the_enemy_faction_and_its_points_and_pool_reset()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, Amarr, pool: 300);
        var system = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0];
        await rig.Service.AddVictoryPointsAsync(system, FwRules.VulnerabilityPoints, rig.Now);

        Assert.Equal(CaptureOutcome.Captured, await rig.Service.CaptureAsync(system, rig.Now));
        var captured = (await rig.Service.OccupiedSystemsAsync()).Single(state => state.SolarSystemId == system);
        Assert.Equal(FwFactions.MinmatarRepublic, captured.OccupierId);
        Assert.Equal(0, captured.AttackerPoints);
        Assert.Equal(0, captured.LpPool);

        // the captured system is no longer vulnerable (its points are gone), so a second capture is refused
        Assert.Equal(CaptureOutcome.NotVulnerable, await rig.Service.CaptureAsync(system, rig.Now));
    }

    [PgFact]
    public async Task A_contested_system_and_an_unoccupied_one_are_not_captured()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, Amarr);
        var system = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0];
        await rig.Service.AddVictoryPointsAsync(system, 74_999, rig.Now);

        Assert.Equal(CaptureOutcome.NotVulnerable, await rig.Service.CaptureAsync(system, rig.Now));
        Assert.Equal(Amarr, (await rig.Service.OccupiedSystemsAsync()).Single(state => state.SolarSystemId == system).OccupierId);
        Assert.Equal(CaptureOutcome.NotOccupied, await rig.Service.CaptureAsync(JitaSystem, rig.Now));
    }
}
