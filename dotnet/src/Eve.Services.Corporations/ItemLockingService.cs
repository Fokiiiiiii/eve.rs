using Eve.Corporations;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Corporations;

/// <summary>
/// The client's <c>itemLocking</c> service: the corporation items a blueprint vote has locked, per location. The client only
/// reads them; the locks are written by the vote (see <see cref="CorporationStore.LockedItemsAsync"/>).
/// </summary>
[Service("itemLocking")]
public sealed partial class ItemLockingService(CorporationStore corporations)
{
    [Method("GetItemsByLocation")]
    public async ValueTask<PyValue> GetItemsByLocation(ServiceCall call)
    {
        var corporationId = SessionCorporationId(call.Session);
        var locationId = Integral(call.Arg(0));
        if (corporationId <= 0 || locationId is not > 0) return new PyDict([]);
        var items = await corporations.LockedItemsAsync(corporationId, locationId, call.Cancellation);
        return new PyDict([.. items.Select(item => new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(item.ItemId), Py.KeyVal(
            ("itemID", InventoryWire.Id(item.ItemId)),
            ("typeID", InventoryWire.Id(item.TypeId)),
            ("ownerID", InventoryWire.Id(item.CorporationId)),
            ("locationID", InventoryWire.Id(item.LocationId)))))]);
    }

    [Method("GetLockedItemLocations")]
    public async ValueTask<PyValue> GetLockedItemLocations(ServiceCall call)
    {
        var corporationId = SessionCorporationId(call.Session);
        if (corporationId <= 0) return InventoryWire.EmptySet();
        var locations = await corporations.LockedItemLocationsAsync(corporationId, call.Cancellation);
        return InventoryWire.Set(locations.Select(InventoryWire.Id));
    }

    /// <summary>The <c>OnLockedItemChange</c> push the client's service takes to refresh its cache, sent to the corporation's sessions.</summary>
    public static void NotifyChanged(CharacterSessions sessions, ItemLockChange change)
    {
        foreach (var session in sessions.OfCorporation(change.CorporationId))
        {
            try
            {
                session.SendNotification("OnLockedItemChange", "ownerid",
                    InventoryWire.Id(change.ItemId), InventoryWire.Id(change.CorporationId),
                    InventoryWire.Id(change.LocationId), PyValue.Of(change.Locked));
            }
            catch (Exception)
            {
                // a session that cannot take the push is left to refresh on its next read
            }
        }
    }

    private static long SessionCorporationId(ClientSession session)
    {
        var id = session.GetInt("corporationID") ?? 0;
        return id > 0 ? id : session.GetInt("corpid") ?? 0;
    }

    private static long? Integral(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };
}
