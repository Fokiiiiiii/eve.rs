namespace Eve.Services.Space.Scanning;

/// <summary>
/// The one place a site source plugs into the probe scan: the cosmic signatures of a system, as scan targets (the <see cref="ScanTargetSite.IsSignature"/> ones, which any probe can
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
