namespace Eve.Services.Core;

/// <summary>The services ported so far, wired to their dependencies.</summary>
public static class CoreServices
{
    /// <summary>Creates every core service. Register them with <see cref="ServiceRegistry.Register"/>.</summary>
    public static IReadOnlyList<IService> Create(SubscriptionSettings settings, IOmegaAccountStore accounts, Func<long> nowUnixMs)
    {
        var licenses = new OmegaLicenses(settings, accounts, nowUnixMs);
        return
        [
            new PingService(),
            new BrowserLockdownService(),
            new SubscriptionMgrService(settings, licenses),
        ];
    }

    /// <summary>The names of the services <see cref="Create"/> returns.</summary>
    public static IReadOnlyList<string> Names => Generated.Eve_Services_CoreServiceCatalog.Names;
}
