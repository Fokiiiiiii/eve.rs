using Eve.Marshal;

namespace Eve.Services.Crimewatch;

public sealed partial class CrimewatchService
{
    public Func<ServiceCall, double, double, CancellationToken, ValueTask>? ApplySecurityTagsAsync { get; set; }

    /// <summary>Consumes the station-hangar tags and fee, then records the security-status change.</summary>
    [Method("ApplyTagsForSecStatus")]
    public async ValueTask<PyValue> ApplyTagsForSecStatus(ServiceCall call)
    {
        var requestedStatus = NumberValue(call.Arg(0));
        var clientStatus = NumberValue(call.Arg(1));
        if (requestedStatus is not { } requested || clientStatus is not { } current ||
            !double.IsFinite(requested) || !double.IsFinite(current))
            throw new ArgumentException("Security tag exchange requires finite target and current security statuses.");
        var apply = ApplySecurityTagsAsync
            ?? throw new InvalidOperationException("Security tag exchange is not configured.");
        await apply(call, requested, current, call.Cancellation);
        return None;
    }

    private static double? NumberValue(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real => real.Value,
        _ => null,
    };
}
