using System.Text.Json;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>The committed level tags: their fields, their counts and provenance, the owner's optional file, and the level answers.</summary>
public sealed class MissionLevelTagsTests
{
    private static string Tag(string rows) => "{\"schema\":\"eve.rs.mission-level-tags.v1\",\"tags\":[" + rows + "]}";

    private const string Good = "{\"missionID\":875,\"level\":1,\"provenance\":\"wiki\",\"confidence\":\"high\"}";

    [Fact]
    public void The_committed_file_has_only_the_allowed_top_level_fields_and_only_the_allowed_row_fields()
    {
        // Fails if the committed file ever gains a field the owner did not approve (names, message text, rewards, dungeon content, rule IDs, URLs).
        using var document = JsonDocument.Parse(MissionLevelTags.CommittedJson());
        foreach (var property in document.RootElement.EnumerateObject())
            Assert.Contains(property.Name, MissionLevelTags.AllowedTopFields);
        var rows = document.RootElement.GetProperty("tags").EnumerateArray().ToArray();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            foreach (var property in row.EnumerateObject())
                Assert.Contains(property.Name, MissionLevelTags.AllowedRowFields);
        }
    }

    [Fact]
    public void The_committed_tags_are_the_1429_basic_missions_with_their_provenance_classes()
    {
        var tags = MissionLevelTags.Committed;
        Assert.Equal(1429, tags.Count);
        Assert.Equal(1019, tags.All.Count(tag => tag.Provenance == "client"));
        Assert.Equal(13, tags.All.Count(tag => tag.Provenance == "tq-log"));
        Assert.Equal(346, tags.All.Count(tag => tag.Provenance == "wiki"));
        Assert.Equal(51, tags.All.Count(tag => tag.Provenance == "rule"));
        Assert.Equal(0, tags.All.Count(tag => tag.Provenance == "owner"));
        Assert.Equal(1324, tags.All.Count(tag => tag.Confidence == "high"));
        Assert.Equal(78, tags.All.Count(tag => tag.Confidence == "medium"));
        Assert.Equal(27, tags.All.Count(tag => tag.Confidence == "low"));
        Assert.All(tags.All, tag => Assert.InRange(tag.Level, 1, 5));
    }

    [Fact]
    public void The_committed_file_counts_match_its_rows_and_no_mission_has_two_tags()
    {
        using var document = JsonDocument.Parse(MissionLevelTags.CommittedJson());
        var declared = document.RootElement.GetProperty("counts");
        var tags = MissionLevelTags.Committed;
        Assert.Equal(declared.GetProperty("total").GetInt32(), tags.Count);
        foreach (var provenance in new[] { "client", "tq-log", "wiki", "rule" })
            Assert.Equal(declared.GetProperty(provenance).GetInt32(), tags.All.Count(tag => tag.Provenance == provenance));
        Assert.Equal(tags.Count, tags.All.Select(tag => tag.MissionId).Distinct().Count());
    }

    [Fact]
    public void A_known_mission_answers_its_level_with_its_provenance_and_confidence()
    {
        var tag = MissionLevelTags.Committed.All.First(item => item.Provenance == "wiki");
        var answer = MissionLevelTags.Committed.Answer(tag.MissionId);
        Assert.Equal(tag.Level, answer.Level);
        Assert.Equal("wiki", answer.Provenance);
        Assert.Equal(tag.Confidence, answer.Confidence);
        Assert.Null(answer.Reason);
    }

    [Fact]
    public void A_mission_without_a_tag_is_unknown_with_a_reason_and_never_defaulted()
    {
        var answer = MissionLevelTags.Committed.Answer(999999999);
        Assert.Null(answer.Level);
        Assert.Equal("unknown", answer.Provenance);
        Assert.Null(answer.Confidence);
        Assert.Contains("has no level tag", answer.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_good_tag_file_loads()
    {
        var tags = MissionLevelTags.FromJson(Tag(Good), "fixture");
        Assert.Equal(1, tags.Count);
        Assert.Equal(1, tags.Answer(875).Level);
    }

    [Theory]
    [InlineData("{\"missionID\":875,\"level\":1,\"provenance\":\"tq\",\"confidence\":\"high\"}", "an unknown provenance")]
    [InlineData("{\"missionID\":875,\"level\":6,\"provenance\":\"wiki\",\"confidence\":\"high\"}", "level 6")]
    [InlineData("{\"missionID\":875,\"level\":1,\"provenance\":\"wiki\",\"confidence\":\"certain\"}", "an unknown confidence")]
    [InlineData("{\"missionID\":875,\"level\":1,\"provenance\":\"wiki\"}", "no confidence")]
    [InlineData("{\"missionID\":875,\"level\":1,\"provenance\":\"wiki\",\"confidence\":\"high\",\"name\":\"Golgothan Fields\"}", "a name field")]
    [InlineData("{\"missionID\":875,\"level\":1,\"provenance\":\"wiki\",\"confidence\":\"high\",\"ruleID\":\"wiki.x\"}", "a rule ID field")]
    [InlineData("{\"missionID\":875,\"level\":1,\"provenance\":\"wiki\",\"confidence\":\"high\",\"sourceUrl\":\"https://x\"}", "a URL field")]
    [InlineData("{\"missionID\":875,\"level\":1,\"provenance\":\"wiki\",\"confidence\":\"high\",\"reward\":500000}", "a reward field")]
    public void A_row_that_breaks_the_rules_is_refused(string row, string because)
    {
        var error = Assert.Throws<InvalidDataException>(() => MissionLevelTags.FromJson(Tag(row), "fixture"));
        Assert.Contains("fixture", error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(because));
    }

    [Fact]
    public void A_top_level_field_outside_the_allowed_set_is_refused()
    {
        var json = "{\"schema\":\"eve.rs.mission-level-tags.v1\",\"names\":[],\"tags\":[]}";
        Assert.Throws<InvalidDataException>(() => MissionLevelTags.FromJson(json, "fixture"));
    }

    [Fact]
    public void A_file_of_another_schema_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => MissionLevelTags.FromJson("{\"schema\":\"other\",\"tags\":[]}", "fixture"));
    }

    [Fact]
    public void The_owners_file_adds_its_tags_as_owner_and_a_second_tag_for_the_same_mission_is_refused()
    {
        var owner = MissionLevelTags.FromJson(Tag(Good.Replace("\"wiki\"", "\"owner\"").Replace("875", "900000001")), "owner file");
        var merged = MissionLevelTags.Committed.With(owner);
        Assert.Equal(1430, merged.Count);
        Assert.Equal("owner", merged.Answer(900000001).Provenance);

        var committedSample = MissionLevelTags.Committed.All.First();
        var clashing = MissionLevelTags.FromJson(
            Tag($"{{\"missionID\":{committedSample.MissionId},\"level\":2,\"provenance\":\"owner\",\"confidence\":\"high\"}}"),
            "owner file");
        Assert.Throws<InvalidDataException>(() => MissionLevelTags.Committed.With(clashing));
    }
}
