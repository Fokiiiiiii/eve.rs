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

    private static double Distance(double[] left, double[] right) =>
        Math.Sqrt(Math.Pow(left[0] - right[0], 2) + Math.Pow(left[1] - right[1], 2) + Math.Pow(left[2] - right[2], 2));

    [PgFact]
    public async Task Gates_without_an_authored_source_room_are_placed_at_the_entry_site()
    {
        await using var rig = await Rig.CreateAsync();
        var dungeon = ((DungeonFound)DungeonCatalog.LoadEmbedded().GetDungeon(1215)).Dungeon;
        var layout = DungeonRoomLayout.Build(dungeon, Origin, Radii);
        var pocket = new DungeonPocket(Instance, System, Origin, layout);

        Assert.Equal(["room:entry", "room:17492", "room:17493", "room:17494"], layout.Rooms.Select(room => room.Key).ToArray());
        Assert.Equal(2, layout.Gates.Count);
        Assert.All(layout.Gates, gate => Assert.Equal("room:entry", gate.SourceRoomKey));

        rig.Sim.IsConnected = false;
        await rig.Containers.ReconcileDeadspacePocketsAsync([pocket]);
        rig.Sim.IsConnected = true;
        await rig.Containers.ReconcileDeadspacePocketsAsync([pocket]);

        var placed = rig.Sim.Heard.OfType<SimPlaceAccelerationGate>().OrderBy(gate => gate.DeployableId).ToArray();
        Assert.Equal(2, placed.Length);
        Assert.All(placed, gate => Assert.Equal(DungeonRoomLayout.GateRingMeters, Distance(gate.Position.ToArray(), Origin), 3));
    }

    [PgFact]
    public async Task Mission_scene_profiles_place_rooms_and_gates_at_their_recorded_relative_positions()
    {
        await using var rig = await Rig.CreateAsync();
        var catalog = DungeonCatalog.LoadEmbedded();
        var missionLayout = DungeonRoomLayout.Build(((DungeonFound)catalog.GetDungeon(2734)).Dungeon, Origin, Radii);
        var combatLayout = DungeonRoomLayout.Build(((DungeonFound)catalog.GetDungeon(283)).Dungeon, Origin, Radii);
        const long missionInstance = 89;
        const long combatInstance = 90;
        var pockets = new[]
        {
            new DungeonPocket(missionInstance, System, Origin, missionLayout),
            new DungeonPocket(combatInstance, System, Origin, combatLayout),
        };

        rig.Sim.IsConnected = false;
        await rig.Containers.ReconcileDeadspacePocketsAsync(pockets);
        rig.Sim.IsConnected = true;
        await rig.Containers.ReconcileDeadspacePocketsAsync(pockets);

        var missionRoomId = SpaceContainers.RoomPocketDeployableId(missionInstance, 1);
        var combatRoomId = SpaceContainers.RoomPocketDeployableId(combatInstance, 1);
        var missionGateId = SpaceContainers.AccelerationGateDeployableId(missionInstance, 0);
        var combatGateId = SpaceContainers.AccelerationGateDeployableId(combatInstance, 0);
        double[] missionRoomPosition = [Origin[0] + 31_457_280, Origin[1], Origin[2]];
        double[] combatRoomPosition = [Origin[0] - 31_457_280, Origin[1], Origin[2]];
        double[] missionGatePosition = [Origin[0] + 13_000, Origin[1], Origin[2]];
        double[] combatGatePosition = [Origin[0], Origin[1] - 750, Origin[2]];

        Assert.Equal(missionRoomPosition, (await rig.Deployed.GetAsync(missionRoomId))!.Position);
        Assert.Equal(combatRoomPosition, (await rig.Deployed.GetAsync(combatRoomId))!.Position);
        Assert.Equal(missionGatePosition, (await rig.Deployed.GetAsync(missionGateId))!.Position);
        Assert.Equal(combatGatePosition, (await rig.Deployed.GetAsync(combatGateId))!.Position);
        Assert.Contains(rig.Sim.Heard.OfType<SimPlaceDeadspacePocket>(), body => body.DeployableId == missionRoomId && body.Position.SequenceEqual(missionRoomPosition));
        Assert.Contains(rig.Sim.Heard.OfType<SimPlaceDeadspacePocket>(), body => body.DeployableId == combatRoomId && body.Position.SequenceEqual(combatRoomPosition));
        Assert.Contains(rig.Sim.Heard.OfType<SimPlaceAccelerationGate>(), body => body.DeployableId == missionGateId && body.Position.SequenceEqual(missionGatePosition));
        Assert.Contains(rig.Sim.Heard.OfType<SimPlaceAccelerationGate>(), body => body.DeployableId == combatGateId && body.Position.SequenceEqual(combatGatePosition));
    }

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
