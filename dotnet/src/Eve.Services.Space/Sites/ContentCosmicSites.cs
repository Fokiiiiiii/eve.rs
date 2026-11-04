using Eve.Services.Space.Scanning;
using Eve.Wormholes;

namespace Eve.Services.Space.Sites;

/// <summary>
/// The cosmic signatures of a system for the probe scan, read from the sites the director keeps. A signature is a scan target of the cosmic signature group (3) and the group 502, found by
/// archetype and faction, and the difficulty. The anomalies are not scan targets of the probes: the client has them from the system scanner's overlay (<c>scanMgr.GetFullState</c>).
/// </summary>
public sealed class ContentCosmicSites(CosmicSiteDirector director) : ICosmicSiteSource
{
    /// <summary>The scan group of the cosmic signatures (the client's <c>probeScanGroupSignatures</c>).</summary>
    public const int SignatureScanGroup = 3;

    public async Task<IReadOnlyList<ScanTargetSite>> SitesInAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        await director.EnsureAsync(solarSystemId, cancellationToken);
        var targets = new List<ScanTargetSite>();
        foreach (var site in await director.ActiveAsync(solarSystemId, cancellationToken))
        {
            if (site.IsAnomaly || director.Templates.ById(site.TemplateId) is not { } template)
            {
                continue;
            }
            targets.Add(new ScanTargetSite(
                site.InstanceId, CosmicSiteDirector.SignatureTypeId, CosmicSiteDirector.SignatureGroupId, SignatureScanGroup, site.Position,
                template.IsWormhole ? WormholeSites.ScanStrength : ProbeScanMath.SignatureStrength(template.Level), IsSignature: true, StrengthAttributeId: template.StrengthAttribute,
                Code: site.Code, DungeonId: template.DungeonId, DungeonNameId: template.DungeonNameId, ArchetypeId: template.ArchetypeId, FactionId: template.FactionId,
                Difficulty: template.Difficulty));
        }
        return targets;
    }
}
