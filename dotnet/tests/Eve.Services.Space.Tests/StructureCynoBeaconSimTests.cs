using Eve.Space;
using Eve.Structures;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The Pharolux cynosural beacon of a structure in the simulation (SpaceStructureCynoBeacons): placed when its Cyno Beacon service (16) comes online, and taken
/// out when the service goes offline. The change comes through the event bus as the timers publish it; the simulation is the rig fake.
/// </summary>
public sealed class StructureCynoBeaconSimTests
{
    private const long Structure = 1_000_000_030, Corporation = 98_000_001;
    private const int PharoluxBeacon = 35_840, Jita = 30_000_142;

    [PgFact]
    public async Task A_pharolux_beacon_is_placed_when_its_service_comes_online_and_taken_out_when_it_goes_offline()
    {
        await using var rig = await Rig.CreateAsync();
        await StructureStore.MigrateAsync(rig.Source, rig.StructureSchema);
        var store = new StructureStore(rig.Source, rig.StructureSchema);
        var beacons = new SpaceStructureCynoBeacons(rig.Source, store, rig.Statics, rig.Space);
        beacons.Subscribe(rig.Events);
        await rig.Events.PumpAsync();

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.CreateAsync(connection, transaction, new NewStructure(
                Structure, PharoluxBeacon, Jita, 1_000_000.0, 2_000.0, 3_000.0, 0.0, Corporation, 0, "Pharolux Test", ""));
            var snapshot = (await store.GetAsync(connection, transaction, Structure, false))!.Snapshot;
            await store.SaveAsync(connection, transaction, snapshot with { State = StructureState.ShieldVulnerable });
            await store.SetServiceAsync(connection, transaction, Structure, StructureServiceIds.CynoBeacon, ServiceState.Online, 1_000);
            await transaction.CommitAsync();
        }
        await rig.Events.PublishAsync(StructureTimers.ServiceChangedTopic,
            new StructureServiceChanged(Structure, Jita, StructureServiceIds.CynoBeacon, ServiceState.Online, 1_000));
        await rig.Events.PumpAsync();

        var placed = Assert.Single(rig.Sim.Heard.OfType<SimPlaceCynoBeacon>());
        Assert.Equal((Structure, Jita, PharoluxBeacon, true), (placed.DeployableId, placed.SolarSystemId, placed.TypeId, placed.Active));
        Assert.Equal(Corporation, placed.CorporationId);
        Assert.True(placed.Radius > 0, "the beacon has the published radius of its type");

        rig.Sim.Heard.Clear();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.SetServiceAsync(connection, transaction, Structure, StructureServiceIds.CynoBeacon, ServiceState.Offline, 0);
            await transaction.CommitAsync();
        }
        await rig.Events.PublishAsync(StructureTimers.ServiceChangedTopic,
            new StructureServiceChanged(Structure, Jita, StructureServiceIds.CynoBeacon, ServiceState.Offline, 0));
        await rig.Events.PumpAsync();

        Assert.Contains(rig.Sim.Heard, message => message is SimRemoveCynoBeacon removed && removed.StructureBeaconId == Structure && removed.SolarSystemId == Jita);
        Assert.Empty(rig.Sim.Heard.OfType<SimPlaceCynoBeacon>());
    }
}
