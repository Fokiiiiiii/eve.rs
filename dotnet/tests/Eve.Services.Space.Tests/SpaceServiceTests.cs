using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

public class SpaceServiceTests
{
    private static long Id(PyValue value) => Events.Number(value);

    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> DockedAsync(TimeSpan? protection = null)
    {
        var rig = await Rig.CreateAsync(protection);
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
        Assert.Equal(3.0, enter.Ship.WarpSpeedAu); // an Ibis warps at 3 AU a second (the reference's WarpTo says 3000)
        Assert.Equal(3.0, SimShips.WarpSpeedAu(1.0, 3.0));
        Assert.Equal(1.5, SimShips.WarpSpeedAu(0.5, 3.0)); // a base speed that something changed counts
        Assert.Equal(3.0, SimShips.WarpSpeedAu(0.0, 3.0)); // no base speed is the base of one
        Assert.Equal(1.0, SimShips.WarpSpeedAu(1.0, 0.0)); // no multiplier is a multiplier of one
        // what the ship may lock: the lower of the ship's and the pilot's limit when both allow some, else the higher
        Assert.Equal([3, 3, 4, 4, 0], new[] { SimShips.LockLimit(3, 4), SimShips.LockLimit(4, 3), SimShips.LockLimit(0, 4), SimShips.LockLimit(4, 0), SimShips.LockLimit(-1, 0) });
        // the numbers of the starter Ibis with the skills of a new character: the range is the one the reference told a client that locked from too far
        // (`TargetNotWithinRangeGeneric`, 29700), and a lock of 52 m at 522.5 mm takes 3.55 s (the recorded one was had 3.8 s after it was asked for: the reference's tick and the call)
        Assert.Equal((29_700.0, 522.5, 52.0, 3), (enter.Ship.MaxTargetRange, enter.Ship.ScanResolution, enter.Ship.SignatureRadius, enter.Ship.MaxLockedTargets));
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
        await rig.BeyonceCallAsync(session, "CmdFollowBall", oid, new PyInt((int)Rig.Station), new PyInt(2500));
        await rig.BeyonceCallAsync(session, "CmdOrbit", oid, new PyInt((int)Rig.Station), new PyReal(5000.5));
        await rig.BeyonceCallAsync(session, "CmdAlignTo", oid, new PyInt((int)Rig.Station));
        await rig.BeyonceCallWithAsync(session, "CmdAlignTo", oid, [], ("dstID", new PyInt((int)Rig.Station))); // named, as the client may call it
        await rig.BeyonceCallAsync(session, "UpdateStateRequest", oid);

        Assert.Equal(
            new SimMessage[]
            {
                new SimCommand((ulong)session.SessionId, SimCommands.SetSpeedFraction, [0.5]),
                new SimCommand((ulong)session.SessionId, SimCommands.GotoDirection, [1, 0, 0]),
                new SimCommand((ulong)session.SessionId, SimCommands.Stop, []),
                new SimCommand((ulong)session.SessionId, SimCommands.FollowBall, [Rig.Station, 2500]),
                new SimCommand((ulong)session.SessionId, SimCommands.Orbit, [Rig.Station, 5000.5]),
                new SimCommand((ulong)session.SessionId, SimCommands.AlignTo, [Rig.Station]),
                new SimCommand((ulong)session.SessionId, SimCommands.AlignTo, [Rig.Station]),
                new SimUpdateState((ulong)session.SessionId),
            }.Select(Describe),
            rig.Sim.Heard.Skip(before).Select(Describe));

        // calls that were not made on an object of this session, or that carry nonsense, are not passed on
        await rig.BeyonceCallAsync(session, "CmdStop", null);
        await rig.BeyonceCallAsync(session, "CmdStop", "N=65450:999");
        await rig.BeyonceCallAsync(session, "CmdSetSpeedFraction", oid, new PyStr("fast"));
        await rig.BeyonceCallAsync(session, "CmdGotoDirection", oid, new PyReal(1));
        await rig.BeyonceCallAsync(session, "CmdFollowBall", oid, new PyStr("station"), new PyInt(500));
        await rig.BeyonceCallAsync(session, "CmdOrbit", "N=65450:999", new PyInt((int)Rig.Station), new PyInt(500));
        await rig.BeyonceCallAsync(session, "CmdAlignTo", oid, new PyInt(0));
        Assert.Equal(before + 8, rig.Sim.Heard.Count);
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
    public async Task The_first_movement_the_simulation_accepts_while_the_protection_lasts_ends_it_and_the_client_is_told_once_after_the_update()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);
        var id = (ulong)session.SessionId;

