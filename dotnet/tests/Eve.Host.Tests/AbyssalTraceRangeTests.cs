using Xunit;
using Eve.Space;

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

    [Fact]
    public void Reconciles_only_a_bounded_follow_to_the_same_gate()
    {
        var followsGate = new SimAbyssalFollowApproach(22, 2_500, 100);
        Assert.True(AbyssalTraceRange.IsWithinCommittedFollowApproach(
            [3_000, 0, 0], 10, [0, 0, 0], 10, 22, followsGate));
        Assert.False(AbyssalTraceRange.IsWithinCommittedFollowApproach(
            [3_000, 0, 0], 10, [0, 0, 0], 10, 23, followsGate));
        Assert.False(AbyssalTraceRange.IsWithinCommittedFollowApproach(
            [3_000, 0, 0], 10, [0, 0, 0], 10, 22, followsGate with { FollowRangeMeters = 2_501 }));
        Assert.False(AbyssalTraceRange.IsWithinCommittedFollowApproach(
            [3_100, 0, 0], 10, [0, 0, 0], 10, 22, followsGate));
    }
}
