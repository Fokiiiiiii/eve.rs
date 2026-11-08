using Eve.Universe;

namespace Eve.Universe.Tests;

public class SolarSystemDistanceTests
{
    private const double V24LightYearMeters = 9_460_000_000_000_000.0;

    [Fact]
    public void Light_year_distances_use_the_v24_jump_distance_scale()
    {
        var catalog = new SolarSystemCatalog([
            new SolarSystemInfo(1, "Origin", 1, 1, 0, X: 0),
            new SolarSystemInfo(2, "One light year", 1, 1, 0, X: V24LightYearMeters),
            new SolarSystemInfo(3, "Beyond one light year", 1, 1, 0, X: 1.00001 * V24LightYearMeters),
        ]);

        Assert.Equal(1.0, catalog.LightYearsBetween(1, 2)!.Value);
        Assert.Equal(1.00001, catalog.LightYearsBetween(1, 3)!.Value, 12);
    }
}
