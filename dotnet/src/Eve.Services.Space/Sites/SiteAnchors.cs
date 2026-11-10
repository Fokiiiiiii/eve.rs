using System.Collections.Concurrent;
using Eve.Sde;

namespace Eve.Services.Space.Sites;

/// <summary>The celestials of a system a site can be placed beside.</summary>
public interface ISiteAnchors
{
    Task<IReadOnlyList<SiteAnchor>> OfAsync(int solarSystemId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The anchors from the static data: the planets and moons, asteroid belts, stations and stargates of the system, in the positions the simulation gives them (not the sun).
/// </summary>
public sealed class SdeSiteAnchors(SdeReader sde) : ISiteAnchors
{
    private static readonly string[] Tables = ["mapPlanets", "mapMoons", "mapAsteroidBelts", "npcStations", "mapStargates"];
    private readonly ConcurrentDictionary<int, Task<IReadOnlyList<SiteAnchor>>> _systems = new();

    public Task<IReadOnlyList<SiteAnchor>> OfAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        var task = _systems.GetOrAdd(solarSystemId, system => ReadAsync(system));
        if (task.IsFaulted)
        {
            _systems.TryRemove(solarSystemId, out _);
        }
        return task.WaitAsync(cancellationToken);
    }

    /// <summary>Reads the anchors of several systems with one SDE query per source table.</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<SiteAnchor>>> ForSystemsAsync(
        IEnumerable<int> solarSystemIds, CancellationToken cancellationToken = default)
    {
        var systems = solarSystemIds.Distinct().ToArray();
        var anchorsBySystem = systems.ToDictionary(system => system, _ => new List<SiteAnchor>());
        if (systems.Length == 0)
            return anchorsBySystem.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<SiteAnchor>)pair.Value);

        var systemIds = systems.Select(system => (long)system).ToArray();
        foreach (var table in Tables)
        {
            var rows = await sde.ReadByFieldAsync(table, "solarSystemID", systemIds, cancellationToken);
            foreach (var row in rows)
            {
                if (!row.Doc.TryGetProperty("solarSystemID", out var systemValue) || !systemValue.TryGetInt32(out var systemId) ||
                    SiteAnchor.FromSdeRow(row) is not { } anchor || !anchorsBySystem.TryGetValue(systemId, out var anchors))
                {
                    continue;
                }
                anchors.Add(anchor);
            }
        }

        return anchorsBySystem.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<SiteAnchor>)pair.Value.AsReadOnly());
    }

    private async Task<IReadOnlyList<SiteAnchor>> ReadAsync(int solarSystemId)
    {
        var anchors = new List<SiteAnchor>();
        foreach (var table in Tables)
        {
            foreach (var row in await sde.ReadByFieldAsync(table, "solarSystemID", [solarSystemId]))
            {
                if (SiteAnchor.FromSdeRow(row) is { } anchor)
                {
                    anchors.Add(anchor);
                }
            }
        }
        // the order of the tables is fixed and each is in file order, so the same system always gives the same list
        return anchors;
    }
}
