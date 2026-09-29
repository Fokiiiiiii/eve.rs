using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

/// <summary>What <c>machoNet</c> tells the client about the cluster.</summary>
public interface IMachoNetInfo
{
    /// <summary>The services the client may call, and where it routes each one.</summary>
    IReadOnlyList<KeyValuePair<string, string?>> Services();

    PyDict GlobalConfig();

    /// <summary>The number of clients connected.</summary>
    int UserCount();
}

/// <summary>
/// Where the client routes a service's calls: most are answered by any node (no location); these depend on
/// the client's station, solar system or character (the client reads the value from <c>GetInitVals</c>).
/// </summary>
public static class ServiceLocations
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal)
    {
        ["station"] = "station",
        ["ship"] = "station",
        ["invbroker"] = "station",
        ["trademgr"] = "station",
        ["tradeMgr"] = "station",
        ["repairSvc"] = "station",
        ["reprocessingSvc"] = "station",
        ["insuranceSvc"] = "station",
        ["LPSvc"] = "station",
        ["LPStoreMgr"] = "station",
        ["beyonce"] = "solarsystem2",
        ["eject"] = "solarsystem2",
        ["planetMgr"] = "solarsystem2",
        ["planetOrbitalRegistryBroker"] = "solarsystem2",
        ["posMgr"] = "solarsystem2",
        ["dogmaIM"] = "character",
    };

    public static string? Of(string service) => Table.GetValueOrDefault(service);
}

/// <summary>The client's first calls: the time, and the service table and configuration it routes and behaves by.</summary>
[Service("machoNet")]
public sealed partial class MachoNetService(IMachoNetInfo info, Func<long> nowUnixMs)
{
    private const string ServerStatusLabel = "/Carbon/MachoNet/ServerStatus/OK";

    private PyValue ServiceInfo() => new PyDict([.. info.Services().Select(entry =>
        new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Key), entry.Value is null ? PyNone.Instance : new PyStr(entry.Value)))]);

    /// <summary>The server time as a Windows FILETIME.</summary>
    [Method("GetTime")]
    public PyValue GetTime(ServiceCall call) => new PyLong(Py.FileTimeFromUnixMs(nowUnixMs()));

    /// <summary><c>(serviceInfo, globalConfig)</c>: the client caches both at start-up.</summary>
    [Method("GetInitVals")]
    public PyValue GetInitVals(ServiceCall call) => Py.Tuple(ServiceInfo(), info.GlobalConfig());

    [Method("GetServiceInfo")]
    public PyValue GetServiceInfo(ServiceCall call) => ServiceInfo();

    [Method("GetGlobalConfig")]
    public PyValue GetGlobalConfig(ServiceCall call) => info.GlobalConfig();

    /// <summary><c>(key, value)</c>, or <c>("", "")</c> for a key that is empty or not set.</summary>
    [Method("GetGlobalConfigValue")]
    public PyValue GetGlobalConfigValue(ServiceCall call)
    {
        var requested = JsSemantics.Truthy(call.Arg(0)) ? call.Arg(0) : call.Kwarg("key") ?? PyNone.Instance;
        var key = (JsSemantics.StringValue(requested) ?? "").Trim();
        var empty = Py.Tuple(new PyStr(""), new PyStr(""));
        if (key.Length == 0)
        {
            return empty;
        }
        foreach (var (name, value) in info.GlobalConfig().Entries)
        {
            if (JsSemantics.StringValue(name) == key)
            {
                return Py.Tuple(new PyStr(key), value);
            }
        }
        return empty;
    }

    [Method("GetServerStatus")]
    public PyValue GetServerStatus(ServiceCall call) => Py.Tuple(
        Py.Tuple(new PyStr(ServerStatusLabel), new PyDict([])),
        Py.Dict(
            ("boot_version", new PyReal(24.01)),
            ("boot_build", new PyInt(3_396_210)),
            ("boot_codename", new PyStr("eve.rs")),
            ("boot_region", new PyStr("ccp")),
            ("update_info", new PyStr("V24.01@ccp")),
            ("cluster_usercount", new PyInt(Math.Max(1, info.UserCount()))),
            ("user_logonqueueposition", new PyInt(1)),
            ("macho_version", new PyInt(496)),
            ("status", new PyStr("OK"))));

    [Method("GetNodeID")]
    public PyValue GetNodeId(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);
}
