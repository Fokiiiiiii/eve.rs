namespace Eve.Market.Tests;

public class TopologyTests
{
    [Fact]
    public void Distances_and_range_special_cases_follow_the_reference()
    {
        var topology = new MarketTopology([(1,2),(2,3),(3,4),(1,2),(7,8)]);
        var here = new MarketLocation(10,1,100,1000);
        var sameSystem = new MarketLocation(11,1,100,1000);
        var neighbor = new MarketLocation(12,2,100,1000);
        var otherConstellation = new MarketLocation(13,3,200,1000);
        var otherRegion = new MarketLocation(14,4,300,2000);
        Assert.Equal(-1,topology.OrderDistance(here,here));
        Assert.Equal(0,topology.OrderDistance(here,sameSystem));
        Assert.Equal(3,topology.Jumps(1,4));
        Assert.Equal(3,topology.Jumps(4,1));
        Assert.Equal(32767,topology.Jumps(1,7));
        Assert.Equal(32767,topology.Jumps(0,0));
        Assert.True(topology.BidInRange(here,-1,here));
        Assert.False(topology.BidInRange(here,-1,sameSystem));
        Assert.True(topology.BidInRange(here,0,sameSystem));
        Assert.True(topology.BidInRange(here,4,neighbor));
        Assert.False(topology.BidInRange(here,4,otherConstellation)); // 4 is the source constellation sentinel
        Assert.True(topology.BidInRange(here,2,otherConstellation));
        Assert.False(topology.BidInRange(here,2,otherRegion));
        Assert.True(topology.BidInRange(here,32767,otherConstellation));
        Assert.False(topology.BidInRange(here,32767,otherRegion));
        Assert.True(topology.AskInRange(neighbor,here,4));
        Assert.False(topology.AskInRange(otherRegion,here,32767));
        Assert.True(topology.AskInRange(default,here,32767)); // reference fallback when regional metadata is absent
        Assert.False(topology.BidInRange(default,32767,here));
    }

    [Fact]
    public void Esi_four_jump_range_is_distinct_from_the_client_constellation_sentinel()
    {
        var topology=new MarketTopology([(1,2),(2,3),(3,4),(4,5),(5,6)]);
        var origin=new MarketLocation(10,1,100,1000);
        var fourJumps=new MarketLocation(11,5,200,1000);
        var fiveJumps=new MarketLocation(12,6,300,1000);

        Assert.False(topology.BidInRange(origin,MarketRules.RangeConstellation,fourJumps));
        Assert.True(topology.BidInRange(origin,MarketRules.RangeEsiFourJumps,fourJumps));
        Assert.False(topology.BidInRange(origin,MarketRules.RangeEsiFourJumps,fiveJumps));
        Assert.Equal(4,MarketRules.RangeForClient(MarketRules.RangeEsiFourJumps));
    }
}
