namespace Eve.Market;

/// <summary>The public order-book facts. Private wallet and item custody are separate persistence facts.</summary>
public sealed record MarketOrder(long Id,int TypeId,bool Bid,decimal Price,long VolumeRemaining,long VolumeEntered,
    long MinimumVolume,int Range,MarketLocation Location,long IssuedMs,int Duration,bool IsNpc = false);
public sealed record MarketFillPlan(MarketOrder Order,long Quantity,decimal GrossAmount);

/// <summary>
/// The fill walk over one book (M7). Candidates are offered one at a time in book order (price, then issue time, then id), so a caller can
/// read the book in batches and stop as soon as the quantity is filled. Each decision depends only on the candidates before it, so the
/// fills of a batched read are the fills of the whole book.
/// </summary>
public sealed class MarketFillWalk
{
    private readonly MarketTopology _topology;
    private readonly int _typeId;
    private readonly bool _buying;
    private readonly MarketLocation _location;
    private readonly decimal _priceLimit;
    private readonly int _buyRange;
    private readonly long _nowMs;
    private readonly List<MarketFillPlan> _fills = [];

    public MarketFillWalk(MarketTopology topology,int typeId,bool buying,MarketLocation location,decimal priceLimit,long quantity,int buyRange,long nowMs)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (priceLimit <= 0 || priceLimit > (decimal)MarketRules.MaximumOrderPrice) throw new ArgumentOutOfRangeException(nameof(priceLimit));
        _topology = topology;
        _typeId = typeId;
        _buying = buying;
        _location = location;
        _priceLimit = priceLimit;
        _buyRange = buyRange;
        _nowMs = nowMs;
        Remaining = quantity;
    }

    /// <summary>The quantity still to fill.</summary>
    public long Remaining { get; private set; }
    public bool Filled => Remaining == 0;
    public IReadOnlyList<MarketFillPlan> Fills => _fills;

    /// <summary>Whether a resting order is a candidate of this walk at all: its type, side, region, expiry and price.</summary>
    public bool IsCandidate(MarketOrder o) => o.TypeId == _typeId && o.Bid != _buying && o.VolumeRemaining > 0 &&
        o.Location.RegionId == _location.RegionId &&
        (o.IsNpc || o.Duration > 0 && (decimal)o.IssuedMs + (decimal)o.Duration * 86400000 > _nowMs) &&
        (_buying ? o.Price <= _priceLimit + 0.0001m : o.Price + 0.0001m >= _priceLimit);

    /// <summary>Offers the next candidate in book order; it is filled as far as the walk still needs, subject to range and minimum volume.</summary>
    public void Offer(MarketOrder order)
    {
        if (Filled || !IsCandidate(order)) return;
        if (_buying ? !_topology.AskInRange(order.Location,_location,_buyRange) : !_topology.BidInRange(order.Location,order.Range,_location)) return;
        var take = Math.Min(Remaining,order.VolumeRemaining);
        if (!_buying && take < order.MinimumVolume &&
            !(order.VolumeRemaining < order.MinimumVolume && take == order.VolumeRemaining)) return;
        _fills.Add(new(order,take,checked(order.Price*take)));
        Remaining -= take;
    }
}

/// <summary>Source price/ station/ id priority and bid minimum-volume rules, with no persistence side effects.</summary>
public static class MarketMatching
{
    public static IReadOnlyList<MarketFillPlan> Plan(IEnumerable<MarketOrder> book,MarketTopology topology,int typeId,
        bool buying,MarketLocation location,decimal priceLimit,long quantity,int buyRange,long nowMs)
    {
        var walk = new MarketFillWalk(topology,typeId,buying,location,priceLimit,quantity,buyRange,nowMs);
        var candidates = book.Where(walk.IsCandidate);
        var ordered = (buying ? candidates.OrderBy(o => o.Price) : candidates.OrderByDescending(o => o.Price))
            .ThenBy(o => o.IssuedMs).ThenBy(o => o.Id);
        foreach (var order in ordered)
        {
            walk.Offer(order);
            if (walk.Filled) break;
        }
        return walk.Fills;
    }
}
