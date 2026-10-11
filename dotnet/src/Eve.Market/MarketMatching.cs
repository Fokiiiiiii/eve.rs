namespace Eve.Market;

/// <summary>The public order-book facts. Private wallet and item custody are separate persistence facts.</summary>
public sealed record MarketOrder(long Id,int TypeId,bool Bid,decimal Price,long VolumeRemaining,long VolumeEntered,
    long MinimumVolume,int Range,MarketLocation Location,long IssuedMs,int Duration,bool IsNpc = false);
public sealed record MarketFillPlan(MarketOrder Order,long Quantity,decimal GrossAmount);

/// <summary>Source price/ station/ id priority and bid minimum-volume rules, with no persistence side effects.</summary>
public static class MarketMatching
{
    public static IReadOnlyList<MarketFillPlan> Plan(IEnumerable<MarketOrder> book,MarketTopology topology,int typeId,
        bool buying,MarketLocation location,decimal priceLimit,long quantity,int buyRange,long nowMs)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (priceLimit <= 0 || priceLimit > (decimal)MarketRules.MaximumOrderPrice) throw new ArgumentOutOfRangeException(nameof(priceLimit));
        var candidates = book.Where(o => o.TypeId == typeId && o.Bid != buying && o.VolumeRemaining > 0 &&
            o.Location.RegionId == location.RegionId &&
            (o.IsNpc || o.Duration > 0 && (decimal)o.IssuedMs + (decimal)o.Duration * 86400000 > nowMs) &&
            (buying ? o.Price <= priceLimit + 0.0001m : o.Price + 0.0001m >= priceLimit));
        var ordered = (buying ? candidates.OrderBy(o => o.Price) : candidates.OrderByDescending(o => o.Price))
            .ThenBy(o => o.IssuedMs).ThenBy(o => o.Id);
        var remaining = quantity;
        var fills = new List<MarketFillPlan>();
        foreach (var order in ordered)
        {
            if (buying ? !topology.AskInRange(order.Location,location,buyRange) : !topology.BidInRange(order.Location,order.Range,location)) continue;
            var take = Math.Min(remaining,order.VolumeRemaining);
            if (!buying && take < order.MinimumVolume &&
                !(order.VolumeRemaining < order.MinimumVolume && take == order.VolumeRemaining)) continue;
            fills.Add(new(order,take,checked(order.Price*take)));
            remaining -= take;
            if (remaining == 0) break;
        }
        return fills;
    }
}
