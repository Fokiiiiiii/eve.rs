using System.Threading;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// ST-3 b: <c>structureDocking.Undock</c> on the host's space service (skipped without EVE_PG_URL; skipped is not passed). The pilot docks at a structure
/// through the same finish as a station docking, then leaves it: the structure is put in the simulation first, the ship is in space in the system, and the
/// session has no structure any more. A structure or ship the session is not in is refused.
/// </summary>
public sealed class StructureUndockingTests
{
    private const long Structure = 1_040_000_000_001;

    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> DockedAtStructureAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        Events.Of(session);
        rig.Space.StructureExistsAsync = (id, _) => Task.FromResult(id == Structure);
        rig.Space.StructureDockingAllowedAsync = (_, _, _) => Task.FromResult(true);
        await rig.Space.DockStructureAsync(session, Structure, character.ShipId, CancellationToken.None);
        rig.Sim.Say(new SimDocked((ulong)session.SessionId, Structure));
        await rig.Space.QuiesceAsync();
        await Rig.UntilAsync(() => session.GetInt("structureid") == Structure);
        Events.Of(session);
        return (rig, character, session);
    }

    [PgFact]
    public async Task An_undock_from_the_structure_puts_the_ship_in_space_in_its_system_and_the_session_out_of_the_structure()
    {
        var (rig, character, session) = await DockedAtStructureAsync();
        await using var _ = rig;
        var system = session.GetInt("solarsystemid")!.Value;
        var locationChanges = new List<(long OldStation, long OldStructure, long? NewStation, long? NewStructure)>();
        rig.Space.DockedLocationChanged = (changedSession, oldStation, oldStructure) =>
            locationChanges.Add((oldStation, oldStructure, changedSession.GetInt("stationid"), changedSession.GetInt("structureid")));
        var placed = new List<int>();
        rig.Space.PlaceStructuresAsync = (solarSystemId, _) =>
        {
            placed.Add(solarSystemId);
            return Task.CompletedTask;
        };

        await rig.Space.UndockStructureAsync(session, Structure, character.ShipId, CancellationToken.None);

        // the structure's ball is in the scene before the ship enters the system (its undock point is the structure's)
        Assert.Equal([(int)system], placed);
        var enter = rig.Sim.Heard.OfType<SimEnter>().Last();
        Assert.Equal(((ulong)session.SessionId, system, EnterHow.Undock, Structure), (enter.SessionId, enter.Scene.LocationId, enter.How, enter.StationId));
        Assert.True(rig.Space.IsInSpace(session));
        Assert.Equal((0L, Structure, (long?)null, (long?)null), Assert.Single(locationChanges));

        Assert.Null(session.GetInt("structureid"));
        Assert.Null(session.GetInt("stationid"));
        Assert.Equal((long?)system, session.GetInt("solarsystemid"));
        Assert.Equal((long?)system, session.GetInt("locationid"));

        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((system, CharacterShips.InSpaceFlag), (ship.LocationId, ship.Flag));
        Assert.Equal(system, (await rig.Characters.GetAsync(character.CharacterId))!.SolarSystemId);
    }

    [PgFact]
    public async Task An_undock_is_refused_for_another_structure_and_for_another_ship_and_the_simulation_is_not_asked()
    {
        var (rig, character, session) = await DockedAtStructureAsync();
        await using var _ = rig;
        var locationChanges = 0;
        rig.Space.DockedLocationChanged = (_, _, _) => locationChanges++;
        var enters = rig.Sim.Heard.OfType<SimEnter>().Count();

        var other = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.UndockStructureAsync(session, Structure + 1, character.ShipId, CancellationToken.None));
        Assert.Equal("DeniedShipChanged", other.Error.UserErrorMessage);

        var otherShip = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.UndockStructureAsync(session, Structure, character.ShipId + 1, CancellationToken.None));
        Assert.Equal("DeniedShipChanged", otherShip.Error.UserErrorMessage);

        Assert.Equal(enters, rig.Sim.Heard.OfType<SimEnter>().Count());
        Assert.Equal(0, locationChanges);
        Assert.Equal((long?)Structure, session.GetInt("structureid"));
        Assert.False(rig.Space.IsInSpace(session));
        var ship = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((Structure, ItemFlags.Hangar), (ship.LocationId, ship.Flag));
    }

    [PgFact]
    public async Task Contraband_warning_preserves_the_docked_ship_until_the_pilot_confirms()
    {
        var (rig, character, session) = await DockedAtStructureAsync();
        await using var _ = rig;
        var inspections = 0;
        rig.Space.InspectUndockContrabandAsync = (owner, ship, system, _) =>
        {
            Assert.Equal(character.CharacterId, owner);
            Assert.Equal(character.ShipId, ship);
            inspections++;
            return Task.FromResult<IReadOnlyList<int>>([3721]);
        };
        var enters = rig.Sim.Heard.OfType<SimEnter>().Count();
        var locationChanges = 0;
        rig.Space.DockedLocationChanged = (_, _, _) => locationChanges++;

        var warning = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            rig.Space.UndockStructureAsync(session, Structure, character.ShipId, CancellationToken.None));
        Assert.Equal("ShipContrabandWarningUndock", warning.Error.UserErrorMessage);
        Assert.Equal(Structure, session.GetInt("structureid"));
        var dockedShip = (await rig.Items.GetAsync(character.ShipId))!;
        Assert.Equal((Structure, ItemFlags.Hangar), (dockedShip.LocationId, dockedShip.Flag));
        Assert.Equal(enters, rig.Sim.Heard.OfType<SimEnter>().Count());
        Assert.Equal(0, locationChanges);

        await rig.Space.UndockStructureAsync(session, Structure, character.ShipId, CancellationToken.None, ignoreContraband: true);
        Assert.True(rig.Space.IsInSpace(session));
        Assert.Null(session.GetInt("structureid"));
        Assert.Equal(1, inspections);
        Assert.Equal(1, locationChanges);
    }
}
