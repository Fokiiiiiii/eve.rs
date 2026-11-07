using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eve.Dungeons;

/// <summary>
/// The dungeon content the host reads, keyed by the official dungeon id. Every lookup answers: the content, or an explicit
/// reason it has none. The data is embedded in this assembly (<c>Data/catalog/v1</c>, written by <c>tools/dev/extract-dungeon-catalog.py</c>);
/// </summary>
public sealed class DungeonCatalog
{
    public const string ManifestSchema = "eve.dungeon-catalog.manifest.v1";
    public const string DungeonSchema = "eve.dungeon-catalog.dungeon.v1";
    public const string LayoutProfilesSchema = "eve.dungeon-catalog.layout-profiles.v1";
    public const string MissionSchema = "eve.dungeon-catalog.missions.v1";
    public const string GapSchema = "eve.dungeon-catalog.known-gaps.v1";

    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
    };

    private readonly Dictionary<long, Dungeon> _dungeons = new();
    private readonly Dictionary<long, long> _dungeonByMission = new();
    private readonly Dictionary<long, List<long>> _missionsByDungeon = new();
    private readonly Dictionary<long, GoldenSecurityMission> _golden = new();
    private readonly Dictionary<long, MissingDungeonTemplate> _missing = new();
    private readonly Dictionary<long, string> _defects = new();
    private readonly Dictionary<long, string> _noDungeon = new();
    private readonly Dictionary<long, string> _epicLike = new();

    private DungeonCatalog(CatalogManifest manifest)
    {
        Manifest = manifest;
    }

    public CatalogManifest Manifest { get; }

    public IEnumerable<Dungeon> Dungeons => _dungeons.Values;

    public IEnumerable<long> OfficialMissionDungeonIDs => _missionsByDungeon.Keys;

    public IEnumerable<MissionBinding> MissionBindings => _dungeonByMission.Select(pair => new MissionBinding(pair.Key, pair.Value));

    public IEnumerable<GoldenSecurityMission> GoldenSecurityMissions => _golden.Values;

    public IEnumerable<MissingDungeonTemplate> MissingDungeonTemplates => _missing.Values;

    public static DungeonCatalog LoadEmbedded() => Load(name => typeof(DungeonCatalog).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidDataException($"the dungeon catalog resource '{name}' is not embedded"));

    /// <summary>Loads a catalog from named JSON streams (the manifest is <c>manifest.json</c>; the other names come from it).</summary>
    public static DungeonCatalog Load(Func<string, Stream> open)
    {
        CatalogManifest manifest = Read<CatalogManifest>(open, "manifest.json");
        if (manifest.Schema != ManifestSchema)
        {
            throw new InvalidDataException($"unexpected manifest schema '{manifest.Schema}'");
        }
        var catalog = new DungeonCatalog(manifest);
        foreach (string file in manifest.DungeonFiles)
        {
            DungeonFile content = Read<DungeonFile>(open, file);
            Expect(content.Schema, DungeonSchema, file);
            foreach (Dungeon dungeon in content.Dungeons)
            {
                if (dungeon.DungeonID <= 0 || !catalog._dungeons.TryAdd(dungeon.DungeonID, dungeon))
                {
                    throw new InvalidDataException($"dungeon id {dungeon.DungeonID} is not positive or appears twice");
                }
            }
        }

        DungeonLayoutProfilesFile layoutProfiles = Read<DungeonLayoutProfilesFile>(open, manifest.LayoutProfilesFile);
        Expect(layoutProfiles.Schema, LayoutProfilesSchema, manifest.LayoutProfilesFile);
        catalog.ApplyLayoutProfiles(layoutProfiles.Profiles, manifest.LayoutProfilesFile);

        MissionFile missions = Read<MissionFile>(open, manifest.MissionFile);
        Expect(missions.Schema, MissionSchema, manifest.MissionFile);
        foreach (MissionBinding binding in missions.KillMissionDungeons)
        {
            if (!catalog._dungeonByMission.TryAdd(binding.MissionID, binding.DungeonID))
            {
                throw new InvalidDataException($"mission {binding.MissionID} is bound twice");
            }
            if (!catalog._missionsByDungeon.TryGetValue(binding.DungeonID, out List<long>? list))
            {
                catalog._missionsByDungeon[binding.DungeonID] = list = new List<long>();
            }
            list.Add(binding.MissionID);
        }
        foreach (GoldenSecurityMission golden in missions.GoldenSecurityMissions)
        {
            catalog._golden[golden.MissionID] = golden;
        }

        KnownGapsFile gaps = Read<KnownGapsFile>(open, manifest.KnownGapsFile);
        Expect(gaps.Schema, GapSchema, manifest.KnownGapsFile);
        foreach (MissingDungeonTemplate missing in gaps.MissingDungeonTemplates)
        {
            catalog._missing[missing.DungeonID] = missing;
        }
        foreach (KnownGap defect in gaps.MissionDefects)
        {
            catalog._defects[defect.MissionID] = defect.Reason;
        }
        foreach (KnownGap none in gaps.MissionsWithoutDungeon)
        {
            catalog._noDungeon[none.MissionID] = none.Reason;
        }
        foreach (KnownGap epic in gaps.EpicLikeMissionsWithoutOfficialArc)
        {
            catalog._epicLike[epic.MissionID] = epic.Reason;
        }
        return catalog;
    }

    private void ApplyLayoutProfiles(IReadOnlyList<DungeonLayoutProfile> profiles, string file)
    {
        var seenDungeons = new HashSet<long>();
        foreach (DungeonLayoutProfile profile in profiles)
        {
            if (!seenDungeons.Add(profile.DungeonID))
            {
                throw new InvalidDataException($"dungeon {profile.DungeonID} has two layout profiles in '{file}'");
            }
            if (!_dungeons.TryGetValue(profile.DungeonID, out Dungeon? dungeon))
            {
                throw new InvalidDataException($"layout profile in '{file}' names missing dungeon {profile.DungeonID}");
            }

            var rooms = dungeon.Rooms.ToDictionary(room => room.Key, StringComparer.Ordinal);
            var gates = dungeon.Gates.ToDictionary(gate => gate.Key, StringComparer.Ordinal);
            var roomProfiles = new Dictionary<string, DungeonRoomLayoutProfile>(StringComparer.Ordinal);
            foreach (DungeonRoomLayoutProfile room in profile.Rooms)
            {
                if (!rooms.ContainsKey(room.RoomKey) || !roomProfiles.TryAdd(room.RoomKey, room))
                {
                    throw new InvalidDataException($"layout profile in '{file}' has an unknown or repeated room '{room.RoomKey}' in dungeon {profile.DungeonID}");
                }
                ValidatePosition(room.PositionOffset, file, profile.DungeonID, room.RoomKey);
            }
            if (profile.Rooms.Count(room => string.Equals(room.InitialState, "active", StringComparison.OrdinalIgnoreCase)) > 1)
            {
                throw new InvalidDataException($"layout profile in '{file}' has multiple active rooms in dungeon {profile.DungeonID}");
            }

            var gateProfiles = new Dictionary<string, DungeonGateLayoutProfile>(StringComparer.Ordinal);
            foreach (DungeonGateLayoutProfile gate in profile.Gates)
            {
                if (!gates.ContainsKey(gate.GateKey) || !gateProfiles.TryAdd(gate.GateKey, gate))
                {
                    throw new InvalidDataException($"layout profile in '{file}' has an unknown or repeated gate '{gate.GateKey}' in dungeon {profile.DungeonID}");
                }
                if (gate.SourceRoomKey is { } sourceRoom && !rooms.ContainsKey(sourceRoom))
                {
                    throw new InvalidDataException($"layout profile in '{file}' names unknown source room '{sourceRoom}' for gate '{gate.GateKey}' in dungeon {profile.DungeonID}");
                }
                ValidatePosition(gate.PositionOffset, file, profile.DungeonID, gate.GateKey);
            }

            _dungeons[profile.DungeonID] = dungeon with
            {
                Rooms = [.. dungeon.Rooms.Select(room => roomProfiles.TryGetValue(room.Key, out DungeonRoomLayoutProfile? scene)
                    ? room with { InitialState = scene.InitialState, PositionOffset = scene.PositionOffset }
                    : room)],
                Gates = [.. dungeon.Gates.Select(gate => gateProfiles.TryGetValue(gate.Key, out DungeonGateLayoutProfile? scene)
                    ? gate with { SourceRoomKey = scene.SourceRoomKey, PositionOffset = scene.PositionOffset }
                    : gate)],
            };
        }
    }

    private static void ValidatePosition(DungeonPosition? position, string file, long dungeonId, string key)
    {
        if (position is not null && (position.X is null || position.Y is null || position.Z is null))
        {
            throw new InvalidDataException($"layout profile in '{file}' has a partial position for '{key}' in dungeon {dungeonId}");
        }
    }

    /// <summary>The content of a client dungeon, or why there is none.</summary>
    public DungeonAnswer GetDungeon(long dungeonID)
    {
        if (_dungeons.TryGetValue(dungeonID, out Dungeon? dungeon))
        {
            return new DungeonFound(dungeon);
        }
        if (_missing.TryGetValue(dungeonID, out MissingDungeonTemplate? missing))
        {
            return new DungeonNoTemplate(dungeonID, missing.MissionIDs, missing.Reason);
        }
        return new DungeonNotInCatalog(dungeonID, "no mission of the official data uses this dungeon id, and the catalog holds no template for it");
    }

    /// <summary>The dungeon of a mission (its <c>killMission.dungeonID</c>) with the golden policy entry when it has one, or why it has no dungeon.</summary>
    public MissionAnswer GetMission(long missionID)
    {
        if (_defects.TryGetValue(missionID, out string? defect))
        {
            return new MissionNoDungeon(missionID, MissionNoDungeonKind.Defect, defect);
        }
        if (_noDungeon.TryGetValue(missionID, out string? none))
        {
            return new MissionNoDungeon(missionID, MissionNoDungeonKind.KillWithoutDungeon, none);
        }
        if (_dungeonByMission.TryGetValue(missionID, out long dungeonID))
        {
            _golden.TryGetValue(missionID, out GoldenSecurityMission? golden);
            var gaps = new List<string>();
            if (_epicLike.TryGetValue(missionID, out string? epic))
            {
                gaps.Add(epic);
            }
            return new MissionFound(missionID, dungeonID, GetDungeon(dungeonID), golden, gaps);
        }
        if (_epicLike.TryGetValue(missionID, out string? epicOnly))
        {
            return new MissionNoDungeon(missionID, MissionNoDungeonKind.NotBound, epicOnly);
        }
        return new MissionNoDungeon(missionID, MissionNoDungeonKind.NotInCatalog,
            "the catalog has no kill dungeon for this mission: it is not a kill mission in the official data (SDE 3405148), or it is not in the catalog");
    }

    private static T Read<T>(Func<string, Stream> open, string name)
    {
        using Stream stream = open(name);
        return JsonSerializer.Deserialize<T>(stream, Options)
            ?? throw new InvalidDataException($"'{name}' is empty");
    }

    private static void Expect(string actual, string expected, string file)
    {
        if (actual != expected)
        {
            throw new InvalidDataException($"'{file}' has schema '{actual}', expected '{expected}'");
        }
    }
}

public abstract record DungeonAnswer;

public sealed record DungeonFound(Dungeon Dungeon) : DungeonAnswer;

public sealed record DungeonNoTemplate(long DungeonID, IReadOnlyList<long> MissionIDs, string Reason) : DungeonAnswer;

public sealed record DungeonNotInCatalog(long DungeonID, string Reason) : DungeonAnswer;

public abstract record MissionAnswer;

public sealed record MissionFound(long MissionID, long DungeonID, DungeonAnswer Dungeon, GoldenSecurityMission? Golden, IReadOnlyList<string> Gaps) : MissionAnswer;

public enum MissionNoDungeonKind
{

    Defect,

    /// <summary>A kill objective with no dungeon id; the mission has no dungeon.</summary>
    KillWithoutDungeon,

    NotBound,

    /// <summary>Not in the catalog.</summary>
    NotInCatalog,
}

public sealed record MissionNoDungeon(long MissionID, MissionNoDungeonKind Kind, string Reason) : MissionAnswer;
