using Eve.Marshal;
using Eve.Space;
using Eve.Structures;

namespace Eve.Services.Space.Tests;

/// <summary>
/// ST-2 b: an anchored structure is put in space when a pilot is in its system (the gap of ST-1 d, closed by <see cref="SpaceStructures.PlaceAsync"/>). The pilot
/// is the real login and undock path; the store, the timers and the bus are the real ones, the simulation is the fake one.
/// </summary>
public sealed class SpaceStructuresPlacementTests
{
    private const int Astrahus = 35_832;
    private const long Corporation = 98_000_001;
    private const long StructureId = 1_040_000_000_077;

    private sealed class Rules : IStructureRulesSource
    {
        public StructureRules RulesOf(int typeId, int solarSystemId) =>
            new(StructureSize.Medium, SecurityBand.HighSec, true, 86_400_000L, 604_800_000L);
    }

    [PgFact]
    public async Task An_anchored_structure_is_put_in_space_once_a_pilot_is_in_its_system()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var system = (int)session.GetInt("solarsystemid2")!.Value;
        await StructureStore.MigrateAsync(rig.Source, rig.StructureSchema);
        var store = new StructureStore(rig.Source, rig.StructureSchema);
        var timers = new StructureTimers(store, rig.Scheduler, rig.Events, new Rules(), () => 0.5);
        var structures = new SpaceStructures(rig.Sim, rig.Space, rig.Source, store, timers, rig.Statics);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await timers.AnchorAsync(connection, transaction,
                new NewStructure(StructureId, Astrahus, system, 1_000_000.0, 2_000.0, 3_000.0, 0.0, Corporation, 0, "Anchored", ""));
            await transaction.CommitAsync();
        }

        // no pilot in the system yet: the structure is not placed
        await structures.PlaceAsync(StructureId);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceStructure>());

        // the pilot undocks into the system: the anchored structure is put in space (the restore on entry and the placement both tell the simulation; the simulation
        // itself ignores a second placement of an id, scene/structures.rs place_structure)
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await structures.PlaceAsync(StructureId);
        var placed = rig.Sim.Heard.OfType<SimPlaceStructure>().ToList();
        Assert.NotEmpty(placed);
        Assert.All(placed, p => Assert.Equal((StructureId, system), (p.StructureId, p.SolarSystemId)));
    }
}
