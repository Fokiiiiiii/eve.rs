using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

public sealed class EventLogRpcService(Action<string>? log = null) : IService
{
    private readonly Action<string> _log = log ?? (message => Console.Error.WriteLine($"[eventLog] {message}"));

    public string Name => "eventLog";

    public bool HasMethod(string method) => method == "LogClientStats";

    public ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return ValueTask.FromResult<PyValue>(PyNone.Instance);

        call.Cancellation.ThrowIfCancellationRequested();
        var locationId = call.Arg(1) switch
        {
            PyInt number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PyLong number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "",
        };
        var rowCount = call.Arg(2) switch
        {
            PyList list => list.Items.Count,
            PyTuple tuple => tuple.Items.Count,
            _ => 0,
        };
        _log($"LogClientStats location={locationId} rows={rowCount}");
        return ValueTask.FromResult<PyValue>(PyNone.Instance);
    }
}
