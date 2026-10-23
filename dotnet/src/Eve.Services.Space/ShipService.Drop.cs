using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space;

public sealed partial class ShipService
{
    /// <summary>
    /// The V24 <c>eveMisc.LaunchFromShip</c> path calls <c>ship.Drop</c>. Each launched item is keyed to its item id and returned in the per-item list
    /// the client consumes; launch refusals become the per-item CustomNotify tuple used by that helper.
    /// </summary>
    [Method("Drop")]
    public async ValueTask<PyValue> Drop(ServiceCall call)
    {
        if (containers is null || !_binding.Owns(call))
        {
            return None;
        }

        IReadOnlyList<PyValue> requests = call.Arg(0) switch
        {
            PyTuple tuple => tuple.Items,
            PyList list => list.Items,
            _ => [],
        };
        var response = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var request in requests)
        {
            IReadOnlyList<PyValue> parts = request switch
            {
                PyTuple tuple => tuple.Items,
                PyList list => list.Items,
                _ => [request],
            };
            if (parts.Count == 0 || IdOf(parts[0]) is not { } itemId || itemId <= 0)
            {
                continue;
            }

            // shortcut: one source item id is launched once; add stack quantities when a Host launch provider consumes stacks.
            var values = new List<PyValue>();
            try
            {
                if (await containers.LaunchFromShipAsync(call.Session, itemId, call.Cancellation) is { } launchedId)
                {
                    values.Add(new PyLong(launchedId));
                }
            }
            catch (MachoWrappedException error) when (error.Error.IsUserError)
            {
                values.Add(Py.Tuple(
                    new PyStr("CustomNotify"),
                    new PyDict([new(new PyStr("notify"), new PyStr(error.Error.UserErrorMessage))])));
            }
            response.Add(new(new PyLong(itemId), new PyList(values)));
        }

        return new PyDict(response);
    }
}
