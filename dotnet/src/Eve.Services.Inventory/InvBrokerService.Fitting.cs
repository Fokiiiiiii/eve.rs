using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Ships;

namespace Eve.Services.Inventory;

/// <summary>
/// The fitting of a saved fitting onto a docked ship (EveJS <c>invBroker/fittingOperations.js</c>, <c>Handle_FitFitting</c>): the modules and rigs by slot,
/// then the cargo, drone, ice, implant, fighter and charge legs. Each piece is one move through the move a single drag makes
/// (<see cref="CharacterShips.MoveAsync"/>), so the rules, the notifications and the persistence are the same. A piece that cannot go is the reply.
/// Not ported (DEVIATIONS "Track S1: fitting a docked ship"): the re-online pass, the space-loot and structure access checks, and the ship-service checks.
/// </summary>
public sealed partial class InvBrokerService
{
    private const long IceProductGroup = 423;   // SDE group "Ice Product" (EveJS fuelBayInventory.js ICE_PRODUCT_GROUP_ID)
    private const int ImplantCategory = 20;     // SDE category "Implant"

    /// <summary>The apply order: subsystems first, then rigs, then the rest by flag (EveJS <c>_rankFitFittingSlotApplyOrder</c>, fittingOperations.js:196).</summary>
    private static int FitRank(long flag) => Slots.FamilyOfFlag((int)flag) switch
    {
        SlotFamily.Subsystem => 0,
        SlotFamily.Rig => 1,
        _ => 2,
    };

