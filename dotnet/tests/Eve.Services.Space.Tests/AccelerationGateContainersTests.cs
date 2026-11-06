using Eve.Dungeons;
using Eve.Services.Space.Waves;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// from their records, and taken out when the site ends.
/// </summary>
public class AccelerationGateContainersTests
{
    private const int System = 30_100_032;
    private const long Instance = 88;
    private static readonly double[] Origin = [5.0e11, 0.0, 2.0e11];
    private static readonly Dictionary<long, double> Radii = new() { [17831] = 12706, [35776] = 14000 };

    [PgFact]
    public async Task A_running_site_places_its_gates_and_room_pockets_and_takes_them_out_when_it_ends()
    {
        await using var rig = await Rig.CreateAsync();
        var dungeon = ((DungeonFound)DungeonCatalog.LoadEmbedded().GetDungeon(283)).Dungeon;
        var layout = DungeonRoomLayout.Build(dungeon, Origin, Radii);
        var pocket = new DungeonPocket(Instance, System, Origin, layout);
        var gateId = SpaceContainers.AccelerationGateDeployableId(Instance, layout.Gates[0].GateIndex);
        var roomPocket = SpaceContainers.RoomPocketDeployableId(Instance, 1);

        rig.Sim.IsConnected = false;
        await rig.Containers.ReconcileDeadspacePocketsAsync([pocket]);
        rig.Sim.IsConnected = true;
        await Task.WhenAll(
            rig.Containers.ReconcileDeadspacePocketsAsync([pocket]),
            rig.Containers.RestoreDeployedAsync(System));

        var gate = Assert.Single(rig.Sim.Heard.OfType<SimPlaceAccelerationGate>(), body => body.DeployableId == gateId);
        Assert.Equal(DungeonRoomLayout.AccelerationGateTypeId, gate.TypeId);
        Assert.Equal(366, gate.GroupId);
        Assert.Equal(layout.Gates[0].Position, gate.Position.ToArray());
        Assert.Equal(SpaceContainers.AccelerationGateKind, (await rig.Deployed.GetAsync(gateId))!.Kind);
        Assert.Contains(rig.Sim.Heard, m => m is SimPlaceDeadspacePocket body && body.DeployableId == roomPocket && body.Position.SequenceEqual(layout.Rooms[1].Anchor));

        rig.Sim.Heard.Clear();
        await rig.Containers.ReconcileDeadspacePocketsAsync([pocket]);
        await rig.Containers.RestoreDeployedAsync(System);
        Assert.Empty(rig.Sim.Heard);

        rig.Sim.LoseLink();
        rig.Sim.IsConnected = true;
        await rig.Containers.RestoreDeployedAsync(System);
        Assert.Contains(rig.Sim.Heard, m => m is SimPlaceAccelerationGate body && body.DeployableId == gateId);
        Assert.Contains(rig.Sim.Heard, m => m is SimPlaceDeadspacePocket body && body.DeployableId == roomPocket);

        rig.Sim.Heard.Clear();
        await rig.Containers.ReconcileDeadspacePocketsAsync([]);
        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveDeployed removed && removed.DeployableId == gateId);
        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveDeployed removed && removed.DeployableId == roomPocket);
        Assert.Null(await rig.Deployed.GetAsync(gateId));

        // a new dungeon reusing the same ids gets fresh scene bodies
        await rig.Containers.ReconcileDeadspacePocketsAsync([pocket]);
        Assert.Contains(rig.Sim.Heard, m => m is SimPlaceAccelerationGate body && body.DeployableId == gateId);
    }

    [PgFact]
    public async Task The_periodic_reconcile_gives_a_running_site_once_per_link_and_again_after_the_link_is_lost()
    {
        await using var rig = await Rig.CreateAsync();
        var dungeon = ((DungeonFound)DungeonCatalog.LoadEmbedded().GetDungeon(283)).Dungeon;
        var layout = DungeonRoomLayout.Build(dungeon, Origin, Radii);
        var pocket = new DungeonPocket(Instance, System, Origin, layout);
        var expectedIds = new[] { SpaceContainers.PocketDeployableId(Instance) }
            .Concat(layout.Rooms.Where(room => room.Index > 0).Select(room => SpaceContainers.RoomPocketDeployableId(Instance, room.Index)))
            .Concat(layout.Gates.Select(gate => SpaceContainers.AccelerationGateDeployableId(Instance, gate.GateIndex)))
            .OrderBy(id => id)
            .ToArray();
        long[] PlacedIds() => rig.Sim.Heard.OfType<SimPlaceDeadspacePocket>().Select(body => body.DeployableId)
            .Concat(rig.Sim.Heard.OfType<SimPlaceAccelerationGate>().Select(body => body.DeployableId))
            .OrderBy(id => id)
            .ToArray();

        rig.Sim.IsConnected = false;
        await rig.Containers.ReconcileDeadspacePocketsAsync([pocket]);
        Assert.Empty(PlacedIds());
        foreach (var id in expectedIds)
        {
            Assert.NotNull(await rig.Deployed.GetAsync(id));
        }

        rig.Sim.IsConnected = true;
        await Task.WhenAll(
            rig.Containers.ReconcileDeadspacePocketsAsync([pocket]),
            rig.Containers.ReconcileDeadspacePocketsAsync([pocket]));
        Assert.Equal(expectedIds, PlacedIds());

        rig.Sim.Heard.Clear();
        await rig.Containers.ReconcileDeadspacePocketsAsync([pocket]);
        Assert.Empty(PlacedIds());

        var gate = layout.Gates[0];
        var gateId = SpaceContainers.AccelerationGateDeployableId(Instance, gate.GateIndex);
        await rig.Containers.PlaceAccelerationGateAsync(System, gateId, gate.TypeId, gate.Radius, gate.Position);
        await rig.Containers.PlaceAccelerationGateAsync(System, gateId, gate.TypeId, gate.Radius, gate.Position);
        Assert.Equal(2, rig.Sim.Heard.OfType<SimPlaceAccelerationGate>().Count());

        rig.Sim.IsConnected = false;
        rig.Sim.Lose();
        rig.Sim.IsConnected = true;
        rig.Sim.Heard.Clear();
        await rig.Containers.ReconcileDeadspacePocketsAsync([pocket]);
        Assert.Equal(expectedIds, PlacedIds());

        rig.Sim.Heard.Clear();
        await rig.Containers.ReconcileDeadspacePocketsAsync([]);
        await rig.Containers.ReconcileDeadspacePocketsAsync([pocket]);
        Assert.Equal(expectedIds, PlacedIds());
    }
}
