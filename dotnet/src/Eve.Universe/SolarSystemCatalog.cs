using System.Text.Json;
using Eve.Sde;

namespace Eve.Universe;

/// <summary>A solar system of the static data: where it is in the universe.</summary>
public sealed record SolarSystemInfo(int Id, string Name, int ConstellationId, int RegionId, double Security);

/// <summary>The solar systems of the game, read from the static data.</summary>
public sealed class SolarSystemCatalog
{
    private readonly Dictionary<int, SolarSystemInfo> _systems;

    public SolarSystemCatalog(IEnumerable<SolarSystemInfo> systems) => _systems = systems.ToDictionary(s => s.Id);

    public int Count => _systems.Count;

    public SolarSystemInfo? Get(int id) => _systems.GetValueOrDefault(id);

    public static async Task<SolarSystemCatalog> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var rows = await sde.ReadAsync("mapSolarSystems", cancellationToken: cancellationToken);
        return new SolarSystemCatalog(rows.Select(r => new SolarSystemInfo(
            (int)r.Key,
            SdeReader.English(r.Doc.GetProperty("name")),
            r.Doc.GetProperty("constellationID").GetInt32(),
            r.Doc.GetProperty("regionID").GetInt32(),
            r.Doc.TryGetProperty("securityStatus", out var security) && security.ValueKind == JsonValueKind.Number ? security.GetDouble() : 0d)));
    }
}
