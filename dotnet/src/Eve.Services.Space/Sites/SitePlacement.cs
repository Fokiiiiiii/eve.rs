using Eve.Services.Space.Scanning;
using Eve.Sde;

namespace Eve.Services.Space.Sites;

/// <summary>A celestial a site can be placed beside: its item id and where it is in the system.</summary>
public sealed record SiteAnchor(long Id, double[] Position)
{
    public static SiteAnchor? FromSdeRow(SdeRow row)
    {
        if (!row.Doc.TryGetProperty("position", out var position) || position.ValueKind != System.Text.Json.JsonValueKind.Object
            || !position.TryGetProperty("x", out var x) || !position.TryGetProperty("y", out var y) || !position.TryGetProperty("z", out var z))
        {
            return null;
        }
        return new SiteAnchor(row.Key, [x.GetDouble(), y.GetDouble(), z.GetDouble()]);
    }
}

/// <summary>
/// the seed; the site lies in a direction the seed gives, at a distance of at least half an astronomical unit and at most four from it (the reference places it at 4 AU, give or take
/// shifts the site vertically by up to 0.14 AU, which can leave the sphere.
/// </summary>
public static class SitePlacement
{
    public const double MinimumAu = 0.5;
    public const double MaximumAu = 4.0;
    private const double VerticalJitter = 0.14;

    public static (SiteAnchor Anchor, double[] Position) Place(IReadOnlyList<SiteAnchor> anchors, string seed)
    {
        var anchor = anchors.Count == 0
            ? new SiteAnchor(0, [0, 0, 0])
            : anchors[(int)(SiteSeed.Hash(seed + ":anchor") % (uint)anchors.Count)];
        var yaw = SiteSeed.Fraction(seed + ":yaw") * 2 * Math.PI;
        var pitch = (SiteSeed.Fraction(seed + ":pitch") * 2 - 1) * VerticalJitter;
        var distance = (MinimumAu + SiteSeed.Fraction(seed + ":distance") * (MaximumAu - MinimumAu)) * ProbeScanMath.AuMeters;
        double[] direction = [Math.Cos(pitch) * Math.Cos(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Sin(yaw)];
        return (anchor, [anchor.Position[0] + direction[0] * distance, anchor.Position[1] + direction[1] * distance, anchor.Position[2] + direction[2] * distance]);
    }
}
