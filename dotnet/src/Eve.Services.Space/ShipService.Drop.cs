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

            var quantity = parts.Count > 1 && IdOf(parts[1]) is { } requestedQuantity
                ? Math.Max(1, requestedQuantity)
                : 1;
            var values = new List<PyValue>();
            for (var launched = 0L; launched < quantity; launched++)
            {
                try
                {
                    if (await containers.LaunchFromShipAsync(call.Session, itemId, call.Cancellation) is not { } launchedId)
                    {
                        break;
                    }
                    values.Add(new PyLong(launchedId));
                }
                catch (MachoWrappedException error) when (error.Error.IsUserError)
                {
                    values.Add(Py.Tuple(
                        new PyStr("CustomNotify"),
                        new PyDict([new(new PyStr("notify"), new PyStr(error.Error.UserErrorMessage))])));
                    break;
                }
            }
            response.Add(new(new PyLong(itemId), new PyList(values)));
        }

        return new PyDict(response);
    }
}
