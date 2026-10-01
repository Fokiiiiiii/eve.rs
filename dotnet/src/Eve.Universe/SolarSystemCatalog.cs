using System.Text.Json;
using Eve.Sde;

namespace Eve.Universe;

/// <summary>
/// A solar system of the static data: where it is in the universe. <c>FactionId</c> is the faction the system belongs to (its own, else its
/// constellation's, else its region's; 0 when none), as the simulation's world takes it.
/// </summary>
public sealed record SolarSystemInfo(int Id, string Name, int ConstellationId, int RegionId, double Security, int FactionId = 0);

/// <summary>The solar systems of the game, read from the static data.</summary>
public sealed class SolarSystemCatalog
{
    private readonly Dictionary<int, SolarSystemInfo> _systems;

    public SolarSystemCatalog(IEnumerable<SolarSystemInfo> systems) => _systems = systems.ToDictionary(s => s.Id);

    public int Count => _systems.Count;

    public SolarSystemInfo? Get(int id) => _systems.GetValueOrDefault(id);

    public static async Task<SolarSystemCatalog> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var constellationFactions = await FactionsOfAsync(sde, "mapConstellations", cancellationToken);
        var regionFactions = await FactionsOfAsync(sde, "mapRegions", cancellationToken);
        var rows = await sde.ReadAsync("mapSolarSystems", cancellationToken: cancellationToken);
        return new SolarSystemCatalog(rows.Select(r =>
        {
            var constellation = r.Doc.GetProperty("constellationID").GetInt32();
            var region = r.Doc.GetProperty("regionID").GetInt32();
            var faction = FactionOf(r.Doc) is > 0 and var own ? own
                : constellationFactions.GetValueOrDefault(constellation) is > 0 and var inherited ? inherited
                : regionFactions.GetValueOrDefault(region);
            return new SolarSystemInfo(
                (int)r.Key,
                SdeReader.English(r.Doc.GetProperty("name")),
                constellation,
                region,
                r.Doc.TryGetProperty("securityStatus", out var security) && security.ValueKind == JsonValueKind.Number ? security.GetDouble() : 0d,
                faction);
        }));
    }

    private static int FactionOf(JsonElement doc) =>
        doc.TryGetProperty("factionID", out var faction) && faction.ValueKind == JsonValueKind.Number ? faction.GetInt32() : 0;

    private static async Task<Dictionary<int, int>> FactionsOfAsync(SdeReader sde, string table, CancellationToken cancellationToken)
    {
        var rows = await sde.ReadAsync(table, cancellationToken: cancellationToken);
        return rows.Select(r => (Id: (int)r.Key, Faction: FactionOf(r.Doc))).Where(r => r.Faction > 0).ToDictionary(r => r.Id, r => r.Faction);
    }
}
