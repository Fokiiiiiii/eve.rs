using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

/// <summary>The settings that reach the client as its global configuration (<c>machoNet.GetGlobalConfig</c>).</summary>
public sealed record ClusterOptions
{
    /// <summary>Where the client fetches portraits and logos from.</summary>
    public string ImageServerUrl { get; init; } = "http://127.0.0.1:26001/";

    /// <summary>Bug reports go to this address; it stays local so nothing leaves the machine.</summary>
    public string BugReportUrl { get; init; } = "http://127.0.0.1:26004/api";

    public bool NewCharacterExperienceEnabled { get; init; }

    public string NewCharacterExperienceMode { get; init; } = "disabled";

    public bool AirNpeEnabled =>
        NewCharacterExperienceEnabled || (NewCharacterExperienceMode is "tutorial" or "cinematic");

    public bool HyperNetKillSwitch { get; init; }

    public long HyperNetPlexPriceOverride { get; init; } = 3_500_000;

    public bool SkillPurchaseEnabled { get; init; } = true;

    public bool ExpertSystemsEnabled { get; init; } = true;
}

/// <summary>Builds the global-config dictionary the client is handed at login and by <c>machoNet</c>.</summary>
public static class GlobalConfig
{
    public static PyDict Build(ClusterOptions options)
    {
        static PyValue Flag(bool on) => new PyInt(on ? 1 : 0);
        static PyValue StoreFlag(bool on) => new PyStr(on ? "1" : "0");
        var store = Eve.Services.Core.NewEdenStoreCatalog.Default;
        return Py.Dict(
            ("bugReporting_BugReportServer", new PyStr(options.BugReportUrl)),
            ("imageserverurl", new PyStr(options.ImageServerUrl)),
            ("defaultPortraitSaveSize", new PyInt(1024)),
            ("air_npe_enabled", Flag(options.AirNpeEnabled)),
            ("HyperNetKillSwitch", Flag(options.HyperNetKillSwitch)),
            ("HyperNetPlexPriceOverride", new PyInt(options.HyperNetPlexPriceOverride)),
            ("SkillPurchaseEnabled", Flag(options.SkillPurchaseEnabled)),
            ("expert_system_feature_enabled", Flag(options.ExpertSystemsEnabled)),
            // The New Eden Store's client settings (EVE.js storeState.js:503-527): the in-client PLEX checkout is on, and the store
            // is served in-client (fake_vgs_enabled). Each value is read by eve-logic globalConfig/getFunctions.py:233-258.
            ("fake_fast_checkout_enabled", StoreFlag(store.FastCheckoutEnabled)),
            ("fake_fast_checkout_response", new PyStr(store.FakeFastCheckoutResponse)),
            ("fake_fast_checkout_plex_offers", new PyStr(store.FastCheckoutPlexOffers)),
            ("fake_vgs_enabled", StoreFlag(store.FakeVgsEnabled)),
            ("fake_china_funnel_enabled", StoreFlag(store.ChinaFunnelEnabled)),
            ("fake_buy_plex_offer_url", new PyStr(store.FakeBuyPlexOfferUrl)),
            ("use_shell_execute_to_buy_plex_offer", StoreFlag(store.UseShellExecuteToBuyPlexOffer)));
    }
}
