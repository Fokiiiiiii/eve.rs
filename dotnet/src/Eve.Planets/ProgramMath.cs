namespace Eve.Planets;

/// <summary>
/// The value a planet's surface holds for one resource, at a point (theta, phi). CCP's value is the native
/// <c>_eveplanetresources</c> harmonic, which is not in the client snapshot (deviation D1 in PI_DESIGN.md). This field is a
/// deterministic stand-in: a fixed function of the planet id and the resource id, bounded by the client's
/// <c>planetResourceMaxValue</c> (1.21, appConst.py:1589).
/// </summary>
public sealed class DeterministicResourceField : IResourceField
{
    public double ValueAt(long planetId, int resourceTypeId, double theta, double phi)
    {
        var (a, b, c, d) = Phases(planetId, resourceTypeId);
        var value = 0.6 + 0.35 * Math.Sin(3 * theta + a) * Math.Cos(2 * phi + b) + 0.25 * Math.Cos(5 * theta + c) * Math.Sin(4 * phi + d);
        return Math.Clamp(value, 0, PlanetRules.MaxResourceValue);
    }

    private static (double A, double B, double C, double D) Phases(long planetId, int resourceTypeId)
    {
        var h = Mix((ulong)planetId * 1_000_003UL ^ (ulong)resourceTypeId);
        double Next() { h = Mix(h + 0x9E3779B97F4A7C15UL); return (h >> 11) * (2 * Math.PI / (1UL << 53)); }
        return (Next(), Next(), Next(), Next());
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

public interface IResourceField
{
    double ValueAt(long planetId, int resourceTypeId, double theta, double phi);
}

/// <summary>An extractor head: its id and its place (latitude, longitude in radians).</summary>
public readonly record struct Head(int Id, double Latitude, double Longitude);

/// <summary>
/// CreateProgram (baseColony.py:897-984): the amount a program distributes per cycle, its cycle time and its number of cycles,
/// from the heads' values and their overlaps with the heads of this and the other programs of the same resource.
/// Distances are on the unit sphere (radians), as the client's SurfacePoint default radius is 1.
/// </summary>
public static class ProgramMath
{
    public readonly record struct Result(long MaxValue, long CycleTime, int NumCycles);

    public static Result Create(long planetId, int resourceTypeId, IReadOnlyList<Head> heads, double headRadius, double overlapFactor,
        double maxVolume, IResourceField field, IEnumerable<(double Radius, IReadOnlyList<Head> Heads)> otherPrograms)
    {
        var points = heads.Select(h => (h.Id, Sp: new SurfacePoint(1.0, h.Longitude, h.Latitude))).ToList();
        var value = new Dictionary<int, double>();
        var modifier = new Dictionary<int, double>();
        foreach (var (index, sp) in points)
        {
            value[index] = Math.Max(field.ValueAt(planetId, resourceTypeId, 2.0 * Math.PI - sp.Theta, sp.Phi), 0);
            modifier[index] = 1.0;
        }

        // overlaps among this program's own heads
        for (var i = 0; i < points.Count; i++)
        for (var j = i + 1; j < points.Count; j++)
        {
            var dist = points[i].Sp.DistanceTo(points[j].Sp);
            if (dist >= headRadius * 2) continue;
            var r2 = headRadius * headRadius;
            var overlap = (2 * r2 * Math.Acos(0.5 * (dist / headRadius)) - 0.5 * dist * Math.Sqrt(4 * r2 - dist * dist)) / (Math.PI * r2);
            var m = Math.Min(1, Math.Max(0, 1 - overlap * overlapFactor));
            value[points[i].Id] *= m;
            value[points[j].Id] *= m;
        }

        // overlaps with the heads of the other programs of the same resource
        var headArea = Math.PI * headRadius * headRadius;
        foreach (var (index, sp1) in points)
        {
            var mod = 1.0;
            foreach (var (otherRadius, otherHeads) in otherPrograms)
            {
                if (mod == 0) break;
                foreach (var other in otherHeads)
                {
                    var sp2 = new SurfacePoint(1.0, other.Longitude, other.Latitude);
                    var d = sp1.DistanceTo(sp2);
                    var R = headRadius;
                    var r = otherRadius;
                    d = d == 0.0 ? 0.0001 : d;
                    R = R == 0.0 ? PlanetRules.RadiusMax : R;
                    r = r == 0.0 ? PlanetRules.RadiusMax : r;
                    if (d > R + r) continue;
                    var r2 = r * r; var R2 = R * R; var d2 = d * d;
                    var alpha = (d2 + r2 - R2) / (2 * d * r);
                    double ov;
                    if (alpha < -1) ov = Math.PI * r2 / headArea;
                    else if (alpha > 1) ov = 1;
                    else
                    {
                        var f1 = r2 * Math.Acos(alpha);
                        var f2 = R2 * Math.Acos((d2 + R2 - r2) / (2 * d * R));
                        var f3 = 0.5 * Math.Sqrt((-d + r + R) * (d + r - R) * (d - r + R) * (d + r + R));
                        ov = (f1 + f2 - f3) / headArea;
                    }
                    mod *= Math.Min(1, Math.Max(0, 1 - ov * 2 * overlapFactor));
                }
            }
            value[index] *= mod;
        }

        var total = value.Values.Sum();
        var maxValue = (long)(maxVolume * total);
        var programLength = PlanetRules.ProgramLength(headRadius);
        var cycleHours = PlanetRules.CycleHours(programLength);
        var numCycles = (int)(programLength / cycleHours);
        return new Result(maxValue, (long)(cycleHours * PlanetRules.Hour), numCycles);
    }
}
