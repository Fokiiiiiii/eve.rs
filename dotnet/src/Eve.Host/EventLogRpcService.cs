using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

[Service("eventLog")]
public sealed partial class EventLogRpcService(Action<string>? log = null)
{
    private const int MaxRows = 5000;
    private readonly Action<string> _log = log ?? (message => Console.Error.WriteLine($"[eventLog] {message}"));

    [Method("LogClientStats")]
    public ValueTask<PyValue> LogClientStats(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Args.Count is < 3 or > 4)
            throw MachoErrors.ThrowUserError("CLIENT_STATS_ARGUMENTS_INVALID");
        if (ReadNonNegativeId(call.Arg(0)) != call.Session.SessionId)
            throw MachoErrors.ThrowUserError("CLIENT_STATS_SESSION_INVALID");
        if (ReadNonNegativeId(call.Arg(1)) is null)
            throw MachoErrors.ThrowUserError("CLIENT_STATS_LOCATION_INVALID");

        var rows = ReadCollectionCount(call.Arg(2));
        if (rows is null)
            throw MachoErrors.ThrowUserError("CLIENT_STATS_ROWS_INVALID");
        if (rows > MaxRows)
            throw MachoErrors.ThrowUserError("CLIENT_STATS_ROW_LIMIT_EXCEEDED");
        if (call.Args.Count == 4)
        {
            var crashes = ReadCollectionCount(call.Arg(3));
            if (crashes is null)
                throw MachoErrors.ThrowUserError("CLIENT_STATS_CRASHES_INVALID");
            if (crashes > MaxRows)
                throw MachoErrors.ThrowUserError("CLIENT_STATS_CRASH_LIMIT_EXCEEDED");
        }
        if (call.Kwargs is not PyNone)
        {
            if (call.Kwargs is not PyDict kwargs || kwargs.Entries.Count > 1 ||
                kwargs.Entries.Any(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) != "isShutdown" || entry.Value is not PyBool))
                throw MachoErrors.ThrowUserError("CLIENT_STATS_SHUTDOWN_FLAG_INVALID");
        }

        var locationId = ReadNonNegativeId(call.Arg(1))!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _log($"LogClientStats location={locationId} rows={rows}");
        return ValueTask.FromResult<PyValue>(PyNone.Instance);
    }

    private static long? ReadNonNegativeId(PyValue value) => value switch
    {
        PyInt number when number.Value >= 0 => number.Value,
        PyLong number when number.Value >= 0 => number.Value,
        _ => null,
    };

    private static int? ReadCollectionCount(PyValue value) => value switch
    {
        PyList list => list.Items.Count,
        PyTuple tuple => tuple.Items.Count,
        _ => null,
    };
}
