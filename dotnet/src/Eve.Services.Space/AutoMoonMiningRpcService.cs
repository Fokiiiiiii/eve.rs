using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space;

public delegate ValueTask<PyValue> ReadAutoMoonMiningCallback(
    ClientSession session,
    long structureId,
    CancellationToken cancellationToken);

/// <summary>The two read-only calls used by the V24 Metenox Moon Drill window.</summary>
[Service("autoMoonMining")]
public sealed partial class AutoMoonMiningRpcService(
    ReadAutoMoonMiningCallback? readDetails = null,
    ReadAutoMoonMiningCallback? readCycleOutput = null)
{
    private static readonly PyValue EmptyDictionary = new PyDict([]);

    [Method("RequestMiningDetails")]
    public async ValueTask<PyValue> RequestMiningDetails(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var structureId = StructureId(call);
        if (structureId <= 0)
            throw MachoErrors.ThrowUserError("CustomInfo",
                ("info", new PyStr("Auto moon miner details require a structure ID.")));
        if (readDetails is not { } read)
            throw MachoErrors.ThrowUserError("CustomInfo",
                ("info", new PyStr("Auto moon miner details are unavailable.")));
        return await read(call.Session, structureId, call.Cancellation).ConfigureAwait(false);
    }

    [Method("RequestMiningCycleOutput")]
    public async ValueTask<PyValue> RequestMiningCycleOutput(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var structureId = StructureId(call);
        if (structureId <= 0)
            return EmptyDictionary;
        if (readCycleOutput is not { } read)
            throw MachoErrors.ThrowUserError("CustomInfo",
                ("info", new PyStr("Auto moon miner cycle output is unavailable.")));
        return await read(call.Session, structureId, call.Cancellation).ConfigureAwait(false);
    }

    private static long StructureId(ServiceCall call)
    {
        var value = call.Args.Count > 1 ? call.Arg(1) :
            call.Session.Get("structureid") ?? call.Session.Get("structureID") ?? call.Session.Get("shipid");
        return value switch
        {
            PyInt integer => Math.Max(0, integer.Value),
            PyLong integer => Math.Max(0, integer.Value),
            _ => 0,
        };
    }
}
