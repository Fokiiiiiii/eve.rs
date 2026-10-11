using System.Reflection;
using System.Text;
using System.Text.Json;
using Eve.Dungeons;

namespace Eve.Dungeons.Tests;

public sealed class DungeonCatalogTests
{
    private static readonly Lazy<DungeonCatalog> Shared = new(DungeonCatalog.LoadEmbedded);

    private static DungeonCatalog Catalog => Shared.Value;

    [Fact]
    public void Manifest_counts_match_the_loaded_content()
    {
        var manifest = Catalog.Manifest;
        Assert.Equal(3405148, manifest.SdeBuild);
        Assert.Equal(4653, Catalog.Dungeons.Count());
        Assert.Equal(manifest.Counts["dungeonTemplates"], Catalog.Dungeons.Count());
        Assert.Equal(manifest.Counts["killMissionBindings"], Catalog.MissionBindings.Count());
        Assert.Equal(manifest.Counts["missionDungeonIDs"], Catalog.OfficialMissionDungeonIDs.Count());
        Assert.Equal(13, Catalog.GoldenSecurityMissions.Count());
        Assert.Equal(0, manifest.Validation["typeIDMismatches"]);
        Assert.Equal(0, manifest.Validation["archetypeIDMismatches"]);
    }

    [Fact]
    public void Official_mission_dungeons_resolve_except_the_seven_named_gaps()
    {
        var ids = Catalog.OfficialMissionDungeonIDs.ToList();
        Assert.Equal(1460, ids.Count);

        var found = 0;
        var missing = new List<long>();
        foreach (long id in ids)
        {
            var answer = Catalog.GetDungeon(id);
            if (answer is DungeonFound)
            {
                found++;
                continue;
            }
            var none = Assert.IsType<DungeonNoTemplate>(answer);
            Assert.False(string.IsNullOrWhiteSpace(none.Reason));
            Assert.NotEmpty(none.MissionIDs);
            missing.Add(id);
        }

        Assert.Equal(1453, found);
        Assert.Equal(new long[] { 13341, 13342, 14097, 14098, 14099, 14100, 14222 }, missing.OrderBy(x => x));
    }

    [Fact]
    public void Unknown_dungeon_id_is_reported_with_a_reason()
    {
        var answer = Assert.IsType<DungeonNotInCatalog>(Catalog.GetDungeon(999999999));
        Assert.False(string.IsNullOrWhiteSpace(answer.Reason));
    }

    [Fact]
    public void Broken_and_dungeonless_missions_answer_with_their_reasons()
    {
        foreach (long broken in new long[] { 14555, 14560 })
        {
            var answer = Assert.IsType<MissionNoDungeon>(Catalog.GetMission(broken));
            Assert.Equal(MissionNoDungeonKind.Defect, answer.Kind);
            Assert.Contains("Python object repr", answer.Reason);
        }
        var noDungeon = Assert.IsType<MissionNoDungeon>(Catalog.GetMission(16414));
        Assert.Equal(MissionNoDungeonKind.KillWithoutDungeon, noDungeon.Kind);
        Assert.False(string.IsNullOrWhiteSpace(noDungeon.Reason));

        var unknown = Assert.IsType<MissionNoDungeon>(Catalog.GetMission(999999));
        Assert.Equal(MissionNoDungeonKind.NotInCatalog, unknown.Kind);
    }

    [Fact]
    public void Epic_like_missions_without_an_official_arc_are_flagged()
    {
        var bound = Assert.IsType<MissionFound>(Catalog.GetMission(17134));
        Assert.Equal(13342, bound.DungeonID);
        Assert.IsType<DungeonNoTemplate>(bound.Dungeon);
        Assert.Single(bound.Gaps);
        Assert.Contains("epic arc", bound.Gaps[0]);

        var unbound = Assert.IsType<MissionNoDungeon>(Catalog.GetMission(14497));
        Assert.Equal(MissionNoDungeonKind.NotBound, unbound.Kind);
        Assert.Contains("epic arc", unbound.Reason);
    }

    [Fact]
    public void Mission_answers_carry_the_dungeon_and_the_golden_policy_entry()
    {
        var golden = Assert.IsType<MissionFound>(Catalog.GetMission(1182));
        Assert.Equal(283, golden.DungeonID);
        Assert.Equal(1, golden.Golden?.AgentLevel);
        Assert.IsType<DungeonFound>(golden.Dungeon);

        var noTemplate = Assert.IsType<MissionFound>(Catalog.GetMission(17183));
        Assert.Equal(14097, noTemplate.DungeonID);
        Assert.IsType<DungeonNoTemplate>(noTemplate.Dungeon);
    }

    [Fact]
    public void Every_golden_policy_dungeon_has_a_template()
    {
        foreach (var entry in Catalog.GoldenSecurityMissions)
        {
            Assert.IsType<DungeonFound>(Catalog.GetDungeon(entry.DungeonID));
        }
    }

