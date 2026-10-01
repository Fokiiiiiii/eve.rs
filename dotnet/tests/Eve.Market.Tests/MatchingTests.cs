namespace Eve.Market.Tests;

public class MatchingTests
{
    [Fact]
    public void Matching_merges_seed_and_player_prices_then_station_and_id_and_respects_minimum_volume_and_expiry()
    {
        var topology = new MarketTopology([(1,2),(2,3)]);
        var here = new MarketLocation(10,1,100,1000);
        var away = new MarketLocation(20,2,100,1000);
        const long now = 100000000;
        MarketOrder Order(long id,decimal price,long remaining,bool bid = false,long min = 1) =>
            new(id,34,bid,price,remaining,remaining,min,32767,here,now,90);
        var seed = Order(9,10,2) with { IsNpc=true,IssuedMs=0,Duration=0 };
        var player = Order(4,10,3);
        var askElsewhere = Order(1,10,5) with { Location=away };
        var expensive = Order(5,11,2);
        var expired = Order(2,1,100) with { Duration=1,IssuedMs=now-86400000 };
        var otherRegion = Order(3,1,100) with { Location=here with { RegionId=2000 } };
        var fills = MarketMatching.Plan([askElsewhere,seed,player,expensive,expired,otherRegion],topology,34,true,here,10,6,32767,now);
        Assert.Equal([4L,9L,1L],fills.Select(f => f.Order.Id));
        Assert.Equal([3L,2L,1L],fills.Select(f => f.Quantity));
        Assert.Equal(60m,fills.Sum(f => f.GrossAmount));
        Assert.Equal(3,player.VolumeRemaining); // a plan does not mutate its input book
        Assert.Equal([4L,9L],MarketMatching.Plan([seed,player,askElsewhere],topology,34,true,here,10,20,-1,now).Select(f => f.Order.Id));
        var largeBid = Order(1,12,10,true,8);
        var smallRemainder = Order(2,11,3,true,8);
        Assert.Empty(MarketMatching.Plan([largeBid],topology,34,false,here,10,7,-1,now));
        Assert.Equal(3,Assert.Single(MarketMatching.Plan([largeBid,smallRemainder],topology,34,false,here,10,7,-1,now)).Quantity);
        Assert.Empty(MarketMatching.Plan([smallRemainder],topology,34,false,here,10,2,-1,now));
        Assert.Equal(8,Assert.Single(MarketMatching.Plan([largeBid,smallRemainder],topology,34,false,here,10,8,-1,now)).Quantity);
        Assert.Throws<ArgumentOutOfRangeException>(() => MarketMatching.Plan([],topology,34,true,here,10,0,-1,now));
    }
}
