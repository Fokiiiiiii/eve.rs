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
