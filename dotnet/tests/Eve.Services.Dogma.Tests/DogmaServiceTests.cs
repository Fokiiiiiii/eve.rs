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
        Assert.Equal(new PyInt(33_810), shipAttributes.Entries.Single(e => Id(e.Key) == 9).Value); // Cockroach structure hit points

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

    [PgFact]
    public async Task GetAvailableTurretSlots_returns_the_active_ships_remaining_hardpoints_as_a_python_integer()
    {
        var (rig, character, session, oid) = await BoundAsync();
        await using var _ = rig;
        var snapshot = await rig.Ships.LoadAsync(character.CharacterId);
        Assert.NotNull(snapshot);
        var expected = Math.Max(0L, (long)snapshot.World.Get(snapshot.Ship.Id, Eve.Dogma.BuiltInEffects.TurretSlotsLeft));

        var result = await rig.CallAsync(session, "GetAvailableTurretSlots", oid);

        Assert.IsType<PyInt>(result);
        Assert.Equal(expected, Id(result));
    }

    [PgFact]
    public async Task Online_turret_damage_amplifiers_modify_matching_weapon_damage_with_SDE_stacking()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var snapshot = await rig.Ships.LoadAsync(character.CharacterId);
        Assert.NotNull(snapshot);
        var world = snapshot.World;
        await rig.Statics.EnsureTypesAsync([520, 484, 2363, 453, 9944, 564]);
        var shipId = snapshot.Ship.Id;
        var nextItemId = long.MaxValue - 1_000;

        Eve.Dogma.DogmaItem Add(int typeId, int flag, Eve.Dogma.ItemState state) =>
            world.Add(new Eve.Dogma.DogmaItem(nextItemId++, typeId, character.CharacterId, shipId, flag, state));

        foreach (var (amplifierTypeId, weaponTypeId) in new[] { (520, 484), (2363, 453), (9944, 564) })
        {
            var weapon = Add(weaponTypeId, 27, Eve.Dogma.ItemState.Online);
            var baseline = world.Get(weapon.Id, 64);
            var amplifier = Add(amplifierTypeId, 11, Eve.Dogma.ItemState.Offline);

            Assert.Equal(baseline, world.Get(weapon.Id, 64));
            world.SetState(amplifier.Id, Eve.Dogma.ItemState.Online);
            var single = world.Get(weapon.Id, 64);
            Assert.True(single > baseline);

            if (amplifierTypeId == 520)
            {
                Add(amplifierTypeId, 11, Eve.Dogma.ItemState.Online);
                var stacked = world.Get(weapon.Id, 64);
                Assert.True(stacked > single);
                Assert.True(stacked < single * single / baseline);
            }
        }
    }

    [PgFact]
    public async Task Online_ballistic_control_system_scales_the_missile_damage_multiplier_with_SDE_stacking()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var snapshot = await rig.Ships.LoadAsync(character.CharacterId);
        Assert.NotNull(snapshot);
        var world = snapshot.World;
        await rig.Statics.EnsureTypesAsync([12274]);
        var shipId = snapshot.Ship.Id;
        var nextItemId = long.MaxValue - 2_000;

        Eve.Dogma.DogmaItem Add(Eve.Dogma.ItemState state) =>
            world.Add(new Eve.Dogma.DogmaItem(nextItemId++, 12274, character.CharacterId, shipId, 11, state));

        var baseline = world.Get(character.CharacterId, 212);
        Assert.True(baseline > 0);
        var first = Add(Eve.Dogma.ItemState.Offline);
        Assert.Equal(baseline, world.Get(character.CharacterId, 212));

        world.SetState(first.Id, Eve.Dogma.ItemState.Online);
        var single = world.Get(character.CharacterId, 212);
        Assert.True(single > baseline);

        Add(Eve.Dogma.ItemState.Online);
        var stacked = world.Get(character.CharacterId, 212);
        Assert.True(stacked > single);
        Assert.True(stacked < single * single / baseline);
    }
}

