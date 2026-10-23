using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

[Service("authentication")]
public sealed partial class AuthenticationRpcService(
    Func<ServiceCall, CancellationToken, ValueTask<PyValue>>? accruedTime = null,
    Func<ServiceCall, CancellationToken, ValueTask<bool>>? amUnderage = null)
{
    private const int MaximumLanguageIdBytes = 32;
    private const int MaximumJwtBytes = 8192;

    private readonly Func<ServiceCall, CancellationToken, ValueTask<PyValue>> _accruedTime = accruedTime ?? EmptyAccruedTime;
    private readonly Func<ServiceCall, CancellationToken, ValueTask<bool>> _amUnderage = amUnderage ?? NotUnderage;

    [Method("AccruedTime")]
    public ValueTask<PyValue> AccruedTime(ServiceCall call)
    {
        RequireShape(call, 0);
        return _accruedTime(call, call.Cancellation);
    }

    [Method("AmUnderage")]
    public async ValueTask<PyValue> AmUnderage(ServiceCall call)
    {
        RequireShape(call, 1);
        if (Text(call.Arg(0)) is not { Length: > 0 } token ||
            System.Text.Encoding.UTF8.GetByteCount(token) > MaximumJwtBytes)
            throw MachoErrors.ThrowUserError("AUTHENTICATION_TOKEN_INVALID");
        return await _amUnderage(call, call.Cancellation).ConfigureAwait(false) ? PyBool.True : PyBool.False;
    }

    [Method("SetLanguageID")]
    public PyValue SetLanguageID(ServiceCall call)
    {
        RequireShape(call, 1);
        var text = Text(call.Arg(0));
        if (text is null || System.Text.Encoding.UTF8.GetByteCount(text) > MaximumLanguageIdBytes)
            throw MachoErrors.ThrowUserError("AUTHENTICATION_LANGUAGE_ID_INVALID");
        var languageId = text.Trim();
        if (string.IsNullOrEmpty(languageId))
            languageId = "EN";

        var value = new PyStr(languageId);
        call.Session.Set("languageID", value);
        call.Session.Set("languageId", value);
        call.Session.Set("languageid", value);
        call.Session.Set("userLanguageID", value);
        return PyNone.Instance;
    }

    private static void RequireShape(ServiceCall call, int argumentCount)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Args.Count != argumentCount || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("AUTHENTICATION_ARGUMENTS_INVALID");
    }

    private static string? Text(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        _ => null,
    };

    private static ValueTask<PyValue> EmptyAccruedTime(ServiceCall _, CancellationToken __) =>
        ValueTask.FromResult<PyValue>(new PyList([]));

    private static ValueTask<bool> NotUnderage(ServiceCall _, CancellationToken __) =>
        ValueTask.FromResult(false);
}

[Service("infoGatheringMgr")]
public sealed partial class InfoGatheringMgrService(
    Func<ServiceCall, CancellationToken, ValueTask<PyValue>>? getStateAndConfig = null,
    Func<ServiceCall, PyValue, CancellationToken, ValueTask<PyValue>>? logInfoEventsFromClient = null)
{
    private static readonly PyValue DefaultStateAndConfig = Py.KeyVal(
        ("isEnabled", new PyInt(0)),
        ("infoTypes", new PyList([])),
        ("clientWorkerInterval", new PyInt(0)),
        ("infoTypesOncePerRun", new PyList([])),
        ("infoTypeAggregates", new PyDict([])),
        ("infoTypeParameters", new PyDict([])));

    private readonly Func<ServiceCall, CancellationToken, ValueTask<PyValue>> _getStateAndConfig =
        getStateAndConfig ?? DefaultConfig;
    private readonly Func<ServiceCall, PyValue, CancellationToken, ValueTask<PyValue>> _logInfoEventsFromClient =
        logInfoEventsFromClient ?? EmptyStatuses;

    [Method("GetStateAndConfig")]
    public ValueTask<PyValue> GetStateAndConfig(ServiceCall call) =>
        _getStateAndConfig(call, call.Cancellation);

    [Method("LogInfoEventsFromClient")]
    public ValueTask<PyValue> LogInfoEventsFromClient(ServiceCall call) =>
        _logInfoEventsFromClient(call, call.Arg(0), call.Cancellation);

    private static ValueTask<PyValue> DefaultConfig(ServiceCall _, CancellationToken __) =>
        ValueTask.FromResult(DefaultStateAndConfig);

    private static ValueTask<PyValue> EmptyStatuses(ServiceCall _, PyValue __, CancellationToken ___) =>
        ValueTask.FromResult<PyValue>(new PyDict([]));
}

[Service("eveguard_report")]
public sealed partial class EveguardReportService(
    Func<string, ServiceCall, CancellationToken, ValueTask>? recordHeartbeat = null)
{
    private readonly Func<string, ServiceCall, CancellationToken, ValueTask> _recordHeartbeat =
        recordHeartbeat ?? IgnoreHeartbeat;

    [Method("heartbeat_started")]
    public ValueTask<PyValue> HeartbeatStarted(ServiceCall call) => RecordAsync("started", call);

    [Method("heartbeat_stopped")]
    public ValueTask<PyValue> HeartbeatStopped(ServiceCall call) => RecordAsync("stopped", call);

    private async ValueTask<PyValue> RecordAsync(string eventType, ServiceCall call)
    {
        await _recordHeartbeat(eventType, call, call.Cancellation).ConfigureAwait(false);
        return PyNone.Instance;
    }

    private static ValueTask IgnoreHeartbeat(string _, ServiceCall __, CancellationToken ___) =>
        ValueTask.CompletedTask;
}
