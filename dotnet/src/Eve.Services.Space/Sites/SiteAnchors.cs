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
