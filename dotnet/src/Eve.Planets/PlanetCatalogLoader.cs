using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Eve.Planets;

/// <summary>A planet body of the static data: its system, type, radius, number and place.</summary>
public sealed record PlanetBody(long PlanetId, int TypeId, long SolarSystemId, double Radius, int CelestialIndex, double X, double Y, double Z);

/// <summary>
/// Reads the planetary static data from the SDE tables of the server database (<c>sde</c>): the pins, links and commodity types,
/// their dogma attributes (missing ones take the attribute default, as the client's <c>GetTypeAttribute</c> does), the
/// schematics, and the planet bodies.
/// </summary>
public static class PlanetCatalogLoader
{
    /// <summary>The pin groups, the link group, and the planetary resource and commodity categories (42, 43).</summary>
    private static readonly string[] Groups = ["1027", "1063", "1028", "1029", "1030", "1036"];
    private static readonly string[] Categories = ["42", "43"];

    public static async Task<PlanetCatalog> LoadAsync(NpgsqlDataSource source, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);

        var types = new Dictionary<int, (int Group, double Volume, long Price, double? Capacity)>();
        await using (var command = new NpgsqlCommand("""
            SELECT t.key, t.doc::text FROM sde.types t JOIN sde.groups g ON g.key = t.doc->>'groupID'
            WHERE g.key = ANY($1) OR g.doc->>'categoryID' = ANY($2)
            """, connection)
        { Parameters = { new() { Value = Groups }, new() { Value = Categories } } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var doc = JsonDocument.Parse(reader.GetString(1)).RootElement;
                var id = int.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
                types[id] = (int.Parse(doc.GetProperty("groupID").GetRawText(), CultureInfo.InvariantCulture), Number(doc, "volume") ?? 0,
                    (long)(Number(doc, "basePrice") ?? 0), Number(doc, "capacity"));
            }

        var keys = types.Keys.Select(k => k.ToString(CultureInfo.InvariantCulture)).ToArray();
        var attributes = new Dictionary<int, Dictionary<int, double>>();
        await using (var command = new NpgsqlCommand("SELECT key, doc::text FROM sde.\"typeDogma\" WHERE key = ANY($1)", connection) { Parameters = { new() { Value = keys } } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new Dictionary<int, double>();
                foreach (var attr in JsonDocument.Parse(reader.GetString(1)).RootElement.GetProperty("dogmaAttributes").EnumerateArray())
                    values[attr.GetProperty("attributeID").GetInt32()] = attr.GetProperty("value").GetDouble();
                attributes[int.Parse(reader.GetString(0), CultureInfo.InvariantCulture)] = values;
            }

        var defaults = new Dictionary<int, double>();
        var wanted = PlanetAttributes.All.Select(a => a.ToString(CultureInfo.InvariantCulture)).ToArray();
        await using (var command = new NpgsqlCommand("SELECT key, doc::text FROM sde.\"dogmaAttributes\" WHERE key = ANY($1)", connection) { Parameters = { new() { Value = wanted } } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                defaults[int.Parse(reader.GetString(0), CultureInfo.InvariantCulture)] = Number(JsonDocument.Parse(reader.GetString(1)).RootElement, "defaultValue") ?? 0;

        var typeInfos = new Dictionary<int, TypeInfo>();
        foreach (var (id, t) in types)
        {
            var values = attributes.GetValueOrDefault(id) ?? new Dictionary<int, double>();
            foreach (var attr in PlanetAttributes.All)
                if (!values.ContainsKey(attr) && defaults.TryGetValue(attr, out var fallback)) values[attr] = fallback;
            typeInfos[id] = new TypeInfo(id, t.Group, t.Volume, t.Price, t.Capacity, values);
        }

        var schematics = new Dictionary<int, Schematic>();
        await using (var command = new NpgsqlCommand("SELECT doc::text FROM sde.\"planetSchematics\"", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var doc = JsonDocument.Parse(reader.GetString(0)).RootElement;
                var inputs = new Dictionary<int, int>();
                var outputs = new Dictionary<int, int>();
                foreach (var t in doc.GetProperty("types").EnumerateArray())
                {
                    var target = t.GetProperty("isInput").GetBoolean() ? inputs : outputs;
                    target[t.GetProperty("_key").GetInt32()] = t.GetProperty("quantity").GetInt32();
                }
                var pins = doc.GetProperty("pins").EnumerateArray().Select(p => p.GetInt32()).ToHashSet();
                var id = doc.GetProperty("_key").GetInt32();
                schematics[id] = new Schematic(id, doc.GetProperty("cycleTime").GetDouble(), inputs, outputs, pins);
            }
        return new PlanetCatalog(typeInfos, schematics);
    }

    /// <summary>A planet body of the static data (mapPlanets); the system is the star's when the planet does not carry it.</summary>
    public static async Task<PlanetBody?> PlanetAsync(NpgsqlDataSource source, long planetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT doc::text FROM sde.\"mapPlanets\" WHERE key = $1", connection)
        {
            Parameters = { new() { Value = planetId.ToString(CultureInfo.InvariantCulture) } },
        };
        if (await command.ExecuteScalarAsync(cancellationToken) is not string text) return null;
        var doc = JsonDocument.Parse(text).RootElement;
        var orbit = doc.GetProperty("orbitID").GetInt64();
        var system = await SolarSystemOfStarAsync(connection, orbit, cancellationToken);
        var position = doc.GetProperty("position");
        return new PlanetBody(planetId, doc.GetProperty("typeID").GetInt32(), system, doc.GetProperty("radius").GetDouble(),
            doc.GetProperty("celestialIndex").GetInt32(), position.GetProperty("x").GetDouble(), position.GetProperty("y").GetDouble(), position.GetProperty("z").GetDouble());
    }

    /// <summary>The security status of a solar system (sde.mapSolarSystems, securityStatus).</summary>
    public static async Task<double> SecurityAsync(NpgsqlDataSource source, long solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT doc::text FROM sde.\"mapSolarSystems\" WHERE key = $1", connection)
        {
            Parameters = { new() { Value = solarSystemId.ToString(CultureInfo.InvariantCulture) } },
        };
        var text = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidDataException($"solar system {solarSystemId} is not in the static data");
        return JsonDocument.Parse(text).RootElement.GetProperty("securityStatus").GetDouble();
    }

    private static async Task<long> SolarSystemOfStarAsync(NpgsqlConnection connection, long orbitId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT doc::text FROM sde.\"mapStars\" WHERE key = $1", connection)
        {
            Parameters = { new() { Value = orbitId.ToString(CultureInfo.InvariantCulture) } },
        };
        var text = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidDataException($"planet orbit {orbitId} is not a star of the static data");
        return JsonDocument.Parse(text).RootElement.GetProperty("solarSystemID").GetInt64();
    }

    private static double? Number(JsonElement doc, string name)
    {
        if (!doc.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? double.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetDouble();
    }
}

/// <summary>The dogma attributes the planetary rules read (for their defaults and values).</summary>
public static class PlanetAttributes
{
    public static readonly int[] All =
    [
        PlanetAttr.Capacity, PlanetAttr.PowerOutput, PlanetAttr.PowerLoad, PlanetAttr.CpuOutput, PlanetAttr.CpuLoad, PlanetAttr.PinCycleTime,
        PlanetAttr.PinExtractionQuantity, PlanetAttr.PlanetRestriction, PlanetAttr.LogisticalCapacity, PlanetAttr.PowerLoadPerKm, PlanetAttr.CpuLoadPerKm,
        PlanetAttr.CpuLoadLevelModifier, PlanetAttr.PowerLoadLevelModifier, PlanetAttr.EcuMaxVolume, PlanetAttr.EcuDecayFactor, PlanetAttr.EcuOverlapFactor,
        PlanetAttr.ImportTax, PlanetAttr.ExportTax, PlanetAttr.ImportTaxMultiplier,
        PlanetAttr.EcuNoiseFactor, PlanetAttr.EcuAreaOfInfluence, PlanetAttr.ExtractorHeadCpu, PlanetAttr.ExtractorHeadPower,
    ];
}
