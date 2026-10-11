using Eve.Services.Space.Scanning;

namespace Eve.Services.Space.Sites;

/// <summary>A celestial a site can be placed beside: its item id and where it is in the system.</summary>
public sealed record SiteAnchor(long Id, double[] Position);

/// <summary>
/// Where a site is. The wiki ("Probe scanning", where to find sites): sites spawn in a sphere of 4 AU around celestials. The anchor is one of the system's celestials, picked by
/// the seed; the site lies in a direction the seed gives, at a distance of at least half an astronomical unit and at most four from it (the reference places it at 4 AU, give or take
/// 0.35 AU, which goes past the wiki's sphere: EveJS dungeonUniverseRuntime.js:961, DEVIATIONS "Cosmic sites"). The pitch is within 0.14 rad, where the reference
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
