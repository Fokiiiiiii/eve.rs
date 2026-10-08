using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;

namespace Eve.Services.Dogma;

/// <summary>
/// The calls that put rounds in a module and take them out (<c>LoadAmmo</c>, <c>UnloadAmmo</c>). The ship's rules and what changed are <see cref="CharacterShips"/>'s; this
/// puts the calls to it and what the client is told in the order and the shapes the reference sends (the recording <c>oracle/fixtures/scenarios/ammo.json</c>).
/// </summary>
public sealed partial class DogmaIMService
{
    /// <summary>The virtual containers the client names a station's hangar by (10004) or a structure's (10014).</summary>
    private static bool IsHangarContainer(long id) => id is 10004 or 10014;

    private static IEnumerable<long> IdsOf(PyValue? value) => value switch
    {
        PyList list => list.Items.SelectMany(IdsOf),
        PyTuple tuple when tuple.Items.Count > 0 && tuple.Items[0] is PyInt or PyLong => [IdOf(tuple.Items[0])!.Value],
        PyTuple tuple => tuple.Items.SelectMany(IdsOf),
        _ when IdOf(value!) is { } id => [id],
        _ => [],
    };

    /// <summary>
    /// <c>LoadAmmo(shipID, moduleIDs, chargeItemIDs, ammoLocationID)</c>: each module takes rounds of the kind asked for from the stacks of the place named (the hangar when
    /// docked), up to what it holds. The answer is None; the client is told what changed.
    /// </summary>
    [Method("LoadAmmo")]
    public async ValueTask<PyValue> LoadAmmo(ServiceCall call)
    {
        if (BoundContextOf(call) is null || call.Session.CharacterId is not { } character || IdOf(call.Arg(0)) is not { } shipId)
        {
            return None;
        }
        var location = IdOf(call.Arg(3)) ?? shipId;
        if (call.Session.DockedLocationId is null && rounds is not null)
        {
            await LoadInSpaceAsync(call.Session, character, shipId, [.. IdsOf(call.Arg(1))], [.. IdsOf(call.Arg(2))], location, call.Cancellation);
            return None;
        }
        if (IsHangarContainer(location) && call.Session.DockedLocationId is { } station)
        {
            location = station;
        }
        var outcome = await ships.LoadAmmoAsync(character, shipId, [.. IdsOf(call.Arg(1))], [.. IdsOf(call.Arg(2))], location, call.Cancellation);
        TellAmmo(call.Session, character, outcome);
        return None;
    }

    /// <summary>
    /// <c>UnloadAmmo(shipID, moduleIDs, destination, quantity)</c>: what the modules hold goes to the place named (the hangar, or the ship's cargo when it is the ship; a tuple
    /// names the place and the flag), all of it or <c>quantity</c> rounds of each.
    /// </summary>
    [Method("UnloadAmmo")]
    public async ValueTask<PyValue> UnloadAmmo(ServiceCall call)
    {
        if (BoundContextOf(call) is null || call.Session.CharacterId is not { } character || IdOf(call.Arg(0)) is not { } shipId)
        {
            return None;
        }
        long location;
        int? flag = null;
        if (call.Arg(2) is PyTuple or PyList)
        {
            var parts = (call.Arg(2) is PyTuple t ? t.Items : ((PyList)call.Arg(2)).Items).Select(IdOf).ToList();
            location = parts.Count > 0 ? parts[0] ?? shipId : shipId;
            flag = parts.Count > 2 && parts[2] is { } given and > 0 ? (int)given : null;
        }
        else
        {
            location = IdOf(call.Arg(2)) ?? shipId;
        }
        if (IsHangarContainer(location) && call.Session.DockedLocationId is { } station)
        {
            location = station;
        }
        var quantity = IdOf(call.Arg(3)) is { } asked and > 0 ? asked : (long?)null;
        if (call.Session.DockedLocationId is null && rounds is not null)
        {
            await UnloadInSpaceAsync(call.Session, character, shipId, [.. IdsOf(call.Arg(1))], location, flag, quantity, call.Cancellation);
            return None;
        }
        var outcome = await ships.UnloadAmmoAsync(character, shipId, [.. IdsOf(call.Arg(1))], location, flag, quantity, call.Cancellation);
        TellAmmo(call.Session, character, outcome);
        return None;
    }

