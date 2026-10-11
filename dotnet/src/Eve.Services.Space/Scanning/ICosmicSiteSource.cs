namespace Eve.Services.Space.Scanning;

/// <summary>
/// The one place a site source plugs into the probe scan: the cosmic signatures of a system, as scan targets (the <see cref="ScanTargetSite.IsSignature"/> ones, which any probe can
/// find). <see cref="Sites.ContentCosmicSites"/> reads the sites the director keeps in the content runtime and is bound by the host once it is wired (ST-11, increment 2); until then <see cref="NoCosmicSites"/>, the source that has none, is the one in use.
/// </summary>
public interface ICosmicSiteSource
{
    Task<IReadOnlyList<ScanTargetSite>> SitesInAsync(int solarSystemId, CancellationToken cancellationToken = default);
}

/// <summary>The source that has no sites: the scan finds the existing objects only.</summary>
public sealed class NoCosmicSites : ICosmicSiteSource
{
    public static readonly NoCosmicSites Instance = new();

    public Task<IReadOnlyList<ScanTargetSite>> SitesInAsync(int solarSystemId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScanTargetSite>>([]);
}
