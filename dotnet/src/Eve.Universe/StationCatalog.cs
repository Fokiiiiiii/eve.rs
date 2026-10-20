using System.Text.Json;
using Eve.Sde;

namespace Eve.Universe;

/// <summary>A station of the static data: what it is called and where it is (its position in the system, in metres).</summary>
public sealed record Station(
    long Id, int TypeId, string Name, long OwnerId, string OwnerName, long? FactionId, string FactionName, int OperationId, long OrbitId,
    int SolarSystemId, string SolarSystemName, int ConstellationId, string ConstellationName, int RegionId, string RegionName,
    double Security, double ReprocessingStationsTake, int ReprocessingHangarFlag, double X = 0, double Y = 0, double Z = 0);

/// <summary>The stations of the game, with the names the client shows, read from the static data.</summary>
public sealed class StationCatalog
{
    private readonly Dictionary<long, Station> _stations;

    public StationCatalog(IEnumerable<Station> stations) => _stations = stations.ToDictionary(s => s.Id);

    public int Count => _stations.Count;

    public Station? Get(long id) => _stations.GetValueOrDefault(id);

    /// <summary>The stations of a solar system (EVE.js getStationsForSystem).</summary>
    public IEnumerable<Station> InSystem(int solarSystemId) => _stations.Values.Where(station => station.SolarSystemId == solarSystemId);

    public IEnumerable<Station> All => _stations.Values;

    /// <summary>
    /// </summary>
    public static string NameOf(string system, int celestialIndex, int? moon, string owner, string? operation)
    {
        var place = moon is { } m ? $"{system} {Roman(celestialIndex)} - Moon {m}" : $"{system} {Roman(celestialIndex)}";
        return operation is { Length: > 0 } ? $"{place} - {owner} {operation}" : $"{place} - {owner}";
    }

    /// <summary>The Roman numeral of a planet's number (1 is I); planets are numbered above zero.</summary>
    public static string Roman(int number)
    {
        if (number <= 0)
        {
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        (int Value, string Text)[] table = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        var text = new System.Text.StringBuilder();
        foreach (var (value, symbol) in table)
        {
            while (number >= value)
            {
                text.Append(symbol);
                number -= value;
            }
        }
        return text.ToString();
    }

    public static async Task<StationCatalog> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var stations = await sde.ReadAsync("npcStations", cancellationToken: cancellationToken);
        var systems = (await sde.ReadAsync("mapSolarSystems", cancellationToken: cancellationToken)).ToDictionary(
            r => (int)r.Key,
            r => (Name: SdeReader.English(r.Doc.GetProperty("name")), Constellation: r.Doc.GetProperty("constellationID").GetInt32(), Region: r.Doc.GetProperty("regionID").GetInt32(),
                Security: r.Doc.TryGetProperty("securityStatus", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0d));
        var constellations = (await sde.ReadAsync("mapConstellations", cancellationToken: cancellationToken)).ToDictionary(r => (int)r.Key, r => SdeReader.English(r.Doc.GetProperty("name")));
        var regions = (await sde.ReadAsync("mapRegions", cancellationToken: cancellationToken)).ToDictionary(r => (int)r.Key, r => SdeReader.English(r.Doc.GetProperty("name")));
        var factions = (await sde.ReadAsync("factions", cancellationToken: cancellationToken)).ToDictionary(r => r.Key, r => SdeReader.English(r.Doc.GetProperty("name")));
        var corporations = (await sde.ReadAsync("npcCorporations", stations.Select(s => s.Doc.GetProperty("ownerID").GetInt64()).Distinct(), cancellationToken))
            .ToDictionary(
                r => r.Key,
                r => (Name: SdeReader.English(r.Doc.GetProperty("name")), Faction: r.Doc.TryGetProperty("factionID", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt64() : (long?)null));
        var operations = (await sde.ReadAsync("stationOperations", cancellationToken: cancellationToken))
            .ToDictionary(r => (int)r.Key, r => r.Doc.TryGetProperty("operationName", out var n) ? SdeReader.English(n) : "");

        var list = new List<Station>(stations.Count);
        foreach (var row in stations)
        {
            var doc = row.Doc;
            var system = doc.GetProperty("solarSystemID").GetInt32();
            var owner = doc.GetProperty("ownerID").GetInt64();
            var operation = doc.GetProperty("operationID").GetInt32();
            if (!systems.TryGetValue(system, out var place) || !corporations.TryGetValue(owner, out var corporation)
                || !constellations.TryGetValue(place.Constellation, out var constellationName) || !regions.TryGetValue(place.Region, out var regionName))
            {
                continue;
            }
            var useOperation = doc.TryGetProperty("useOperationName", out var use) && use.ValueKind == JsonValueKind.True;
            int? moon = doc.TryGetProperty("orbitIndex", out var index) && index.ValueKind == JsonValueKind.Number ? index.GetInt32() : null;
            var celestial = doc.TryGetProperty("celestialIndex", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            list.Add(new Station(
                row.Key, doc.GetProperty("typeID").GetInt32(),
                NameOf(place.Name, celestial, moon, corporation.Name, useOperation ? operations.GetValueOrDefault(operation) : null),
                owner, corporation.Name, corporation.Faction, corporation.Faction is { } faction ? factions.GetValueOrDefault(faction, "") : "", operation,
                doc.TryGetProperty("orbitID", out var orbit) && orbit.ValueKind == JsonValueKind.Number ? orbit.GetInt64() : 0,
                system, place.Name, place.Constellation, constellationName, place.Region, regionName, place.Security,
                Number(doc, "reprocessingStationsTake"), (int)Number(doc, "reprocessingHangarFlag"),
                PositionAxis(doc, "x"), PositionAxis(doc, "y"), PositionAxis(doc, "z")));
        }
        return new StationCatalog(list);
    }

    private static double Number(JsonElement doc, string name) => doc.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0d;

    /// <summary>One coordinate of the station's <c>position</c> object in the static data (0 when the data has none).</summary>
    private static double PositionAxis(JsonElement doc, string axis) =>
        doc.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Object ? Number(position, axis) : 0d;
}