    /// <summary>
    /// <c>FitFitting(shipID, shipTypeID, itemsByType, sourceLocationID, fittingKeyVal, cargoByType, fitRigs)</c> on the bound ship inventory. Answers the
    /// (typeID, quantity) pairs of what did not fit, sorted by type (EveJS <c>buildList</c> of <c>missingByType</c>).
    /// </summary>
    [Method("FitFitting")]
    public async ValueTask<PyValue> FitFitting(ServiceCall call)
    {
        if (ContextOf(call) is not { Kind: Kind.Ship } context || call.Session.CharacterId is not { } character)
        {
            return new PyList([]);
        }
        if (StationOf(call.Session) is not { } station)
        {
            // the fitting of a ship in space is not served here (DEVIATIONS "Track S1: fitting a docked ship")
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("A ship can be fitted only while it is docked.")));
        }
        if (!await IsDockedShipContextAsync(call.Session, character, context.ItemId, call.Cancellation))
        {
            return new PyList([]);
        }
        var ship = await items.GetAsync(context.ItemId, call.Cancellation);
        if (ship is null || IdOf(call.Arg(1)) is not { } shipType || shipType != ship.TypeId)
        {
            throw MachoErrors.ThrowUserError("ShipTypeInFittingNotSameAsShip");
        }
        var source = IdOf(call.Arg(3)) ?? station;
        var fitRigs = call.Arg(6) is not PyBool { Value: false };
        var ct = call.Cancellation;

        // the hangar items of each type the fitting needs, in the order the client gave them
        var candidates = new Dictionary<long, List<long>>();
        foreach (var (key, value) in Entries(call.Arg(2)))
        {
            if (IdOf(key) is not { } typeId || typeId <= 0)
            {
                continue;
            }
            var list = candidates.TryGetValue(typeId, out var existing) ? existing : candidates[typeId] = [];
            foreach (var itemId in value switch { PyTuple t => t.Items, PyList l => l.Items, _ => [value] })
            {
                if (IdOf(itemId) is { } id && id > 0 && !list.Contains(id))
                {
                    list.Add(id);
                }
            }
        }

        var keyVal = Entries(call.Arg(4)).ToList();
        var modules = Pairs(Named(keyVal, "modulesByFlag"));
        modules = [.. modules.OrderBy(m => FitRank(m.Key)).ThenBy(m => m.Key)];
        var charges = Pairs(Named(keyVal, "chargesByType"));
        var drones = Pairs(Named(keyVal, "dronesByType"));
        var fighters = Pairs(Named(keyVal, "fightersByTypeID"));
        var ice = Pairs(Named(keyVal, "iceByType"));
        var implants = Pairs(Named(keyVal, "implantsByTypeID"));
        var cargo = Pairs(call.Arg(5));

        var missing = new SortedDictionary<long, long>();
        void Missing(long typeId, long quantity)
        {
            if (quantity > 0)
            {
                missing[typeId] = missing.GetValueOrDefault(typeId) + quantity;
            }
        }
        async Task<long> PlaceAsync(long itemId, long typeId, long flag, long want, Func<ItemType, bool>? allowed)
        {
            if (await items.GetAsync(itemId, ct) is not { } item || item.TypeId != typeId || item.LocationId != source || item.OwnerId != character)
            {
                return 0;
            }
            if (allowed is not null && (await types.GetAsync((int)typeId, ct) is not { } itemType || !allowed(itemType)))
            {
                return 0;
            }
            var quantity = item.Singleton ? 1 : Math.Min(want, item.Quantity);
            var outcome = await ships.MoveAsync(character, itemId, source, context.ItemId, (int)flag, item.Singleton ? null : quantity, ct);
            if (outcome is not ShipChanged changed)
            {
                return 0;
            }
            ShipNotifications.Send(call.Session, character, changed, DogmaRow, Py.FileTimeFromUnixMs(nowUnixMs()));
            return quantity;
        }

        // the modules and rigs: one of each, into the slot it names (EveJS module loop)
        var fitted = (await items.InLocationAsync(ship.Id, character, ct)).Select(i => ((long)i.Flag, (long)i.TypeId)).ToHashSet();
        foreach (var (flag, typeId) in modules)
        {
            if (!fitRigs && Slots.FamilyOfFlag((int)flag) == SlotFamily.Rig)
            {
                continue;
            }
            if (fitted.Contains((flag, typeId)))
            {
                continue;
            }
            var placed = false;
            if (candidates.TryGetValue(typeId, out var list))
            {
                foreach (var itemId in list.ToList())
                {
                    if (await PlaceAsync(itemId, typeId, flag, 1, null) == 1)
                    {
                        list.Remove(itemId);
                        fitted.Add((flag, typeId));
                        placed = true;
                        break;
                    }
                }
            }
            if (!placed)
            {
                Missing(typeId, 1);
            }
        }

        // the bay legs, in the reference's order: cargo (rigs that were not fitted), drones, ice, implants, fighters, charges
        async Task LegAsync(IEnumerable<(long TypeId, long Quantity)> wanted, long flag, Func<ItemType, bool>? allowed)
        {
            foreach (var (typeId, quantity) in wanted)
            {
                var existing = (await items.InLocationAsync(ship.Id, character, ct))
                    .Where(i => i.Flag == flag && i.TypeId == typeId).Sum(i => (long)i.Quantity);
                var remaining = Math.Max(0, quantity - existing);
                if (candidates.TryGetValue(typeId, out var list))
                {
                    foreach (var itemId in list.ToList())
                    {
                        if (remaining <= 0)
                        {
                            break;
                        }
                        remaining -= await PlaceAsync(itemId, typeId, flag, remaining, allowed);
                    }
                }
                Missing(typeId, remaining);
            }
        }

        await LegAsync(cargo, CharacterShips.CargoFlag, null);
        await LegAsync(drones, CharacterShips.DroneBayFlag, null);
        await LegAsync(ice, CharacterShips.CargoFlag, t => t.GroupId == IceProductGroup);
        await LegAsync(implants, CharacterShips.CargoFlag, t => t.CategoryId == ImplantCategory);
        await LegAsync(fighters, CharacterShips.FighterBayFlag, null);
        await LegAsync(charges, CharacterShips.CargoFlag, null);

        return new PyList([.. missing.Select(pair => (PyValue)Py.Tuple(new PyInt(pair.Key), new PyInt(pair.Value)))]);
    }

    /// <summary>The entries of a dict, or of a KeyVal whose state is one (the client's <c>KeyVal</c> arguments).</summary>
    private static IEnumerable<KeyValuePair<PyValue, PyValue>> Entries(PyValue value) => value switch
    {
        PyObject { Args: PyDict dict } => dict.Entries,
        PyDict dict => dict.Entries,
        _ => [],
    };

    /// <summary>The value of a named entry of a KeyVal, or an empty dict when there is none.</summary>
    private static PyValue Named(List<KeyValuePair<PyValue, PyValue>> keyVal, string name) =>
        keyVal.FirstOrDefault(e => JsSemantics.StringValue(e.Key) == name).Value ?? PyNone.Instance;

    /// <summary>The integer pairs of a dict (typeID or flag to a number), in the order given.</summary>
    private static List<(long Key, long Value)> Pairs(PyValue value) =>
        [.. Entries(value).Select(e => (IdOf(e.Key), IdOf(e.Value))).Where(p => p.Item1 is > 0 && p.Item2 is > 0).Select(p => (p.Item1!.Value, p.Item2!.Value))];
}
