using Xunit;

namespace Eve.Services.Space.Tests;

public sealed class ModuleEffectNameTests
{
    [Fact]
    public void Deactivation_matches_only_the_running_effect_name()
    {
        Assert.True(SpaceSessions.EffectNameMatches("targetAttack", "targetAttack"));
        Assert.False(SpaceSessions.EffectNameMatches("targetAttack", "targetRepair"));
        Assert.False(SpaceSessions.EffectNameMatches("targetAttack", "TargetAttack"));
    }
}
