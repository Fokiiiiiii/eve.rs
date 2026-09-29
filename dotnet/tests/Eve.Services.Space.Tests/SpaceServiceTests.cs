using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

public class SpaceServiceTests
{
    private static long Id(PyValue value) => Events.Number(value);

    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> DockedAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        return (rig, character, await rig.SessionAsync(character));
    }

    /// <summary>Binds the ship service and undocks.</summary>
    private static async Task<(string Oid, PyValue Answer)> UndockAsync(Rig rig, ClientSession session)
    {
        var oid = await rig.BindShipAsync(session);
        var answer = await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        return (oid, answer);
    }

    private static async Task<(string Oid, PyValue Answer)> InSpaceAsync(Rig rig, ClientSession session)
    {
        var undocked = await UndockAsync(rig, session);
        Sequence.Of(session);
        return undocked;
    }

    // ── binding ──────────────────────────────────────────────────────────────

    [PgFact]
    public async Task The_ship_service_binds_to_the_station_the_character_is_at_and_to_no_other_place()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;

        var oid = await rig.BindShipAsync(session);
        Assert.StartsWith("N=65450:", oid);
        Assert.Equal(PyNone.Instance, await rig.ShipCallAsync(session, "MachoBindObject", null, Py.Tuple(new PyInt(60000001), new PyInt(15)), PyNone.Instance));
        Assert.Equal(new PyInt(MachoMessage.ProxyNodeId), await rig.ShipCallAsync(session, "MachoResolveObject"));
    }

    [PgFact]
    public async Task An_undock_that_is_not_addressed_to_an_object_the_session_was_given_does_nothing()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        Assert.Equal(PyNone.Instance, await rig.ShipCallAsync(session, "Undock", null, new PyLong(session.GetInt("shipid")!.Value), PyBool.False));
        Assert.Equal(PyNone.Instance, await rig.ShipCallAsync(session, "Undock", "N=65450:999", new PyLong(1), PyBool.False));
        Assert.Empty(rig.Sim.Heard);
    }

    // ── undocking ────────────────────────────────────────────────────────────

    [PgFact]
    public async Task Undocking_tells_the_client_in_the_reference_order_and_answers_with_the_object_and_the_time()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        // the client holds another object bound to the station too (dogma, an inventory)
        session.RememberBoundObject("N=65450:900");

        var (oid, answer) = await UndockAsync(rig, session);

        Assert.Equal(["OnItemsChanged", "OnMachoObjectDisconnect", "OnMachoObjectDisconnect", "$sessionchange", "OnInvulunOnUndockingUpdated"], Sequence.Of(session));
        var tuple = Assert.IsType<PyTuple>(answer);
        Assert.Equal(oid, JsSemantics.StringValue(tuple.Items[0]));
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(Rig.ClockMs + 10_000)), tuple.Items[1]);
        Assert.Empty(session.BoundOids); // every object of the station was let go
    }

    [PgFact]
    public async Task Undocking_moves_the_ship_item_and_the_session_and_tells_the_simulation()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var system = (await rig.Characters.GetAsync(character.CharacterId))!.SolarSystemId;

        await UndockAsync(rig, session);

        // the item
        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((system, 0), (ship.LocationId, ship.Flag));
        // the session: three attributes, in this order, and nothing else
        Assert.Equal(
            [("stationid", new PyInt(Rig.Station), PyNone.Instance), ("locationid", new PyInt(Rig.Station), new PyInt(system)), ("solarsystemid", PyNone.Instance, new PyInt(system))],
            Sequence.LastChange(session));
        Assert.Null(session.GetInt("stationid"));
        Assert.Equal(Rig.Station, session.GetInt("stationid2"));
        Assert.Equal(system, session.GetInt("solarsystemid"));
        // the simulation
        var enter = Assert.IsType<SimEnter>(Assert.Single(rig.Sim.Heard));
        Assert.Equal(((ulong)session.SessionId, system, EnterHow.Undock, Rig.Station), (enter.SessionId, enter.SolarSystemId, enter.How, enter.StationId));
        Assert.Equal((character.ShipId, character.CharacterId, "Ibis"), (enter.Ship.ShipId, enter.Ship.CharacterId, enter.Ship.Name));
        Assert.True(enter.Ship.Mass > 0 && enter.Ship.MaxVelocity > 0 && enter.Ship.Agility > 0 && enter.Ship.Radius > 0 && enter.Ship.ShieldRechargeMs > 0);
        Assert.Equal(3, enter.Ship.Modules.Count);
        Assert.True(rig.Space.IsInSpace(session));
    }

    [PgFact]
    public async Task What_the_client_is_told_of_the_ship_and_the_protection_is_what_the_reference_sent()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var system = (await rig.Characters.GetAsync(character.CharacterId))!.SolarSystemId;
        var oid = await rig.BindShipAsync(session);
        Events.Of(session);

        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(character.ShipId), PyBool.False);
        var events = Events.Of(session);

        var items = events[0];
        Assert.Equal(("OnItemsChanged", "charid"), (items.Name, items.IdType));
        var row = (PyPackedRow)((PyList)items.Args[0]).Items.Single();
        Assert.Equal($"Undocking:{Rig.Station}", JsSemantics.StringValue(row.Values[8]));
        Assert.Equal((character.ShipId, (long)system, 0L), (Id(row.Values[0]), Id(row.Values[3]), Id(row.Values[4])));
        var change = (PyDict)items.Args[1];
        Assert.Equal([(3L, (long)Rig.Station), (4L, (long)ItemFlags.Hangar)], change.Entries.Select(e => (Id(e.Key), Id(e.Value))));
        Assert.Equal(PyNone.Instance, items.Args[2]);

        var disconnect = events[1];
        Assert.Equal(("OnMachoObjectDisconnect", "+clientID"), (disconnect.Name, disconnect.IdType));
        Assert.Equal((oid, session.ClientId), (JsSemantics.StringValue(disconnect.Args[0]), Id(disconnect.Args[1])));
        Assert.Equal(PyNone.Instance, disconnect.Args[2]);

        var invulnerable = events[2];
        Assert.Equal(("OnInvulunOnUndockingUpdated", "shipid"), (invulnerable.Name, invulnerable.IdType));
        Assert.Equal((character.ShipId, Py.FileTimeFromUnixMs(Rig.ClockMs) + 300_000_000, 300_000_000L), (Id(invulnerable.Args[0]), Id(invulnerable.Args[1]), Id(invulnerable.Args[2])));
    }

    [PgFact]
    public async Task Without_the_simulation_an_undock_is_refused_with_a_message_and_nothing_moves()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        rig.Sim.IsConnected = false;
        var oid = await rig.BindShipAsync(session);
        Events.Of(session);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.ShipCallAsync(session, "Undock", oid, new PyLong(character.ShipId), PyBool.False));
        Assert.True(error.Error.IsUserError);
        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((Rig.Station, ItemFlags.Hangar), (ship.LocationId, ship.Flag));
        Assert.Empty(Events.Of(session));
        Assert.Equal(Rig.Station, session.GetInt("stationid"));
        Assert.False(rig.Space.IsInSpace(session));
    }

    [PgFact]
    public async Task When_the_simulation_will_not_take_the_ship_it_is_put_back_and_the_client_is_told_nothing()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        rig.Sim.EnterAnswer = "UNKNOWN_STATION 60015249";
        var oid = await rig.BindShipAsync(session);
        Events.Of(session);

        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.ShipCallAsync(session, "Undock", oid, new PyLong(character.ShipId), PyBool.False));

        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((Rig.Station, ItemFlags.Hangar), (ship.LocationId, ship.Flag));
        Assert.Empty(Events.Of(session));
        Assert.False(rig.Space.IsInSpace(session));
        Assert.Contains(rig.Log, line => line.Contains("UNKNOWN_STATION"));
    }

    [PgFact]
    public async Task A_ship_in_space_does_not_undock_again()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        var (oid, _) = await InSpaceAsync(rig, session);
        var again = await rig.ShipCallAsync(session, "Undock", oid, new PyLong(1), PyBool.False);
        Assert.Equal(PyNone.Instance, again);
        Assert.Single(rig.Sim.Heard);
    }

    // ── in space ─────────────────────────────────────────────────────────────

    [PgFact]
    public async Task The_ballpark_binds_only_for_a_ship_in_space_and_once_the_answer_has_gone_the_simulation_sends_the_scene()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        var system = session.GetInt("solarsystemid2")!.Value;

        Assert.Equal(PyNone.Instance, await rig.BeyonceCallAsync(session, "MachoBindObject", null, Py.Tuple(new PyInt(system), new PyInt(5)), PyNone.Instance));

        await InSpaceAsync(rig, session);
        var answer = (PyTuple)await rig.BeyonceCallAsync(session, "MachoBindObject", null, Py.Tuple(new PyInt(system), new PyInt(5)), PyNone.Instance);
        Assert.StartsWith("N=65450:", Rig.OidOf(answer.Items[0]));
        Assert.Equal(PyNone.Instance, answer.Items[1]);
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimBind);

        session.RunAfterResponse();
        Assert.Equal(new SimBind((ulong)session.SessionId), rig.Sim.Heard.Last());
        // not the ballpark of another system
        Assert.Equal(PyNone.Instance, await rig.BeyonceCallAsync(session, "MachoBindObject", null, Py.Tuple(new PyInt(30000142), new PyInt(5)), PyNone.Instance));
    }

    [PgFact]
    public async Task Commands_reach_the_simulation_from_a_bound_object_the_session_was_given_and_from_no_other()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        var before = rig.Sim.Heard.Count;

        await rig.BeyonceCallAsync(session, "CmdSetSpeedFraction", oid, new PyReal(0.5));
        await rig.BeyonceCallAsync(session, "CmdGotoDirection", oid, new PyReal(1), new PyInt(0), new PyReal(0));
        await rig.BeyonceCallAsync(session, "CmdStop", oid);
        await rig.BeyonceCallAsync(session, "UpdateStateRequest", oid);

        Assert.Equal(
            new SimMessage[]
            {
                new SimCommand((ulong)session.SessionId, SimCommands.SetSpeedFraction, [0.5]),
                new SimCommand((ulong)session.SessionId, SimCommands.GotoDirection, [1, 0, 0]),
                new SimCommand((ulong)session.SessionId, SimCommands.Stop, []),
                new SimUpdateState((ulong)session.SessionId),
            }.Select(Describe),
            rig.Sim.Heard.Skip(before).Select(Describe));

        // calls that were not made on an object of this session, or that carry nonsense, are not passed on
        await rig.BeyonceCallAsync(session, "CmdStop", null);
        await rig.BeyonceCallAsync(session, "CmdStop", "N=65450:999");
        await rig.BeyonceCallAsync(session, "CmdSetSpeedFraction", oid, new PyStr("fast"));
        await rig.BeyonceCallAsync(session, "CmdGotoDirection", oid, new PyReal(1));
        Assert.Equal(before + 4, rig.Sim.Heard.Count);
    }

    private static string Describe(SimMessage message) => message switch
    {
        SimCommand c => $"command {c.Code} {string.Join(",", c.Args)}",
        var other => other.ToString()!,
    };

    private static SimNotify Update(ClientSession session) => new(
        (ulong)session.SessionId, "DoDestinyUpdate", "clientID",
        MarshalCodec.Encode(Py.Tuple(new PyList([Py.Tuple(new PyInt(1790679102), Py.Tuple(new PyStr("Stop"), Py.Tuple(new PyLong(9988400001939))))]), PyBool.False)));

    [PgFact]
    public async Task The_first_speed_command_while_the_protection_lasts_ends_it_and_the_client_is_told_once_after_the_update()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);

        // going in a direction, or stopping, does not end it
        await rig.BeyonceCallAsync(session, "CmdGotoDirection", oid, new PyReal(1), new PyInt(0), new PyReal(0));
        await rig.BeyonceCallAsync(session, "CmdStop", oid);
        rig.Sim.Say(Update(session));
        Assert.Equal(["DoDestinyUpdate"], Events.Of(session).Select(e => e.Name));

        // a speed does, and the client hears of it after what the simulation said the command did
        await rig.BeyonceCallAsync(session, "CmdSetSpeedFraction", oid, new PyReal(0.5));
        Assert.Empty(Events.Of(session));
        rig.Sim.Say(Update(session));
        var events = Events.Of(session);
        Assert.Equal(["DoDestinyUpdate", "OnInvulnCancelled"], events.Select(e => e.Name));
        Assert.Equal(("OnInvulnCancelled", "shipid"), (events[1].Name, events[1].IdType));
        Assert.Equal(session.GetInt("shipid"), Id(events[1].Args[0]));

        // once
        await rig.BeyonceCallAsync(session, "CmdSetSpeedFraction", oid, new PyReal(1));
        rig.Sim.Say(Update(session));
        Assert.Equal(["DoDestinyUpdate"], Events.Of(session).Select(e => e.Name));
    }

    [PgFact]
    public async Task The_formations_are_kept_by_the_client_for_the_length_of_the_session()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        var answer = Assert.IsType<PyObject>(await rig.BeyonceCallAsync(session, "GetFormations"));
        Assert.Equal(ObjectCaching.ResultClass, JsSemantics.StringValue(answer.Name));
        var formations = (PyList)((PySubStream)((PyTuple)answer.Args).Items[1]).Decoded!;
        Assert.Equal(["Diamond", "Arrow"], formations.Items.Select(f => JsSemantics.StringValue(((PyTuple)f).Items[0])));
    }

    // ── what the simulation says ─────────────────────────────────────────────

    [PgFact]
    public async Task What_the_simulation_says_for_a_session_reaches_that_client_as_the_notification_it_named()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var args = MarshalCodec.Encode(Py.Tuple(new PyList([Py.Tuple(new PyInt(1790679102), Py.Tuple(new PyStr("Stop"), Py.Tuple(new PyLong(9988400001939))))]), PyBool.False));

        rig.Sim.Say(new SimNotify((ulong)session.SessionId, "DoDestinyUpdate", "clientID", args));
        rig.Sim.Say(new SimNotify(999, "DoDestinyUpdate", "clientID", args)); // a session that is not in space: nobody's

        var events = Events.Of(session);
        var update = Assert.Single(events);
        Assert.Equal(("DoDestinyUpdate", "clientID"), (update.Name, update.IdType));
        var actions = ((PyList)update.Args[0]).Items;
        Assert.Equal(1790679102, Id(((PyTuple)actions.Single()).Items[0]));
        Assert.Equal(PyBool.False, update.Args[1]);
    }

    // ── docking ──────────────────────────────────────────────────────────────

    [PgFact]
    public async Task A_dock_is_answered_with_the_time_it_was_accepted_and_with_nothing_when_it_was_not()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);

        var answer = await rig.BeyonceCallAsync(session, "CmdDock", oid, new PyInt((int)Rig.Station), new PyLong(session.GetInt("shipid")!.Value));
        Assert.Equal(new PyLong(134351527136350000), answer);
        Assert.Equal(Describe(new SimCommand((ulong)session.SessionId, SimCommands.Dock, [Rig.Station])), Describe(rig.Sim.Heard.Last()));

        rig.Sim.DockAnswer = new DockReply(false, 0, "DOCKING_APPROACH_REQUIRED");
        Assert.Equal(PyNone.Instance, await rig.BeyonceCallAsync(session, "CmdDock", oid, new PyInt((int)Rig.Station), new PyLong(1)));
        Assert.True(rig.Space.IsInSpace(session), "a refused docking leaves the ship where it is");
    }

    [PgFact]
    public async Task When_the_simulation_says_the_ship_has_docked_the_client_is_told_in_the_reference_order_and_the_ship_is_in_the_hangar()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var system = session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session, clear: false);
        var sink = (Sink)session.Sink!;
        sink.Sent.Clear();

        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Rig.Station));
        await Rig.UntilAsync(() => session.GetInt("stationid") == Rig.Station);

        Assert.Equal(["OnInvulnCancelled", "OnMachoObjectDisconnect", "OnItemsChanged", "$sessionchange", "OnDockingFinished"], Sequence.Of(session, clear: false));
        Assert.Equal(
            [("stationid", PyNone.Instance, new PyInt(Rig.Station)), ("locationid", new PyInt(system), new PyInt(Rig.Station)), ("solarsystemid", new PyInt(system), PyNone.Instance)],
            Sequence.LastChange(session));
        Assert.False(rig.Space.IsInSpace(session));

        var events = Events.Of(session);
        Assert.Equal((oid, "+clientID"), (JsSemantics.StringValue(events[1].Args[0]), events[1].IdType));
        var items = events[2];
        Assert.Equal(("OnItemsChanged", "charid"), (items.Name, items.IdType));
        var row = (PyPackedRow)((PyList)items.Args[0]).Items.Single();
        Assert.Equal((character.ShipId, (long)Rig.Station, (long)ItemFlags.Hangar), (Id(row.Values[0]), Id(row.Values[3]), Id(row.Values[4])));
        Assert.Equal("", JsSemantics.StringValue(row.Values[8]));
        Assert.Equal([(3L, (long)system), (4L, 0L)], ((PyDict)items.Args[1]).Entries.Select(e => (Id(e.Key), Id(e.Value))));
        var place = (PyTuple)items.Args[2];
        Assert.Equal(("Station", Rig.Station, "StationHangar"), (JsSemantics.StringValue(place.Items[0]), Id(place.Items[1]), JsSemantics.StringValue(place.Items[2])));
        Assert.Equal(("OnDockingFinished", "charid", (long)Rig.Station), (events[3].Name, events[3].IdType, Id(events[3].Args[0])));

        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((Rig.Station, ItemFlags.Hangar), (ship.LocationId, ship.Flag));
    }

    [PgFact]
    public async Task A_docking_that_is_reported_twice_is_finished_once()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        ((Sink)session.Sink!).Sent.Clear();

        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Rig.Station));
        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Rig.Station));
        await Rig.UntilAsync(() => session.GetInt("stationid") == Rig.Station);
        await Task.Delay(100);

        var told = Sequence.Of(session);
        Assert.Equal(1, told.Count(name => name == "OnDockingFinished"));
        Assert.Equal(1, told.Count(name => name == "OnInvulnCancelled"));
        Assert.Equal(1, told.Count(name => name == "OnItemsChanged"));
    }

    [PgFact]
    public async Task A_ship_can_undock_again_after_it_has_docked()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Rig.Station));
        await Rig.UntilAsync(() => session.GetInt("stationid") == Rig.Station);

        var (_, answer) = await UndockAsync(rig, session);
        Assert.IsType<PyTuple>(answer);
        Assert.True(rig.Space.IsInSpace(session));
    }

    // ── losing the ship ──────────────────────────────────────────────────────

    [PgFact]
    public async Task When_the_simulation_is_lost_the_ship_docks_at_the_station_the_character_left()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        ((Sink)session.Sink!).Sent.Clear();

        rig.Sim.Lose();
        await Rig.UntilAsync(() => session.GetInt("stationid") == Rig.Station);

        Assert.Contains("OnDockingFinished", Sequence.Of(session));
        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((Rig.Station, ItemFlags.Hangar), (ship.LocationId, ship.Flag));
        Assert.False(rig.Space.IsInSpace(session));
    }

    [PgFact]
    public async Task When_a_session_ends_in_space_the_simulation_is_told_and_the_ship_is_put_back_in_the_hangar()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);

        session.Close();

        Assert.Equal(new SimLeave((ulong)session.SessionId, LeaveWhy.LoggedOff), rig.Sim.Heard.Last());
        await Rig.UntilAsync(() => rig.Items.GetAsync(character.ShipId).GetAwaiter().GetResult() is { LocationId: Rig.Station });
        Assert.False(rig.Space.IsInSpace(session));
    }
}
