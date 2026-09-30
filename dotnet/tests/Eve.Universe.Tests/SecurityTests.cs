using Eve.Universe;

namespace Eve.Universe.Tests;

/// <summary>The security classes as <c>eveuniverse/security.py</c> of the client has them.</summary>
public class SecurityTests
{
    [Theory]
    [InlineData(-1.0, SecurityClass.ZeroSec)]
    [InlineData(0.0, SecurityClass.ZeroSec)]
    [InlineData(0.0001, SecurityClass.LowSec)]
    [InlineData(0.4499, SecurityClass.LowSec)]
    [InlineData(0.45, SecurityClass.HighSec)]
    [InlineData(0.9499, SecurityClass.HighSec)]
    [InlineData(0.95, SecurityClass.SafeSec)]
    [InlineData(1.0, SecurityClass.SafeSec)]
    public void A_security_level_is_in_the_class_the_game_puts_it(double level, SecurityClass expected) =>
        Assert.Equal(expected, Security.ClassFromLevel(level));

    [Fact]
    public void The_questions_about_a_class_are_the_ones_the_client_asks()
    {
        Assert.Equal(
            [true, false, false, false],
            [.. Enum.GetValues<SecurityClass>().Select(Security.IsZeroSec)]);
        Assert.Equal(
            [false, true, false, false],
            [.. Enum.GetValues<SecurityClass>().Select(Security.IsLowSec)]);
        Assert.Equal(
            [false, false, true, true],
            [.. Enum.GetValues<SecurityClass>().Select(Security.IsHighSecOrHigher)]);
        Assert.Equal(
            [true, true, false, false],
            [.. Enum.GetValues<SecurityClass>().Select(Security.IsLowSecOrLower)]);
        Assert.Equal(
            [false, true, true, true],
            [.. Enum.GetValues<SecurityClass>().Select(Security.IsLowSecOrHigher)]);
        Assert.Equal(
            [false, false, false, true],
            [.. Enum.GetValues<SecurityClass>().Select(Security.IsSafeSec)]);
        Assert.Equal(["Null Security", "Low Security", "High Security", "Safe Security"], [.. Enum.GetValues<SecurityClass>().Select(Security.Text)]);
        Assert.Equal("Unknown Security Class", Security.Text((SecurityClass)9));
    }
}
