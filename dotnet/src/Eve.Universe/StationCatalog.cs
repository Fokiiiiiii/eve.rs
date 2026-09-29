using System.Text.Json;
using Eve.Sde;

namespace Eve.Universe;

/// <summary>A station of the static data: what it is called and where it is.</summary>
public sealed record Station(
    long Id, int TypeId, string Name, long OwnerId, int OperationId, int SolarSystemId, int ConstellationId, int RegionId, double Security);

/// <summary>The stations of the game, with the names the client shows, read from the static data.</summary>
public sealed class StationCatalog
{
    private readonly Dictionary<long, Station> _stations;

    public StationCatalog(IEnumerable<Station> stations) => _stations = stations.ToDictionary(s => s.Id);

    public int Count => _stations.Count;

    public Station? Get(long id) => _stations.GetValueOrDefault(id);

    public IEnumerable<Station> All => _stations.Values;

    /// <summary>
    /// A station is named <c>System Roman[ - Moon N] - Owner[ Operation]</c>: the system and the number of the planet it is at
    /// (or that its moon orbits), the moon if it is at one, and its owner with the kind of station it is (for most stations).
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
        var corporations = (await sde.ReadAsync("npcCorporations", stations.Select(s => s.Doc.GetProperty("ownerID").GetInt64()).Distinct(), cancellationToken))
            .ToDictionary(r => r.Key, r => SdeReader.English(r.Doc.GetProperty("name")));
        var operations = (await sde.ReadAsync("stationOperations", cancellationToken: cancellationToken))
            .ToDictionary(r => (int)r.Key, r => r.Doc.TryGetProperty("operationName", out var n) ? SdeReader.English(n) : "");

        var list = new List<Station>(stations.Count);
        foreach (var row in stations)
        {
            var doc = row.Doc;
            var system = doc.GetProperty("solarSystemID").GetInt32();
            var owner = doc.GetProperty("ownerID").GetInt64();
            var operation = doc.GetProperty("operationID").GetInt32();
            if (!systems.TryGetValue(system, out var place) || !corporations.TryGetValue(owner, out var ownerName))
            {
                continue; // a station in a system or of a corporation the data does not have cannot be named
            }
            var useOperation = doc.TryGetProperty("useOperationName", out var use) && use.ValueKind == JsonValueKind.True;
            int? moon = doc.TryGetProperty("orbitIndex", out var index) && index.ValueKind == JsonValueKind.Number ? index.GetInt32() : null;
            var celestial = doc.TryGetProperty("celestialIndex", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            list.Add(new Station(
                row.Key, doc.GetProperty("typeID").GetInt32(),
                NameOf(place.Name, celestial, moon, ownerName, useOperation ? operations.GetValueOrDefault(operation) : null),
                owner, operation, system, place.Constellation, place.Region, place.Security));
        }
        return new StationCatalog(list);
    }
}
