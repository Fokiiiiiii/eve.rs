namespace Eve.Services.Space.Scanning;

/// <summary>
/// The one place a site source plugs into the probe scan: the cosmic signatures and anomalies of a system, as scan targets (the
/// <see cref="ScanTargetSite.IsSignature"/> ones, which any probe can find). The site generator is a separate system and is not built; the server binds
/// <see cref="NoCosmicSites"/>, so no signature is found until a source is bound (TODO section 27).
/// </summary>
public interface ICosmicSiteSource
{
    IReadOnlyList<ScanTargetSite> SitesIn(int solarSystemId);
}

/// <summary>The source that has no sites: the scan finds the existing objects only.</summary>
public sealed class NoCosmicSites : ICosmicSiteSource
{
    public static readonly NoCosmicSites Instance = new();

    public IReadOnlyList<ScanTargetSite> SitesIn(int solarSystemId) => [];
}
