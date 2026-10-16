using Xunit;

namespace Eve.Host.Tests;

public sealed class AbyssalTraceRangeTests
{
    [Fact]
    public void Uses_surface_distance_and_includes_the_authoritative_range_boundary()
    {
        Assert.True(AbyssalTraceRange.IsWithin([0, 0, 0], 10, [2_520, 0, 0], 10));
        Assert.False(AbyssalTraceRange.IsWithin([0, 0, 0], 10, [2_521, 0, 0], 10));
        Assert.True(AbyssalTraceRange.IsWithin([0, 0, 0], 10, [19, 0, 0], 10));
    }
}
