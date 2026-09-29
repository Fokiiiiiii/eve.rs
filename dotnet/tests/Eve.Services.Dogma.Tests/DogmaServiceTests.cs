using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Dogma.Tests;

public class DogmaServiceTests
{
    private static PyValue Field(PyValue keyVal, string name) =>
        ((PyDict)((PyObject)keyVal).Args).Entries.Single(e => JsSemantics.StringValue(e.Key) == name).Value;

    private static IEnumerable<string> Fields(PyValue keyVal) => ((PyDict)((PyObject)keyVal).Args).Entries.Select(e => JsSemantics.StringValue(e.Key)!);

    private static long Id(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, _ => throw new InvalidOperationException(value.ToString()) };

    private static PyDict Dict(PyValue value) => (PyDict)value;

    private static readonly PyValue[] Everything = [PyBool.True, PyBool.True, PyNone.Instance];

    private static async Task<(Rig Rig, Created Character, ClientSession Session, string Oid)> BoundAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = Rig.Session(character.CharacterId);
        return (rig, character, session, await rig.BindAsync(session));
    }

    [PgFact]
    public async Task Binding_hands_out_an_object_and_makes_the_nested_call_whose_answer_comes_with_it()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = Rig.Session(character.CharacterId);

        var answer = (PyTuple)await rig.CallAsync(session, "MachoBindObject", null,
            Py.Tuple(new PyInt(Rig.Station), new PyInt(15)), Py.Tuple(new PyStr("GetAllInfo"), Py.Tuple(PyBool.True, PyBool.True, PyNone.Instance), PyNone.Instance));

        Assert.Equal(2, answer.Items.Count);
        var bound = Assert.IsType<PySubStruct>(answer.Items[0]);
        var handout = (PyTuple)((PySubStream)bound.Value).Decoded!;
        Assert.StartsWith("N=65450:", JsSemantics.StringValue(handout.Items[0]));
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(Rig.ClockMs)), handout.Items[1]);
        Assert.Equal("util.KeyVal", JsSemantics.StringValue(((PyObject)answer.Items[1]).Name));
    }

    [PgFact]
    public async Task Only_the_station_the_character_is_docked_at_can_be_bound()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = Rig.Session(character.CharacterId);
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "MachoBindObject", null, Py.Tuple(new PyInt(60000610), new PyInt(15)), PyNone.Instance));
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "MachoBindObject", null, new PyStr("x"), PyNone.Instance));
        Assert.Equal(PyNone.Instance, await rig.CallAsync(Rig.Session(character.CharacterId, station: null), "MachoBindObject", null, Py.Tuple(new PyInt(Rig.Station), new PyInt(15)), PyNone.Instance));
    }

    [PgFact]
    public async Task A_bound_object_answers_only_the_session_that_opened_it_and_only_until_it_is_released_or_the_connection_ends()
    {
        var (rig, character, mine, oid) = await BoundAsync();
        await using var _ = rig;
        var theirs = Rig.Session(character.CharacterId, id: 2);
        Assert.Equal(PyNone.Instance, await rig.CallAsync(theirs, "GetAllInfo", oid, Everything));
        Assert.Equal(PyNone.Instance, await rig.CallAsync(mine, "GetAllInfo", "N=65450:99999", Everything)); // never handed out
        Assert.Equal(PyNone.Instance, await rig.CallAsync(mine, "GetAllInfo", null, Everything)); // not bound at all
        Assert.IsType<PyObject>(await rig.CallAsync(mine, "GetAllInfo", oid, Everything));

        rig.Service.OnBoundObjectReleased(oid, mine);
        Assert.Equal(PyNone.Instance, await rig.CallAsync(mine, "GetAllInfo", oid, Everything));

        var second = await rig.BindAsync(mine);
        Assert.IsType<PyObject>(await rig.CallAsync(mine, "GetAllInfo", second, Everything));
        mine.Close();
        Assert.Equal(PyNone.Instance, await rig.CallAsync(mine, "GetAllInfo", second, Everything));
    }

    [PgFact]
    public async Task A_session_without_a_character_or_a_character_without_a_ship_gets_none()
    {
        await using var rig = await Rig.CreateAsync();
        var anonymous = Rig.Session(null);
        var oid = await rig.BindAsync(anonymous);
        Assert.Equal(PyNone.Instance, await rig.CallAsync(anonymous, "GetAllInfo", oid, Everything));

        var stranger = Rig.Session(140_999_999);
        var strangerOid = await rig.BindAsync(stranger);
        Assert.Equal(PyNone.Instance, await rig.CallAsync(stranger, "GetAllInfo", strangerOid, Everything));
    }

    [PgFact]
    public async Task GetAllInfo_tells_the_ship_its_modules_and_the_character()
    {
        var (rig, character, session, oid) = await BoundAsync();
        await using var _ = rig;
        var info = await rig.CallAsync(session, "GetAllInfo", oid, Everything);

        Assert.Equal(
            ["systemWideEffectsOnShip", "shipModifiedCharAttribs", "structureInfo", "locationInfo", "shipInfo", "activeShipID", "shipState", "charInfo"], Fields(info));
        Assert.Equal(character.ShipId, Id(Field(info, "activeShipID")));

        // the ship and its three modules, in the order of the slots
        var shipInfo = Dict(Field(info, "shipInfo"));
        Assert.Equal([character.ShipId, character.ShipId + 1, character.ShipId + 2, character.ShipId + 3], shipInfo.Entries.Select(e => Id(e.Key)));
        var ship = shipInfo.Entries[0].Value;
        Assert.Equal(["itemID", "invItem", "activeEffects", "time", "attributes", "wallclockTime"], Fields(ship));
        Assert.Empty(Dict(Field(ship, "activeEffects")).Entries);
        var shipAttributes = Dict(Field(ship, "attributes"));
        Assert.Equal(shipAttributes.Entries.Select(e => Id(e.Key)).Order(), shipAttributes.Entries.Select(e => Id(e.Key))); // in the order of the ids
        Assert.Equal(new PyReal(201.25), shipAttributes.Entries.Single(e => Id(e.Key) == 9).Value); // hit points with the skill that raises them

        foreach (var (module, expectedType, expectedFlag) in new[] { (1, 21857, 19), (2, 3638, 27), (3, 3651, 28) })
        {
            var entry = shipInfo.Entries[module].Value;
            var row = (PyPackedRow)Field(entry, "invItem");
            Assert.Equal((new PyInt(expectedType), new PyInt(expectedFlag), PyNone.Instance), (row.Values[1], row.Values[4], row.Values[8]));
            var running = Dict(Field(entry, "activeEffects"));
            Assert.Equal([16L], running.Entries.Select(e => Id(e.Key)));
            var effect = (PyTuple)running.Entries[0].Value;
            Assert.Equal(10, effect.Items.Count);
            Assert.Equal((character.ShipId + module, character.CharacterId, character.ShipId, new PyInt(16)), (Id(effect.Items[0]), Id(effect.Items[1]), Id(effect.Items[2]), effect.Items[6]));
        }

        // the character: two lists of attributes, and the brain
        var charInfo = (PyTuple)Field(info, "charInfo");
        var charEntry = Dict(charInfo.Items[0]).Entries.Single();
        Assert.Equal(character.CharacterId, Id(charEntry.Key));
        Assert.Equal(
            [164L, 165, 166, 167, 168, 192, 196, 219, 385, 387, 398, 467, 979, 1174, 1267, 1277, 1921, 1959, 2610, 2662, 2664],
            Dict(Field(charEntry.Value, "attributes")).Entries.Select(e => Id(e.Key)));
        Assert.Equal(new PyStr(""), ((PyPackedRow)Field(charEntry.Value, "invItem")).Values[8]);
        var brain = (PyTuple)charInfo.Items[1];
        Assert.Equal(4, brain.Items.Count);
        Assert.Equal(new PyInt(0), brain.Items[0]);
        Assert.Equal(9, ((PyTuple)brain.Items[1]).Items.Count);
        Assert.Equal(55, ((PyTuple)brain.Items[2]).Items.Count);
        Assert.Empty(((PyTuple)brain.Items[3]).Items);
        Assert.NotEqual(PyNone.Instance, Field(info, "shipModifiedCharAttribs"));
        Assert.Empty(Dict(Field(info, "locationInfo")).Entries);
        Assert.Empty(Dict(Field(info, "structureInfo")).Entries);
    }

    [PgFact]
    public async Task The_state_tells_the_ship_the_character_and_each_module()
    {
        var (rig, character, session, oid) = await BoundAsync();
        await using var _ = rig;
        var state = (PyTuple)Field(await rig.CallAsync(session, "GetAllInfo", oid, Everything), "shipState");
        var rows = Dict(state.Items[0]).Entries;
        Assert.Equal([character.ShipId, character.CharacterId, character.ShipId + 1, character.ShipId + 2, character.ShipId + 3], rows.Select(e => Id(e.Key)));
        // instanceID, online, damage, charge, skillPoints, armorDamage, shieldCharge, incapacitated
        Assert.Equal(
            [new PyLong(character.ShipId), PyBool.False, new PyReal(0), new PyReal(1), new PyInt(0), new PyReal(0), new PyReal(1), PyBool.False],
            ((PyPackedRow)rows[0].Value).Values);
        var pilot = ((PyPackedRow)rows[1].Value).Values;
        Assert.Equal((PyBool.True, new PyReal(0)), (pilot[1], pilot[3]));
        Assert.Equal(new PyInt(await rig.Skills.ForCharacterAsync(character.CharacterId) is var skills ? skills.Sum(s => s.SkillPoints) : 0), pilot[4]);
        Assert.All(rows.Skip(2), r => Assert.Equal(PyBool.True, ((PyPackedRow)r.Value).Values[1]));
        Assert.Empty(Dict(state.Items[1]).Entries);
        Assert.Empty(Dict(state.Items[2]).Entries);
        Assert.Equal([1176L, 1177, 1175], Dict(state.Items[3]).Entries.Select(e => Id(e.Key)));
    }

    [PgFact]
    public async Task What_is_left_out_of_the_question_is_left_out_of_the_answer_as_the_reference_leaves_it()
    {
        var (rig, character, session, oid) = await BoundAsync();
        await using var _ = rig;

        // the character alone
        var charOnly = await rig.CallAsync(session, "GetAllInfo", oid, PyBool.True, PyBool.False, PyNone.Instance);
        Assert.Equal(PyNone.Instance, Field(charOnly, "shipModifiedCharAttribs")); // "as the ship changes it" needs the ship
        Assert.Empty(Dict(Field(charOnly, "shipInfo")).Entries);
        Assert.Equal(PyNone.Instance, Field(charOnly, "shipState"));
        Assert.IsType<PyTuple>(Field(charOnly, "charInfo"));
        Assert.Equal(character.ShipId, Id(Field(charOnly, "activeShipID"))); // always told

        // the ship alone, docked: the character comes with it
        var shipOnly = await rig.CallAsync(session, "GetAllInfo", oid, PyBool.False, PyBool.True, PyNone.Instance);
        Assert.Equal(PyNone.Instance, Field(shipOnly, "shipModifiedCharAttribs"));
        Assert.Equal(4, Dict(Field(shipOnly, "shipInfo")).Entries.Count);
        Assert.IsType<PyTuple>(Field(shipOnly, "shipState"));
        Assert.IsType<PyTuple>(Field(shipOnly, "charInfo"));

        // neither
        var neither = await rig.CallAsync(session, "GetAllInfo", oid, PyBool.False, PyBool.False, PyNone.Instance);
        Assert.Equal(PyNone.Instance, Field(neither, "charInfo"));
        Assert.Equal(PyNone.Instance, Field(neither, "shipState"));
        Assert.Empty(Dict(Field(neither, "shipInfo")).Entries);

        // no arguments at all: everything, as the reference does
        var defaults = await rig.CallAsync(session, "GetAllInfo", oid);
        Assert.Equal(4, Dict(Field(defaults, "shipInfo")).Entries.Count);
        Assert.IsType<PyTuple>(Field(defaults, "charInfo"));
    }

    [PgFact]
    public async Task Each_race_gets_the_weapon_of_its_own_kind_in_its_ship()
    {
        await using var rig = await Rig.CreateAsync();
        var minmatar = await rig.NewCharacterAsync("Min Matar", race: 2, bloodline: 3, school: 33);
        var session = Rig.Session(minmatar.CharacterId);
        var oid = await rig.BindAsync(session);
        var shipInfo = Dict(Field(await rig.CallAsync(session, "GetAllInfo", oid, Everything), "shipInfo"));
        Assert.Equal([588, 21857, 3636, 3651], shipInfo.Entries.Select(e => (int)((PyInt)((PyPackedRow)Field(e.Value, "invItem")).Values[1]).Value));
    }
}
