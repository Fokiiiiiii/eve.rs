using Eve.Sde;

namespace Eve.Universe;

/// <summary>
/// The stargate graph of the static data (<c>mapStargates</c>: each gate's system and the system at its destination). A jump is one stargate; the graph is undirected
/// (EVE's gates pair up, and a missing return gate is ignored as the market topology ignores it).
/// </summary>
public sealed class StargateGraph
{
    private readonly Dictionary<int, int[]> _neighbors;
    private readonly Dictionary<int, Dictionary<int, int>> _fromSource = [];

    public StargateGraph(IEnumerable<(int From, int To)> links)
    {
        _neighbors = links.Where(l => l.From > 0 && l.To > 0 && l.From != l.To)
            .SelectMany(l => new[] { (l.From, l.To), (l.To, l.From) })
            .GroupBy(l => l.Item1)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Item2).Distinct().ToArray());
    }

    public static async Task<StargateGraph> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var rows = await sde.ReadAsync("mapStargates", cancellationToken: cancellationToken);
        return new StargateGraph(rows
            .Where(r => r.Doc.TryGetProperty("destination", out _))
            .Select(r => (r.Doc.GetProperty("solarSystemID").GetInt32(), r.Doc.GetProperty("destination").GetProperty("solarSystemID").GetInt32())));
    }

    /// <summary>The jumps from one system to every system it reaches (a breadth-first search, cached per source). A system it cannot reach is absent.</summary>
    public IReadOnlyDictionary<int, int> JumpsFrom(int source)
    {
        lock (_fromSource)
        {
            if (_fromSource.TryGetValue(source, out var known))
            {
                return known;
            }
            // shortcut: the cache is cleared past 64 sources (a full table is about 8,000 entries per source), upgrade to an LRU if measured misses justify it
            if (_fromSource.Count >= 64)
            {
                _fromSource.Clear();
            }
            var distances = new Dictionary<int, int> { [source] = 0 };
            var queue = new Queue<int>();
            queue.Enqueue(source);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var neighbor in _neighbors.GetValueOrDefault(current) ?? [])
                {
                    if (distances.TryAdd(neighbor, distances[current] + 1))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }
            _fromSource[source] = distances;
            return distances;
        }
    }
}