    [Fact]
    public void Client_overlay_replaces_the_authority_fields_of_the_same_dungeon()
    {
        var dungeon = Assert.IsType<DungeonFound>(Catalog.GetDungeon(5496)).Dungeon;
        Assert.Equal("ice", dungeon.SiteFamily);
        Assert.Equal("anomaly", dungeon.SiteKind);
        Assert.NotEmpty(dungeon.MiningLocks);
    }

    [Fact]
    public void Mining_pack_templates_are_keyed_by_their_source_dungeon_id()
    {
        var dungeon = Assert.IsType<DungeonFound>(Catalog.GetDungeon(2449)).Dungeon;
        Assert.Equal(28617, dungeon.MiningLocks[0].TypeID);
        Assert.Single(dungeon.Resources.Ore);
    }

    [Fact]
    public void Every_wave_spawn_carries_a_positive_type_id()
    {
        var spawns = Catalog.Dungeons.SelectMany(d => d.Waves).SelectMany(w => w.Spawns).ToList();
        Assert.Equal(345, spawns.Count);
        Assert.All(spawns, spawn => Assert.True(spawn.TypeID is > 0));
    }

    [Fact]
    public void Room_references_resolve_inside_their_dungeon()
    {
        foreach (var dungeon in Catalog.Dungeons)
        {
            var rooms = dungeon.Rooms.Select(r => r.Key).ToHashSet();
            Assert.True(dungeon.Rooms.Count(r => r.Entry) <= 1, $"dungeon {dungeon.DungeonID} has two entry rooms");
            foreach (var wave in dungeon.Waves.Where(w => w.RoomKey is not null))
            {
                Assert.Contains(wave.RoomKey!, rooms);
            }
            foreach (var prop in dungeon.Props.Where(p => p.RoomKey is not null))
            {
                Assert.Contains(prop.RoomKey!, rooms);
            }
            foreach (var rock in dungeon.MiningLocks.Where(m => m.RoomKey is not null))
            {
                Assert.Contains(rock.RoomKey!, rooms);
            }
            foreach (var gate in dungeon.Gates.Where(g => g.DestinationRoomKey is not null))
            {
                Assert.Contains(gate.DestinationRoomKey!, rooms);
            }
        }
    }

    [Fact]
    public void Props_without_a_type_id_keep_their_candidate_names()
    {
        var nameOnly = Catalog.Dungeons.SelectMany(d => d.Props).Where(p => p.TypeID is null).ToList();
        var typed = Catalog.Dungeons.SelectMany(d => d.Props).Where(p => p.TypeID is not null).ToList();
        Assert.Equal(14453, nameOnly.Count);
        Assert.Equal(931, typed.Count);
        Assert.All(nameOnly, prop => Assert.NotEmpty(prop.TypeNameCandidates));
    }

    [Fact]
    public void Content_totals_match_the_extracted_catalog()
    {
        var dungeons = Catalog.Dungeons.ToList();
        Assert.Equal(3049, dungeons.Sum(d => d.Gates.Count));
        Assert.Equal(7083, dungeons.Sum(d => d.Waves.Count));
        Assert.Equal(1161, dungeons.Sum(d => d.MiningLocks.Count));
        Assert.Equal(287, dungeons.Sum(d => d.Containers.Count));
        Assert.Equal(17, dungeons.Sum(d => d.Hazards.Count));
        Assert.Equal(5219, dungeons.Sum(d => d.LootProfiles.Count));
    }