    private static PyValue ChargeKey(long shipId, int flag, int typeId) => Py.Tuple(InventoryWire.Id(shipId), new PyInt(flag), new PyInt(typeId));

    private PyValue ItemRow(Item item) => InventoryWire.PackedRow(DogmaInfo.RowOf(item, statics.Type(item.TypeId)));

    /// <summary>What the client is told of a change of ammunition, event by event in the order the ship worked it out.</summary>
    private void TellAmmo(ClientSession session, long characterId, ShipOutcome outcome)
    {
        if (outcome is Reloaded reloaded)
        {
            outcome = reloaded.Changed;
        }
        if (outcome is not AmmoChanged changed)
        {
            return;
        }
        var fileTime = Py.FileTimeFromUnixMs(nowUnixMs());
        // a ship in space is told what moved in its hold as a batch for the ship (the reference's OnItemsChanged, with the hold named); one in a station as an item that changed
        var inSpace = session.GetInt("stationid") is null;
        var cargo = Py.Tuple(new PyStr("Ship"), InventoryWire.Id(changed.ShipId), new PyStr("ShipCargo"));
        foreach (var e in changed.Events)
        {
            switch (e)
            {
                case ItemChanged item when inSpace:
                    ShipNotifications.SendItemsChanged(session, item, i => DogmaInfo.RowOf(i, statics.Type(i.TypeId)), cargo, "charid");
                    break;
                case ItemChanged item:
                    ShipNotifications.SendItemChange(session, item, i => DogmaInfo.RowOf(i, statics.Type(i.TypeId)));
                    break;
                case ChargeBeingLoaded loading:
                    session.SendNotification("OnChargeBeingLoadedToModule", "charid", new PyList([.. loading.ModuleIds.Select(InventoryWire.Id)]), new PyInt(loading.ChargeTypeId), new PyInt(loading.ReloadMs));
                    break;
                case ChargeQuantityChanged quantity:
                    session.SendNotification(
                        "OnModuleAttributeChanges", "clientID",
                        new PyList([ShipNotifications.AttributeChange(characterId, ChargeKey(quantity.ShipId, quantity.Flag, quantity.TypeId), ShipSnapshot.QuantityCharge, fileTime, quantity.Old, quantity.New)]));
                    break;
                case ChargePrimed primed:
                    session.SendNotification(
                        "OnGodmaPrimeItem", "clientID", InventoryWire.Id(primed.ShipId),
                        Py.KeyVal(
                            ("itemID", ChargeKey(primed.ShipId, primed.Flag, primed.TypeId)),
                            ("invItem", None),
                            ("activeEffects", new PyDict([])),
                            ("attributes", DogmaWire.Attributes(primed.Attributes)),
                            ("description", new PyStr("charge")),
                            ("time", new PyLong(fileTime)),
                            ("wallclockTime", new PyLong(fileTime))));
                    break;
                case ModuleAttributesChanged attributes:
                    session.SendNotification(
                        "OnModuleAttributeChanges", "clientID",
                        new PyList([.. attributes.Changes.Select(c => ShipNotifications.AttributeChange(characterId, c.ItemId, c.AttributeId, fileTime, c.Old, c.New))]));
                    break;
                case FittingRefreshed refreshed:
                    foreach (var module in refreshed.Modules)
                    {
                        session.SendNotification("OnItemChange", "clientID", ItemRow(module), new PyDict([]), None);
                    }
                    break;
            }
        }
    }
}
