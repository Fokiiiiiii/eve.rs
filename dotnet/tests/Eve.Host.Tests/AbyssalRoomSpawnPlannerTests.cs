using Eve.Content;
using Xunit;

namespace Eve.Host.Tests;

public sealed class AbyssalRoomSpawnPlannerTests
{
    [Fact]
    public void Spawn_reservation_uses_room_ordinal_and_stable_SDE_backed_entities()
    {
        var room = new AbyssalRoomPlan(
            1,
            [new AbyssalNpcSpawnPlan(34567, 2)],
            [],
            [],
            "abyssal:env:room-1")
        {
            NebulaGraphicId = 1234,
        };

        var entities = AbyssalRoomSpawnPlanner.Plan(
            instanceId: 81,
            contentId: "9a56e7b5-8696-47a0-9b66-1391481f79fd",
            spaceLocationId: 31000001,
            roomOrdinal: 1,
            room,
            roomCenter: new AbyssalPosition(1_000, 2_000, 3_000),
            shipDirection: new AbyssalPosition(0, 0, 1),
            npcEntityIds: [101, 102],
            gateEntityId: 103,
            containerEntityId: 104,
            containerTypeId: 47951);

        Assert.Collection(entities,
            gate =>
            {
                Assert.Equal(103, gate.EntityId);
                Assert.Equal(47685, gate.TypeId);
                Assert.Equal(AbyssalEntityRole.Gate, gate.Role);
                Assert.Equal(new AbyssalPosition(1_000, 2_000, 38_000), gate.Position);
            },
            container =>
            {
                Assert.Equal(104, container.EntityId);
                Assert.Equal(47951, container.TypeId);
                Assert.Equal(AbyssalEntityRole.Container, container.Role);
                Assert.Equal(new AbyssalPosition(1_000, 2_000, 46_000), container.Position);
            },
            firstNpc =>
            {
                Assert.Equal((101, 34567, AbyssalEntityRole.Npc), (firstNpc.EntityId, firstNpc.TypeId, firstNpc.Role));
                Assert.Equal(new AbyssalPosition(1_000, 2_000, 48_000), firstNpc.Position);
            },
            secondNpc =>
            {
                Assert.Equal((102, 34567, AbyssalEntityRole.Npc), (secondNpc.EntityId, secondNpc.TypeId, secondNpc.Role));
                Assert.Equal(1_000, secondNpc.Position.X);
                Assert.Equal(2_000, secondNpc.Position.Y, precision: 9);
                Assert.Equal(28_000, secondNpc.Position.Z);
            });
    }

    [Fact]
    public void Persisted_artifacts_and_environment_keep_exact_keys_and_transform_source_offsets_once()
    {
        var room = new AbyssalRoomPlan(1, [], [], [], "env")
        {
            Artifacts =
            [
                new("main", 47951, AbyssalArtifactKind.MainCache, new(35000, 10000, 0), [], "PRE_SCALED", "JS"),
                new("side", 49661, AbyssalArtifactKind.ExtractionNode, new(35000, 15000, 0), [], "PRE_SCALED", "JS"),
            ],
            EnvironmentObjects = [new("cloud", 47437, new(10, 20, 30), new(0, 0, 0), "cloud", "JS")],
        };
        var entities = AbyssalRoomSpawnPlanner.Plan(81, "9a56e7b5-8696-47a0-9b66-1391481f79fd",
            32000010, 1, room, new(100, 200, 300), new(1, 0, 0), [], 101, 102, 47951,
            new Dictionary<string, long> { ["main"] = 102, ["side"] = 103, ["cloud"] = 104 });
        Assert.Equal(4, entities.Count);
        Assert.Equal(new AbyssalPosition(35100, 10200, 300), entities.Single(value => value.PlanKey == "main").Position);
        Assert.Equal(new AbyssalPosition(35100, 15200, 300), entities.Single(value => value.PlanKey == "side").Position);
        Assert.Equal((104L, AbyssalEntityRole.Cloud, new AbyssalPosition(110, 220, 330)),
            (entities.Single(value => value.PlanKey == "cloud").EntityId,
             entities.Single(value => value.PlanKey == "cloud").Role, entities.Single(value => value.PlanKey == "cloud").Position));
        Assert.Equal(new[] { "main", "side", "cloud" }, entities.Where(value => value.PlanKey is not null).Select(value => value.PlanKey));
        Assert.Equal(new AbyssalPosition(70, 220, 310),
            AbyssalRoomSpawnPlanner.LocalToWorld(new(100, 200, 300), new(0, 0, 1), new(10, 20, 30)));
    }

    [Fact]
    public void Final_room_uses_the_origin_conduit_type_and_requires_the_saved_NPC_count()
    {
        var room = new AbyssalRoomPlan(3, [new AbyssalNpcSpawnPlan(34567, 1)], [], [], "env");

        var entities = AbyssalRoomSpawnPlanner.Plan(
            81,
            "9a56e7b5-8696-47a0-9b66-1391481f79fd",
            31000001,
            3,
            room,
            new AbyssalPosition(0, 0, 0),
            new AbyssalPosition(1, 0, 0),
            [101],
            103,
            104,
            49689);

        Assert.Equal(47686, Assert.Single(entities, entity => entity.Role == AbyssalEntityRole.Gate).TypeId);
        Assert.Throws<InvalidDataException>(() => AbyssalRoomSpawnPlanner.Plan(
            81,
            "9a56e7b5-8696-47a0-9b66-1391481f79fd",
            31000001,
            3,
            room,
            new AbyssalPosition(0, 0, 0),
            new AbyssalPosition(1, 0, 0),
            [],
            103,
            104,
            49689));
    }
}