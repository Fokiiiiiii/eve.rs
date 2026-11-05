using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// What a ship has taken: <c>/dmg</c> and <c>/heal</c> (the reference's, oracle/fixtures/scenarios/health.json), what dogma then reports of the ship and its
/// modules, and the ship in space, whose condition is the simulation's.
/// </summary>
public class HealthTests
{
    private static PyValue Field(PyValue keyVal, string name) =>
        ((PyDict)((PyObject)keyVal).Args).Entries.Single(e => JsSemantics.StringValue(e.Key) == name).Value;

    private static long Id(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, _ => throw new InvalidOperationException(value.ToString()) };

    private static double Number(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, PyReal r => r.Value, _ => throw new InvalidOperationException(value.ToString()) };

    private static readonly PyValue[] Everything = [PyBool.True, PyBool.True, PyNone.Instance];

    /// <summary>The value of an attribute of an item in the answer to <c>GetAllInfo</c> (the ship or a module).</summary>
    private static double Attribute(PyValue info, long itemId, int attribute)
    {
        var item = ((PyDict)Field(info, "shipInfo")).Entries.Single(e => Id(e.Key) == itemId).Value;
        return Number(((PyDict)Field(item, "attributes")).Entries.Single(e => Id(e.Key) == attribute).Value);
    }

    /// <summary>The state row of an item in the answer to <c>GetAllInfo</c>: <c>(damage, charge, armorDamage, shieldCharge, incapacitated)</c>.</summary>
    private static (double Damage, double Charge, double ArmorDamage, double ShieldCharge, bool Incapacitated) State(PyValue info, long itemId)
    {
        var row = (PyPackedRow)((PyDict)((PyTuple)Field(info, "shipState")).Items[0]).Entries.Single(e => Id(e.Key) == itemId).Value;
        var values = row.Values;
        return (Number(values[2]), Number(values[3]), Number(values[5]), Number(values[6]), values[7] is PyBool { Value: true });
    }

