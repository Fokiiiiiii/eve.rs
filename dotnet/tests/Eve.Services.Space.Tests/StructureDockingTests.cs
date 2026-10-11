using System.Threading;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// ST-3 a: <c>structureDocking.Dock</c> on the host's space service (skipped without EVE_PG_URL; skipped is not passed). A structure is a record the host
/// knows (<see cref="SpaceSessions.StructureExistsAsync"/>) and the character's access to it (<see cref="SpaceSessions.StructureDockingAllowedAsync"/>); the
/// simulation is the fake one. A docking out of range is answered with the distance, in range the simulation is asked to accept it, and the ship and the
/// session are in the structure when the simulation reports it docked.
/// </summary>
public sealed class StructureDockingTests
{
    private const long Structure = 1_040_000_000_001;

    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> InSpaceAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        Events.Of(session);
        rig.Space.StructureExistsAsync = (id, _) => Task.FromResult(id == Structure);
        rig.Space.StructureDockingAllowedAsync = (_, _, _) => Task.FromResult(true);
        return (rig, character, session);
    }

    private static IEnumerable<SimCommand> Docks(Rig rig) =>
        rig.Sim.Heard.OfType<SimCommand>().Where(command => command.Code == SimCommands.Dock);

    /// <summary>The argument of a user error's payload, by its name.</summary>
    private static PyValue ErrorArgument(MachoWrappedException error, string name)
    {
        var values = (PyDict)((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items[1];
        return values.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;
    }

    [PgFact]
    public async Task A_dock_out_of_range_is_answered_with_the_distance_and_in_range_the_simulation_is_asked_to_accept_it()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;
        var ship = session.GetInt("shipid")!.Value;

        rig.Sim.DockAnswer = new CommandReply(false, 0, null, 15_974.5);
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.DockStructureAsync(session, Structure, ship, CancellationToken.None));
        Assert.Equal("DockingApproach", error.Error.UserErrorMessage);
        Assert.Equal(15_975L, ((PyInt)ErrorArgument(error, "distance")).Value);
        var approach = Assert.Single(Docks(rig));
        Assert.Equal(((ulong)session.SessionId, (double)Structure), (approach.SessionId, approach.Args.Single()));
        Assert.True(rig.Space.IsInSpace(session), "the ship is on its way to the structure, not gone");

        rig.Sim.DockAnswer = new CommandReply(true, 134351527136350000, null);
        await rig.Space.DockStructureAsync(session, Structure, ship, CancellationToken.None);
        var accepted = Docks(rig).Last();
        Assert.Equal((double)Structure, accepted.Args.Single());
        Assert.Equal(2, Docks(rig).Count());
        Assert.True(rig.Space.IsInSpace(session), "accepted, the ship stays in space until the simulation reports it docked");
    }

    [PgFact]
    public async Task A_docking_the_simulation_reports_puts_the_ship_in_the_structure_hangar_and_the_session_in_the_structure()
    {
        var (rig, character, session) = await InSpaceAsync();
        await using var _ = rig;
        var system = session.GetInt("solarsystemid2")!.Value;
        // a bound object the docking lets go of (the reference's OnMachoObjectDisconnect), as the station docking test has
        await rig.BindBeyonceAsync(session);
        await rig.Space.DockStructureAsync(session, Structure, character.ShipId, CancellationToken.None);
        Events.Of(session, clear: false);
        ((Sink)session.Sink!).Sent.Clear();

        var locationChanged = new TaskCompletionSource<(long OldStation, long OldStructure, long? NewStation, long? NewStructure)>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Space.DockedLocationChanged = (changedSession, oldStation, oldStructure) =>
            locationChanged.TrySetResult((oldStation, oldStructure, changedSession.GetInt("stationid"), changedSession.GetInt("structureid")));
        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Structure));
        await rig.Space.QuiesceAsync();
        await Rig.UntilAsync(() => locationChanged.Task.IsCompleted);
        Assert.Equal((0L, 0L, (long?)null, (long?)Structure), await locationChanged.Task);

        Assert.Equal(["OnInvulnCancelled", "OnMachoObjectDisconnect", "OnItemsChanged", "$sessionchange", "OnDockingFinished"], Sequence.Of(session, clear: false));
        Assert.Equal((long?)Structure, session.GetInt("structureid"));
        Assert.Null(session.GetInt("stationid"));
        Assert.Equal((long?)system, session.GetInt("solarsystemid"));
        Assert.Equal((long?)system, session.GetInt("locationid"));
        Assert.False(rig.Space.IsInSpace(session));

        var events = Events.Of(session);
        var items = events.Single(e => e.Name == "OnItemsChanged");
        var place = (PyTuple)items.Args[2];
        Assert.Equal(("Structure", Structure, "StructureShipHangar"), (JsSemantics.StringValue(place.Items[0]), Events.Number(place.Items[1]), JsSemantics.StringValue(place.Items[2])));
        var docked = events.Single(e => e.Name == "OnDockingFinished");
        Assert.Equal(Structure, Events.Number(docked.Args[0]));

        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((Structure, ItemFlags.Hangar), (ship.LocationId, ship.Flag));
        Assert.Equal(Structure, (await rig.Characters.GetAsync(character.CharacterId))!.StationId);
    }

    [PgFact]
    public async Task A_structure_docking_is_refused_for_an_unknown_structure_for_a_character_without_access_and_for_a_hull_too_large()
    {
        var (rig, _, session) = await InSpaceAsync();
        await using var _ = rig;
        var ship = session.GetInt("shipid")!.Value;

        var unknown = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.DockStructureAsync(session, Structure + 1, ship, CancellationToken.None));
        Assert.Equal("TargetingAttemptCancelled", unknown.Error.UserErrorMessage);

        // without the docking access the simulation is not asked at all
        rig.Space.StructureDockingAllowedAsync = (_, _, _) => Task.FromResult(false);
        var denied = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.DockStructureAsync(session, Structure, ship, CancellationToken.None));
        Assert.Equal("DockingRequestDenied", denied.Error.UserErrorMessage);
        Assert.Equal("Structure docking access denied.", JsSemantics.StringValue(ErrorArgument(denied, "reason")));
        Assert.Empty(Docks(rig));

        // a hull the structure's docks do not take is refused by the simulation, and the reference's label is named
        rig.Space.StructureDockingAllowedAsync = (_, _, _) => Task.FromResult(true);
        rig.Sim.DockAnswer = new CommandReply(false, 0, "SHIP_TOO_LARGE");
        var large = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.DockStructureAsync(session, Structure, ship, CancellationToken.None));
        Assert.Equal("DockingRequestDenied", large.Error.UserErrorMessage);
        var reason = Assert.IsType<PyTuple>(ErrorArgument(large, "reason"));
        Assert.Equal((101L, "UI/Station/StationManagment/ShipTooLarge"), (((PyInt)reason.Items[0]).Value, JsSemantics.StringValue(reason.Items[1])));
        Assert.Single(Docks(rig));
        Assert.True(rig.Space.IsInSpace(session), "a refused docking leaves the ship where it is");
    }
}
