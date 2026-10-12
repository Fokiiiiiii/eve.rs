using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>A smartbomb is lit only at the red safety in low, high or safe space (the client's GetRequiredSafetyLevelForEffect; the wiki, Smartbombs).</summary>
public class SmartbombSafetyHostTests
{
    private const int SmallEmpSmartbomb = 1563, HighSlot = 27, Raven = 638;

    [PgFact]
    public async Task A_smartbomb_in_high_security_space_is_lit_only_at_the_red_safety()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _, module) = await RepairHostTests.InSpaceWithAsync(rig, SmallEmpSmartbomb, HighSlot, Raven);
        var level = Eve.Crimewatch.SafetyLevel.Full;
        rig.Space.SafetyLevelOf = (_, _) => Task.FromResult(level);
        var activations = rig.Sim.Heard.OfType<SimActivate>().Count();

        foreach (var stopped in new[] { Eve.Crimewatch.SafetyLevel.Full, Eve.Crimewatch.SafetyLevel.Partial })
        {
            level = stopped;
            var refused = await rig.Space.ActivateAsync(session, module, "empWave", 0, 1000, CancellationToken.None);
            Assert.Equal("SafetyActivated", refused!.Reason);
        }
        Assert.Equal(activations, rig.Sim.Heard.OfType<SimActivate>().Count());

        level = Eve.Crimewatch.SafetyLevel.None;
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, module, "empWave", 0, 1000, CancellationToken.None));
        Assert.IsType<SimAction.Smartbomb>(Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect.Action);
    }
}