    [Fact]
    public void Schema_allowlist_admits_only_the_documented_fields()
    {
        var assembly = typeof(DungeonCatalog).Assembly;
        JsonElement manifest = ReadRaw(assembly, "manifest.json");
        AssertFields(manifest, "manifest", "schema", "sdeBuild", "dungeonFiles", "missionFile", "knownGapsFile", "counts", "validation");

        foreach (JsonElement file in manifest.GetProperty("dungeonFiles").EnumerateArray())
        {
            JsonElement content = ReadRaw(assembly, file.GetString()!);
            AssertFields(content, "dungeon file", "schema", "dungeons");
            foreach (JsonElement dungeon in content.GetProperty("dungeons").EnumerateArray())
            {
                AssertFields(dungeon, "dungeon", "dungeonID", "archetypeID", "difficulty", "siteFamily", "siteKind", "entryObjectTypeID", "entryRoomKey",
                    "completionMode", "resources", "rooms", "gates", "waves", "props", "containers", "hazards", "miningLocks", "lootProfiles");
                AssertFields(dungeon.GetProperty("resources"), "resources", "ore", "gas", "ice");
                Each(dungeon.GetProperty("rooms"), room => AssertFields(room, "room", "key", "roomID", "entry"));
                Each(dungeon.GetProperty("gates"), gate => AssertFields(gate, "gate", "key", "typeID", "fromObjectID", "toObjectID", "destinationRoomKey", "allowedRaces"));
                Each(dungeon.GetProperty("waves"), wave =>
                {
                    AssertFields(wave, "wave", "key", "waveIndex", "trigger", "roomKey", "spawnQuery", "amount", "deadspace", "countdownSeconds", "lootProfile", "lootTags", "spawns");
                    Each(wave.GetProperty("spawns"), spawn => AssertFields(spawn, "spawn", "typeID", "entityKind"));
                });
                Each(dungeon.GetProperty("props"), prop =>
                {
                    AssertFields(prop, "prop", "key", "typeID", "typeNameCandidates", "roomKey", "positionOffset");
                    AssertPosition(prop);
                });
                Each(dungeon.GetProperty("containers"), box => AssertFields(box, "container", "role", "count", "lootProfile", "lootTags", "trigger", "bonus", "failureExplodes", "persistsAfterResponse"));
                Each(dungeon.GetProperty("hazards"), hazard => AssertFields(hazard, "hazard", "kind", "visibleCountdownSeconds", "hiddenTimerMinSeconds", "hiddenTimerMaxSeconds", "failureTriggersExplosion"));
                Each(dungeon.GetProperty("miningLocks"), rock =>
                {
                    AssertFields(rock, "mining lock", "key", "typeID", "oreTypeID", "roomKey", "count", "quantity", "positionOffset");
                    AssertPosition(rock);
                });
                Each(dungeon.GetProperty("lootProfiles"), profile => AssertFields(profile, "loot profile", "key", "tags"));
            }
        }

        JsonElement missions = ReadRaw(assembly, manifest.GetProperty("missionFile").GetString()!);
        AssertFields(missions, "missions", "schema", "killMissionDungeons", "goldenSecurityMissions");
        Each(missions.GetProperty("killMissionDungeons"), binding => AssertFields(binding, "binding", "missionID", "dungeonID"));
        Each(missions.GetProperty("goldenSecurityMissions"), golden => AssertFields(golden, "golden", "missionID", "dungeonID", "agentLevel"));

        JsonElement gaps = ReadRaw(assembly, manifest.GetProperty("knownGapsFile").GetString()!);
        AssertFields(gaps, "known gaps", "schema", "missingDungeonTemplates", "missionDefects", "missionsWithoutDungeon", "epicLikeMissionsWithoutOfficialArc");
        Each(gaps.GetProperty("missingDungeonTemplates"), missing => AssertFields(missing, "missing dungeon", "dungeonID", "missionIDs", "reason"));
        foreach (string list in new[] { "missionDefects", "missionsWithoutDungeon", "epicLikeMissionsWithoutOfficialArc" })
        {
            Each(gaps.GetProperty(list), gap => AssertFields(gap, list, "missionID", "reason"));
        }
    }

    [Fact]
    public void Loader_rejects_a_field_outside_the_schema()
    {
        string manifest = """{"schema":"eve.dungeon-catalog.manifest.v1","sdeBuild":1,"dungeonFiles":["d.json"],"missionFile":"m.json","knownGapsFile":"g.json","counts":{},"validation":{}}""";
        string dungeons = """{"schema":"eve.dungeon-catalog.dungeon.v1","dungeons":[{"dungeonID":1,"provenance":"client"}]}""";
        string missions = """{"schema":"eve.dungeon-catalog.missions.v1","killMissionDungeons":[],"goldenSecurityMissions":[]}""";
        string gaps = """{"schema":"eve.dungeon-catalog.known-gaps.v1","missingDungeonTemplates":[],"missionDefects":[],"missionsWithoutDungeon":[],"epicLikeMissionsWithoutOfficialArc":[]}""";
        var files = new Dictionary<string, string> { ["manifest.json"] = manifest, ["d.json"] = dungeons, ["m.json"] = missions, ["g.json"] = gaps };

        Assert.ThrowsAny<JsonException>(() => DungeonCatalog.Load(name => Open(files[name])));
    }

    private static Stream Open(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static JsonElement ReadRaw(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"resource {name} is not embedded");
        using JsonDocument document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static void AssertFields(JsonElement element, string where, params string[] allowed)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        var set = allowed.ToHashSet();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            Assert.True(set.Contains(property.Name), $"{where}: unknown field '{property.Name}'");
        }
    }

    private static void AssertPosition(JsonElement element)
    {
        if (element.TryGetProperty("positionOffset", out JsonElement position) && position.ValueKind == JsonValueKind.Object)
        {
            AssertFields(position, "position", "x", "y", "z");
        }
    }

    private static void Each(JsonElement array, Action<JsonElement> check)
    {
        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        foreach (JsonElement item in array.EnumerateArray())
        {
            check(item);
        }
    }
}
