using Eve.Sde;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>The epic arc availability table, and what the catalog offers from it.</summary>
public sealed class EpicArcAvailabilityTests
{
    private const string Schema = "eve.rs.epic-arc-availability.v1";

    private static string Table(string rows) => "{\"schema\":\"" + Schema + "\",\"arcs\":[" + rows + "]}";

    private const string InRow = "{\"arcID\":132,\"availability\":\"in-client-build\",\"evidence\":\"listed in the client constants\"}";
    private const string NewerRow = "{\"arcID\":64,\"availability\":\"newer-than-client\",\"evidence\":\"not in the client constants\"}";

    private const string ArcInClient = """
        {"_key": 132, "name": {"en": "Fixture arc"}, "arcRestartInterval": 129600, "iconID": 1, "factionID": 500001,
         "missions": [{"_key": 17183, "agentID": 3020234, "nextMissions": [17189]}, {"_key": 17189, "agentID": 3020234, "nextMissions": []}]}
        """;

    private const string ArcNewer = """
        {"_key": 64, "name": {"en": "Fixture course"}, "arcRestartInterval": 129600, "iconID": 2,
         "missions": [{"_key": 17500, "agentID": 3019486, "nextMissions": []}]}
        """;

    private static MissionStaticCatalog Catalog(bool includeUnavailable) =>
        MissionStaticCatalog.FromRows(
            3405148,
            [Fixture(17183), Fixture(17189), Fixture(17500)],
            [Row(132, ArcInClient), Row(64, ArcNewer)],
            availability: EpicArcAvailability.FromJson(Table(InRow + "," + NewerRow), "fixture"),
            includeUnavailableArcs: includeUnavailable);

    private static SdeRow Row(long key, string json) => new(key, System.Text.Json.JsonDocument.Parse(json).RootElement.Clone());

    private static SdeRow Fixture(long key) => new(key, System.Text.Json.JsonDocument.Parse("{\"_key\": " + key + ", \"name\": {\"en\": \"Fixture mission\"}}").RootElement.Clone());

    [Fact]
    public void The_committed_table_holds_21_arcs_with_9_in_the_client_build_and_12_newer_and_no_unknown()
    {
        var table = EpicArcAvailability.Committed;
        Assert.Equal(21, table.All.Count);
        Assert.Equal(9, table.All.Count(row => row.Availability == "in-client-build"));
        Assert.Equal(12, table.All.Count(row => row.Availability == "newer-than-client"));
        Assert.Equal(0, table.All.Count(row => row.Availability == "unknown"));
        foreach (var arc in new long[] { 29, 40, 48, 52, 53, 55, 56, 128, 132 })
            Assert.Equal("in-client-build", table.Availability(arc));
        foreach (var arc in new long[] { 64, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77 })
            Assert.Equal("newer-than-client", table.Availability(arc));
    }

    [Fact]
    public void The_committed_table_has_only_the_allowed_fields()
    {
        using var document = System.Text.Json.JsonDocument.Parse(EpicArcAvailability.CommittedJson());
        foreach (var property in document.RootElement.EnumerateObject())
            Assert.Contains(property.Name, EpicArcAvailability.AllowedTopFields);
        foreach (var row in document.RootElement.GetProperty("arcs").EnumerateArray())
        {
            foreach (var property in row.EnumerateObject())
                Assert.Contains(property.Name, EpicArcAvailability.AllowedRowFields);
        }
    }

    [Fact]
    public void An_arc_with_no_row_is_unknown()
    {
        Assert.Equal("unknown", EpicArcAvailability.Committed.Availability(999999));
        Assert.Null(EpicArcAvailability.Committed.Row(999999));
    }

    [Theory]
    [InlineData("{\"arcID\":132,\"availability\":\"maybe\",\"evidence\":\"x\"}", "an unknown availability")]
    [InlineData("{\"arcID\":132,\"availability\":\"in-client-build\",\"evidence\":\"x\",\"name\":\"Visions\"}", "an arc name")]
    [InlineData("{\"arcID\":132,\"availability\":\"in-client-build\",\"evidence\":\"\"}", "no evidence")]
    [InlineData("{\"arcID\":0,\"availability\":\"in-client-build\",\"evidence\":\"x\"}", "a zero arc ID")]
    [InlineData("{\"arcID\":132,\"availability\":\"in-client-build\"}", "no evidence field")]
    public void A_row_that_breaks_the_rules_is_refused(string row, string because)
    {
        var error = Assert.Throws<InvalidDataException>(() => EpicArcAvailability.FromJson(Table(row), "fixture"));
        Assert.Contains("fixture", error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(because));
    }

    [Fact]
    public void Two_rows_for_one_arc_and_another_schema_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => EpicArcAvailability.FromJson(Table(InRow + "," + InRow), "fixture"));
        Assert.Throws<InvalidDataException>(() => EpicArcAvailability.FromJson("{\"schema\":\"other\",\"arcs\":[]}", "fixture"));
    }

    [Fact]
    public void By_default_only_the_in_client_build_arc_is_offered_to_its_agent()
    {
        var catalog = Catalog(includeUnavailable: false);
        Assert.Equal([17183L, 17189L], catalog.PlausibleMissionIds(3020234));
        Assert.Empty(catalog.PlausibleMissionIds(3019486));
        Assert.Equal([132L], catalog.OfferableEpicArcs.Select(arc => arc.ArcId));
        Assert.False(catalog.IsOfferable(64));
    }

    [Fact]
    public void The_explicit_switch_offers_the_newer_arc_too()
    {
        var catalog = Catalog(includeUnavailable: true);
        Assert.Equal([17500L], catalog.PlausibleMissionIds(3019486));
        Assert.Equal([64L, 132L], catalog.OfferableEpicArcs.Select(arc => arc.ArcId));
        Assert.True(catalog.IncludesUnavailableArcs);
    }

    [Fact]
    public void A_mission_takes_its_arc_availability_and_a_mission_outside_an_arc_is_unknown_with_a_reason()
    {
        var catalog = Catalog(includeUnavailable: false);
        var inArc = catalog.MissionAvailability(17500);
        Assert.Equal("newer-than-client", inArc.Availability);
        Assert.Equal(64, inArc.ArcId);
        var outside = catalog.MissionAvailability(123456789);
        Assert.Equal("unknown", outside.Availability);
        Assert.Null(outside.ArcId);
        Assert.Contains("no evidence", outside.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_switch_reads_the_environment_as_one_or_true_only()
    {
        var saved = Environment.GetEnvironmentVariable(MissionStaticCatalog.IncludeUnavailableVariable);
        try
        {
            Environment.SetEnvironmentVariable(MissionStaticCatalog.IncludeUnavailableVariable, "1");
            Assert.True(MissionStaticCatalog.IncludeUnavailableFromEnvironment());
            Environment.SetEnvironmentVariable(MissionStaticCatalog.IncludeUnavailableVariable, "yes");
            Assert.False(MissionStaticCatalog.IncludeUnavailableFromEnvironment());
            Environment.SetEnvironmentVariable(MissionStaticCatalog.IncludeUnavailableVariable, null);
            Assert.False(MissionStaticCatalog.IncludeUnavailableFromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(MissionStaticCatalog.IncludeUnavailableVariable, saved);
        }
    }
}
