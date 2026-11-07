using Eve.Dungeons;

namespace Eve.Dungeons.Tests;

public sealed class DungeonRoomLayoutTests
{
    private static readonly Lazy<DungeonCatalog> Shared = new(DungeonCatalog.LoadEmbedded);

    private static readonly double[] Origin = [1.0e11, 2.0e9, -3.0e10];
    private static readonly Dictionary<long, double> Radii = new() { [17831] = 12706, [35776] = 14000 };

    private static Dungeon Template(long id) => Shared.Value.GetDungeon(id) is DungeonFound found
        ? found.Dungeon
        : throw new InvalidOperationException($"no dungeon {id}");

    private static double Distance(double[] a, double[] b) =>
        Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2) + Math.Pow(a[2] - b[2], 2));

    [Fact]
    public void A_single_room_site_has_its_gates_on_the_ring_around_the_site_and_the_pilot_arrives_15_km_out()
    {
        // dungeon 47: one room (the entry), gates into the entry room
        var layout = DungeonRoomLayout.Build(Template(47), Origin, Radii);

        var room = Assert.Single(layout.Rooms);
        Assert.Equal(0, room.Index);
        Assert.Equal(Origin, room.Anchor);
        Assert.Equal(DungeonRoomLayout.EntryOffsetMeters, Distance(room.Anchor, room.EntryPoint), 2);
        Assert.NotEmpty(layout.Gates);
        foreach (var gate in layout.Gates)
        {
            Assert.Equal("room:entry", gate.DestinationRoomKey);
            Assert.Equal(DungeonRoomLayout.GateRingMeters, Distance(gate.Position, Origin), 6);
            Assert.Equal(DungeonRoomLayout.AccelerationGateTypeId, gate.TypeId);
        }
    }

    [Fact]
    public void A_multi_room_site_uses_its_recorded_room_and_gate_positions()
    {
        // dungeon 283: EveJS records room 13849 active, room 13850 pending, and the gate in 13849
        var template = Template(283);
        var layout = DungeonRoomLayout.Build(template, Origin, Radii);

        Assert.Equal(["room:13849", "room:13850"], layout.Rooms.Select(room => room.Key).ToArray());
        var second = layout.Room("room:13850")!;
        Assert.Equal(-31_457_280, second.Anchor[0] - Origin[0], 3);
        Assert.Equal(0, second.Anchor[1] - Origin[1], 3);
        Assert.Equal(0, second.Anchor[2] - Origin[2], 3);
        Assert.Equal(DungeonRoomLayout.EntryOffsetMeters, Distance(second.EntryPoint, second.Anchor), 2);

        var gate = Assert.Single(layout.Gates, g => g.DestinationRoomKey == "room:13850");
        Assert.Equal("room:13849", gate.SourceRoomKey);
        Assert.Equal([Origin[0], Origin[1] - 750, Origin[2]], gate.Position);
    }

    [Fact]
    public void A_mission_scene_profile_uses_the_active_room_and_its_gate_source()
    {
        var layout = DungeonRoomLayout.Build(Template(2734), Origin, Radii);

        Assert.Equal(["room:20206", "room:20191"], layout.Rooms.Select(room => room.Key).ToArray());
        Assert.Equal(Origin, layout.Room("room:20206")!.Anchor);
        Assert.Equal([Origin[0] + 31_457_280, Origin[1], Origin[2]], layout.Room("room:20191")!.Anchor);
        var gate = Assert.Single(layout.Gates);
        Assert.Equal(("room:20206", "room:20191"), (gate.SourceRoomKey, gate.DestinationRoomKey));
        Assert.Equal([Origin[0] + 13_000, Origin[1], Origin[2]], gate.Position);
    }

    [Fact]
    public void Every_placed_gate_leads_to_a_room_of_its_site_and_the_placement_is_deterministic()
    {
        var multi = Shared.Value.Dungeons.Where(dungeon => dungeon.Rooms.Count > 1).ToList();
        Assert.NotEmpty(multi);
        foreach (var dungeon in multi.Concat(Shared.Value.Dungeons.Take(200)))
        {
            var first = DungeonRoomLayout.Build(dungeon, Origin, Radii);
            Assert.Equal(dungeon.Rooms.Count, first.Rooms.Count);
            foreach (var gate in first.Gates)
            {
                Assert.NotNull(first.Room(gate.DestinationRoomKey));
                Assert.All(gate.Position, value => Assert.True(double.IsFinite(value)));
            }
            var again = DungeonRoomLayout.Build(dungeon, Origin, Radii);
            Assert.Equal(first.Gates.Select(g => g.Position), again.Gates.Select(g => g.Position));
        }
    }

    [Fact]
    public void A_large_acceleration_gate_is_placed_with_its_own_radius()
    {

        var dungeon = Shared.Value.Dungeons.First(d => d.Gates.Any(g => g.TypeID == 35776 && g.DestinationRoomKey is not null));
        var layout = DungeonRoomLayout.Build(dungeon, Origin, Radii);
        Assert.Contains(layout.Gates, g => g.TypeId == 35776 && g.Radius == 14_000);
    }

    [Fact]
    public void The_room_distances_follow_the_reference_steps()
    {
        Assert.Equal(Origin, DungeonRoomLayout.AnchorOf(0, Origin));
        var third = DungeonRoomLayout.AnchorOf(3, Origin);
        Assert.Equal(DungeonRoomLayout.RoomBaseDistanceMeters + 2 * DungeonRoomLayout.RoomDistanceStepMeters, Math.Sqrt(Math.Pow(third[0] - Origin[0], 2) + Math.Pow(third[2] - Origin[2], 2)), 3);
        Assert.Equal(-3 * DungeonRoomLayout.RoomVerticalStepMeters, third[1] - Origin[1], 6);
    }
}
