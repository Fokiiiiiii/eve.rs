using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Dogma;
using Eve.Ships;

namespace Eve.Services.Inventory;

/// <summary>
/// (<see cref="CharacterShips.MoveAsync"/>), so the rules, the notifications and the persistence are the same. A piece that cannot go is the reply.
/// Not ported (DEVIATIONS "Track S1: fitting a docked ship"): the space-loot and structure access checks, the ship-service checks, and the in-space fitting services.
/// </summary>
public sealed partial class InvBrokerService
{
    private const int MaxFittingPayloadItems = 500;
    private const long IceProductGroup = 423;
    private const int ImplantCategory = 20;     // SDE category "Implant"

    private static int FitRank(long flag) => Slots.FamilyOfFlag((int)flag) switch
    {
        SlotFamily.Subsystem => 0,
        SlotFamily.Rig => 1,
        _ => 2,
    };

    /// <summary>
        /// </summary>
    [Method("FitFitting")]
    public async ValueTask<PyValue> FitFitting(ServiceCall call)
    {
        RequireMutableInventory(call);
        var itemEntries = Entries(call.Arg(2));
        var keyVal = Entries(call.Arg(4));
        var rawItems = 0;
        void AddRawItems(int count)
        {
            if (count > MaxFittingPayloadItems || rawItems > MaxFittingPayloadItems - count)
                throw MachoErrors.ThrowUserError("INVENTORY_BULK_LIMIT_EXCEEDED");
            rawItems += count;
        }

        AddRawItems(itemEntries.Count);
        foreach (var entry in itemEntries)
            AddRawItems(SequenceItems(entry.Value).Count);
        AddRawItems(keyVal.Count);
        foreach (var key in new[] { "modulesByFlag", "chargesByType", "dronesByType", "fightersByTypeID", "iceByType", "implantsByTypeID" })
            AddRawItems(Entries(Named(keyVal, key)).Count);
        AddRawItems(Entries(call.Arg(5)).Count);

        if (ContextOf(call) is not { Kind: Kind.Ship } context || call.Session.CharacterId is not { } character)
        {
            return new PyList([]);
        }
        if (StationOf(call.Session) is not { } station)
        {
            // shortcut: no in-space fitting service (Mobile Depot, maintenance-bay ship, Ship Maintenance Array) is modelled, so this is always the not-found notify (EveJS IN_SPACE_FITTING_SERVICE_NOT_FOUND)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You must be within 3000 meters of an active Mobile Depot you own, or within 5000 meters of an accessible maintenance-bay ship or array, to refit in space.")));
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
        var outcomes = new List<ShipChanged>();
        var placedModules = new List<(long ItemId, long Flag, long TypeId)>();

        // the hangar items of each type the fitting needs, in the order the client gave them
        var candidates = new Dictionary<long, List<long>>();
        foreach (var (key, value) in itemEntries)
        {
            if (IdOf(key) is not { } typeId || typeId <= 0)
            {
                continue;
            }
            var list = candidates.TryGetValue(typeId, out var existing) ? existing : candidates[typeId] = [];
            foreach (var itemId in SequenceItems(value))
            {
                if (IdOf(itemId) is { } id && id > 0 && !list.Contains(id))
                {
                    list.Add(id);
                }
            }
        }

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
            outcomes.Add(changed);
            return quantity;
        }

        var fittedItems = (await items.InLocationAsync(ship.Id, character, ct)).ToDictionary(i => i.Id);
        var fitted = fittedItems.Values.Select(i => ((long)i.Flag, (long)i.TypeId)).ToHashSet();
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

            // A saved fit replaces an occupied high, mid, or low slot. Move the old item
            // through the same ship mutation path so dogma and item notifications stay in sync.
            if (Slots.FamilyOfFlag((int)flag) is SlotFamily.High or SlotFamily.Medium or SlotFamily.Low)
            {
                foreach (var occupant in fittedItems.Values.Where(i => i.Flag == flag).ToList())
                {
                    if (await ships.MoveAsync(character, occupant.Id, ship.Id, station, ItemFlags.Hangar, null, ct) is ShipChanged removed)
                    {
                        outcomes.Add(removed);
                        fittedItems.Remove(occupant.Id);
                        fitted.Remove((flag, occupant.TypeId));
                    }
                }
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
                        placedModules.Add((itemId, flag, typeId));
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

        foreach (var (itemId, flag, typeId) in placedModules.OrderBy(m => m.Flag))
        {
            if (Slots.FamilyOfFlag((int)flag) is SlotFamily.Rig or SlotFamily.Subsystem)
            {
                continue;
            }
            await statics.EnsureTypesAsync([(int)typeId], ct);
            if (statics.Type((int)typeId)?.EffectIds.Contains(BuiltInEffects.Online) != true)
            {
                continue;
            }
            if (await ships.SetOnlineAsync(character, context.ItemId, itemId, true, ct) is ShipChanged switched)
            {
                outcomes.Add(switched);
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

        ShipNotifications.SendBatch(call.Session, character, outcomes, DogmaRow, Py.FileTimeFromUnixMs(nowUnixMs()));
        return new PyList([.. missing.Select(pair => (PyValue)Py.Tuple(new PyInt(pair.Key), new PyInt(pair.Value)))]);
    }

    /// <summary>The entries of a dict, or of a KeyVal whose state is one (the client's <c>KeyVal</c> arguments).</summary>
    private static IReadOnlyList<KeyValuePair<PyValue, PyValue>> Entries(PyValue value) => value switch
    {
        PyObject { Args: PyDict dict } => dict.Entries,
        PyDict dict => dict.Entries,
        _ => [],
    };

    private static IReadOnlyList<PyValue> SequenceItems(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        PyObjectEx
        {
            Kind: ObjectExKind.Ex1,
            Header: PyTuple { Items: [PyToken { Value: "__builtin__.set" }, PyTuple { Items: [PyList setItems] }] },
            List.Count: 0,
            Dict.Count: 0,
        } => setItems.Items,
        _ => [value],
    };

    /// <summary>The value of a named entry of a KeyVal, or an empty dict when there is none.</summary>
    private static PyValue Named(IReadOnlyList<KeyValuePair<PyValue, PyValue>> keyVal, string name) =>
        keyVal.FirstOrDefault(e => JsSemantics.StringValue(e.Key) == name).Value ?? PyNone.Instance;

    /// <summary>The integer pairs of a dict (typeID or flag to a number), in the order given.</summary>
    private static List<(long Key, long Value)> Pairs(PyValue value) =>
        [.. Entries(value).Select(e => (IdOf(e.Key), IdOf(e.Value))).Where(p => p.Item1 is > 0 && p.Item2 is > 0).Select(p => (p.Item1!.Value, p.Item2!.Value))];
}
