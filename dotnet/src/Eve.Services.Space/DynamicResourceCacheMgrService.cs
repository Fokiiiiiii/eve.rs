using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space;

public sealed class DynamicResourceCacheMgrService(
    Func<ServiceCall, CancellationToken, ValueTask<PyValue?>>? rpcHandler = null) : IService
{
    private static readonly (string Name, double Value)[] DefaultSettings =
    [
        ("minOutput", 1.0), ("maxOutput", 2.0), ("equilibriumValue", 1.5),
        ("timeIntervalSeconds", 86400), ("bountiesCut", 0.4), ("mainBankPayRatio", 0.975),
        ("autopaymentIntervalSeconds", 9900), ("bufferTimeSeconds", 360),
        ("reservePulseIntervalSeconds", 60), ("reserveBankMaxPayout", 0),
        ("reserveBankTimeAccessFactor", 3), ("reserveWindowAccessFactor", 1.5),
        ("reservePayoutExponentLead", 2), ("reservePayoutExponentTail", 2),
        ("reservePayoutPaddingValue", 0), ("earlyPayoutThresholdFactor", 2d / 3),
        ("latePayoutThresholdFactor", 1d / 3),
    ];

    public string Name => "dynamicResourceCacheMgr";

    public bool HasMethod(string method) => method is
        "GetDynamicResourceSettings" or
        "GetDBSMapData" or
        "GetESSAgencyData" or
        "GetESSSystemDetails" or
        "GetESSReserveBankKeys";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        if (rpcHandler is not null && await rpcHandler(call, call.Cancellation) is { } response)
            return response;

        return call.Method switch
        {
            "GetDynamicResourceSettings" => Py.Dict([.. DefaultSettings.Select(setting =>
                (setting.Name, Py.Number(setting.Value)))]),
            "GetDBSMapData" or "GetESSAgencyData" => new PyDict([]),
            "GetESSSystemDetails" => Py.Dict(
                ("autopaymentWindowState", new PyInt(0)),
                ("activeLinks", new PyInt(0)),
                ("totalPulses", new PyInt(0)),
                ("reserveTimeWindowState", new PyInt(0)),
                ("reserveTimeChosen", new PyInt(0))),
            "GetESSReserveBankKeys" => new PyList([]),
            _ => PyNone.Instance,
        };
    }
}