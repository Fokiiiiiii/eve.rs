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
        return Py.Dict(
            ("bugReporting_BugReportServer", new PyStr(options.BugReportUrl)),
            ("imageserverurl", new PyStr(options.ImageServerUrl)),
            ("defaultPortraitSaveSize", new PyInt(1024)),
            ("air_npe_enabled", Flag(options.AirNpeEnabled)),
            ("HyperNetKillSwitch", Flag(options.HyperNetKillSwitch)),
            ("HyperNetPlexPriceOverride", new PyInt(options.HyperNetPlexPriceOverride)),
            ("SkillPurchaseEnabled", Flag(options.SkillPurchaseEnabled)),
            ("expert_system_feature_enabled", Flag(options.ExpertSystemsEnabled)));
    }
}
