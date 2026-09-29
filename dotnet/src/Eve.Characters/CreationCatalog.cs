using System.Text.Json;
using Eve.Sde;

namespace Eve.Characters;

public sealed record Race(int Id, int ShipTypeId, string ShipName);

public sealed record Bloodline(int Id, int RaceId);

/// <summary>A school: which race it takes and the corporation its pupils join.</summary>
public sealed record School(int Id, int RaceId, long CorporationId);

/// <summary>Where a new character of a corporation starts.</summary>
public sealed record StarterLocation(long CorporationId, long? FactionId, long StationId, int SolarSystemId, int ConstellationId, int RegionId);

/// <summary>
/// What character creation needs from the static data: the four playable races and the rookie ship each starts
/// in, the bloodlines, and, for each starting corporation, its headquarters station and where that is.
/// </summary>
public sealed class CreationCatalog
{
    /// <summary>
    /// The schools the client offers a new player, two per race: a game fact (the school's number and the corporation
    /// it belongs to), not part of the export.
    /// </summary>
    public static readonly IReadOnlyList<School> Schools =
    [
        new(31, 4, 1000166), new(32, 4, 1000167),
        new(33, 2, 1000044), new(34, 2, 1000045),
        new(35, 1, 1000006), new(36, 1, 1000007),
        new(37, 8, 1000094), new(38, 8, 1000095),
    ];

    /// <summary>The "character" item type the client shows for each bloodline (a legacy numbering it still expects).</summary>
    private static readonly Dictionary<int, int> CharacterTypes = new()
    {
        [1] = 1373, [2] = 1374, [3] = 1375, [4] = 1376, [5] = 1377, [6] = 1378, [7] = 1379, [8] = 1380,
        [11] = 1383, [12] = 1384, [13] = 1385, [14] = 1386,
    };

    private const int DefaultCharacterType = 1373;

    private readonly Dictionary<int, Race> _races;
    private readonly Dictionary<int, Bloodline> _bloodlines;
    private readonly Dictionary<long, StarterLocation> _starters;

    public CreationCatalog(IEnumerable<Race> races, IEnumerable<Bloodline> bloodlines, IEnumerable<StarterLocation> starters)
    {
        _races = races.ToDictionary(r => r.Id);
        _bloodlines = bloodlines.ToDictionary(b => b.Id);
        _starters = starters.ToDictionary(s => s.CorporationId);
    }

    public IReadOnlyCollection<Race> Races => _races.Values;

    public IReadOnlyCollection<Bloodline> Bloodlines => _bloodlines.Values;

    public Race? Race(int id) => _races.GetValueOrDefault(id);

    /// <summary>The name of a rookie ship type, or "" for any other type.</summary>
    public string ShipName(int typeId) => _races.Values.FirstOrDefault(r => r.ShipTypeId == typeId)?.ShipName ?? "";

    public Bloodline? Bloodline(int id) => _bloodlines.GetValueOrDefault(id);

    public StarterLocation? StarterFor(long corporationId) => _starters.GetValueOrDefault(corporationId);

    public static int CharacterTypeId(int bloodlineId) => CharacterTypes.GetValueOrDefault(bloodlineId, DefaultCharacterType);

    /// <summary>
    /// The school a new character joins: the one asked for if it takes that race, else the race's first. Null when the
    /// race has none (only the four playable races do).
    /// </summary>
    public static School? ResolveSchool(int requested, int raceId) =>
        Schools.FirstOrDefault(s => s.Id == requested && s.RaceId == raceId) ?? Schools.FirstOrDefault(s => s.RaceId == raceId);

    /// <summary>
    /// Reads the catalog from the static data. Every new character starts at <paramref name="startStationId"/> when it
    /// is given (as on the reference, which starts everyone at one station), and otherwise at the headquarters of its
    /// school's corporation.
    /// </summary>
    public static async Task<CreationCatalog> LoadAsync(SdeReader sde, long? startStationId = null, CancellationToken cancellationToken = default)
    {
        var races = new List<(int Id, int ShipType)>();
        foreach (var row in await sde.ReadAsync("races", cancellationToken: cancellationToken))
        {
            // Only the playable races have a rookie ship.
            if (row.Doc.TryGetProperty("shipTypeID", out var ship) && ship.ValueKind == JsonValueKind.Number)
            {
                races.Add(((int)row.Key, ship.GetInt32()));
            }
        }
        var shipNames = (await sde.ReadAsync("types", races.Select(r => (long)r.ShipType), cancellationToken))
            .ToDictionary(r => r.Key, r => SdeReader.English(r.Doc.GetProperty("name")));

        var bloodlines = (await sde.ReadAsync("bloodlines", cancellationToken: cancellationToken))
            .Select(row => new Bloodline((int)row.Key, row.Doc.GetProperty("raceID").GetInt32()))
            .ToList();

        var corporations = await sde.ReadAsync("npcCorporations", Schools.Select(s => s.CorporationId), cancellationToken);

        // Where each corporation's pupils start: the one station asked for, or the corporation's own.
        var stations = new Dictionary<long, long>();
        if (startStationId is { } chosen)
        {
            foreach (var corporation in corporations)
            {
                stations[corporation.Key] = chosen;
            }
        }
        else
        {
            foreach (var corporation in corporations.Where(c => c.Doc.TryGetProperty("stationID", out _)))
            {
                stations[corporation.Key] = corporation.Doc.GetProperty("stationID").GetInt64();
            }
        }
        var stationRows = (await sde.ReadAsync("npcStations", stations.Values.Distinct(), cancellationToken))
            .ToDictionary(r => r.Key, r => r.Doc.GetProperty("solarSystemID").GetInt32());
        var systems = (await sde.ReadAsync("mapSolarSystems", stationRows.Values.Distinct().Select(v => (long)v), cancellationToken))
            .ToDictionary(r => (int)r.Key, r => (Constellation: r.Doc.GetProperty("constellationID").GetInt32(), Region: r.Doc.GetProperty("regionID").GetInt32()));

        var starters = new List<StarterLocation>();
        foreach (var row in corporations)
        {
            if (!stations.TryGetValue(row.Key, out var station) || !stationRows.TryGetValue(station, out var systemId) || !systems.TryGetValue(systemId, out var place))
            {
                continue;
            }
            long? faction = row.Doc.TryGetProperty("factionID", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt64() : null;
            starters.Add(new StarterLocation(row.Key, faction, station, systemId, place.Constellation, place.Region));
        }
        return new CreationCatalog(races.Select(r => new Race(r.Id, r.ShipType, shipNames.GetValueOrDefault(r.ShipType, ""))), bloodlines, starters);
    }
}
