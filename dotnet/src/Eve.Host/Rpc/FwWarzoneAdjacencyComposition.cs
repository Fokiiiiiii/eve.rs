using Eve.FactionWarfare;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private readonly SemaphoreSlim _fwWarzoneNeighborsLock = new(1, 1);
    private IReadOnlyDictionary<long, IReadOnlyCollection<long>>? _fwWarzoneNeighbors;
    private IReadOnlyDictionary<long, long>? _fwWarzoneHeadquarters;

    private async ValueTask<IReadOnlyDictionary<long, long>> ReadFwWarzoneHeadquartersAsync(CancellationToken cancellationToken)
    {
        if (_fwWarzoneHeadquarters is { } cached)
            return cached;

        await _fwWarzoneNeighborsLock.WaitAsync(cancellationToken);
        try
        {
            if (_fwWarzoneHeadquarters is { } loaded)
                return loaded;

            var factions = FwWarzones.Ids.SelectMany(FwWarzones.FactionsOf).Distinct().ToArray();
            var rows = await sde.ReadAsync("chrFactions", factions, cancellationToken);
            var headquarters = new Dictionary<long, long>(rows.Count);
            foreach (var row in rows)
            {
                if (!row.Doc.TryGetProperty("solarSystemID", out var system) ||
                    !system.TryGetInt64(out var systemId) || systemId <= 0 ||
                    !headquarters.TryAdd(row.Key, systemId))
                    throw new InvalidDataException($"Faction {row.Key} has invalid headquarters data in the SDE.");
            }
            if (headquarters.Count != factions.Length || factions.Any(faction => !headquarters.ContainsKey(faction)))
                throw new InvalidDataException("Faction headquarters data is incomplete in the SDE.");

            return _fwWarzoneHeadquarters = headquarters;
        }
        finally
        {
            _fwWarzoneNeighborsLock.Release();
        }
    }

    private async ValueTask<IReadOnlyDictionary<long, IReadOnlyCollection<long>>> ReadFwWarzoneNeighborsAsync(CancellationToken cancellationToken)
    {
        if (_fwWarzoneNeighbors is { } cached)
            return cached;

        await _fwWarzoneNeighborsLock.WaitAsync(cancellationToken);
        try
        {
            if (_fwWarzoneNeighbors is { } loaded)
                return loaded;

            var systems = FwWarzones.Ids.SelectMany(FwWarzones.SystemsOf).Distinct().ToArray();
            var gates = await sde.ReadByFieldAsync("mapStargates", "solarSystemID", systems, cancellationToken);
            var neighbors = new Dictionary<long, HashSet<long>>();
            foreach (var gate in gates)
            {
                var from = gate.Doc.GetProperty("solarSystemID").GetInt64();
                var to = gate.Doc.GetProperty("destination").GetProperty("solarSystemID").GetInt64();
                if (from <= 0 || to <= 0 || from == to)
                    continue;
                if (!neighbors.TryGetValue(from, out var fromNeighbors))
                    neighbors[from] = fromNeighbors = [];
                if (!neighbors.TryGetValue(to, out var toNeighbors))
                    neighbors[to] = toNeighbors = [];
                fromNeighbors.Add(to);
                toNeighbors.Add(from);
            }

            return _fwWarzoneNeighbors = neighbors.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyCollection<long>)entry.Value);
        }
        finally
        {
            _fwWarzoneNeighborsLock.Release();
        }
    }
}
