using System.Text.Json;
using Eve.Sde;

namespace Eve.Universe;

/// <summary>
/// A solar system of the static data: where it is in the universe. <c>FactionId</c> is the faction the system belongs to (its own, else its
/// constellation's, else its region's; 0 when none), as the simulation's world takes it.
/// </summary>
/// <summary>
/// The client's per-system flags (<see cref="ClientSystemFlags"/>): no cynosural field may be lit here, and no scanning (probes) is possible here.
/// </summary>
public sealed record SolarSystemGate(int TypeId, double X, double Y, double Z, double Radius);

public sealed record SolarSystemInfo(int Id, string Name, int ConstellationId, int RegionId, double Security, int FactionId = 0, double X = 0, double Y = 0, double Z = 0,
    bool DisallowCyno = false, bool DisallowScanning = false, double? GateX = null, double? GateY = null, double? GateZ = null,
    double GateRadius = 0, string? GateName = null)
{
    public string RegionName { get; init; } = "";
    public IReadOnlyList<SolarSystemGate> Gates { get; init; } = [];
}

/// <summary>The solar systems of the game, read from the static data.</summary>
public sealed class SolarSystemCatalog
{
    private readonly Dictionary<int, SolarSystemInfo> _systems;

    public SolarSystemCatalog(IEnumerable<SolarSystemInfo> systems) => _systems = systems.ToDictionary(s => s.Id);

    public int Count => _systems.Count;

    public SolarSystemInfo? Get(int id) => _systems.GetValueOrDefault(id);

    /// <summary>Every solar system of the static data.</summary>
    public IEnumerable<SolarSystemInfo> Systems => _systems.Values;

    /// <summary>The straight-line distance between two systems in light years (the jump drive's range and fuel are measured so), or null when one is unknown.</summary>
    public double? LightYearsBetween(int from, int to)
    {
        if (Get(from) is not { } a || Get(to) is not { } b)
        {
            return null;
        }
        var (dx, dy, dz) = (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) / MetersPerLightYear;
    }

    /// <summary>One light year in metres for jump range calculations (V24.01: 9,460,000,000,000,000 m).</summary>
    public const double MetersPerLightYear = 9_460_000_000_000_000.0;

    public static async Task<SolarSystemCatalog> LoadAsync(SdeReader sde, IReadOnlyDictionary<int, ClientSystemFlags>? clientFlags = null, CancellationToken cancellationToken = default)
    {
        var constellationFactions = await FactionsOfAsync(sde, "mapConstellations", cancellationToken);
        var regionRows = await sde.ReadAsync("mapRegions", cancellationToken: cancellationToken);
        var regionFactions = regionRows.Select(row => (Id: (int)row.Key, Faction: FactionOf(row.Doc)))
            .Where(row => row.Faction > 0).ToDictionary(row => row.Id, row => row.Faction);
        var regionNames = regionRows.ToDictionary(row => (int)row.Key, row => SdeReader.English(row.Doc.GetProperty("name")));
        var rows = await sde.ReadAsync("mapSolarSystems", cancellationToken: cancellationToken);
        var gates = (await sde.ReadAsync("mapStargates", cancellationToken: cancellationToken))
            .Where(row => row.Doc.TryGetProperty("solarSystemID", out var system) && system.ValueKind == JsonValueKind.Number)
            .GroupBy(row => row.Doc.GetProperty("solarSystemID").GetInt32())
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Key).ToArray());
        var gateTypeIds = gates.Values.SelectMany(rows => rows).Select(row => row.Doc.GetProperty("typeID").GetInt64()).Distinct().ToArray();
        var typeRadii = (await sde.ReadAsync("types", gateTypeIds, cancellationToken))
            .ToDictionary(row => row.Key, row => Position(row.Doc, "radius"));
        return new SolarSystemCatalog(rows.Select(r =>
        {
            var constellation = r.Doc.GetProperty("constellationID").GetInt32();
            var region = r.Doc.GetProperty("regionID").GetInt32();
            var faction = FactionOf(r.Doc) is > 0 and var own ? own
                : constellationFactions.GetValueOrDefault(constellation) is > 0 and var inherited ? inherited
                : regionFactions.GetValueOrDefault(region);
            var flags = clientFlags?.GetValueOrDefault((int)r.Key);
            var systemGates = gates.GetValueOrDefault((int)r.Key) ?? [];
            var gate = systemGates.FirstOrDefault()?.Doc;
            var gateRadius = 0d;
            if (gate is { } gateRow)
            {
                var typeRadius = gateRow.TryGetProperty("typeID", out var typeId) && typeId.ValueKind == JsonValueKind.Number
                    ? typeRadii.GetValueOrDefault(typeId.GetInt64()) : 0;
                gateRadius = typeRadius > 0 ? typeRadius : Position(gateRow, "radius");
            }
            var gatePositions = systemGates.Select(row =>
            {
                var doc = row.Doc;
                var typeId = doc.GetProperty("typeID").GetInt64();
                var radius = typeRadii.GetValueOrDefault(typeId);
                if (radius <= 0) radius = Position(doc, "radius");
                return new SolarSystemGate((int)typeId, Position(doc, "x"), Position(doc, "y"), Position(doc, "z"), radius);
            }).ToArray();
            return new SolarSystemInfo(
                (int)r.Key,
                SdeReader.English(r.Doc.GetProperty("name")),
                constellation,
                region,
                r.Doc.TryGetProperty("securityStatus", out var security) && security.ValueKind == JsonValueKind.Number ? security.GetDouble() : 0d,
                faction,
                Position(r.Doc, "x"), Position(r.Doc, "y"), Position(r.Doc, "z"),
                flags?.DisallowCyno ?? false, flags?.DisallowScanning ?? false,
                gate is null ? null : Position(gate.Value, "x"),
                gate is null ? null : Position(gate.Value, "y"),
                gate is null ? null : Position(gate.Value, "z"),
                gateRadius,
                null)
            {
                RegionName = regionNames.GetValueOrDefault(region) ?? "",
                Gates = gatePositions,
            };
        }));
    }

    /// <summary>A coordinate of a system: the <c>position</c> object of the static data, in metres (0 when the row has none).</summary>
    private static double Position(JsonElement doc, string axis) =>
        doc.TryGetProperty("position", out var position) && position.TryGetProperty(axis, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0d;

    private static int FactionOf(JsonElement doc) =>
        doc.TryGetProperty("factionID", out var faction) && faction.ValueKind == JsonValueKind.Number ? faction.GetInt32() : 0;

    private static async Task<Dictionary<int, int>> FactionsOfAsync(SdeReader sde, string table, CancellationToken cancellationToken)
    {
        var rows = await sde.ReadAsync(table, cancellationToken: cancellationToken);
        return rows.Select(r => (Id: (int)r.Key, Faction: FactionOf(r.Doc))).Where(r => r.Faction > 0).ToDictionary(r => r.Id, r => r.Faction);
    }
}
