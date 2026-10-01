using Eve.Dogma;
using Eve.Marshal;
using Eve.Ships;

namespace Eve.Services.Ships;

/// <summary>What the client is told when its ship or an item changes: the events dogma reports, and the items that moved.</summary>
public static class ShipNotifications
{
    /// <summary>How the client is told an attribute of a module or ship has a new value (the client keeps the old one to show the change).</summary>
    public static PyValue AttributeChange(long characterId, long itemId, int attributeId, long fileTime, double? old, double now) => Py.Tuple(
        new PyStr("OnModuleAttributeChange"), InventoryWire.Id(characterId), InventoryWire.Id(itemId), new PyInt(attributeId), new PyLong(fileTime),
        Py.Number(now), old is { } value ? Py.Number(value) : PyNone.Instance, new PyLong(fileTime));

    /// <summary>The same for an item the client knows by a key of its own (a charge in a slot: the ship, the slot's flag and the type).</summary>
    public static PyValue AttributeChange(long characterId, PyValue itemKey, int attributeId, long fileTime, double? old, double now) => Py.Tuple(
        new PyStr("OnModuleAttributeChange"), InventoryWire.Id(characterId), itemKey, new PyInt(attributeId), new PyLong(fileTime),
        Py.Number(now), old is { } value ? Py.Number(value) : PyNone.Instance, new PyLong(fileTime));

    /// <summary>An effect on a module that started or stopped: who and what it is on, and whether it is running.</summary>
    public static PyValue ShipEffect(long characterId, long shipId, long moduleId, int effectId, long fileTime, bool started) => Py.Tuple(
        new PyStr("OnGodmaShipEffect"), InventoryWire.Id(moduleId), new PyInt(effectId), new PyLong(fileTime), new PyInt(started ? 1 : 0), new PyInt(started ? 1 : 0),
        Py.Tuple(InventoryWire.Id(moduleId), InventoryWire.Id(characterId), InventoryWire.Id(shipId), PyNone.Instance, PyNone.Instance, new PyList([]), new PyInt(effectId), PyNone.Instance),
        PyNone.Instance, new PyInt(-1), PyBool.False, PyNone.Instance);

    /// <summary>
    /// <c>OnItemChange</c>: the row of an item that changed and, as the reference sends it, only what differs from before: the place it left (3), the flag it left (4) and
    /// the size of the stack it was (9).
    /// </summary>
    public static void SendItemChange(ClientSession session, ItemChanged change, Func<Eve.Inventory.Item, ItemRow> row)
    {
        var differences = new List<KeyValuePair<PyValue, PyValue>>();
        if (change.After.LocationId != change.PreviousLocationId)
        {
            differences.Add(new(new PyInt(3), InventoryWire.Id(change.PreviousLocationId)));
        }
        if (change.After.Flag != change.PreviousFlag)
        {
            differences.Add(new(new PyInt(4), new PyInt(change.PreviousFlag)));
        }
        if (change.After.Quantity != change.PreviousQuantity)
        {
            differences.Add(new(new PyInt(9), InventoryWire.Id(change.PreviousQuantity)));
        }
        session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(row(change.After)), new PyDict(differences), PyNone.Instance);
    }

    /// <summary>The events of a change as the batch the client is sent: each with the time it happened.</summary>
    public static PyValue MultiEvent(long characterId, long shipId, IEnumerable<DogmaEvent> events, long fileTime) => new PyList([.. events.Select(e => Py.Tuple(
        e switch
        {
            AttributeChanged a => AttributeChange(characterId, a.ItemId, a.AttributeId, fileTime, a.Old, a.New),
            EffectChanged c => ShipEffect(characterId, shipId, c.ItemId, c.EffectId, fileTime, c.Started),
            _ => throw new InvalidOperationException($"an event dogma has no words for: {e}"),
        },
        new PyLong(fileTime)))]);

    /// <summary>Tells the session about a change: the dogma events first, then the items that moved.</summary>
    public static void Send(ClientSession session, long characterId, ShipChanged changed, Func<Eve.Inventory.Item, ItemRow> row, long fileTime)
    {
        if (changed.Events.Count > 0)
        {
            session.SendNotification("OnMultiEvent", "clientID", MultiEvent(characterId, changed.ShipId, changed.Events, fileTime));
        }
        foreach (var moved in changed.Items)
        {
            session.SendNotification(
                "OnItemsChanged", "charid",
                new PyList([InventoryWire.PackedRow(row(moved.After))]),
                new PyDict([new(new PyInt(3), InventoryWire.Id(moved.PreviousLocationId)), new(new PyInt(4), new PyInt(moved.PreviousFlag))]),
                PyNone.Instance);
        }
        foreach (var stack in changed.Stacks ?? [])
        {
            SendItemChange(session, stack, row);
        }
    }
}
