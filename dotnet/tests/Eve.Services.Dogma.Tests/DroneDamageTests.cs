using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;

namespace Eve.Services.Dogma.Tests;

public class DroneDamageTests
{
    [PgFact]
    public async Task Drone_bay_damage_uses_stored_condition_and_accepts_the_clients_set()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync();
        var session = Rig.Session(pilot.CharacterId);
        var oid = await rig.BindAsync(session);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var damaged = await rig.Items.CreateAsync(connection, transaction, new NewItem(2454, pilot.CharacterId, pilot.ShipId, CharacterShips.DroneBayFlag, 1, true));
        var intact = await rig.Items.CreateAsync(connection, transaction, new NewItem(2454, pilot.CharacterId, pilot.ShipId, CharacterShips.DroneBayFlag, 1, true));
        var unknown = await rig.Items.CreateAsync(connection, transaction, new NewItem(2454, pilot.CharacterId, pilot.ShipId, CharacterShips.DroneBayFlag, 1, true));
        var outside = await rig.Items.CreateAsync(connection, transaction, new NewItem(2454, pilot.CharacterId, Rig.Station, ItemFlags.Hangar, 1, true));
        await transaction.CommitAsync();
        await rig.Modules.SetModuleConditionsAsync(new Dictionary<long, ModuleCondition> { [damaged] = new(0.25, 0.5, 0.4, false), [intact] = ModuleCondition.Mended });

        var request = MarshalCodec.Decode(MarshalCodec.Encode(PyValue.PythonSet([new PyLong(damaged), new PyLong(intact), new PyLong(unknown), new PyLong(outside)])));
        var result = Assert.IsType<PyDict>(await rig.CallAsync(session, "GetLayerDamageValuesByItems", oid, request));
        Assert.Equal(2, result.Entries.Count);
        var drone = (await rig.Items.GetAsync(damaged))!;
        var attributes = await rig.Ships.DroneAttributesAsync(drone);
        PyValue Field(PyValue value, string name) => ((PyDict)((PyObject)value).Args).Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;
        var damagedValue = result.Entries.Single(entry => entry.Key == new PyLong(damaged) || entry.Key == new PyInt(damaged)).Value;
        var shield = Assert.IsType<PyTuple>(Field(damagedValue, "shieldInfo"));
        Assert.Equal(new PyReal(0.4), shield.Items[0]);
        Assert.Equal(new PyReal(attributes.GetValueOrDefault(479) / 5), shield.Items[1]);
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(Rig.ClockMs)), shield.Items[2]);
        Assert.Equal(new PyReal(attributes.GetValueOrDefault(265) * 0.5), Field(damagedValue, "armorDamage"));
        Assert.Equal(new PyReal(attributes.GetValueOrDefault(9) * 0.25), Field(damagedValue, "hullDamage"));
        var intactValue = result.Entries.Single(entry => entry.Key == new PyLong(intact) || entry.Key == new PyInt(intact)).Value;
        Assert.Equal(new PyReal(1), Assert.IsType<PyTuple>(Field(intactValue, "shieldInfo")).Items[0]);
        Assert.Equal(new PyReal(0), Field(intactValue, "armorDamage"));
        Assert.Equal(new PyReal(0), Field(intactValue, "hullDamage"));
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "GetLayerDamageValuesByItems", null, request));
    }
}
