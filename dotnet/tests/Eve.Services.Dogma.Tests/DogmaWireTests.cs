using Eve.Dogma;
using Eve.Marshal;

namespace Eve.Services.Dogma.Tests;

public class DogmaWireTests
{
    [Theory]
    [InlineData(9, 175.0, "int")]      // a whole number is an integer, as JavaScript's marshal writes it
    [InlineData(9, 201.25, "real")]
    [InlineData(73, 5000.0, "real")]   // durations, rates of fire, recharge and capacitor size are real even when whole
    [InlineData(51, 1692.8, "real")]
    [InlineData(55, 65000.0, "real")]
    [InlineData(482, 130.0, "real")]
    [InlineData(73, -1.0, "int")]      // a negative one is cut to an integer
    [InlineData(3, 0.0, "int")]
    public void An_attribute_value_is_typed_as_the_client_reads_it(int attribute, double value, string kind)
    {
        var wire = DogmaWire.AttributeValue(attribute, value);
        Assert.Equal(kind, wire is PyReal ? "real" : wire is PyInt ? "int" : wire.GetType().Name);
    }

    [Fact]
    public void Attributes_go_out_in_the_order_of_their_ids()
    {
        var wire = (PyDict)DogmaWire.Attributes(new Dictionary<int, double> { [55] = 1.5, [9] = 2, [3] = 0 });
        Assert.Equal([3L, 9, 55], wire.Entries.Select(e => ((PyInt)e.Key).Value));
    }

    [Fact]
    public void An_instance_row_survives_the_marshal_round_trip_with_its_column_types()
    {
        var row = DogmaWire.InstanceRow(9_988_400_000_710, online: true, damage: 0.25, charge: 1, skillPoints: 384_036, armorDamage: 0.5, shieldCharge: 0.75, incapacitated: true);
        var decoded = Assert.IsType<PyPackedRow>(MarshalCodec.Decode(MarshalCodec.Encode(row)));
        Assert.Equal(
            ["instanceID", "online", "damage", "charge", "skillPoints", "armorDamage", "shieldCharge", "incapacitated"],
            decoded.Descriptor.Columns.Select(c => c.Name));
        Assert.Equal(
            [new PyInt(9_988_400_000_710), PyBool.True, new PyReal(0.25), new PyReal(1), new PyInt(384_036), new PyReal(0.5), new PyReal(0.75), PyBool.True], // 64-bit values decode as integers
            decoded.Values);
    }

    [Fact]
    public void The_heat_of_the_slots_is_told_as_nothing_built_up()
    {
        var heat = (PyDict)DogmaWire.HeatState(134_351_321_725_850_000);
        Assert.Equal([1176L, 1177, 1175], heat.Entries.Select(e => ((PyInt)e.Key).Value));
        Assert.Equal(
            Py.Tuple(new PyInt(0), new PyInt(100), new PyInt(0), new PyInt(1), new PyReal(0.01), new PyLong(134_351_321_725_850_000)),
            heat.Entries[0].Value);
    }

    [Fact]
    public void A_running_effect_names_the_module_the_character_the_ship_and_when_it_started()
    {
        var effect = (PyTuple)DogmaWire.RunningEffect(9_988_400_000_711, 140_000_154, 9_988_400_000_710, 16, 134_351_321_725_460_000);
        Assert.Equal(
            [new PyLong(9_988_400_000_711), new PyInt(140_000_154), new PyLong(9_988_400_000_710), PyNone.Instance, PyNone.Instance, Py.Tuple(), new PyInt(16), new PyLong(134_351_321_725_460_000), new PyInt(-1), new PyInt(1)],
            effect.Items);
    }

    [Fact]
    public void A_brain_effect_is_an_object_the_client_builds_from_its_arguments_and_its_state()
    {
        var effect = new BrainEffect(3412, "ORS", BrainDomain.Character, 1371, 6, [3412], 15);
        var wire = Assert.IsType<PyObjectEx>(DogmaWire.BrainEffect(effect, 140_000_154));
        var header = (PyTuple)wire.Header;
        Assert.Equal(new PyToken("eve.common.script.dogma.effect.BrainEffect"), header.Items[0]);
        var arguments = (PyTuple)header.Items[1];
        Assert.Equal((new PyInt(140_000_154), new PyStr("ORS"), new PyInt(1371), new PyInt(6), Py.Tuple(new PyInt(3412))), (arguments.Items[1], arguments.Items[2], arguments.Items[3], arguments.Items[4], arguments.Items[5]));
        var state = ((PyDict)header.Items[2]).Entries;
        Assert.Equal(
            ["fromAttrib", "value", "toItemID", "modifierType", "toAttribID", "operation", "extras", "skills"],
            state.Select(e => ((PyStr)e.Key).Value));
        Assert.Equal(new PyList([new PyInt(3412)]), state[7].Value); // the skills are a list, the extras a tuple
        Assert.Equal(Py.Tuple(new PyInt(3412)), state[6].Value);
        Assert.Equal(new PyInt(15), state[1].Value);
        var decoded = Assert.IsType<PyObjectEx>(MarshalCodec.Decode(MarshalCodec.Encode(wire))); // it survives the round trip
        Assert.Equal(3, ((PyTuple)decoded.Header).Items.Count);
        Assert.Equal(8, ((PyDict)((PyTuple)decoded.Header).Items[2]).Entries.Count);
    }

    [Fact]
    public void A_brain_is_a_version_and_the_effects_on_the_character_the_ship_and_the_structure()
    {
        var brain = new Brain(
            [new BrainEffect(1, "M", BrainDomain.Character, 2, 0, [], 3)],
            [new BrainEffect(4, "M", BrainDomain.Ship, 5, 6, [], 7), new BrainEffect(8, "M", BrainDomain.Ship, 9, 6, [], 1.5)],
            []);
        var wire = (PyTuple)DogmaWire.Brain(brain, 100, 200, 300);
        Assert.Equal((new PyInt(0), 1, 2, 0), (wire.Items[0], ((PyTuple)wire.Items[1]).Items.Count, ((PyTuple)wire.Items[2]).Items.Count, ((PyTuple)wire.Items[3]).Items.Count));
        var onShip = (PyObjectEx)((PyTuple)wire.Items[2]).Items[0];
        Assert.Equal(new PyInt(200), ((PyTuple)((PyTuple)onShip.Header).Items[1]).Items[1]); // aimed at the ship
    }
}
