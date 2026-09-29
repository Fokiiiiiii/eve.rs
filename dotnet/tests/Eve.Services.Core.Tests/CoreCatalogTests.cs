
namespace Eve.Services.Core.Tests;

/// <summary>The catalog of the core services.</summary>
public class CoreCatalogTests
{
    [Fact]
    public void The_catalog_names_the_core_services()
    {
        Assert.Equal(["browserLockdownSvc", "pingService", "subscriptionMgr"], CoreServices.Names.Order());
        var created = CoreServices.Create(new SubscriptionSettings(), new NoAccounts(), () => 0);
        Assert.Equal(CoreServices.Names.Order(), created.Select(s => s.Name).Order());
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => null;
    }
}
