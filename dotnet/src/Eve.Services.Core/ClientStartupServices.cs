using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Eve.Marshal;

namespace Eve.Services.Core;

[Service("securityMgr")]
public sealed partial class SecurityMgrService
{
    private static PyValue EmptyModifiedSystems()
    {
        var descriptorHeader = new PyObjectEx(ObjectExKind.Ex1,
            Py.Tuple(new PyToken("blue.DBRowDescriptor"), Py.Tuple(Py.Tuple(
                Py.Tuple(new PyStr("solarSystemID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("security"), new PyInt(0x05)),
                Py.Tuple(new PyStr("modifiedSecurity"), new PyInt(0x05))))), [], []);
        var descriptor = Descriptor.FromHeader(descriptorHeader);
        return new PyObjectEx(ObjectExKind.Ex2,
            Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset")),
                Py.Dict(("header", descriptor.Header), ("columnName", new PyStr("solarSystemID")))), [], []);
    }

    [Method("get_modified_systems")]
    public PyValue GetModifiedSystems(ServiceCall call) => EmptyModifiedSystems();
}

[Service("alert")]
public sealed partial class AlertService
{
    private const int MaxRememberedStackHashes = 10_000;
    private static long _nextErrorId;
    private static readonly object ErrorIdGate = new();
    private static readonly Dictionary<string, long> ErrorIdByStackHash = new(StringComparer.Ordinal);
    private static readonly Queue<string> StackHashOrder = new();

    [Method("BeanCount")]
    public PyValue BeanCount(ServiceCall call)
    {
        if (call.Args.Count != 1 || call.Kwargs is not PyNone || call.Arg(0) is not (PyInt or PyLong))
            throw MachoErrors.ThrowUserError("ALERT_ARGUMENTS_INVALID");
        return new PyTuple([PyNone.Instance, PyNone.Instance]);
    }

    [Method("SendClientStackTraceAlert")]
    public PyValue SendClientStackTraceAlert(ServiceCall call)
    {
        if (call.Kwargs is not PyNone || call.Args.Count is < 3 or > 4)
            throw MachoErrors.ThrowUserError("ALERT_ARGUMENTS_INVALID");
        var stackId = call.Arg(0) switch
        {
            PyTuple tuple => tuple.Items,
            PyList list => list.Items,
            _ => [],
        };
        if (stackId.Count != 2 || !StableKeyAllowed(stackId[0]) || !BoundedText(stackId[1], 256)
            || !BoundedText(call.Arg(1), 16_384) || !BoundedText(call.Arg(2), 32)
            || (call.Args.Count == 4 && call.Arg(3) is not PyNone && !StableKeyAllowed(call.Arg(3))))
            throw MachoErrors.ThrowUserError("ALERT_ARGUMENTS_INVALID");
        var stackHash = stackId.Count > 0 ? StableKey(stackId[0]) : null;
        var stackKey = stackId.Count > 1 ? Limit(Text(stackId[1]), 256) : "";
        var mode = Limit(Text(call.Arg(2)), 32);
        var trace = Limit(Text(call.Arg(1)), 16_384);
        Console.Error.WriteLine($"eve-host: client stack trace {stackKey} ({mode})\n{trace}");
        var key = stackHash is not null ? $"h:{stackHash}" : stackKey.Length > 0 ? $"k:{stackKey}" : null;
        var errorId = ResolveErrorId(key);
        return new PyTuple([new PyLong(errorId), new PyStr("D")]);
    }

    private static bool StableKeyAllowed(PyValue value) => value switch
    {
        PyInt or PyLong or PyStr or PyToken => true,
        PyBytes bytes => bytes.Value.Length <= 256,
        _ => false,
    };

    private static bool BoundedText(PyValue value, int maxBytes) => value switch
    {
        PyStr text => Encoding.UTF8.GetByteCount(text.Value) <= maxBytes,
        PyWString text => Encoding.UTF8.GetByteCount(text.Value) <= maxBytes,
        PyToken text => Encoding.UTF8.GetByteCount(text.Value) <= maxBytes,
        PyBytes bytes => bytes.Value.Length <= maxBytes,
        _ => false,
    };

    private static long ResolveErrorId(string? key)
    {
        if (key is null) return Interlocked.Increment(ref _nextErrorId);
        lock (ErrorIdGate)
        {
            if (ErrorIdByStackHash.TryGetValue(key, out var existing)) return existing;
            var allocated = Interlocked.Increment(ref _nextErrorId);
            if (ErrorIdByStackHash.Count == MaxRememberedStackHashes)
                ErrorIdByStackHash.Remove(StackHashOrder.Dequeue());
            ErrorIdByStackHash.Add(key, allocated);
            StackHashOrder.Enqueue(key);
            return allocated;
        }
    }

    private static string? StableKey(PyValue value) => value switch
    {
        PyInt integer => $"i:{integer.Value.ToString(CultureInfo.InvariantCulture)}",
        PyLong integer => $"l:{integer.Value.ToString(CultureInfo.InvariantCulture)}",
        PyStr text => $"s:{Limit(text.Value, 256)}",
        PyToken token => $"t:{Limit(token.Value, 256)}",
        PyBytes bytes when bytes.Value.Length <= 256 => $"b:{Convert.ToHexString(bytes.Value.Span)}",
        _ => null,
    };

