using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

public sealed partial class EventLogRpcService
{
    [Method("LogClientEvent")]
    public ValueTask<PyValue> LogClientEvent(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Args.Count < 3)
            throw MachoErrors.ThrowUserError("CLIENT_EVENT_ARGUMENTS_INVALID");

        var category = EventName(call.Arg(0));
        var columnCount = call.Arg(1) switch
        {
            PyList list => list.Items.Count,
            PyTuple tuple => tuple.Items.Count,
            _ => -1,
        };
        if (category.Length > 256 || columnCount < 0 || columnCount > MaxRows)
            throw MachoErrors.ThrowUserError("CLIENT_EVENT_COLUMNS_INVALID");

        var eventName = EventName(call.Arg(2));
        if (eventName.Length > 256 || call.Args.Count - 3 > MaxRows)
            throw MachoErrors.ThrowUserError("CLIENT_EVENT_VALUES_INVALID");
        if (call.Kwargs is not PyNone &&
            (call.Kwargs is not PyDict kwargs || kwargs.Entries.Count > 1 ||
             kwargs.Entries.Any(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) != "machoVersion" ||
                                         entry.Value is not PyInt and not PyLong)))
            throw MachoErrors.ThrowUserError("CLIENT_EVENT_KEYWORDS_INVALID");

        _log($"LogClientEvent category={category} event={eventName}");
        return ValueTask.FromResult<PyValue>(PyNone.Instance);
    }

    private static string EventName(PyValue value) =>
        value is PyNone ? "" : Eve.Macho.JsSemantics.StringValue(value) ?? "";
}
