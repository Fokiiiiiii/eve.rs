using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host;

public sealed record MachoNetGlobalConfigUpdate(
    string Key,
    PyValue Value,
    bool IsDelete,
    bool IsUpdate,
    bool IsUpdateClients);

public delegate ValueTask MachoNetGlobalConfigUpdateCallback(
    MachoNetGlobalConfigUpdate update,
    ClientSession session,
    CancellationToken cancellationToken);

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
public sealed partial class MachoNetService(
    IMachoNetInfo info,
    Func<long> nowUnixMs,
    MachoNetGlobalConfigUpdateCallback? setGlobalConfigValue = null)
{
    private const string ServerStatusLabel = "/Carbon/MachoNet/ServerStatus/OK";
    private const long RoleAdmin = 1L << 56;   // ROLE_ADMIN (carbon/common/script/sys/serviceConst.py)
    private const long RoleService = 1L << 60; // ROLE_SERVICE

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

    /// <summary>
    /// Sets (or, with <c>isDelete</c> or a None value, removes) a global-config key. The reference allows this call to
    /// ROLE_ADMIN and ROLE_SERVICE only (<c>machoNet.py</c>, the exported calls). It is saved and answered with None; a write
    /// that cannot be kept is refused, never answered as done.
    /// </summary>
    [Method("SetGlobalConfigValue")]
    public async ValueTask<PyValue> SetGlobalConfigValue(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var roles = (call.Session.GetInt("accountRole") ?? 0) | (call.Session.GetInt("role") ?? 0);
        if ((roles & (RoleAdmin | RoleService)) == 0)
        {
            throw new MachoWrappedException(MachoErrors.Wrapped("exceptions.RuntimeError", [
                new PyStr("RoleNotAssigned"),
                new PyStr("machoNet.SetGlobalConfigValue requires ROLE_ADMIN or ROLE_SERVICE"),
            ]));
        }
        var key = (JsSemantics.StringValue(Argument(call, "key", 0)) ?? "").Trim();
        if (key.Length == 0)
            return PyNone.Instance;

        if (setGlobalConfigValue is not { } callback)
        {
            throw new MachoWrappedException(MachoErrors.Wrapped("exceptions.RuntimeError", [
                new PyStr("GlobalConfigNotWritable"),
                new PyStr("this machoNet has no global-config store to keep the value in"),
            ]));
        }
        await callback(new MachoNetGlobalConfigUpdate(
                key,
                Argument(call, "value", 1) ?? PyNone.Instance,
                BooleanArgument(call, "isDelete", 2, false),
                BooleanArgument(call, "isUpdate", 3, true),
                BooleanArgument(call, "isUpdateClients", 4, true)),
            call.Session,
            call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetServerStatus")]
    public PyValue GetServerStatus(ServiceCall call) => Py.Tuple(
        Py.Tuple(new PyStr(ServerStatusLabel), new PyDict([])),
        Py.Dict(
            ("boot_version", new PyReal(ServerBuildInfo.BootVersion)),
            ("boot_build", new PyInt(ServerBuildInfo.BootBuild)),
            ("boot_codename", new PyStr("eve.rs")),
            ("boot_region", new PyStr("ccp")),
            ("update_info", new PyStr("V24.01@ccp")),
            ("cluster_usercount", new PyInt(Math.Max(1, info.UserCount()))),
            ("user_logonqueueposition", new PyInt(1)),
            ("macho_version", new PyInt(496)),
            ("status", new PyStr("OK"))));

    [Method("GetNodeID")]
    public PyValue GetNodeId(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    private static PyValue? Argument(ServiceCall call, string name, int index) =>
        call.Kwarg(name) ?? (call.Args.Count > index ? call.Args[index] : null);

    private static bool BooleanArgument(ServiceCall call, string name, int index, bool fallback)
    {
        var value = Argument(call, name, index);
        if (value is null or PyNone)
            return fallback;
        if (value is PyBool boolean)
            return boolean.Value;
        if (value is PyInt integer)
            return integer.Value != 0;
        if (value is PyLong longInteger)
            return longInteger.Value != 0;
        if (value is PyReal real)
            return real.Value != 0;

        var normalized = (JsSemantics.StringValue(value) ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
    }
}
