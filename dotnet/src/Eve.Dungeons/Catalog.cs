using System.Text.Json.Serialization;

namespace Eve.Dungeons;

public sealed record CatalogManifest(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("sdeBuild")] long SdeBuild,
    [property: JsonPropertyName("dungeonFiles")] IReadOnlyList<string> DungeonFiles,
    [property: JsonPropertyName("missionFile")] string MissionFile,
    [property: JsonPropertyName("knownGapsFile")] string KnownGapsFile,
    [property: JsonPropertyName("counts")] IReadOnlyDictionary<string, long> Counts,
    [property: JsonPropertyName("validation")] IReadOnlyDictionary<string, long> Validation);

public sealed record DungeonFile(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("dungeons")] IReadOnlyList<Dungeon> Dungeons);

public sealed record Dungeon(
    [property: JsonPropertyName("dungeonID")] long DungeonID,
    [property: JsonPropertyName("archetypeID")] long? ArchetypeID,
    [property: JsonPropertyName("difficulty")] int? Difficulty,
    [property: JsonPropertyName("siteFamily")] string? SiteFamily,
    [property: JsonPropertyName("siteKind")] string? SiteKind,
    [property: JsonPropertyName("entryObjectTypeID")] long? EntryObjectTypeID,
    [property: JsonPropertyName("entryRoomKey")] string? EntryRoomKey,
    [property: JsonPropertyName("completionMode")] string? CompletionMode,
    [property: JsonPropertyName("resources")] DungeonResources Resources,
    [property: JsonPropertyName("rooms")] IReadOnlyList<DungeonRoom> Rooms,
    [property: JsonPropertyName("gates")] IReadOnlyList<DungeonGate> Gates,
    [property: JsonPropertyName("waves")] IReadOnlyList<DungeonWave> Waves,
    [property: JsonPropertyName("props")] IReadOnlyList<DungeonProp> Props,
    [property: JsonPropertyName("containers")] IReadOnlyList<DungeonContainer> Containers,
    [property: JsonPropertyName("hazards")] IReadOnlyList<DungeonHazard> Hazards,
    [property: JsonPropertyName("miningLocks")] IReadOnlyList<MiningLock> MiningLocks,
    [property: JsonPropertyName("lootProfiles")] IReadOnlyList<LootProfile> LootProfiles);

public sealed record DungeonResources(
    [property: JsonPropertyName("ore")] IReadOnlyList<long> Ore,
    [property: JsonPropertyName("gas")] IReadOnlyList<long> Gas,
    [property: JsonPropertyName("ice")] IReadOnlyList<long> Ice);

public sealed record DungeonPosition(
    [property: JsonPropertyName("x")] double? X,
    [property: JsonPropertyName("y")] double? Y,
    [property: JsonPropertyName("z")] double? Z);

public sealed record DungeonRoom(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("roomID")] long? RoomID,
    [property: JsonPropertyName("entry")] bool Entry);

public sealed record DungeonGate(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("typeID")] long? TypeID,
    [property: JsonPropertyName("fromObjectID")] long? FromObjectID,
    [property: JsonPropertyName("toObjectID")] long? ToObjectID,
    [property: JsonPropertyName("destinationRoomKey")] string? DestinationRoomKey,
    [property: JsonPropertyName("allowedRaces")] IReadOnlyList<long> AllowedRaces);

public sealed record DungeonWave(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("waveIndex")] int? WaveIndex,
    [property: JsonPropertyName("trigger")] string? Trigger,
    [property: JsonPropertyName("roomKey")] string? RoomKey,
    [property: JsonPropertyName("spawnQuery")] string? SpawnQuery,
    [property: JsonPropertyName("amount")] int? Amount,
    [property: JsonPropertyName("deadspace")] bool? Deadspace,
    [property: JsonPropertyName("countdownSeconds")] int? CountdownSeconds,
    [property: JsonPropertyName("lootProfile")] string? LootProfile,
    [property: JsonPropertyName("lootTags")] IReadOnlyList<string> LootTags,
    [property: JsonPropertyName("spawns")] IReadOnlyList<DungeonSpawn> Spawns);

public sealed record DungeonSpawn(
    [property: JsonPropertyName("typeID")] long? TypeID,
    [property: JsonPropertyName("entityKind")] string? EntityKind);

public sealed record DungeonProp(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("typeID")] long? TypeID,
    [property: JsonPropertyName("typeNameCandidates")] IReadOnlyList<string> TypeNameCandidates,
    [property: JsonPropertyName("roomKey")] string? RoomKey,
    [property: JsonPropertyName("positionOffset")] DungeonPosition? PositionOffset);

public sealed record DungeonContainer(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("count")] int? Count,
    [property: JsonPropertyName("lootProfile")] string? LootProfile,
    [property: JsonPropertyName("lootTags")] IReadOnlyList<string> LootTags,
    [property: JsonPropertyName("trigger")] string? Trigger,
    [property: JsonPropertyName("bonus")] bool? Bonus,
    [property: JsonPropertyName("failureExplodes")] bool? FailureExplodes,
    [property: JsonPropertyName("persistsAfterResponse")] bool? PersistsAfterResponse);

public sealed record DungeonHazard(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("visibleCountdownSeconds")] int? VisibleCountdownSeconds,
    [property: JsonPropertyName("hiddenTimerMinSeconds")] int? HiddenTimerMinSeconds,
    [property: JsonPropertyName("hiddenTimerMaxSeconds")] int? HiddenTimerMaxSeconds,
    [property: JsonPropertyName("failureTriggersExplosion")] bool? FailureTriggersExplosion);

public sealed record MiningLock(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("typeID")] long TypeID,
    [property: JsonPropertyName("oreTypeID")] long? OreTypeID,
    [property: JsonPropertyName("roomKey")] string? RoomKey,
    [property: JsonPropertyName("count")] int? Count,
    [property: JsonPropertyName("quantity")] double? Quantity,
    [property: JsonPropertyName("positionOffset")] DungeonPosition? PositionOffset);

public sealed record LootProfile(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags);

public sealed record MissionBinding(
    [property: JsonPropertyName("missionID")] long MissionID,
    [property: JsonPropertyName("dungeonID")] long DungeonID);

public sealed record GoldenSecurityMission(
    [property: JsonPropertyName("missionID")] long MissionID,
    [property: JsonPropertyName("dungeonID")] long DungeonID,
    [property: JsonPropertyName("agentLevel")] int AgentLevel);

public sealed record MissionFile(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("killMissionDungeons")] IReadOnlyList<MissionBinding> KillMissionDungeons,
    [property: JsonPropertyName("goldenSecurityMissions")] IReadOnlyList<GoldenSecurityMission> GoldenSecurityMissions);

public sealed record MissingDungeonTemplate(
    [property: JsonPropertyName("dungeonID")] long DungeonID,
    [property: JsonPropertyName("missionIDs")] IReadOnlyList<long> MissionIDs,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record KnownGap(
    [property: JsonPropertyName("missionID")] long MissionID,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record KnownGapsFile(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("missingDungeonTemplates")] IReadOnlyList<MissingDungeonTemplate> MissingDungeonTemplates,
    [property: JsonPropertyName("missionDefects")] IReadOnlyList<KnownGap> MissionDefects,
    [property: JsonPropertyName("missionsWithoutDungeon")] IReadOnlyList<KnownGap> MissionsWithoutDungeon,
    [property: JsonPropertyName("epicLikeMissionsWithoutOfficialArc")] IReadOnlyList<KnownGap> EpicLikeMissionsWithoutOfficialArc);
