using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Sde;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>The official mission catalog, from synthetic rows shaped like CCP's <c>missions</c> and <c>epicArcs</c> records.</summary>
public sealed class MissionStaticCatalogTests
{
    private const string KillMission = """
        {"_key": 17183, "name": {"de": "Golgothan", "en": "Golgothan Fields", "ja": "x"},
         "hasStandingRewards": true, "expirationTime": 10080, "factionID": 500001,
         "killMission": {"dungeonID": 4242, "objectiveQuantity": 0, "objectiveTypeID": 587, "dropItemInMissionContainer": 30762},
         "messages": [{"_key": "messages.mission.briefing", "de": "Hallo", "en": "Go to the fields."}],
         "missionRewards": {"reward": {"rewardTypeID": 29, "rewardQuantity": 500000},
                            "bonusReward": {"rewardTypeID": 29, "rewardQuantity": 1000000}, "bonusTimeInterval": 300},
         "extraStandings": [{"_key": 500002, "_value": 1.5}]}
        """;

    private const string CourierMission = """
        {"_key": 17179, "name": {"en": "Paragon Requests"}, "hasStandingRewards": false,
         "courierMission": {"objectiveTypeID": 34, "objectiveQuantity": 5, "objectiveSingleton": true},
         "agentTypeID": 13, "corporationID": 1000419, "expirationTime": 1440,
         "initialAgentGiftTypeID": 587, "initialAgentGiftQuantity": 1}
        """;

    private const string Arc = """
        {"_key": 132, "name": {"en": "Visions of Greatness"}, "arcRestartInterval": 129600, "iconID": 3804, "factionID": 500001,
         "missions": [
           {"_key": 17183, "agentID": 3020234, "nextMissions": [17189]},
           {"_key": 17184, "agentID": 3020235, "nextMissions": [17190], "failMissionID": 17184},
           {"_key": 17189, "agentID": 3020234, "nextMissions": []}
         ]}
        """;

    private static SdeRow Row(long key, string json) => new(key, JsonDocument.Parse(json).RootElement.Clone());

    private static MissionStaticCatalog Fixture() => MissionStaticCatalog.FromRows(
        3405148,
        [Row(17183, KillMission), Row(17179, CourierMission)],
        [Row(132, Arc)]);

    [Fact]
    public void A_kill_mission_keeps_its_dungeon_objective_rewards_and_english_messages()
    {
        var mission = Fixture().Mission(17183);
        Assert.NotNull(mission);
        Assert.Equal("Golgothan Fields", mission.NameEn);
        Assert.True(mission.HasStandingRewards);
        Assert.Equal(10080, mission.ExpirationMinutes);
        Assert.Equal(500001, mission.FactionId);
        Assert.Null(mission.CorporationId);
        Assert.Null(mission.AgentTypeId);
        Assert.Equal(4242, mission.Kill!.DungeonId);
        Assert.Equal(587, mission.Kill.ObjectiveTypeId);
        Assert.Equal(0, mission.Kill.ObjectiveQuantity);
        Assert.Equal(30762, mission.Kill.DropItemTypeId);
        Assert.Null(mission.Courier);
        Assert.Equal(29, mission.Reward!.RewardTypeId);
        Assert.Equal(500000, mission.Reward.RewardQuantity);
        Assert.Equal(1000000, mission.Reward.BonusRewardQuantity);
        Assert.Equal(300, mission.Reward.BonusIntervalMinutes);
        Assert.Equal([(500002L, 1.5)], mission.ExtraStandings);
        Assert.Equal("Go to the fields.", mission.EnglishMessages["messages.mission.briefing"]);
    }

    [Fact]
    public void A_courier_mission_keeps_its_objective_and_its_agent_constraints_and_leaves_absent_fields_null()
    {
        var mission = Fixture().Mission(17179);
        Assert.NotNull(mission);
        Assert.Null(mission.Kill);
        Assert.Equal(34, mission.Courier!.ObjectiveTypeId);
        Assert.Equal(5, mission.Courier.ObjectiveQuantity);
        Assert.True(mission.Courier.Singleton);
        Assert.Null(mission.Reward);
        Assert.Equal(13, mission.AgentTypeId);
        Assert.Equal(1000419, mission.CorporationId);
        Assert.Null(mission.FactionId);
        Assert.Equal(587, mission.InitialAgentGiftTypeId);
        Assert.Empty(mission.ExtraStandings);
        Assert.Empty(mission.EnglishMessages);
    }

    [Fact]
    public void An_epic_arc_lists_its_steps_and_each_step_names_its_agent()
    {
        var arc = Fixture().EpicArc(132);
        Assert.NotNull(arc);
        Assert.Equal("Visions of Greatness", arc.NameEn);
        Assert.Equal(129600, arc.RestartIntervalMinutes);
        Assert.Equal(3804, arc.IconId);
        Assert.Equal(3, arc.Steps.Count);
        Assert.Equal(17189, arc.Steps[2].MissionId);
        Assert.Equal(3020234, arc.Steps[2].AgentId);
        Assert.Empty(arc.Steps[2].NextMissionIds);
        Assert.Null(arc.Steps[2].FailMissionId);
        Assert.Equal(17184, arc.Steps[1].FailMissionId);
    }

