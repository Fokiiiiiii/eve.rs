using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>The rule of the agents in space: 20 km from the system centre, the same place for the same agent, a different place for another agent.</summary>
public class InSpaceAgentPlacementTests
{
    [Fact]
    public void An_agent_in_space_is_placed_20_km_from_the_system_centre_deterministically()
    {
        var first = InSpaceAgentPlacement.Position(3019369, -2.2e17, 4.0e16, 2.5e16);
        var again = InSpaceAgentPlacement.Position(3019369, -2.2e17, 4.0e16, 2.5e16);
        var other = InSpaceAgentPlacement.Position(3019370, -2.2e17, 4.0e16, 2.5e16);
        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        var offset = Math.Sqrt(Math.Pow(first[0] + 2.2e17, 2) + Math.Pow(first[2] - 2.5e16, 2));
        Assert.Equal(InSpaceAgentPlacement.RadiusMetres, offset, 1e-3 * 1e3);
        Assert.Equal(4.0e16, first[1]);
    }
}
