using Eve.Dogma;
using Eve.Services.Space;
using Xunit;

namespace Eve.Services.Space.Tests;

public sealed class StructureFieldTargetTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void Capital_transport_classification_uses_the_SDE_attribute(double capital, bool expected)
    {
        var type = new TypeDef(1, 25, 6, new Dictionary<int, double> { [1785] = capital }, []);
        Assert.Equal(expected, SpaceSessions.IsCapitalFieldTarget(type));
    }

    [Fact]
    public void An_absent_capital_attribute_does_not_make_a_ship_capital()
    {
        var type = new TypeDef(11019, 25, 6, new Dictionary<int, double>(), []);
        Assert.False(SpaceSessions.IsCapitalFieldTarget(type));
    }
}
