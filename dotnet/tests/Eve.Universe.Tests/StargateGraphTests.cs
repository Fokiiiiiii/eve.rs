using Eve.Universe;

namespace Eve.Universe.Tests;

public sealed class StargateGraphTests
{
    [Fact]
    public void Jumps_count_the_gates_on_the_shortest_route_both_ways()
    {
        // 1 - 2 - 3 - 4, and 2 - 5: a gate pairs up, so the route runs either way
        var graph = new StargateGraph([(1, 2), (2, 3), (3, 4), (5, 2)]);
        var from1 = graph.JumpsFrom(1);
        Assert.Equal(0, from1[1]);
        Assert.Equal(1, from1[2]);
        Assert.Equal(2, from1[3]);
        Assert.Equal(3, from1[4]);
        Assert.Equal(2, from1[5]);
        Assert.Equal(2, graph.JumpsFrom(4)[2]);
        Assert.Equal(1, graph.JumpsFrom(4)[3]);
    }

    [Fact]
    public void A_system_no_gate_reaches_is_absent()
    {
        var graph = new StargateGraph([(1, 2), (7, 8)]);
        Assert.DoesNotContain(7, graph.JumpsFrom(1).Keys);
        Assert.Equal(1, graph.JumpsFrom(1)[2]);
    }
}