        // a command the simulation refuses costs the pilot nothing, and neither does a docking that is accepted
        await rig.BeyonceCallAsync(session, "CmdAlignTo", oid, new PyInt(42));
        rig.Sim.Say(new SimRefused(id, SimCommands.AlignTo, "UNKNOWN_TARGET"));
        rig.Sim.Say(new SimAccepted(id, SimCommands.Dock, 134351527136350000));
        rig.Sim.Say(Update(session));
        Assert.Equal(["DoDestinyUpdate"], Events.Of(session).Select(e => e.Name));

        // a movement it accepts does, whatever it is (stopping included), and the client hears of it after the update the command made
        await rig.BeyonceCallAsync(session, "CmdStop", oid);
        rig.Sim.Say(Update(session));
        Assert.Equal(["DoDestinyUpdate"], Events.Of(session).Select(e => e.Name));
        rig.Sim.Say(new SimAccepted(id, SimCommands.Stop, 134351527136350000));
        var events = Events.Of(session);
        Assert.Equal(["OnInvulnCancelled"], events.Select(e => e.Name));
        Assert.Equal(("OnInvulnCancelled", "shipid"), (events[0].Name, events[0].IdType));
        Assert.Equal(session.GetInt("shipid"), Id(events[0].Args[0]));

        // once
        await rig.BeyonceCallAsync(session, "CmdSetSpeedFraction", oid, new PyReal(1));
        rig.Sim.Say(Update(session));
        rig.Sim.Say(new SimAccepted(id, SimCommands.SetSpeedFraction, 134351527136350000));
        Assert.Equal(["DoDestinyUpdate"], Events.Of(session).Select(e => e.Name));
    }

    // ── targets ──────────────────────────────────────────────────────────────

    private static string Describe(TargetingResult? result) =>
        result is null ? "none" : $"{result.Ok} {result.Pending} [{string.Join(",", result.Targets)}] {result.Reason} {result.TargetType} {result.DesiredRange} {result.ActualDistance}";

    [PgFact]
    public async Task An_order_about_targets_is_given_to_the_simulation_and_is_answered_with_what_it_says_only_for_a_ship_in_space()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        Assert.Null(await rig.Space.OrderAsync(session, TargetingOrder.Lock, [42], CancellationToken.None));
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimCommand);
        await InSpaceAsync(rig, session);
        rig.Sim.TargetAnswer = new CommandReply(true, 0, null) { Pending = false, Targets = [60015249, 9988400002151] };
        var answer = await rig.Space.OrderAsync(session, TargetingOrder.Remove, [42, 43], CancellationToken.None);
        Assert.Equal("True False [60015249,9988400002151]  0 0 0", Describe(answer));
        Assert.Equal(Describe(new SimCommand((ulong)session.SessionId, SimCommands.RemoveTargets, [42, 43])), Describe(rig.Sim.Heard.Last()));
        foreach (var (order, code) in new[] { (TargetingOrder.Lock, SimCommands.AddTarget), (TargetingOrder.Cancel, SimCommands.CancelAddTarget), (TargetingOrder.Clear, SimCommands.ClearTargets), (TargetingOrder.Targets, SimCommands.GetTargets), (TargetingOrder.Targeters, SimCommands.GetTargeters) })
        {
            await rig.Space.OrderAsync(session, order, [], CancellationToken.None);
            Assert.Equal(code, ((SimCommand)rig.Sim.Heard.Last()).Code);
        }
    }

    [PgFact]
    public async Task A_lock_the_simulation_accepts_ends_the_protection_and_the_client_is_told_twice_as_the_reference_tells_it()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        Events.Of(session);
        var answer = await rig.Space.OrderAsync(session, TargetingOrder.Lock, [9988400002151], CancellationToken.None);
        Assert.Equal("True True []  0 0 0", Describe(answer));
        var told = Events.Of(session);
        Assert.Equal(["OnInvulnCancelled", "OnInvulnCancelled"], told.Select(e => e.Name));
        Assert.All(told, e => Assert.Equal(("shipid", session.GetInt("shipid")), (e.IdType, Id(e.Args[0]))));
        // the protection is over: the next lock is told once, and so is a movement that follows
        await rig.Space.OrderAsync(session, TargetingOrder.Lock, [9988400002152], CancellationToken.None);
        Assert.Equal(["OnInvulnCancelled"], Events.Of(session).Select(e => e.Name));
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 134351527136350000));
        Assert.Empty(Events.Of(session));
    }

    [PgFact]
    public async Task A_lock_that_is_refused_costs_the_pilot_its_protection_nothing_and_says_why()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        Events.Of(session);
        rig.Sim.TargetAnswer = new CommandReply(false, 0, "TARGET_OUT_OF_RANGE") { TargetType = 92885, DesiredRange = 29700, ActualDistance = 306288156879.55023 };
        var answer = await rig.Space.OrderAsync(session, TargetingOrder.Lock, [60015249], CancellationToken.None);
        Assert.Equal("False False [] TARGET_OUT_OF_RANGE 92885 29700 306288156879.55023", Describe(answer));
        Assert.Empty(Events.Of(session));
        // the protection is still there to be ended by a movement
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 134351527136350000));
        Assert.Equal(["OnInvulnCancelled"], Events.Of(session).Select(e => e.Name));
    }

    [PgFact]
    public async Task Questions_about_targets_do_not_end_the_protection()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        Events.Of(session);
        rig.Sim.TargetAnswer = new CommandReply(true, 0, null) { Targets = [1] };
        foreach (var order in new[] { TargetingOrder.Targets, TargetingOrder.Targeters, TargetingOrder.Clear, TargetingOrder.Cancel, TargetingOrder.Remove })
        {
            await rig.Space.OrderAsync(session, order, [], CancellationToken.None);
        }
        Assert.Empty(Events.Of(session));
    }

    [PgFact]
    public async Task Without_the_simulation_a_targeting_order_is_a_message_the_client_shows()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        rig.Sim.TargetsUnavailable = true;
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.Space.OrderAsync(session, TargetingOrder.Lock, [1], CancellationToken.None));
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
        Assert.Equal("CustomNotify", ((PyStr)arguments[0]).Value);
        Assert.Equal("Space is not available right now. Try again in a moment.", ((PyStr)((PyDict)arguments[1]).Entries.Single().Value).Value);
    }

    [PgFact]
    public async Task The_orders_of_a_pilot_are_asked_one_at_a_time_and_each_is_answered_to_the_one_that_asked()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        rig.Sim.TargetGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = rig.Space.OrderAsync(session, TargetingOrder.Lock, [1], CancellationToken.None);
        var second = rig.Space.OrderAsync(session, TargetingOrder.Lock, [2], CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal([1.0], rig.Sim.Heard.OfType<SimCommand>().Where(c => c.Code == SimCommands.AddTarget).SelectMany(c => c.Args).ToArray());
        rig.Sim.TargetGate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal([1.0, 2.0], rig.Sim.Heard.OfType<SimCommand>().Where(c => c.Code == SimCommands.AddTarget).SelectMany(c => c.Args).ToArray());
    }

    // ── warp ─────────────────────────────────────────────────────────────────

    [PgFact]
    public async Task A_warp_is_given_to_the_simulation_with_the_range_the_client_asked_for_and_ends_the_protection()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);
        var before = rig.Sim.Heard.Count;

        var answer = await rig.BeyonceCallWithAsync(session, "CmdWarpToStuff", oid, [new PyStr("item"), new PyInt(40491233)], ("minRange", new PyInt(30000)));
        Assert.Equal(PyNone.Instance, answer);
        Assert.Equal(
            [Describe(new SimCommand((ulong)session.SessionId, SimCommands.WarpTo, [40491233, 30000]))],
            rig.Sim.Heard.Skip(before).Select(Describe));
        // no range asked for is none
        await rig.BeyonceCallAsync(session, "CmdWarpToStuff", oid, new PyStr("item"), new PyInt(40491233));
        Assert.Equal(Describe(new SimCommand((ulong)session.SessionId, SimCommands.WarpTo, [40491233, 0])), Describe(rig.Sim.Heard.Last()));

        // the ship has left its protection: the client is told once, whatever the simulation said before
        var told = Events.Of(session).Where(e => e.Name == "OnInvulnCancelled").ToArray();
        Assert.Single(told);
        Assert.Equal(("OnInvulnCancelled", "shipid"), (told[0].Name, told[0].IdType));
    }

    [PgFact]
    public async Task A_warp_that_cannot_be_done_is_a_message_the_client_shows_and_one_of_another_kind_of_place_is_not_passed_on()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);
        var before = rig.Sim.Heard.Count;

        foreach (var reason in new[] { "TARGET_NOT_FOUND", "WARP_DISTANCE_TOO_CLOSE" })
        {
            rig.Sim.WarpAnswer = new CommandReply(false, 0, reason);
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.BeyonceCallAsync(session, "CmdWarpToStuff", oid, new PyStr("item"), new PyInt(99999999)));
            Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
            var values = (PyDict)((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items[1];
            Assert.Equal("You cannot warp there right now.", JsSemantics.StringValue(values.Entries.Single().Value));
        }
        Assert.DoesNotContain(Events.Of(session), e => e.Name == "OnInvulnCancelled"); // a warp that was refused costs the pilot nothing

        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.BeyonceCallAsync(session, "CmdWarpToStuff", oid, new PyStr("bookmark"), new PyInt(5)));
        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.BeyonceCallAsync(session, "CmdWarpToStuff", oid, new PyStr("item"), new PyStr("planet")));
        Assert.Equal(before + 2, rig.Sim.Heard.Count);

        // not from an object of this session
        Assert.Equal(PyNone.Instance, await rig.BeyonceCallAsync(session, "CmdWarpToStuff", null, new PyStr("item"), new PyInt(40491233)));
        Assert.Equal(before + 2, rig.Sim.Heard.Count);
    }

    [PgFact]
    public async Task A_ship_that_is_in_warp_is_told_so_when_it_is_asked_to_warp_or_to_stop_and_is_not_otherwise_answered()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);

        rig.Sim.WarpAnswer = new CommandReply(false, 0, "IN_WARP");
        Assert.Equal(PyNone.Instance, await rig.BeyonceCallAsync(session, "CmdWarpToStuff", oid, new PyStr("item"), new PyInt(40491233)));
        var told = Assert.Single(Events.Of(session));
        Assert.Equal(("OnRemoteMessage", "clientID"), (told.Name, told.IdType));
        Assert.Equal("ShipInWarp", JsSemantics.StringValue(told.Args[0]));
        Assert.Equal(PyNone.Instance, told.Args[1]);

        // a stop in warp is refused by the simulation and the client is told the same
        await rig.BeyonceCallAsync(session, "CmdStop", oid);
        rig.Sim.Say(new SimRefused((ulong)session.SessionId, SimCommands.Stop, "IN_WARP"));
        Assert.Equal(["OnRemoteMessage"], Events.Of(session).Select(e => e.Name));
        // other orders that are refused in warp say nothing
        rig.Sim.Say(new SimRefused((ulong)session.SessionId, SimCommands.GotoDirection, "IN_WARP"));
        Assert.Empty(Events.Of(session));
    }

    [PgFact]
    public async Task When_the_time_is_up_and_the_pilot_has_not_flown_the_client_is_told_the_protection_ended_once()
    {
        var (rig, _, session) = await DockedAsync(TimeSpan.FromMilliseconds(150));
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        Events.Of(session);

        await Rig.UntilAsync(() => Events.Of(session, clear: false).Any(e => e.Name == "OnInvulnCancelled"));
        var events = Events.Of(session);
        var told = events.Single(e => e.Name == "OnInvulnCancelled");
        Assert.Equal(("OnInvulnCancelled", "shipid"), (told.Name, told.IdType));
        Assert.Equal(session.GetInt("shipid"), Id(told.Args[0]));

        // a movement after that has nothing left to end
        var oid = await rig.BindBeyonceAsync(session);
        await rig.BeyonceCallAsync(session, "CmdStop", oid);
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 134351527136350000));
        await Task.Delay(300);
        Assert.DoesNotContain(Events.Of(session), e => e.Name == "OnInvulnCancelled");
    }

    [PgFact]
    public async Task A_pilot_who_flew_before_the_time_was_up_is_not_told_again_when_it_is()
    {
        var (rig, _, session) = await DockedAsync(TimeSpan.FromMilliseconds(400));
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);

        await rig.BeyonceCallAsync(session, "CmdStop", oid);
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 134351527136350000));
        Assert.Equal(["OnInvulnCancelled"], Events.Of(session).Select(e => e.Name));
        await Task.Delay(800);
        Assert.Empty(Events.Of(session));
    }

    // ── jumps ────────────────────────────────────────────────────────────────

    private const long Gate = 50016630;
    private const long NextGate = 50016839;
    private const int NextSystem = 30100034;

    private static Task<PyValue> JumpAsync(Rig rig, ClientSession session, string oid, long ship) =>
        rig.BeyonceCallAsync(session, "CmdStargateJump", oid, new PyInt((int)Gate), new PyInt((int)NextGate), new PyLong(ship));

    [PgFact]
    public async Task A_jump_is_given_to_the_simulation_the_client_is_told_the_queue_and_the_answer_is_the_object_and_the_time()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);
        var before = rig.Sim.Heard.Count;

        var answer = (PyTuple)await JumpAsync(rig, session, oid, character.ShipId);
        Assert.Equal(oid, JsSemantics.StringValue(answer.Items[0]));
        Assert.Equal(Py.FileTimeFromUnixMs(Rig.ClockMs + 10_000), Id(answer.Items[1]));
        Assert.Equal(
            [Describe(new SimCommand((ulong)session.SessionId, SimCommands.Jump, [Gate, NextGate]))],
            rig.Sim.Heard.Skip(before).Select(Describe));

        var told = Events.Of(session);
        var queue = Assert.Single(told, e => e.Name == "OnJumpQueueUpdate");
        Assert.Equal("clientID", queue.IdType);
        Assert.Equal(NextSystem, Id(queue.Args[0]));
        Assert.Equal(1000, Id(queue.Args[1]));
        Assert.Equal([character.CharacterId], ((PyTuple)queue.Args[2]).Items.Select(Id));
        var stamps = ((PyDict)queue.Args[3]).Entries.Select(e => (Id(e.Key), Id(e.Value))).ToArray();
        Assert.Equal([(character.CharacterId, Py.FileTimeFromUnixMs(Rig.ClockMs))], stamps);
        Assert.Equal(180, Id(queue.Args[4]));
        Assert.True(rig.Space.IsInSpace(session), "the ship has not left yet");

        // asking again does not queue the pilot twice
        await JumpAsync(rig, session, oid, character.ShipId);
        var again = Events.Of(session).Single(e => e.Name == "OnJumpQueueUpdate");
        Assert.Equal([character.CharacterId], ((PyTuple)again.Args[2]).Items.Select(Id));
    }

    [PgFact]
    public async Task A_jump_that_cannot_be_done_is_the_message_the_reference_tells()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Events.Of(session);

        async Task<MachoWrappedException> Asked(string reason, long? ship = null)
        {
            rig.Sim.JumpAnswer = new CommandReply(false, 0, reason);
            return await Assert.ThrowsAsync<MachoWrappedException>(() => JumpAsync(rig, session, oid, ship ?? character.ShipId));
        }

        static string Info(MachoWrappedException error)
        {
            var values = (PyDict)((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items[1];
            var info = Assert.Single(values.Entries, e => JsSemantics.StringValue(e.Key) == "info").Value;
            return info is PyTuple label ? $"{Id(label.Items[0])}:{JsSemantics.StringValue(label.Items[1])}" : JsSemantics.StringValue(info)!;
        }

        Assert.Equal("TargetingAttemptCancelled", (await Asked("STARGATE_NOT_FOUND")).Error.UserErrorMessage);
        Assert.Equal("TargetingAttemptCancelled", (await Asked("STARGATE_DESTINATION_MISMATCH")).Error.UserErrorMessage);
        Assert.Equal("DeniedShipChanged", (await Asked("NO_SHIP_IN_SPACE")).Error.UserErrorMessage);
        Assert.Equal("DeniedShipChanged", (await Asked("WRONG_SOLAR_SYSTEM")).Error.UserErrorMessage);
        var far = await Asked("TOO_FAR_FROM_STARGATE");
        Assert.Equal(("CustomInfo", "101:UI/Menusvc/MenuHints/NotWithingMaxJumpDist"), (far.Error.UserErrorMessage, Info(far)));
        Assert.Equal("101:UI/GateIcons/GateClosed", Info(await Asked("STARGATE_NOT_ACTIVE")));
        Assert.Equal("Stargate jump already in progress.", Info(await Asked("STARGATE_JUMP_IN_PROGRESS")));
        Assert.Equal("The stargate jump could not be completed.", Info(await Asked("IN_WARP")));
        // a ship that is not the one flown is refused before the simulation hears of it
        var before = rig.Sim.Heard.Count;
        Assert.Equal("DeniedShipChanged", (await Asked("STARGATE_NOT_FOUND", character.ShipId + 1)).Error.UserErrorMessage);
        Assert.Equal(before, rig.Sim.Heard.Count);
        Assert.DoesNotContain(Events.Of(session), e => e.Name == "OnJumpQueueUpdate");
        Assert.True(rig.Space.IsInSpace(session));
    }

    [PgFact]
    public async Task A_jump_asked_of_a_ship_that_is_not_in_space_or_on_an_object_the_session_was_not_given_does_nothing_or_is_denied()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        // docked: the call is to nothing the session was given
        Assert.Equal(PyNone.Instance, await JumpAsync(rig, session, "N=1:1", character.ShipId));
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        Assert.Equal(PyNone.Instance, await JumpAsync(rig, session, "N=1:999", character.ShipId));
        Assert.DoesNotContain(rig.Sim.Heard, m => m is SimCommand { Code: SimCommands.Jump });
        Assert.NotEmpty(oid);
    }

    [PgFact]
    public async Task When_the_simulation_says_the_ship_has_jumped_the_client_is_told_in_the_reference_order_and_the_ship_and_the_character_are_in_the_other_system()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var from = (int)session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);
        await JumpAsync(rig, session, oid, character.ShipId);
        Events.Of(session, clear: false);
        ((Sink)session.Sink!).Sent.Clear();

        rig.Sim.Say(new SimJumped((ulong)session.SessionId, from, Gate, NextSystem, NextGate));
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == NextSystem);

        Assert.Equal(["OnMachoObjectDisconnect", "OnItemChange", "OnJumpCloakUpdated", "$sessionchange"], Sequence.Of(session, clear: false));
        Assert.Equal(
            [
                ("solarsystemid", new PyInt(from), new PyInt(NextSystem)),
                ("solarsystemid2", new PyInt(from), new PyInt(NextSystem)),
                ("constellationid", new PyInt(20010001), new PyInt(20010002)),
                ("locationid", new PyInt(from), new PyInt(NextSystem)),
            ],
            Sequence.LastChange(session));

        var events = Events.Of(session);
        Assert.Equal(("OnMachoObjectDisconnect", "+clientID"), (events[0].Name, events[0].IdType));
        Assert.Equal(oid, JsSemantics.StringValue(events[0].Args[0]));
        var item = events[1];
        Assert.Equal(("OnItemChange", "clientID"), (item.Name, item.IdType));
        var row = (PyPackedRow)item.Args[0];
        Assert.Equal((character.ShipId, (long)NextSystem, 0L), (Id(row.Values[0]), Id(row.Values[3]), Id(row.Values[4])));
        Assert.Equal($"Undocking:{Rig.Station}", JsSemantics.StringValue(row.Values[8]));
        Assert.Equal([(3L, (long)from)], ((PyDict)item.Args[1]).Entries.Select(e => (Id(e.Key), Id(e.Value))));
        Assert.Equal(PyNone.Instance, item.Args[2]);
        var cloak = events[2];
        Assert.Equal(("OnJumpCloakUpdated", "shipid"), (cloak.Name, cloak.IdType));
        Assert.Equal((character.ShipId, Py.FileTimeFromUnixMs(Rig.ClockMs) + Eve.Services.Space.SpaceSessions.JumpCloakTicks, Eve.Services.Space.SpaceSessions.JumpCloakTicks), (Id(cloak.Args[0]), Id(cloak.Args[1]), Id(cloak.Args[2])));

        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((NextSystem, 0), (ship.LocationId, ship.Flag));
        var record = (await rig.Characters.GetAsync(character.CharacterId))!;
        Assert.Equal((NextSystem, 20010002), (record.SolarSystemId, record.ConstellationId));
        Assert.Equal(NextSystem, rig.Space.SolarSystemOf(session));

        // the client binds the ballpark of the new system, and the simulation is asked to send the scene
        var before = rig.Sim.Heard.Count;
        await rig.BindBeyonceAsync(session);
        session.RunAfterResponse();
        Assert.Contains(rig.Sim.Heard.Skip(before), m => m is SimBind);
    }

    [PgFact]
    public async Task A_jump_that_is_reported_twice_is_finished_once()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var from = (int)session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        ((Sink)session.Sink!).Sent.Clear();

        var jumped = new SimJumped((ulong)session.SessionId, from, Gate, NextSystem, NextGate);
        rig.Sim.Say(jumped);
        rig.Sim.Say(jumped);
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == NextSystem);
        await Task.Delay(200);

        Assert.Single(Sequence.Of(session), "$sessionchange");
        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal(NextSystem, ship.LocationId);
    }

    [PgFact]
    public async Task The_cloak_of_a_ship_that_has_jumped_ends_with_the_first_movement_and_the_client_is_told_once_in_the_reference_order()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var from = (int)session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        rig.Sim.Say(new SimJumped((ulong)session.SessionId, from, Gate, NextSystem, NextGate));
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == NextSystem);
        Events.Of(session);

        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.SetSpeedFraction, 0));
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 0));
        var told = Events.Of(session);
        Assert.Equal(["OnJumpCloakCancelled", "OnInvulnCancelled"], told.Select(e => e.Name));
        Assert.All(told, e => Assert.Equal(("shipid", character.ShipId), (e.IdType, Id(e.Args[0]))));
    }

    [PgFact]
    public async Task The_cloak_of_a_ship_that_has_jumped_ends_when_its_time_is_up_if_the_pilot_has_not_flown()
    {
        var rig = await Rig.CreateAsync(jumpCloak: TimeSpan.FromMilliseconds(150));
        await using var _ = rig;
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var from = (int)session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        rig.Sim.Say(new SimJumped((ulong)session.SessionId, from, Gate, NextSystem, NextGate));
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == NextSystem);
        Events.Of(session);

        await Rig.UntilAsync(() => ((Sink)session.Sink!).Sent.Count >= 2);
        Assert.Equal(["OnJumpCloakCancelled", "OnInvulnCancelled"], Events.Of(session).Select(e => e.Name));
        // and it is not told again by a movement
        rig.Sim.Say(new SimAccepted((ulong)session.SessionId, SimCommands.Stop, 0));
        Assert.Empty(Events.Of(session));
    }

    [PgFact]
    public async Task A_ship_that_docks_in_the_other_system_is_in_the_hangar_of_a_station_there()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var from = (int)session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        rig.Sim.Say(new SimJumped((ulong)session.SessionId, from, Gate, NextSystem, NextGate));
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == NextSystem);
        Events.Of(session);

        var station = rig.Stations.All.First(s => s.SolarSystemId == NextSystem);
        rig.Sim.Say(new SimDocked((ulong)session.SessionId, station.Id));
        await Rig.UntilAsync(() => session.GetInt("stationid") == station.Id);

        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((station.Id, ItemFlags.Hangar), (ship.LocationId, ship.Flag));
        var record = (await rig.Characters.GetAsync(character.CharacterId))!;
        Assert.Equal((station.Id, NextSystem), (record.StationId, record.SolarSystemId));
    }

    [PgFact]
    public async Task When_a_session_ends_after_a_jump_the_ship_is_put_back_in_the_hangar_of_the_station_it_left_and_the_character_is_in_its_system()
    {
        var (rig, character, session) = await DockedAsync();
        await using var _ = rig;
        var from = (int)session.GetInt("solarsystemid2")!.Value;
        await InSpaceAsync(rig, session);
        rig.Sim.Say(new SimJumped((ulong)session.SessionId, from, Gate, NextSystem, NextGate));
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == NextSystem);

        session.Close();
        await Rig.UntilAsync(() => rig.Items.GetAsync(character.ShipId).GetAwaiter().GetResult()!.Flag == ItemFlags.Hangar);

        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal(Rig.Station, ship.LocationId);
        var record = (await rig.Characters.GetAsync(character.CharacterId))!;
        Assert.Equal((from, Rig.Station), (record.SolarSystemId, record.StationId));
        Assert.Contains(rig.Sim.Heard, m => m is SimLeave { Why: LeaveWhy.LoggedOff });
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

        rig.Sim.DockAnswer = new CommandReply(false, 0, "STATION_NOT_FOUND");
        Assert.Equal(PyNone.Instance, await rig.BeyonceCallAsync(session, "CmdDock", oid, new PyInt((int)Rig.Station), new PyLong(1)));
        Assert.True(rig.Space.IsInSpace(session), "a refused docking leaves the ship where it is");
    }

    [PgFact]
    public async Task A_dock_from_beyond_the_range_is_answered_with_the_reference_user_error_and_the_distance_rounded_half_up()
    {
        var (rig, _, session) = await DockedAsync();
        await using var _ = rig;
        await InSpaceAsync(rig, session);
        var oid = await rig.BindBeyonceAsync(session);

        async Task<MachoWrappedException> Asked(double distance)
        {
            rig.Sim.DockAnswer = new CommandReply(false, 0, null, distance);
            return await Assert.ThrowsAsync<MachoWrappedException>(() => rig.BeyonceCallAsync(session, "CmdDock", oid, new PyInt((int)Rig.Station), new PyLong(1)));
        }

        static long DistanceOf(MachoWrappedException error)
        {
            var values = (PyDict)((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items[1];
            var (name, distance) = Assert.Single(values.Entries.Select(e => (JsSemantics.StringValue(e.Key), Id(e.Value))));
            Assert.Equal("distance", name);
            return distance;
        }

        var error = await Asked(15974.5);
        Assert.True(error.Error.IsUserError);
        Assert.Equal("DockingApproach", error.Error.UserErrorMessage);
        Assert.Equal(15975, DistanceOf(error));
        Assert.Equal(15974, DistanceOf(await Asked(15974.4)));
        Assert.Equal(0, DistanceOf(await Asked(0.4)));
        Assert.True(rig.Space.IsInSpace(session), "the ship is on its way, not gone");
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