    [Fact]
    public void An_agent_is_offered_only_the_arc_missions_it_gives_and_no_other_pool()
    {
        var catalog = Fixture();
        Assert.Equal([17183L, 17189L], catalog.PlausibleMissionIds(3020234));
        Assert.Equal([17184L], catalog.PlausibleMissionIds(3020235));
        Assert.Empty(catalog.PlausibleMissionIds(3020999));
        Assert.Empty(catalog.PlausibleMissionIds(0));
    }

    [Fact]
    public void Nothing_is_disabled_and_each_epic_journal_text_type_has_no_id()
    {
        var catalog = Fixture();
        Assert.Empty(catalog.DisabledMissionIds);
        var maps = catalog.EpicArcMessageMaps;
        Assert.Equal(3, maps.Count);
        foreach (var messageType in MissionDataGaps.EpicJournalMessageTypes)
            Assert.Empty((JsonObject)maps[messageType]!);
    }

    [Fact]
    public void A_catalog_without_a_loaded_build_answers_nothing_and_says_why()
    {
        var catalog = MissionStaticCatalog.Unavailable("no mission build is loaded");
        Assert.False(catalog.IsLoaded);
        Assert.Null(catalog.Build);
        Assert.Equal("no mission build is loaded", catalog.UnavailableReason);
        Assert.Empty(catalog.Missions);
        Assert.Empty(catalog.EpicArcs);
        Assert.Empty(catalog.PlausibleMissionIds(3020234));
        Assert.Empty(catalog.DisabledMissionIds);
    }

    [Fact]
    public void Every_gap_has_an_area_and_a_reason_and_the_dungeon_gap_names_its_dungeon()
    {
        var gaps = Fixture().Gaps();
        Assert.Equal(6, gaps.Count);
        foreach (var (area, reason) in gaps)
        {
            Assert.False(string.IsNullOrWhiteSpace(area));
            Assert.Contains("official", reason, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("4242", MissionDataGaps.DungeonContent(4242), StringComparison.Ordinal);
    }

    [Fact]
    public void A_kill_mission_keeps_its_official_dungeon_id_and_says_its_content_is_not_available()
    {
        var answer = Fixture().KillDungeon(17183);
        Assert.Equal(4242, answer.DungeonId);
        Assert.False(answer.OfficialContent);
        Assert.Contains("not available from official data", answer.Reason, StringComparison.Ordinal);
        Assert.Contains("4242", answer.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mission_without_a_kill_objective_or_in_no_build_names_no_dungeon_and_says_why()
    {
        var catalog = Fixture();
        var courier = catalog.KillDungeon(17179);
        Assert.Null(courier.DungeonId);
        Assert.False(courier.OfficialContent);
        Assert.Contains("no kill objective", courier.Reason, StringComparison.Ordinal);
        var absent = catalog.KillDungeon(123456789);
        Assert.Null(absent.DungeonId);
        Assert.Contains("not in the official build", absent.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_level_of_a_mission_comes_from_the_tags_and_a_mission_without_one_is_unknown()
    {
        var catalog = Fixture();
        var tagged = MissionLevelTags.Committed.All.First();
        var answer = catalog.Level(tagged.MissionId);
        Assert.Equal(tagged.Level, answer.Level);
        Assert.Equal(tagged.Provenance, answer.Provenance);
        var unknown = catalog.Level(17183);
        Assert.Null(unknown.Level);
        Assert.Equal("unknown", unknown.Provenance);
        Assert.NotNull(unknown.Reason);
    }

    private const string OnByDefaultArc = """{"_key": 55, "name": {"en": "On By Default"}, "arcRestartInterval": 129600, "missions": [{"_key": 9501, "agentID": 3000001, "nextMissions": []}]}""";
    private const string OtherArc = """{"_key": 9001, "name": {"en": "Other"}, "arcRestartInterval": 129600, "missions": [{"_key": 9502, "agentID": 3000002, "nextMissions": []}]}""";

    [Fact]
    public void An_arc_on_by_default_offers_its_generic_steps_and_any_other_arc_holds_them_back()
    {
        var sites = new Dictionary<long, string> { [9501] = "generic", [9502] = "generic" };
        var catalog = MissionStaticCatalog.FromRows(3405148, [], [
            new SdeRow(55, JsonDocument.Parse(OnByDefaultArc).RootElement.Clone()),
            new SdeRow(9001, JsonDocument.Parse(OtherArc).RootElement.Clone()),
        ], siteStatus: sites);

        Assert.False(catalog.IsHeldBack(9501));
        Assert.True(catalog.IsHeldBack(9502));
    }
}
