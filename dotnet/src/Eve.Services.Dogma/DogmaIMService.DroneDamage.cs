using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Ships;

namespace Eve.Services.Dogma;

public sealed partial class DogmaIMService
{
    [Method("GetLayerDamageValuesByItems")]
    public async ValueTask<PyValue> GetLayerDamageValuesByItems(ServiceCall call)
    {
        if (BoundContextOf(call) is null || call.Session.CharacterId is not { } character
            || await ReadSnapshotAsync(call.Session, call.Cancellation) is not { } snapshot)
            return None;

        // The client passes a Python set, represented on the wire by set.__reduce__.
        var argument = call.Arg(0);
        if (argument is PyObjectEx { Kind: ObjectExKind.Ex1, Header: PyTuple { Items.Count: 2 } header }
            && JsSemantics.StringValue(header.Items[0]) == "__builtin__.set"
            && header.Items[1] is PyTuple { Items.Count: 1 } args)
            argument = args.Items[0];
        var ids = argument switch
        {
            PyList list => list.Items.Select(IdOf).OfType<long>().ToArray(),
            PyTuple tuple => tuple.Items.Select(IdOf).OfType<long>().ToArray(),
            _ => [],
        };
        var drones = await ships.DroneBayConditionsAsync(character, snapshot.Ship.Id, ids, call.Cancellation);
        var now = Py.FileTimeFromUnixMs(nowUnixMs());
        return new PyDict(drones.Select(drone =>
        {
            var attributes = drone.Attributes;
            var condition = drone.Condition;
            var shield = Py.Tuple(new PyReal(condition.ShieldCharge), new PyReal(attributes.GetValueOrDefault(479) / 5.0), new PyLong(now));
            return new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(drone.Drone.Id), Py.KeyVal(
                ("shieldInfo", shield),
                ("armorDamage", new PyReal(condition.ArmorDamage * attributes.GetValueOrDefault(265))),
                ("hullDamage", new PyReal(condition.Damage * attributes.GetValueOrDefault(9)))));
        }).ToArray());
    }
}
