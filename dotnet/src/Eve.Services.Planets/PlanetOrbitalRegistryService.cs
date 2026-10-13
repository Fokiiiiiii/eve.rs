using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Services.Planets;

/// <summary>
/// The client's <c>planetOrbitalRegistryBroker</c> (bound per solar system by <c>eveMoniker.GetPlanetOrbitalRegistry</c>): the tax rate of
/// a customs office, which the import and export window shows and sends back (importExportUI.py:383, infosvc.py:2066).
/// </summary>
[Service("planetOrbitalRegistryBroker")]
public sealed partial class PlanetOrbitalRegistryService(PlanetMgrService planets, Func<long> nowMs)
{
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    /// <summary><c>MachoBindObject(solarSystemID, (method, args, kwargs))</c>: binds the system's registry, then makes the nested call.</summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        var bound = new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(nowMs())))));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = [.. nested.Items.Count > 1 && nested.Items[1] is PyTuple args ? args.Items : []],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : PyNone.Instance,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(bound, result);
    }

    /// <summary>GetTaxRate(customsOfficeID): the rate the pilot pays at that office, or nothing when the office is not one of the server's.</summary>
    [Method("GetTaxRate")]
    public async ValueTask<PyValue> GetTaxRate(ServiceCall call)
    {
        var office = call.Arg(0) switch { PyInt i => i.Value, PyLong l => l.Value, _ => 0L };
        if (call.Session.CharacterId is not { } character) return PyNone.Instance;
        return await planets.TaxRateAsync(office, character, call.Cancellation) is { } rate ? new PyReal(rate) : PyNone.Instance;
    }
}
