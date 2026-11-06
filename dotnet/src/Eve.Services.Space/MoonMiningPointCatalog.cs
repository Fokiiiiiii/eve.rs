using System.Security.Cryptography;
using System.Text.Json;
using Eve.Space;

namespace Eve.Services.Space;

public static class MoonMiningPointCatalog
{
    private const string DatasetVersion = "evejs-moon-mining-points-3396210-v1";
    private const string SourceSha256 = "cfd0da86c148980dd140462526e373128d72d3bc5860cb99b2f549db6e2d536b";
    private const string ContentHash = "6e8821d54c191e0a9e9324e95d2eae17130a97ac36fe744c95d0a1deecf6aebd";
    private const long BeaconIdBase = 1_000_000_000;
    private sealed record MoonMiningPoint(WarpCoordinates Coordinates, double DistanceFromMoon);
    private static readonly Lazy<IReadOnlyDictionary<long, MoonMiningPoint>> Points = new(Load);

    public static WarpCoordinates? Resolve(long moonId, int solarSystemId)
    {
        if (moonId <= 0 || solarSystemId <= 0 || !Points.Value.TryGetValue(moonId, out var point) || point.Coordinates.SolarSystemId != solarSystemId)
            return null;
        return point.Coordinates;
    }

    public static bool IsAtMoonMiningPoint(long moonId, int solarSystemId, double moonX, double moonY, double moonZ)
    {
        if (!Points.Value.TryGetValue(moonId, out var point) || point.Coordinates.SolarSystemId != solarSystemId
            || !double.IsFinite(moonX) || !double.IsFinite(moonY) || !double.IsFinite(moonZ))
            return false;
        var dx = point.Coordinates.X - moonX;
        var dy = point.Coordinates.Y - moonY;
        var dz = point.Coordinates.Z - moonZ;
        var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return double.IsFinite(distance) && Math.Abs(distance - point.DistanceFromMoon) <= 1d;
    }

    private static IReadOnlyDictionary<long, MoonMiningPoint> Load()
    {
        using var stream = typeof(MoonMiningPointCatalog).Assembly.GetManifestResourceStream("moonMiningPoints.json")
            ?? throw new InvalidDataException("the moon mining point dataset is missing");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || root.GetProperty("datasetVersion").GetString() != DatasetVersion
            || root.GetProperty("sourceBuild").GetInt32() != 3396210
            || root.GetProperty("source").GetProperty("sha256").GetString() != SourceSha256
            || root.GetProperty("contentHash").GetString() != ContentHash)
            throw new InvalidDataException("the moon mining point dataset does not match the pinned V24 data");

        var count = root.GetProperty("count").GetInt32();
        if (count <= 0)
            throw new InvalidDataException("the moon mining point dataset is empty");
        var points = new Dictionary<long, MoonMiningPoint>(count);
        var rows = new List<(long MoonId, int SolarSystemId, long BeaconId, double X, double Y, double Z, double DistanceFromMoon)>(count);
        foreach (var row in root.GetProperty("points").EnumerateArray())
        {
            var moonId = row.GetProperty("moonID").GetInt64();
            var solarSystemId = row.GetProperty("solarSystemID").GetInt32();
            var beaconId = row.GetProperty("beaconID").GetInt64();
            var position = row.GetProperty("position");
            var x = position.GetProperty("x").GetDouble();
            var y = position.GetProperty("y").GetDouble();
            var z = position.GetProperty("z").GetDouble();
            var distanceFromMoon = row.GetProperty("distanceFromMoon").GetDouble();
            if (moonId <= 0 || moonId > long.MaxValue - BeaconIdBase || solarSystemId <= 0
                || beaconId != BeaconIdBase + moonId
                || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)
                || !double.IsFinite(distanceFromMoon) || distanceFromMoon <= 0
                || !points.TryAdd(moonId, new MoonMiningPoint(new WarpCoordinates(solarSystemId, x, y, z), distanceFromMoon)))
                throw new InvalidDataException($"the moon mining point dataset has an invalid or duplicate point for moon {moonId}");
            rows.Add((moonId, solarSystemId, beaconId, x, y, z, distanceFromMoon));
        }

        if (points.Count != count)
            throw new InvalidDataException("the moon mining point dataset count does not match its rows");

        rows.Sort((left, right) => left.MoonId.CompareTo(right.MoonId));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in rows)
        {
            var encoded = JsonSerializer.SerializeToUtf8Bytes(new object?[]
            {
                row.MoonId, row.SolarSystemId, row.BeaconId, JsonNumber(row.X), JsonNumber(row.Y), JsonNumber(row.Z), JsonNumber(row.DistanceFromMoon),
            });
            hash.AppendData(encoded);
            hash.AppendData("\n"u8);
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("the moon mining point dataset content hash does not match its points");
        return points;
    }

    private static double JsonNumber(double value) => value == 0 ? 0d : value;
}
