using Eve.Crimewatch;

namespace Eve.Services.Crimewatch.Tests;

public sealed class SecurityTagRequirementsTests
{
    [Fact]
    public void Crossing_tag_tiers_uses_each_band_and_only_tags_needed()
    {
        var result = SecurityTagRequirements.Calculate(-9, -7.5);

        Assert.Equal(
        [
            new SecurityTagRequirement(33138, 2),
            new SecurityTagRequirement(33139, 1),
        ], result);
    }

    [Fact]
    public void Last_tag_at_zero_keeps_its_full_half_point_gain()
    {
        Assert.Equal([new SecurityTagRequirement(33141, 1)], SecurityTagRequirements.Calculate(-0.09, 0));
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(-1, 0.5)]
    [InlineData(-11, -10)]
    public void Requests_outside_available_tags_are_rejected(double current, double requested) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SecurityTagRequirements.Calculate(current, requested));
}