public class ShipEventTests
{
    private static async Task<(Rig Rig, Created Character, ClientSession Session, string Dogma, string Hangar)> ReadyAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = Rig.Session(character.CharacterId);
        var dogma = await rig.BindAsync(session);
        var hangar = Rig.OidOf(await rig.InventoryAsync(session, "GetInventory", args: [new PyInt(10004)]));
        Events.Of(session); // what the setup sent is not what the test looks at
        return (rig, character, session, dogma, hangar);
    }

    private static Task<PyValue> Inventory(Rig rig, ClientSession session, string bound, string method, PyValue[] args, PyValue? kwargs = null) =>
        rig.InventoryAsync(session, method, bound, args, kwargs);

    private static PyValue Kw(string name, long value) => new PyDict([new(new PyStr(name), new PyInt(value))]);

    [PgFact]
    public async Task GetAllInfo_is_followed_by_the_characters_attributes_and_the_brain_and_the_brain_version_counts()
    {
        var (rig, _, session, oid, _) = await ReadyAsync();
        await using var owned = rig;
        await rig.CallAsync(session, "GetAllInfo", oid, PyBool.True, PyBool.True, PyNone.Instance);
        Assert.Empty(Events.Of(session, clear: false)); // nothing is sent before the answer has gone
        session.RunAfterResponse();

        var events = Events.Of(session);
        Assert.Equal(["OnModuleAttributeChanges", "OnModuleAttributeChanges", "OnServerBrainUpdated"], events.Select(e => e.Name));
        Assert.All(events, e => Assert.Equal("clientID", e.IdType));
        var industry = ((PyList)events[0].Args[0]).Items.Cast<PyTuple>().ToArray();
        Assert.Equal([196L, 219, 385, 387, 398, 467, 1959, 2662, 2664], industry.Select(t => Events.Number(t.Items[3])));
        Assert.All(industry, t => Assert.Equal((new PyStr("OnModuleAttributeChange"), (PyValue)PyNone.Instance), (t.Items[0], t.Items[6])));
        Assert.Equal(new PyReal(0.96), industry[1].Items[5]);
        var learning = ((PyList)events[1].Args[0]).Items.Cast<PyTuple>().ToArray();
        Assert.Equal([164L, 165, 166, 167, 168], learning.Select(t => Events.Number(t.Items[3])));
        Assert.All(learning, t => Assert.Equal(new PyInt(20), t.Items[5]));

        var brain = (PyTuple)events[2].Args[0];
        Assert.Equal(new PyInt(1), brain.Items[0]);
        var lists = (PyTuple)MarshalCodec.Decode(((PyBuffer)brain.Items[1]).Value.ToArray());
        Assert.Equal((9, 55, 0), (((PyList)lists.Items[0]).Items.Count, ((PyList)lists.Items[1]).Items.Count, ((PyList)lists.Items[2]).Items.Count));

        await rig.CallAsync(session, "GetAllInfo", oid, PyBool.True, PyBool.True, PyNone.Instance);
        session.RunAfterResponse();
        Assert.Equal(new PyInt(2), ((PyTuple)Events.Of(session).Last().Args[0]).Items[0]);
    }

    [PgFact]
    public async Task Fitting_bootstrap_never_sends_sublocation_quantity_for_normal_item_ids()
    {
        var (rig, character, session, _, _) = await ReadyAsync();
        await using var owned = rig;
        session.Set("shipid", new PyLong(character.ShipId));
        var sync = new CharacterDogmaSync(rig.Statics, rig.Ships, () => Rig.ClockMs);
        await sync.OnCharacterEnteredAsync(session, character.CharacterId, CancellationToken.None);
        await sync.OnInventoryRpcAsync(session, "List", true, false, CancellationToken.None);
        Assert.Empty(Events.Of(session, clear: false));
        session.RunAfterResponse();
        var events = Events.Of(session);
        var attributes = Assert.Single(events, e => e.Name == "OnModuleAttributeChanges");
        var changes = Assert.IsType<PyList>(attributes.Args[0]).Items.Cast<PyTuple>().ToArray();
        Assert.NotEmpty(changes);
        Assert.All(changes, change =>
        {
            Assert.IsType<PyLong>(change.Items[2]);
            Assert.NotEqual(805L, Events.Number(change.Items[3]));
        });
        Assert.Contains(changes, change => Events.Number(change.Items[2]) == character.ShipId);
        Assert.Contains(changes, change => Events.Number(change.Items[2]) != character.ShipId);
        await sync.OnInventoryRpcAsync(session, "List", true, false, CancellationToken.None);
        session.RunAfterResponse();
        Assert.Empty(Events.Of(session));
    }

    [PgFact]
    public async Task Asking_for_only_the_ship_sends_the_character_events_too_but_asking_for_nothing_sends_none()
    {
        var (rig, _, session, oid, _) = await ReadyAsync();
        await using var owned = rig;
        await rig.CallAsync(session, "GetAllInfo", oid, PyBool.False, PyBool.False, PyNone.Instance);
        session.RunAfterResponse();
        Assert.Empty(Events.Of(session));
        await rig.CallAsync(session, "GetAllInfo", oid, PyBool.False, PyBool.True, PyNone.Instance);
        session.RunAfterResponse();
        Assert.Equal(3, Events.Of(session).Count);
    }

    [PgFact]
    public async Task Taking_a_module_offline_and_online_tells_the_client_in_one_batch_each_and_answers_none()
    {
        var (rig, character, session, oid, _) = await ReadyAsync();
        await using var owned = rig;
        var ship = character.ShipId;
        var afterburner = ship + 1;

        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "TakeModuleOffline", oid, new PyLong(ship), new PyLong(afterburner)));
        var off = Assert.Single(Events.Of(session));
        Assert.Equal(("OnMultiEvent", "clientID"), (off.Name, off.IdType));
        var batch = ((PyList)off.Args[0]).Items.Cast<PyTuple>().ToArray();
        // (event, time) each; a module's attribute and the ship's, then the effect
        Assert.Equal(
            ["OnModuleAttributeChange", "OnModuleAttributeChange", "OnModuleAttributeChange", "OnGodmaShipEffect"],
            batch.Select(b => JsSemantics.StringValue(Events.Items(b.Items[0])[0])));
        var first = Events.Items(batch[0].Items[0]);
        Assert.Equal((afterburner, 2L, new PyInt(0), new PyInt(1)), (Events.Number(first[2]), Events.Number(first[3]), first[5], first[6]));
        var effect = Events.Items(batch[3].Items[0]);
        Assert.Equal((new PyInt(16), new PyInt(0), new PyInt(0), PyBool.False, new PyInt(-1)), (effect[2], effect[4], effect[5], effect[9], effect[8]));
        Assert.All(batch, b => Assert.IsType<PyLong>(b.Items[1]));

        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "SetModuleOnline", oid, new PyLong(ship), new PyLong(afterburner)));
        var on = Assert.Single(Events.Of(session));
        Assert.Equal(new PyInt(1), Events.Items(((PyTuple)((PyList)on.Args[0]).Items[^1]).Items[0])[4]); // the effect starts
    }

    [PgFact]
    public async Task A_call_about_a_module_that_is_not_the_characters_or_a_session_that_did_not_bind_is_answered_with_a_message_or_none()
    {
        var (rig, character, session, oid, _) = await ReadyAsync();
        await using var owned = rig;
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.CallAsync(session, "SetModuleOnline", oid, new PyLong(character.ShipId), new PyLong(12345)));
        Assert.Equal("DeniedShipChanged", refusal.Error.UserErrorMessage);
        Assert.Equal(PyNone.Instance, await rig.CallAsync(Rig.Session(character.CharacterId, id: 2), "SetModuleOnline", oid, new PyLong(character.ShipId), new PyLong(character.ShipId + 1)));
        Assert.Empty(Events.Of(session));
    }

    [PgFact]
    public async Task Adding_a_module_to_the_hangar_takes_it_out_of_its_slot_and_the_client_is_told_in_the_order_the_reference_does()
    {
        var (rig, character, session, _, hangar) = await ReadyAsync();
        await using var owned = rig;
        var weapon = character.ShipId + 2;
        var answer = await Inventory(rig, session, hangar, "Add", [new PyLong(weapon), new PyLong(character.ShipId)], Kw("flag", 4));
        Assert.Equal(weapon, Events.Number(answer)); // ids of new items fit an int, and the wire follows the value

        var events = Events.Of(session);
        Assert.Equal(["OnMultiEvent", "OnItemsChanged"], events.Select(e => e.Name));
        Assert.Equal(["clientID", "charid"], events.Select(e => e.IdType));
        var changed = events[1].Args;
        var row = (PyPackedRow)((PyList)changed[0]).Items.Single();
        Assert.Equal((weapon, 3638L, Rig.Station, 4L), (Events.Number(row.Values[0]), Events.Number(row.Values[1]), Events.Number(row.Values[3]), Events.Number(row.Values[4])));
        Assert.Empty(Assert.IsType<PyBytes>(row.Values[8]).Value.ToArray()); // no custom info
        var change = (PyDict)changed[1];
        Assert.Equal([(3L, character.ShipId), (4L, 27L)], change.Entries.Select(e => (Events.Number(e.Key), Events.Number(e.Value))));
        Assert.Equal(PyNone.Instance, changed[2]);
        var listed = ((PyList)await Inventory(rig, session, hangar, "List", [])).Items.Cast<PyPackedRow>().Select(r => Events.Number(r.Values[0]));
        Assert.Equal([character.ShipId, weapon], listed.Order());
    }

    [PgFact]
    public async Task Adding_to_the_ship_fits_the_module_and_a_refusal_is_a_message_the_client_shows()
    {
        var (rig, character, session, _, hangar) = await ReadyAsync();
        await using var owned = rig;
        var ship = character.ShipId;
        var shipInventory = Rig.OidOf(await rig.InventoryAsync(session, "GetInventoryFromId", args: [new PyLong(ship)]));
        await Inventory(rig, session, hangar, "Add", [new PyLong(ship + 2), new PyLong(ship)], Kw("flag", 4));
        Events.Of(session);

        Assert.Equal(ship + 2, Events.Number(await Inventory(rig, session, shipInventory, "Add", [new PyLong(ship + 2), new PyLong(Rig.Station)], Kw("flag", 27))));
        var events = Events.Of(session);
        Assert.Equal(["OnMultiEvent", "OnItemsChanged"], events.Select(e => e.Name));
        Assert.Single(((PyList)events[0].Args[0]).Items); // only the turret hard point, an offline module costs nothing

        // the slot is taken now: a user error with the reference's words
        await Inventory(rig, session, hangar, "Add", [new PyLong(ship + 3), new PyLong(ship)], Kw("flag", 4)); // the miner out
        Events.Of(session);
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(async () => await Inventory(rig, session, shipInventory, "Add", [new PyLong(ship + 3), new PyLong(Rig.Station)], Kw("flag", 27)));
        Assert.Equal("CustomNotify", refused.Error.UserErrorMessage);
        Assert.Empty(Events.Of(session));
    }

    [PgFact]
    public async Task MultiAdd_uses_the_client_auto_fit_flag_to_fit_selected_modules()
    {
        var (rig, character, session, _, hangar) = await ReadyAsync();
        await using var owned = rig;
        var ship = character.ShipId;
        var shipInventory = Rig.OidOf(await rig.InventoryAsync(session, "GetInventoryFromId", args: [new PyLong(ship)]));
        var module = ship + 2;
        await Inventory(rig, session, hangar, "Add", [new PyLong(module), new PyLong(ship)], Kw("flag", 4));
        Events.Of(session);

        Assert.Equal(PyBool.True, await Inventory(rig, session, shipInventory, "MultiAdd",
            [new PyList([new PyLong(module)]), new PyLong(Rig.Station)], Kw("flag", 0)));
        Assert.Equal(["OnMultiEvent", "OnItemsChanged"], Events.Of(session).Select(e => e.Name));

        var rows = (PyList)await Inventory(rig, session, shipInventory, "GetItems", [new PyTuple([new PyLong(module)])]);
        var fitted = Assert.IsType<PyPackedRow>(Assert.Single(rows.Items));
        Assert.Equal(27L, Events.Number(fitted.Values[4]));
    }

    [PgFact]
    public async Task Adding_something_that_is_not_there_is_answered_with_none_and_tells_nothing()
    {
        var (rig, character, session, _, hangar) = await ReadyAsync();
        await using var owned = rig;
        Assert.Equal(PyNone.Instance, await Inventory(rig, session, hangar, "Add", [new PyLong(123456789), new PyLong(character.ShipId)], Kw("flag", 4)));
        Assert.Equal(PyNone.Instance, await Inventory(rig, session, hangar, "Add", [PyNone.Instance, PyNone.Instance]));
        Assert.Empty(Events.Of(session));
    }
}