    private static async Task<(Rig Rig, Created Character, ClientSession Session, string Oid, long ShipId, long[] Modules)> DockedAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync(shipTypeId: Rig.Ibis);
        var session = Rig.Session(character.CharacterId);
        var oid = await rig.BindAsync(session);
        var snapshot = (await rig.Ships.LoadAsync(character.CharacterId))!;
        return (rig, character, session, oid, snapshot.Ship.Id, [.. snapshot.Fitted.Select(m => m.Id)]);
    }

    private static string[] Names(IEnumerable<Events.Event> events) => [.. events.Select(e => e.Name)];

    [PgFact]
    public async Task A_damaged_ship_in_the_hangar_is_told_as_the_reference_tells_it()
    {
        var (rig, _, session, _, shipId, modules) = await DockedAsync();
        await using var _ = rig;
        Events.Of(session);

        var message = await rig.SlashAsync(session, "/dmg light");

        Assert.Equal("Applied light test damage to your active ship and 3 fitted modules.", message);
        var events = Events.Of(session);
        Assert.Equal(["OnItemChange", "OnCfgDataChanged", "OnItemChange", "OnItemChange", "OnItemChange"], Names(events));
        Assert.All(events.Where(e => e.Name == "OnItemChange"), e => Assert.Equal("clientID", e.IdType));
        Assert.Equal("charid", events[1].IdType);
        // each item as it is, the way the client gets it (three arguments: the row, what changed of it, and none)
        Assert.All(events.Where(e => e.Name == "OnItemChange"), e => Assert.Equal((3, true), (e.Args.Length, e.Args[1] is PyDict { Entries.Count: 0 } && e.Args[2] is PyNone)));
        var rows = events.Where(e => e.Name == "OnItemChange").Select(e => ((PyPackedRow)e.Args[0]).Values).ToArray();
        Assert.Equal([shipId, .. modules], rows.Select(r => Id(r[0])));
        Assert.Equal(Rig.Station, Id(rows[0][3]));
        Assert.Equal(shipId, Id(rows[1][3]));
        // the ship's place in the name table: (id, name, solar system, 0, 0, 0, none)
        Assert.Equal("evelocations", JsSemantics.StringValue(events[1].Args[0]));
        var location = ((PyList)events[1].Args[1]).Items;
        Assert.Equal((shipId, 7), (Id(location[0]), location.Count));
        Assert.All(location.Skip(3).Take(3), v => Assert.Equal(0, Id(v)));
        Assert.IsType<PyNone>(location[6]);
    }

    [PgFact]
    public async Task Dogma_reports_what_a_ship_and_its_modules_have_taken_in_the_hangar()
    {
        var (rig, character, session, oid, shipId, modules) = await DockedAsync();
        await using var _ = rig;
        var whole = await rig.CallAsync(session, "GetAllInfo", oid, Everything);
        var (capacity, shield, armor, hull) = (Attribute(whole, shipId, 18), Attribute(whole, shipId, 263), Attribute(whole, shipId, 265), Attribute(whole, shipId, 9));
        Assert.Equal((0, capacity, shield, 0), (Attribute(whole, shipId, 3), Attribute(whole, shipId, 18), Attribute(whole, shipId, 264), Attribute(whole, shipId, 266)));
        Assert.Equal((0.0, 1.0, 0.0, 1.0, false), State(whole, shipId).ToTuple5());

        await rig.SlashAsync(session, "/dmg light");
        var light = await rig.CallAsync(session, "GetAllInfo", oid, Everything);

        // the ship: its hull as a fraction, the capacitor, shield and armor in points
        Assert.Equal(0.1, Attribute(light, shipId, 3));
        Assert.Equal(Math.Round(capacity * 0.65, 6), Attribute(light, shipId, 18));
        Assert.Equal(Math.Round(shield * 0.7, 6), Attribute(light, shipId, 264));
        Assert.Equal(Math.Round(armor * 0.18, 6), Attribute(light, shipId, 266));
        Assert.Equal((0.1, 0.65, 0.18, 0.7, false), State(light, shipId));
        // a module: its hull in points of its own, its shield and armor as the fractions it keeps them as
        var moduleHull = Attribute(light, modules[0], 9);
        Assert.Equal(Math.Round(moduleHull * 0.08, 6), Attribute(light, modules[0], 3));
        Assert.Equal(0.8, Attribute(light, modules[0], 264));
        Assert.Equal(0.04, Attribute(light, modules[0], 266));
        Assert.All(modules, m => Assert.Equal((0.08, 0.0, 0.04, 0.8, false), State(light, m)));
        Assert.Equal(hull, Attribute(light, shipId, 9));

        // the character in the ship is not hurt
        Assert.Equal((0.0, 0.0, 0.0, 0.0), State(light, character.CharacterId) is var c ? (c.Damage, c.ArmorDamage, c.ShieldCharge, 0.0) : default);
    }

    [PgFact]
    public async Task A_heavier_preset_leaves_less_and_a_preset_is_the_medium_one_when_none_is_named()
    {
        var (rig, _, session, oid, shipId, _) = await DockedAsync();
        await using var _ = rig;
        Assert.Equal("Applied medium test damage to your active ship and 3 fitted modules.", await rig.SlashAsync(session, "/dmg"));
        Assert.Equal((0.2, 0.35, 0.35, 0.45, false), State(await rig.CallAsync(session, "GetAllInfo", oid, Everything), shipId));
        Assert.Equal("Applied heavy test damage to your active ship and 3 fitted modules.", await rig.SlashAsync(session, "/DMG Heavy"));
        Assert.Equal((0.35, 0.18, 0.6, 0.22, false), State(await rig.CallAsync(session, "GetAllInfo", oid, Everything), shipId));
    }

    [PgFact]
    public async Task A_preset_that_is_not_one_and_a_ship_that_is_not_docked_are_answered_with_a_message_and_told_nothing()
    {
        var (rig, character, session, _, _, _) = await DockedAsync();
        await using var _ = rig;
        Events.Of(session);
        Assert.Equal("Usage: /dmg [light|medium|heavy]", await rig.SlashAsync(session, "/dmg fatal"));
        Assert.Equal("Select a character before using /dmg.", await rig.SlashAsync(Rig.Session(null), "/dmg"));
        var flying = Rig.Session(character.CharacterId, id: 2, station: null);
        flying.Set("solarsystemid", new PyInt(30100032));
        Assert.Equal("Dock first before using /dmg so the repair flow stays easy to test.", await rig.SlashAsync(flying, "/dmg heavy"));
        Assert.Empty(Events.Of(session));
        Assert.Empty(Events.Of(flying));
        Assert.Equal("Unknown command: /nothing. Use /help.", await rig.SlashAsync(session, "/nothing"));
    }

    [PgFact]
    public async Task A_heal_in_the_hangar_tells_the_modules_that_were_hurt_and_then_the_items()
    {
        var (rig, character, session, oid, shipId, modules) = await DockedAsync();
        await using var _ = rig;
        await rig.SlashAsync(session, "/dmg heavy");
        var hurt = await rig.CallAsync(session, "GetAllInfo", oid, Everything);
        var moduleHull = Attribute(hurt, modules[0], 3);
        Events.Of(session);

        var message = await rig.SlashAsync(session, "/heal");

        Assert.Equal("Restored full shields, armor, hull, capacitor, and fitted modules on your active ship.", message);
        var events = Events.Of(session);
        Assert.Equal(["OnModuleAttributeChanges", "OnModuleAttributeChanges", "OnModuleAttributeChanges", "OnItemChange", "OnCfgDataChanged", "OnItemChange", "OnItemChange", "OnItemChange"], Names(events));
        // each module: its hull now (nothing) and what it was, in a notification of its own
        for (var i = 0; i < 3; i++)
        {
            var line = (PyTuple)((PyList)events[i].Args[0]).Items.Single();
            Assert.Equal(
                ("OnModuleAttributeChange", character.CharacterId, modules[i], 3L, 0.0, moduleHull),
                (JsSemantics.StringValue(line.Items[0]), Id(line.Items[1]), Id(line.Items[2]), Id(line.Items[3]), Number(line.Items[5]), Number(line.Items[6])));
        }
        var healed = await rig.CallAsync(session, "GetAllInfo", oid, Everything);
        Assert.Equal((0.0, 1.0, 0.0, 1.0, false), State(healed, shipId).ToTuple5());
        // a module that was mended has all of its shield (one nobody has touched has none)
        Assert.All(modules, m => Assert.Equal((0.0, 0.0, 0.0, 1.0, false), State(healed, m)));
        Assert.Equal(1.0, Attribute(healed, modules[0], 264));
    }

    [PgFact]
    public async Task A_heal_of_a_ship_that_needs_none_tells_only_the_ship_and_a_module_nobody_touched_becomes_mended()
    {
        var (rig, _, session, oid, _, modules) = await DockedAsync();
        await using var _ = rig;
        Events.Of(session);
        await rig.SlashAsync(session, "/heal");
        // nothing was hurt, so no module has a hull to tell; the modules are mended all the same (they gain their shield), so their items are told
        Assert.Equal(["OnItemChange", "OnCfgDataChanged", "OnItemChange", "OnItemChange", "OnItemChange"], Names(Events.Of(session)));
        Assert.Equal(1.0, Attribute(await rig.CallAsync(session, "GetAllInfo", oid, Everything), modules[0], 264));

        // mended already: only the ship
        await rig.SlashAsync(session, "/heal");
        Assert.Equal(["OnItemChange", "OnCfgDataChanged"], Names(Events.Of(session)));
    }

    [PgFact]
    public async Task A_ship_in_space_is_mended_by_the_simulation_and_the_host_tells_the_modules_and_the_heat()
    {
        var (rig, character, session, _, shipId, modules) = await DockedAsync();
        await using var _ = rig;
        await rig.SlashAsync(session, "/dmg heavy");
        var flying = Rig.Session(character.CharacterId, id: 2, station: null);
        flying.Set("solarsystemid", new PyInt(30100032));
        rig.Health.Condition = new SpaceCondition(0.35, 1, 1, 0.6);

        var message = await rig.SlashAsync(flying, "/heal");

        Assert.Equal("Restored full shields, armor, hull, capacitor, and fitted modules on your active ship.", message);
        Assert.Equal(1, rig.Health.Healed);
        var events = Events.Of(flying);
        // the modules' hulls (a notification each), then the heat of the slots: nothing of the ship's items, they did not move
        Assert.Equal(["OnModuleAttributeChanges", "OnModuleAttributeChanges", "OnModuleAttributeChanges", "OnModuleAttributeChanges"], Names(events));
        var heat = ((PyList)events[3].Args[0]).Items.Cast<PyTuple>().ToArray();
        Assert.Equal([1175L, 1176L, 1177L], heat.Select(l => Id(l.Items[3])));
        Assert.All(heat, l => Assert.Equal((shipId, 0.0, true), (Id(l.Items[2]), Number(l.Items[5]), l.Items[6] is PyNone)));
        Assert.Equal(modules, ((PyList)events[0].Args[0]).Items.Cast<PyTuple>().Select(l => Id(l.Items[2])).Concat(((PyList)events[1].Args[0]).Items.Cast<PyTuple>().Select(l => Id(l.Items[2]))).Concat(((PyList)events[2].Args[0]).Items.Cast<PyTuple>().Select(l => Id(l.Items[2]))));
    }

    [PgFact]
    public async Task A_heal_in_space_the_simulation_does_not_hold_is_answered_with_a_message()
    {
        var (rig, character, _, _, _, _) = await DockedAsync();
        await using var _ = rig;
        var flying = Rig.Session(character.CharacterId, id: 2, station: null);
        flying.Set("solarsystemid", new PyInt(30100032));
        rig.Health.Condition = null;
        Assert.Equal("Your ship is not loaded in space yet.", await rig.SlashAsync(flying, "/heal"));
        Assert.Empty(Events.Of(flying));
        Assert.Equal("Select a character before using /heal.", await rig.SlashAsync(Rig.Session(null), "/heal"));
    }

    [PgFact]
    public async Task Dogma_tells_a_ship_in_space_what_the_simulation_says_it_has_taken()
    {
        var (rig, character, session, _, shipId, _) = await DockedAsync();
        await using var _ = rig;
        await rig.SlashAsync(session, "/dmg light");
        var whole = await rig.CallAsync(session, "GetAllInfo", await rig.BindAsync(session), Everything);
        var flying = Rig.Session(character.CharacterId, id: 2, station: null);
        flying.Set("solarsystemid", new PyInt(30100032));
        var oid = await rig.BindInSpaceAsync(flying, 30100032);
        // it left the hangar with its shield and capacitor full, and has lost since half its armor
        rig.Health.Condition = new SpaceCondition(0.35, 1, 1, 0.5);

        var info = await rig.CallAsync(flying, "GetAllInfo", oid, Everything);

        Assert.Equal((0.35, 1.0, 0.5, 1.0), (Attribute(info, shipId, 3), State(info, shipId).Charge, State(info, shipId).ArmorDamage, State(info, shipId).ShieldCharge));
        Assert.Equal(Attribute(whole, shipId, 263), Attribute(info, shipId, 264));
        Assert.Equal(Math.Round(Attribute(whole, shipId, 265) * 0.5, 6), Attribute(info, shipId, 266));
        // the capacitor holds all it can (the hangar's, damaged, held 65 of a hundred)
        Assert.Equal(Attribute(whole, shipId, 18) / 0.65, Attribute(info, shipId, 18), 6);
    }
}

internal static class StateExtensions
{
    /// <summary>The row of an item's state as <c>(damage, charge, armor damage, shield charge, incapacitated)</c> tuple of the first four.</summary>
    public static (double, double, double, double, bool) ToTuple5(this (double Damage, double Charge, double ArmorDamage, double ShieldCharge, bool Incapacitated) s) =>
        (s.Damage, s.Charge, s.ArmorDamage, s.ShieldCharge, s.Incapacitated);
}
