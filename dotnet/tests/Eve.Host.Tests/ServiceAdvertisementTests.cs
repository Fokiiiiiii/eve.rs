using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

/// <summary>
/// What <c>machoNet.GetServiceInfo</c> tells the client: every name it advertises reaches a registered service (the client calls
/// an advertised name and gets None or an error from a name nobody serves), and the names the reference advertises that this
/// host leaves out are exactly the documented ones (DEVIATIONS.md, "Handshake, machoNet and the global config").
/// </summary>
public class ServiceAdvertisementTests
{
    private const long NowMs = 1_790_646_377_000;

    /// <summary>
    /// The 23 names the reference advertises that eve.rs does not register. Each is left out on purpose, with its reason in
    /// DEVIATIONS.md; the structure and planet services belong to the structures track.
    /// </summary>
    private static readonly string[] NotAdvertised =
    [
        "objectCaching", "FastCheckoutService", "corpStationMgr", "itemLocking", "structure", "loadService",
        "planetOrbitalRegistryBroker", "tradeMgr", "LSC", "warRegistry", "certificateMgr", "tutorialSvc", "nes_intro",
        "bookmarkMgr", "accessGroupBookmarkMgr", "ownerGroupManager", "standing2", "contractMgr", "blueprintManager",
        "repairSvc", "reprocessingSvc", "LPSvc", "publicGatewaySvc",
    ];

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => null;
    }

    private static (ServiceRegistry Registry, GlobalConfigStore Config, IService MachoNet) Host()
    {
        Func<long> now = () => NowMs;
        var registry = new ServiceRegistry();
        foreach (var service in CoreServices.Create(new SubscriptionSettings(), new NoAccounts(), now))
        {
            registry.Register(service);
        }
        var config = GlobalConfigStore.OpenAsync(GlobalConfig.Build(new ClusterOptions()), new InMemoryGlobalConfigPersistence())
            .GetAwaiter().GetResult();
        IService machoNet = new MachoNetService(new HostMachoNetInfo(registry, config.Current), now);
        registry.Register(machoNet);
        return (registry, config, machoNet);
    }

    [Fact]
    public void Every_advertised_name_reaches_a_registered_service()
    {
        var (registry, config, _) = Host();

        var advertised = new HostMachoNetInfo(registry, config.Current).Services();

        Assert.NotEmpty(advertised);
        foreach (var (name, _) in advertised)
        {
            Assert.True(registry.Lookup(name) is not null, $"{name} is advertised but no service answers to it");
        }
    }

    [Fact]
    public async Task The_wire_answer_lists_the_same_names_as_the_registry()
    {
        var (registry, config, machoNet) = Host();
        var advertised = new HostMachoNetInfo(registry, config.Current).Services().Select(entry => entry.Key).OrderBy(n => n, StringComparer.Ordinal);

        var answer = Assert.IsType<PyDict>(await machoNet.CallAsync(new ServiceCall
        {
            Method = "GetServiceInfo",
            Args = [],
            Session = new ClientSession(),
        }));

        var wire = answer.Entries.Select(entry => JsSemantics.StringValue(entry.Key)!).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(advertised, wire);
    }

    [Fact]
    public void The_names_left_out_are_the_documented_ones_and_no_service_answers_to_them()
    {
        var (registry, config, _) = Host();
        var advertised = new HostMachoNetInfo(registry, config.Current).Services().Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(23, NotAdvertised.Length);
        foreach (var name in NotAdvertised)
        {
            Assert.DoesNotContain(name, advertised);
            Assert.Null(registry.Lookup(name));
        }
    }

    [Fact]
    public void Each_advertised_name_with_a_route_is_routed_by_the_client_table()
    {
        var (registry, config, _) = Host();

        foreach (var (name, location) in new HostMachoNetInfo(registry, config.Current).Services())
        {
            Assert.Equal(ServiceLocations.Of(name), location);
        }
    }
}
