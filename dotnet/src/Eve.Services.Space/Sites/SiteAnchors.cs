using System.Collections.Concurrent;
using System.Text.Json;
using Eve.Sde;

namespace Eve.Services.Space.Sites;

/// <summary>The celestials of a system a site can be placed beside.</summary>
public interface ISiteAnchors
{
    Task<IReadOnlyList<SiteAnchor>> OfAsync(int solarSystemId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The anchors from the static data: the planets, asteroid belts, stations and stargates of the system, in the positions the simulation gives them (not the sun: the reference
/// leaves it out, and a site is not found at the star). The moons are left out, as the wiki says "may only be planets" and a planet has several.
/// </summary>
public sealed class SdeSiteAnchors(SdeReader sde) : ISiteAnchors
{
    private static readonly string[] Tables = ["mapPlanets", "mapAsteroidBelts", "npcStations", "mapStargates"];
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
                if (row.Doc.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Object
                    && position.TryGetProperty("x", out var x) && position.TryGetProperty("y", out var y) && position.TryGetProperty("z", out var z))
                {
                    anchors.Add(new SiteAnchor(row.Key, [x.GetDouble(), y.GetDouble(), z.GetDouble()]));
                }
            }
        }
        // the order of the tables is fixed and each is in file order, so the same system always gives the same list
        return anchors;
    }
}
