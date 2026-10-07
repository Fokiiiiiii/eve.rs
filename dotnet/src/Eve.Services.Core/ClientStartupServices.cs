using System.Text;
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
    [Method("GetFightersForShip")]
    public PyValue GetFightersForShip(ServiceCall call) => new PyTuple([
        new PyList([]),
        new PyList([]),
        new PyDict([]),
    ]);

    // The current space protocol controls drones, not player fighter squadrons. Keep the fighterMgr wire contract available until a fighter backend is exposed.
    private const string FighterBackendUnavailable = "Fighter commands are not connected to the space simulation.";

    [Method("CmdAbandonFighter")]
    public PyValue CmdAbandonFighter(ServiceCall call) => PyBool.False;

    [Method("CmdScoopAbandonedFighterFromSpace")]
    public PyValue CmdScoopAbandonedFighterFromSpace(ServiceCall call) => PyBool.False;

    [Method("ExecuteMovementCommandOnFighters")]
    public PyValue ExecuteMovementCommandOnFighters(ServiceCall call) => PyNone.Instance;

    [Method("LaunchFightersFromTubes")]
    public PyValue LaunchFightersFromTubes(ServiceCall call) => UnavailableErrors(call, 0);

    [Method("LoadFightersToTube")]
    public PyValue LoadFightersToTube(ServiceCall call) => PyBool.False;

    [Method("RecallFightersToTubes")]
    public PyValue RecallFightersToTubes(ServiceCall call) => UnavailableErrors(call, 0);

    [Method("UnloadTubeToFighterBay")]
    public PyValue UnloadTubeToFighterBay(ServiceCall call) => PyBool.False;

    private static PyDict UnavailableErrors(ServiceCall call, int argumentIndex)
    {
        var error = new PyTuple([
            new PyStr("CustomNotify"),
            new PyDict([new KeyValuePair<PyValue, PyValue>(
                new PyStr("notify"), new PyStr(FighterBackendUnavailable))]),
        ]);
        var entries = RequestedIds(call.Arg(argumentIndex))
            .Select(id => new KeyValuePair<PyValue, PyValue>(FighterId(id), error))
            .ToArray();
        return new PyDict(entries);
    }

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
public sealed partial class ShipKillCounterService
{
    [Method("GetItemKillCountPlayer")]
    public PyValue GetItemKillCountPlayer(ServiceCall call) => new PyInt(0);
}
