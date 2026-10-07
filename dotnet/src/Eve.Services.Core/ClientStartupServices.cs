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
}

[Service("shipKillCounter")]
public sealed partial class ShipKillCounterService
{
    [Method("GetItemKillCountPlayer")]
    public PyValue GetItemKillCountPlayer(ServiceCall call) => new PyInt(0);
}
