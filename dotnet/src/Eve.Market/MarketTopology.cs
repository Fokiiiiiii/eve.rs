using System.Collections.Concurrent;
using Eve.Sde;
using Eve.Universe;

namespace Eve.Market;

public readonly record struct MarketLocation(long StationId,int SolarSystemId,int ConstellationId,int RegionId)
{
    public static MarketLocation From(Station station) => new(station.Id,station.SolarSystemId,station.ConstellationId,station.RegionId);
}

/// <summary>Market distances and eligibility ported from marketTopology.js. The stargate graph is undirected.</summary>
public sealed class MarketTopology
{
    private readonly Dictionary<int,int[]> _neighbors;
    private readonly ConcurrentDictionary<(int From,int To),int> _distances = new();
    public MarketTopology(IEnumerable<(int From,int To)> links) => _neighbors = links
        .Where(e => e.From > 0 && e.To > 0).SelectMany(e => new[]{e,(e.To,e.From)})
        .GroupBy(e => e.Item1).ToDictionary(g => g.Key,g => g.Select(e => e.Item2).Distinct().ToArray());

    public static async Task<MarketTopology> LoadAsync(SdeReader sde,CancellationToken token = default) =>
        new((await sde.ReadAsync("mapStargates",cancellationToken:token))
            .Select(r => (r.Doc.GetProperty("solarSystemID").GetInt32(),r.Doc.GetProperty("destination").GetProperty("solarSystemID").GetInt32())));

    public int Jumps(int from,int to)
    {
        if (from <= 0 || to <= 0) return MarketRules.RangeRegion;
        if (from == to) return 0;
        var key = from < to ? (from,to) : (to,from);
        if (_distances.TryGetValue(key,out var known)) return known;
        var visited = new HashSet<int>{from};
        var queue = new Queue<(int System,int Distance)>();
        queue.Enqueue((from,0));
        var result = MarketRules.RangeRegion;
        while (queue.TryDequeue(out var current))
        {
            foreach (var neighbor in _neighbors.GetValueOrDefault(current.System) ?? [])
            {
                if (!visited.Add(neighbor)) continue;
                if (neighbor == to) { result=current.Distance+1; queue.Clear(); break; }
                queue.Enqueue((neighbor,current.Distance+1));
            }
        }
        // ponytail: clear after 4096 pairs; use an LRU only if measured cache misses justify it.
        if (_distances.Count >= 4096) _distances.Clear();
        _distances[key] = result;
        return result;
    }

    public int OrderDistance(MarketLocation viewer,MarketLocation order) =>
        viewer.StationId > 0 && viewer.StationId == order.StationId ? -1 : Jumps(viewer.SolarSystemId,order.SolarSystemId);

    public bool BidInRange(MarketLocation bid,int range,MarketLocation seller) => range switch
    {
        MarketRules.RangeStation => bid.StationId > 0 && bid.StationId == seller.StationId,
        MarketRules.RangeSolarSystem => bid.SolarSystemId > 0 && bid.SolarSystemId == seller.SolarSystemId,
        MarketRules.RangeEsiFourJumps => Jumps(bid.SolarSystemId,seller.SolarSystemId) <= 4,
        MarketRules.RangeConstellation => bid.SolarSystemId > 0 && bid.ConstellationId == seller.ConstellationId,
        MarketRules.RangeRegion => bid.RegionId > 0 && bid.RegionId == seller.RegionId,
        > 0 => Jumps(bid.SolarSystemId,seller.SolarSystemId) <= range,
        _ => false,
    };
    public bool AskInRange(MarketLocation ask,MarketLocation buyer,int buyRange) => buyRange switch
    {
        MarketRules.RangeStation => ask.StationId > 0 && ask.StationId == buyer.StationId,
        MarketRules.RangeSolarSystem => ask.SolarSystemId > 0 && ask.SolarSystemId == buyer.SolarSystemId,
        MarketRules.RangeConstellation => ask.SolarSystemId > 0 && ask.ConstellationId == buyer.ConstellationId,
        MarketRules.RangeRegion => ask.RegionId <= 0 || buyer.RegionId <= 0 || ask.RegionId == buyer.RegionId,
        > 0 => Jumps(buyer.SolarSystemId,ask.SolarSystemId) <= buyRange,
        _ => false,
    };
}
