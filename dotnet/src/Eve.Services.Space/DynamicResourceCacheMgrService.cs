using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space;

/// <summary>
/// The dynamic resource cache of the client: the settings the agency and the tooltips show, and the ESS state of the systems.
/// Without <paramref name="readSystem"/> and <paramref name="listSystems"/> the ESS answers are the empty ones (no ESS anywhere).
/// </summary>
public sealed class DynamicResourceCacheMgrService(
    Func<ServiceCall, CancellationToken, ValueTask<PyValue?>>? rpcHandler = null,
    Func<long, CancellationToken, ValueTask<EssMgrSystemData?>>? readSystem = null,
    Func<CancellationToken, ValueTask<IReadOnlyList<EssMgrSystemData>>>? listSystems = null,
    Func<int, string?>? quadrantOf = null) : IService
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
            "GetDBSMapData" => await DbsMapDataAsync(call.Cancellation),
            "GetESSAgencyData" => await AgencyDataAsync(call.Cancellation),
            "GetESSSystemDetails" => await SystemDetailsAsync(call),
            "GetESSReserveBankKeys" => await ReserveKeysAsync(call),
            _ => PyNone.Instance,
        };
    }

    private async ValueTask<PyValue> DbsMapDataAsync(CancellationToken cancellationToken)
    {
        if (listSystems is null)
            return new PyDict([]);

        var entries = (await listSystems(cancellationToken))
            .Where(state => state.SolarSystemId > 0 && state.CurrentOutput > 0)
            .Select(state => new KeyValuePair<PyValue, PyValue>(
                Py.Number(state.SolarSystemId), Py.Number(state.CurrentOutput)))
            .ToArray();
        return new PyDict(entries);
    }

    private async ValueTask<PyValue> SystemDetailsAsync(ServiceCall call)
    {
        var state = readSystem is null ? null : await readSystem(SystemIdArgument(call), call.Cancellation);
        if (state is null)
        {
            return Py.Dict(
                ("autopaymentWindowState", new PyInt(EssRules.AutopayStateLong)),
                ("activeLinks", new PyInt(0)),
                ("totalPulses", new PyInt(0)),
                ("reserveTimeWindowState", new PyInt(EssRules.ReserveWindowInactive)),
                ("reserveTimeChosen", new PyInt(0)));
        }
        var window = EssRules.ReserveWindowState(state.ReserveBankPulsesRemaining, state.ReserveBankPulsesTotal);
        return Py.Dict(
            ("autopaymentWindowState", new PyInt(EssRules.AutopayWindowState((decimal)state.MainValue, state.MainBankLink is not null))),
            ("activeLinks", new PyInt((state.ReserveLinkedCharacterIds?.Count ?? 0) + (state.MainBankLink is null ? 0 : 1))),
            ("totalPulses", new PyInt(state.ReserveBankPulsesTotal)),
            ("reserveTimeWindowState", new PyInt(window)),
            ("reserveTimeChosen", new PyInt(window == EssRules.ReserveWindowInactive ? 0 : state.ReserveBankPulsesTotal * 60)));
    }

    private async ValueTask<PyValue> AgencyDataAsync(CancellationToken cancellationToken)
    {
        if (listSystems is null)
        {
            return new PyDict([]);
        }
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var state in await listSystems(cancellationToken))
        {
            entries.Add(new(Py.Number(state.SolarSystemId), Py.Dict(
                ("currentOutput", Py.Number(state.CurrentOutput)),
                ("mainValue", Py.Number(state.MainValue)),
                ("reserveValue", Py.Number(state.ReserveValue)),
                ("vaultOpen", state.ReserveBankPulsesRemaining > 0 ? PyBool.True : PyBool.False))));
        }
        return new PyDict(entries);
    }

    private async ValueTask<PyValue> ReserveKeysAsync(ServiceCall call)
    {
        // the keys of the system's quadrant (CCP's ESS reserve key article); a system in no quadrant lists none
        var systemId = SystemIdArgument(call);
        var state = readSystem is null ? null : await readSystem(systemId, call.Cancellation);
        var quadrant = quadrantOf?.Invoke((int)systemId);
        return state is null || quadrant is null
            ? new PyList([])
            : new PyList([.. EssReserveKeys.All.Where(key => key.Quadrant == quadrant).Select(key => (PyValue)Py.Number(key.KeyTypeId))]);
    }

    private static long SystemIdArgument(ServiceCall call) => call.Args.Count > 0 && call.Args[0] is PyInt { Value: > 0 } id ? id.Value : 0;
}