    private static string Limit(string value, int length) => value.Length > length ? value[..length] : value;

    private static string Text(PyValue? value) => value switch
    {
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        PyToken text => text.Value,
        PyBytes text => Encoding.UTF8.GetString(text.Value.Span),
        _ => "",
    };
}

[Service("fighterMgr")]
public sealed partial class FighterMgrService
{
    /// <summary>The text of a refusal when no fighter provider is installed (the simulation and its fighter service are not running).</summary>
    public const string UnavailableText = "Player fighters are not available on this server: the fighter service is not running.";

    /// <summary>
    /// The provider of the fighter calls. Without one a call is refused, never answered as done: a dictionary answer names each id with a user error, the other
    /// answers throw one. A fighter is never reported launched, recalled, moved or scooped when nothing did it.
    /// </summary>
    public ISpaceFighters? Provider { get; set; }

    [Method("GetFightersForShip")]
    public ValueTask<PyValue> GetFightersForShip(ServiceCall call) =>
        Provider is { } provider ? provider.GetFightersForShipAsync(call) : Unavailable();

    [Method("CmdAbandonFighter")]
    public ValueTask<PyValue> CmdAbandonFighter(ServiceCall call) =>
        Provider is { } provider ? provider.CmdAbandonFighterAsync(call) : Unavailable();

    [Method("CmdActivateAbilitySlots")]
    public ValueTask<PyValue> CmdActivateAbilitySlots(ServiceCall call) =>
        Provider is { } provider ? provider.CmdActivateAbilitySlotsAsync(call) : UnavailableForEachId(call);

    [Method("CmdDeactivateAbilitySlots")]
    public ValueTask<PyValue> CmdDeactivateAbilitySlots(ServiceCall call) =>
        Provider is { } provider ? provider.CmdDeactivateAbilitySlotsAsync(call) : UnavailableForEachId(call);

    [Method("CmdScoopAbandonedFighterFromSpace")]
    public ValueTask<PyValue> CmdScoopAbandonedFighterFromSpace(ServiceCall call) =>
        Provider is { } provider ? provider.CmdScoopAbandonedFighterFromSpaceAsync(call) : Unavailable();

    [Method("ExecuteMovementCommandOnFighters")]
    public ValueTask<PyValue> ExecuteMovementCommandOnFighters(ServiceCall call) =>
        Provider is { } provider ? provider.ExecuteMovementCommandOnFightersAsync(call) : Unavailable();

    [Method("LaunchFightersFromTubes")]
    public ValueTask<PyValue> LaunchFightersFromTubes(ServiceCall call) =>
        Provider is { } provider ? provider.LaunchFightersFromTubesAsync(call) : UnavailableForEachId(call);

    [Method("LoadFightersToTube")]
    public ValueTask<PyValue> LoadFightersToTube(ServiceCall call) =>
        Provider is { } provider ? provider.LoadFightersToTubeAsync(call) : Unavailable();

    [Method("RecallFightersToTubes")]
    public ValueTask<PyValue> RecallFightersToTubes(ServiceCall call) =>
        Provider is { } provider ? provider.RecallFightersToTubesAsync(call) : UnavailableForEachId(call);

    [Method("UnloadTubeToFighterBay")]
    public ValueTask<PyValue> UnloadTubeToFighterBay(ServiceCall call) =>
        Provider is { } provider ? provider.UnloadTubeToFighterBayAsync(call) : Unavailable();

    private static ValueTask<PyValue> Unavailable() =>
        ValueTask.FromException<PyValue>(MachoErrors.ThrowUserError(UnavailableText));

    /// <summary>The per-id answer of a call without a provider: each id the call names is refused with the same user error.</summary>
    private static ValueTask<PyValue> UnavailableForEachId(ServiceCall call) => ValueTask.FromResult<PyValue>(new PyDict(
        RequestedIds(call.Arg(0)).Select(id => new KeyValuePair<PyValue, PyValue>(
            FighterId(id),
            MachoErrors.UserErrorPayload(UnavailableText)))
        .ToArray()));

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
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Args.Count != 1 || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("SHIP_KILL_COUNTER_ARGUMENTS_INVALID");
        var itemId = call.Arg(0) switch
        {
            PyInt value when value.Value > 0 => value.Value,
            PyLong value when value.Value > 0 => value.Value,
            _ => 0,
        };
        if (itemId <= 0)
            throw MachoErrors.ThrowUserError("SHIP_KILL_COUNTER_ITEM_ID_INVALID");
        if (readShipKillCounters is not { } readCounters)
            throw MachoErrors.ThrowUserError("SHIP_KILL_COUNTER_UNAVAILABLE");

        var table = await readCounters(call.Cancellation).ConfigureAwait(false);
        var record = (table["ships"] as JsonObject)?
            [itemId.ToString(CultureInfo.InvariantCulture)] as JsonObject;
        if (record?["playerKills"] is not { } storedCount ||
            !double.TryParse(storedCount.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var count) ||
            !double.IsFinite(count))
            return new PyInt(0);

        return new PyInt((int)Math.Clamp(Math.Truncate(count), 0d, MaxDisplayedKillmarks));
    }
}
