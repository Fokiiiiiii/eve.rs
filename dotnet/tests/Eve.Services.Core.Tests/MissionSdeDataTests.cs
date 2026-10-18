using Eve.Data;
using Eve.Sde;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Services.Core.Tests;

/// <summary>
/// The rule and the mission catalog against the real loaded data: the pinned SDE (<c>sde</c>, build 3396210) for the agent rule,
/// and the mission build (<c>sde_mission</c>, build 3405148) for the catalog. Both need PostgreSQL and the data loaded
/// (<c>eve-sde ensure</c>, and <c>eve-sde ensure --missions</c>).
/// </summary>
public sealed class MissionSdeDataTests
{
    private sealed class PgFactAttribute : FactAttribute
    {
        public PgFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null)
                Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }

    private static NpgsqlDataSource Source() =>
        NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));

    [PgFact]
    public async Task The_rule_gives_the_pinned_agents_the_kinds_its_counts_state()
    {
        await using var source = Source();
        var reader = new SdeReader(source);
        Assert.True(await reader.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        var agents = (await reader.ReadAsync("npcCharacters"))
            .Where(row => row.Doc.TryGetProperty("agent", out var agent) && agent.ValueKind == System.Text.Json.JsonValueKind.Object)
            .Select(row => (Type: Read(row.Doc.GetProperty("agent"), "agentTypeID"), Division: Read(row.Doc.GetProperty("agent"), "divisionID")))
            .ToArray();
        Assert.Equal(10966, agents.Length);

        var byKind = agents
            .Select(agent => (agent.Type, Class: AgentMissionKindRules.Classify(agent.Type, agent.Division)))
            .GroupBy(agent => (agent.Type, agent.Class.MissionKind, agent.Class.ImportantMission))
            .ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(3729, byKind[(2, "courier", false)]);
        Assert.Equal(3671, byKind[(2, "encounter", false)]);
        Assert.Equal(1375, byKind[(2, "mining", false)]);
        Assert.Equal(244, byKind[(4, "research", false)]);
        Assert.Equal(52, byKind[(10, "epicArc", true)]);
        Assert.Equal(260, byKind[(9, "encounter", false)]);
        Assert.Equal(12, byKind[(13, "heraldry", false)]);
        Assert.Equal(696, byKind[(8, "", false)]);
        Assert.Equal(143, byKind[(5, "", false)]);
    }

    [PgFact]
    public async Task The_mission_build_is_loaded_and_its_catalog_has_the_ids_the_server_reads()
    {
        await using var source = Source();
        var catalog = await MissionStaticCatalog.LoadAsync(new SdeReader(source, MissionStaticCatalog.DefaultSchema));
        Assert.True(catalog.IsLoaded, catalog.UnavailableReason);
        Assert.Equal(3405148, catalog.Build);
        Assert.Equal(2892, catalog.Missions.Count);
        Assert.Equal(21, catalog.EpicArcs.Count);
        for (var id = 17179L; id <= 17190; id++)
            Assert.NotNull(catalog.Mission(id));
        var arc = catalog.EpicArc(132);
        Assert.NotNull(arc);
        Assert.Contains(17183L, catalog.PlausibleMissionIds(3020234));
    }

    [PgFact]
    public async Task Every_committed_level_tag_names_a_mission_of_the_loaded_build()
    {
        await using var source = Source();
        var catalog = await MissionStaticCatalog.LoadAsync(new SdeReader(source, MissionStaticCatalog.DefaultSchema), MissionLevelTags.Committed);
        Assert.True(catalog.IsLoaded, catalog.UnavailableReason);
        var missing = MissionLevelTags.Committed.All.Where(tag => catalog.Mission(tag.MissionId) is null).Select(tag => tag.MissionId).ToArray();
        Assert.Empty(missing);
    }

    [PgFact]
    public async Task The_committed_tags_by_kind_and_level_are_the_counted_ones_and_none_is_an_epic_arc_step()
    {
        await using var source = Source();
        var catalog = await MissionStaticCatalog.LoadAsync(new SdeReader(source, MissionStaticCatalog.DefaultSchema), MissionLevelTags.Committed);
        Assert.True(catalog.IsLoaded, catalog.UnavailableReason);
        var tagged = MissionLevelTags.Committed.All.Select(tag => (Mission: catalog.Mission(tag.MissionId)!, tag.Level)).ToArray();
        Assert.All(tagged, item => Assert.NotNull(item.Mission));
        Assert.All(tagged, item => Assert.Null(catalog.MissionAvailability(item.Mission.MissionId).ArcId));
        var byKind = tagged
            .GroupBy(item => (Kind: item.Mission.Courier is not null ? "courier" : item.Mission.Kill is not null ? "kill" : "other", item.Level))
            .ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(124, byKind[("courier", 1)]);
        Assert.Equal(53, byKind[("courier", 2)]);
        Assert.Equal(73, byKind[("courier", 3)]);
        Assert.Equal(64, byKind[("courier", 4)]);
        Assert.Equal(1, byKind[("courier", 5)]);
        Assert.Equal(409, byKind[("kill", 1)]);
        Assert.Equal(234, byKind[("kill", 2)]);
        Assert.Equal(328, byKind[("kill", 3)]);
        Assert.Equal(356, byKind[("kill", 4)]);
        Assert.Equal(150, byKind[("kill", 5)]);
        Assert.DoesNotContain(byKind.Keys, key => key.Kind == "other");
    }

    [PgFact]
    public async Task The_untagged_missions_have_no_static_level_or_dungeon_difficulty_basis()
    {
        await using var source = Source();
        var reader = new SdeReader(source, MissionStaticCatalog.DefaultSchema);
        var missionCatalog = await MissionStaticCatalog.LoadAsync(reader, MissionLevelTags.Committed);
        Assert.True(missionCatalog.IsLoaded, missionCatalog.UnavailableReason);
        var rows = await reader.ReadAsync("missions");
        var taggedIds = MissionLevelTags.Committed.All.Select(tag => tag.MissionId).ToHashSet();
        var untagged = missionCatalog.Missions.Where(mission => !taggedIds.Contains(mission.MissionId)).ToArray();
        Assert.Equal(1100, untagged.Length);

        var untaggedRows = rows.Where(row => !taggedIds.Contains(row.Key)).ToArray();
        Assert.Equal(untagged.Length, untaggedRows.Length);
        Assert.DoesNotContain(untaggedRows, row =>
            new[] { "level", "missionLevel", "agentLevel" }
                .Any(field => row.Doc.TryGetProperty(field, out _)));

        var arcStepIds = missionCatalog.EpicArcs
            .SelectMany(arc => arc.Steps)
            .Select(step => step.MissionId)
            .ToHashSet();
        var dungeons = Eve.Dungeons.DungeonCatalog.LoadEmbedded();
        Assert.DoesNotContain(untagged, mission =>
            !arcStepIds.Contains(mission.MissionId) &&
            mission.Kill is not null &&
            dungeons.GetMission(mission.MissionId) is Eve.Dungeons.MissionFound
            {
                Dungeon: Eve.Dungeons.DungeonFound { Dungeon.Difficulty: not null },
            });

        foreach (var missionId in new long[] { 17183, 17184, 17185, 17186 })
        {
            Assert.Contains(missionId, arcStepIds);
            Assert.Null(MissionLevelTags.Committed.Answer(missionId).Level);
            var mission = Assert.IsType<Eve.Dungeons.MissionFound>(dungeons.GetMission(missionId));
            Assert.IsType<Eve.Dungeons.DungeonNoTemplate>(mission.Dungeon);
        }
    }

    private static long Read(System.Text.Json.JsonElement agent, string name) =>
        agent.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
}
