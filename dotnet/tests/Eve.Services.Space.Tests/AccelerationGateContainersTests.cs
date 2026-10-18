using Eve.Dungeons;
using Eve.Services.Space.Waves;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A running site's gates and room pockets (docs/project/ACCELERATION_GATES.md): placed in the scene at their rooms when the site runs, given back after a restart
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

        await rig.Containers.ReconcileDeadspacePocketsAsync([pocket]);

        var gate = Assert.Single(rig.Sim.Heard.OfType<SimPlaceAccelerationGate>(), body => body.DeployableId == gateId);
        Assert.Equal(DungeonRoomLayout.AccelerationGateTypeId, gate.TypeId);
        Assert.Equal(366, gate.GroupId);
        Assert.Equal(layout.Gates[0].Position, gate.Position.ToArray());
        Assert.Equal(SpaceContainers.AccelerationGateKind, (await rig.Deployed.GetAsync(gateId))!.Kind);
        Assert.Contains(rig.Sim.Heard, m => m is SimPlaceDeadspacePocket body && body.DeployableId == roomPocket && body.Position.SequenceEqual(layout.Rooms[1].Anchor));

        rig.Sim.Heard.Clear();
        await rig.Containers.ReconcileDeadspacePocketsAsync([]);
        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveDeployed removed && removed.DeployableId == gateId);
        Assert.Contains(rig.Sim.Heard, m => m is SimRemoveDeployed removed && removed.DeployableId == roomPocket);
        Assert.Null(await rig.Deployed.GetAsync(gateId));

        // a restart puts the body of a running gate back in the scene from its record
        await rig.Containers.ReconcileDeadspacePocketsAsync([pocket]);
        rig.Sim.Heard.Clear();
        await rig.Containers.RestoreDeployedAsync(System);
        Assert.Contains(rig.Sim.Heard, m => m is SimPlaceAccelerationGate body && body.DeployableId == gateId);
    }
}
