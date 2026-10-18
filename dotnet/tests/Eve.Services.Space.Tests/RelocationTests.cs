using Eve.Inventory;
using Eve.Ships;
using Eve.Space;
using Eve.Universe;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The game master's relocation (<c>/solar</c> and <c>/tele</c> reach it through <see cref="SpaceSessions.RelocateAsync"/>) on the real stores: a pilot that is docked
/// or in space goes into space in another solar system, at the system's anchor or at the place given. The simulation lets the ship go from its old scene and takes
/// it in the new one, the ship and the character are written in the new system, and the session is told the change. A refused entry puts the ship back in its
/// station. Database-backed (<see cref="PgFactAttribute"/>).
/// </summary>
public class RelocationTests
{
    /// <summary>A solar system of the static data other than the one the given system is.</summary>
    private static SolarSystemInfo Elsewhere(Rig rig, int system) => rig.Systems.Systems.First(candidate => candidate.Id != system);

    [PgFact]
    public async Task A_docked_pilot_relocated_into_another_system_is_in_space_there_at_the_anchor()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var origin = (int)session.GetInt("solarsystemid2")!.Value;
        var target = Elsewhere(rig, origin);

        Assert.Null(await rig.Space.RelocateAsync(session, target.Id, null, null, CancellationToken.None));

        var enter = Assert.Single(rig.Sim.Heard.OfType<SimEnter>());
        Assert.Equal(((ulong)session.SessionId, target.Id, EnterHow.Anchor), (enter.SessionId, enter.SolarSystemId, enter.How));
        Assert.Null(enter.Position);
        Assert.True(rig.Space.IsInSpace(session));
        Assert.Equal(target.Id, rig.Space.SolarSystemOf(session));
        Assert.Equal((long)target.Id, session.GetInt("solarsystemid"));
        Assert.Null(session.GetInt("stationid"));

        var ship = await rig.Items.GetAsync(character.ShipId);
        Assert.Equal((target.Id, CharacterShips.InSpaceFlag), (ship!.LocationId, ship.Flag));
        var snapshot = await rig.Ships.LoadAsync(session.CharacterId!.Value);
        Assert.Equal((target.Id, target.ConstellationId, target.RegionId), (snapshot!.Character.SolarSystemId, snapshot.Character.ConstellationId, snapshot.Character.RegionId));
    }

    [PgFact]
    public async Task A_pilot_in_space_leaves_its_scene_and_enters_the_other_system_at_the_place_given()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        await rig.Space.UndockAsync(session, null, CancellationToken.None);
        Assert.True(rig.Space.IsInSpace(session));
        var origin = rig.Space.SolarSystemOf(session)!.Value;
        var target = Elsewhere(rig, origin);
        double[] place = [1_000.0, 2_000.0, 3_000.0];
        double[] facing = [-1.0, 0.0, 0.0];

        Assert.Null(await rig.Space.RelocateAsync(session, target.Id, place, facing, CancellationToken.None));

        // the scene it left lets the ship go before the new scene takes it (the undock's enter comes first)
        var after = rig.Sim.Heard.ToArray().SkipWhile(message => message is not SimLeave).ToArray();
        Assert.Equal(new SimLeave((ulong)session.SessionId, LeaveWhy.Jumped), after[0]);
        var enter = Assert.IsType<SimEnter>(after[1]);
        Assert.Equal((target.Id, EnterHow.InSpace), (enter.SolarSystemId, enter.How));
        Assert.Equal(place, enter.Position);
        Assert.Equal(facing, enter.Direction);
        Assert.True(rig.Space.IsInSpace(session));
        Assert.Equal(target.Id, rig.Space.SolarSystemOf(session));
        var ship = await rig.Items.GetAsync(character.ShipId);
        Assert.Equal((target.Id, CharacterShips.InSpaceFlag), (ship!.LocationId, ship.Flag));
    }

    [PgFact]
    public async Task A_refused_entry_puts_the_ship_back_in_the_station_of_its_character()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var origin = (int)session.GetInt("solarsystemid2")!.Value;
        var target = Elsewhere(rig, origin);
        rig.Sim.EnterAnswer = "SCENE_FULL";

        var refusal = await rig.Space.RelocateAsync(session, target.Id, null, null, CancellationToken.None);

        Assert.Equal("Space is not available right now. Try again in a moment.", refusal);
        Assert.False(rig.Space.IsInSpace(session));
        var ship = await rig.Items.GetAsync(character.ShipId);
        Assert.Equal((Rig.Station, ItemFlags.Hangar), (ship!.LocationId, ship.Flag));
        var snapshot = await rig.Ships.LoadAsync(session.CharacterId!.Value);
        Assert.Equal(origin, snapshot!.Character.SolarSystemId);
    }

    [PgFact]
    public async Task A_docked_pilot_is_found_at_the_anchor_of_its_system()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var origin = (int)session.GetInt("solarsystemid2")!.Value;

        var destination = await rig.Space.PilotDestinationAsync(session, CancellationToken.None);

        Assert.Equal(origin, destination!.Value.SolarSystemId);
        Assert.Null(destination.Value.Position);
        Assert.Null(destination.Value.Direction);
    }

    [PgFact]
    public async Task An_unknown_solar_system_is_refused_without_a_move()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);

        Assert.Equal("Solar system not found: 1.", await rig.Space.RelocateAsync(session, 1, null, null, CancellationToken.None));
        Assert.Empty(rig.Sim.Heard.OfType<SimEnter>());
        Assert.False(rig.Space.IsInSpace(session));
    }
}
