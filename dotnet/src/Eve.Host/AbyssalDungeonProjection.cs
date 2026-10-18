using System.Text.Json;
using Eve.Content;
using Eve.Marshal;

namespace Eve.Host;

/// <summary>Builds keeper.GetCurrentDungeonForCharacter from the active Domain room and its SDE-backed EveJS environment profile.</summary>
public static class AbyssalDungeonProjection
{
    public static PyValue CurrentInfo(
        long instanceId,
        AbyssalRoomPlan room,
        AbyssalEnvironmentRoomDefinition environment)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(environment);
        if (instanceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(instanceId));
        var (dungeonId, roomId) = RoomIds(room.LocationId, environment);

        return new PyList([
            new PyInt(dungeonId),
            new PyInt(roomId),
            new PyLong(instanceId),
            new PyDict([]),
        ]);
    }

    public static (int DungeonId, int RoomId) RoomIds(
        int locationId,
        AbyssalEnvironmentRoomDefinition environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (locationId is < 1 or > 3 || environment.RoomIndex != locationId)
            throw new InvalidDataException("Abyssal current-room ordinal does not match its environment profile.");

        var dungeonId = RequiredInt(environment.Data, "volumetricCloudDungeonID", environment.EnvironmentProfileId);
        var roomId = RequiredInt(environment.Data, "dungeonRoomID", environment.EnvironmentProfileId);
        if (dungeonId <= 0 || roomId < 0)
            throw new InvalidDataException($"Abyssal environment profile '{environment.EnvironmentProfileId}' has invalid dungeon or room identifiers.");
        return (dungeonId, roomId);
    }


    private static int RequiredInt(JsonElement data, string key, string profileId)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty(key, out var value) ||
            !value.TryGetInt32(out var parsed))
        {
            throw new InvalidDataException($"Abyssal environment profile '{profileId}' is missing integer '{key}'.");
        }
        return parsed;
    }
}
