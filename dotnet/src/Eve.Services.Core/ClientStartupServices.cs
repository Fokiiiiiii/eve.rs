using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Eve.Marshal;

namespace Eve.Services.Core;

[Service("securityMgr")]
public sealed partial class SecurityMgrService
{
    [Method("get_modified_systems")]
    public PyValue GetModifiedSystems(ServiceCall call) => new PyDict([]);
}

[Service("alert")]
public sealed partial class AlertService
{
    private static long _nextErrorId;

    [Method("BeanCount")]
    public PyValue BeanCount(ServiceCall call) => new PyTuple([PyNone.Instance, PyNone.Instance]);

    [Method("SendClientStackTraceAlert")]
    public PyValue SendClientStackTraceAlert(ServiceCall call)
    {
        var stackKey = call.Arg(0) is PyTuple stackId && stackId.Items.Count > 1
            ? Limit(Text(stackId.Items[1]), 256)
            : "unknown";
        var mode = Limit(Text(call.Arg(2)), 32);
        var trace = Limit(Text(call.Arg(1)), 16_384);
        Console.Error.WriteLine($"eve-host: client stack trace {stackKey} ({mode})\n{trace}");
        return new PyTuple([new PyLong(System.Threading.Interlocked.Increment(ref _nextErrorId)), new PyStr("D")]);
    }

    private static string Limit(string value, int length) => value.Length > length ? value[..length] : value;

    private static string Text(PyValue? value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        PyToken text => text.Value,
        PyBytes text => Encoding.UTF8.GetString(text.Value.Span),
        _ => "",
    };
}

[Service("fighterMgr")]
public sealed partial class FighterMgrService
{
    public enum AbilitySlotOperation { Activate, Deactivate }

    public sealed record AbilitySlotRequest(
        AbilitySlotOperation Operation,
        ClientSession Session,
        IReadOnlyList<long> FighterIds,
        PyValue AbilitySlotId,
        PyValue Target);

    public delegate ValueTask<IReadOnlyDictionary<long, PyValue>?> AbilitySlotCallback(
        AbilitySlotRequest request,
        CancellationToken cancellationToken);

    /// <summary>The Host installs this handler when player-fighter inventory and simulation support is available.</summary>
    public Func<ServiceCall, ValueTask<PyValue>>? RpcHandler { get; set; }
    public AbilitySlotCallback? AbilitySlotHandler { get; set; }

    [Method("GetFightersForShip")]
    public ValueTask<PyValue> GetFightersForShip(ServiceCall call) => Dispatch(call, new PyTuple([
        new PyList([]),
        new PyList([]),
        new PyDict([]),
    ]));

    [Method("CmdAbandonFighter")]
    public ValueTask<PyValue> CmdAbandonFighter(ServiceCall call) => Dispatch(call, PyBool.False);

    [Method("CmdActivateAbilitySlots")]
    public ValueTask<PyValue> CmdActivateAbilitySlots(ServiceCall call) =>
        DispatchAbilitySlots(call, AbilitySlotOperation.Activate);

    [Method("CmdDeactivateAbilitySlots")]
    public ValueTask<PyValue> CmdDeactivateAbilitySlots(ServiceCall call) =>
        DispatchAbilitySlots(call, AbilitySlotOperation.Deactivate);

    [Method("CmdScoopAbandonedFighterFromSpace")]
    public ValueTask<PyValue> CmdScoopAbandonedFighterFromSpace(ServiceCall call) => Dispatch(call, PyBool.False);

    [Method("ExecuteMovementCommandOnFighters")]
    public ValueTask<PyValue> ExecuteMovementCommandOnFighters(ServiceCall call) => Dispatch(call, PyNone.Instance);

    [Method("LaunchFightersFromTubes")]
    public ValueTask<PyValue> LaunchFightersFromTubes(ServiceCall call) => Dispatch(call, new PyDict([]));

    [Method("LoadFightersToTube")]
    public ValueTask<PyValue> LoadFightersToTube(ServiceCall call) => Dispatch(call, PyBool.False);

    [Method("RecallFightersToTubes")]
    public ValueTask<PyValue> RecallFightersToTubes(ServiceCall call) => Dispatch(call, RecallFallback(call));

    [Method("UnloadTubeToFighterBay")]
    public ValueTask<PyValue> UnloadTubeToFighterBay(ServiceCall call) => Dispatch(call, PyBool.False);

    private ValueTask<PyValue> Dispatch(ServiceCall call, PyValue fallback) =>
        RpcHandler is { } handler ? handler(call) : ValueTask.FromResult(fallback);

    private ValueTask<PyValue> DispatchAbilitySlots(ServiceCall call, AbilitySlotOperation operation)
    {
        if (AbilitySlotHandler is { } abilitySlotHandler)
        {
            var request = new AbilitySlotRequest(
                operation,
                call.Session,
                RequestedIds(call.Arg(0)),
                call.Arg(1),
                operation == AbilitySlotOperation.Activate && call.Args.Count > 2
                    ? call.Arg(2)
                    : PyNone.Instance);
            return BuildAbilitySlotResultAsync(abilitySlotHandler, request, call.Cancellation);
        }
        return RpcHandler is { } rpcHandler
            ? rpcHandler(call)
            : ValueTask.FromResult<PyValue>(RecallFallback(call));
    }

    private static async ValueTask<PyValue> BuildAbilitySlotResultAsync(
        AbilitySlotCallback callback,
        AbilitySlotRequest request,
        CancellationToken cancellationToken)
    {
        var entries = await callback(request, cancellationToken);
        return entries is null
            ? new PyDict([])
            : new PyDict([.. entries.Where(entry => entry.Key > 0).Select(entry =>
                new KeyValuePair<PyValue, PyValue>(FighterId(entry.Key), entry.Value))]);
    }

    private static PyDict RecallFallback(ServiceCall call) => new(RequestedIds(call.Arg(0))
        .Select(id => new KeyValuePair<PyValue, PyValue>(FighterId(id), PyNone.Instance))
        .ToArray());

    private static IReadOnlyList<long> RequestedIds(PyValue value)
    {
        var values = value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => Array.Empty<PyValue>(),
        };
        return values
            .Select(value => value switch
            {
                PyInt integer => (long)integer.Value,
                PyLong integer => integer.Value,
                _ => 0,
            })
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
    }

    private static PyValue FighterId(long id) => id <= int.MaxValue
        ? new PyInt((int)id)
        : new PyLong(id);
}

[Service("shipKillCounter")]
public sealed partial class ShipKillCounterService(
    Func<CancellationToken, Task<JsonObject>>? readShipKillCounters = null)
{
    private const int MaxDisplayedKillmarks = 999;

    [Method("GetItemKillCountPlayer")]
    public async ValueTask<PyValue> GetItemKillCountPlayer(ServiceCall call)
    {
        var itemId = call.Arg(0) switch
        {
            PyInt value => value.Value,
            PyLong value => value.Value,
            _ => 0,
        };
        if (itemId <= 0 || readShipKillCounters is null)
            return new PyInt(0);

        var table = await readShipKillCounters(call.Cancellation).ConfigureAwait(false);
        var record = (table["ships"] as JsonObject)?
            [itemId.ToString(CultureInfo.InvariantCulture)] as JsonObject;
        if (record?["playerKills"] is not { } storedCount ||
            !double.TryParse(storedCount.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var count) ||
            !double.IsFinite(count))
            return new PyInt(0);

        return new PyInt((int)Math.Clamp(Math.Truncate(count), 0d, MaxDisplayedKillmarks));
    }
}
