using System.Text.Json;
using Eve.Content;
using Eve.Marshal;
using Xunit;

namespace Eve.Host.Tests;

public sealed class AbyssalDungeonProjectionTests
{
    [Fact]
    public void Current_info_uses_profile_dungeon_ids_and_the_domain_instance_id()
    {
        using var document = JsonDocument.Parse("""
            {
              "volumetricCloudDungeonID": 9001,
              "dungeonRoomID": 17
            }
            """);
        var environment = Environment(document.RootElement.Clone(), roomIndex: 2);
        var room = Room(locationId: 2, environment.EnvironmentProfileId);

        var info = Assert.IsType<PyList>(AbyssalDungeonProjection.CurrentInfo(9876, room, environment));

        Assert.Collection(info.Items,
            dungeon => Assert.Equal(new PyInt(9001), dungeon),
            roomId => Assert.Equal(new PyInt(17), roomId),
            instance => Assert.Equal(new PyLong(9876), instance),
            values => Assert.Empty(Assert.IsType<PyDict>(values).Entries));
    }


    [Fact]
    public void Room_ids_resolve_the_client_dungeon_numbers_from_the_saved_profile()
    {
        using var document = JsonDocument.Parse("{\"volumetricCloudDungeonID\":9001,\"dungeonRoomID\":17}");
        var environment = Environment(document.RootElement.Clone(), roomIndex: 2);

        Assert.Equal((9001, 17), AbyssalDungeonProjection.RoomIds(2, environment));
        Assert.Throws<InvalidDataException>(() => AbyssalDungeonProjection.RoomIds(1, environment));
    }

    [Fact]
    public void Current_info_rejects_missing_or_mismatched_profile_metadata()
    {
        using var document = JsonDocument.Parse("{\"dungeonRoomID\": 17}");
        var missingDungeon = Environment(document.RootElement.Clone(), roomIndex: 2);
        Assert.Throws<InvalidDataException>(() =>
            AbyssalDungeonProjection.CurrentInfo(9876, Room(2, missingDungeon.EnvironmentProfileId), missingDungeon));

        var wrongRoom = Environment(JsonDocument.Parse("""
            {
              "volumetricCloudDungeonID": 9001,
              "dungeonRoomID": 17
            }
            """).RootElement.Clone(), roomIndex: 1);
        Assert.Throws<InvalidDataException>(() =>
            AbyssalDungeonProjection.CurrentInfo(9876, Room(2, wrongRoom.EnvironmentProfileId), wrongRoom));
    }

    private static AbyssalEnvironmentRoomDefinition Environment(JsonElement data, int roomIndex) =>
        new(
            $"abyssal:t0:dark:room{roomIndex}",
            "abyssal:t0:dark",
            "evejs-reference",
            default,
            roomIndex,
            4001,
            data,
            Array.Empty<int>(),
            Array.Empty<int>());

    private static AbyssalRoomPlan Room(int locationId, string environmentProfileId) =>
        new(
            locationId,
            Array.Empty<AbyssalNpcSpawnPlan>(),
            Array.Empty<AbyssalLootPlan>(),
            Array.Empty<AbyssalObjectivePlan>(),
            environmentProfileId);
}
