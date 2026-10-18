using System.Text.Json;
using Eve.Sde;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>The offer rule of the epic arcs, from a synthetic arc shaped like CCP's <c>epicArcs</c> record (a fork, a branch of another agent, a terminal).</summary>
public sealed class EpicArcOffersTests
{
    // 9101 (agent 5001, a kill) forks to 9102 (agent 5001) and 9103 (agent 5002); both lead to the terminal 9104 (agent 5001).
    private const string Arc = """
        {"_key": 9001, "name": {"en": "Fixture Arc"}, "arcRestartInterval": 129600,
         "missions": [
           {"_key": 9101, "agentID": 5001, "nextMissions": [9102, 9103]},
           {"_key": 9102, "agentID": 5001, "nextMissions": [9104]},
           {"_key": 9103, "agentID": 5002, "nextMissions": [9104]},
           {"_key": 9104, "agentID": 5001, "nextMissions": []}
         ]}
        """;

    // A second arc of the same agent, with a lower mission ID than the first: the lowest offerable ID wins.
    private const string OtherArc = """
        {"_key": 9002, "name": {"en": "Other Fixture Arc"}, "arcRestartInterval": 129600,
         "missions": [{"_key": 9050, "agentID": 5001, "nextMissions": []}]}
        """;

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static SdeRow Row(long key, string json) => new(key, JsonDocument.Parse(json).RootElement.Clone());

    private static MissionStaticCatalog Catalog(bool genericSwitch, IReadOnlyDictionary<long, string>? siteStatus = null) =>
        MissionStaticCatalog.FromRows(
            3405148, [], [Row(9001, Arc), Row(9002, OtherArc)],
            includeUnavailableArcs: true,
            siteStatus: siteStatus ?? new Dictionary<long, string> { [9101] = MissionSiteStatus.Generic, [9050] = MissionSiteStatus.Missing },
            includeGenericArcs: genericSwitch);

    private static IEnumerable<(long MissionId, DateTimeOffset CompletedAt)> Done(params long[] missions) =>
        missions.Select(id => (id, T0));

    [Fact]
    public void A_generic_site_is_held_back_by_default_and_offered_only_with_the_switch()
    {
        Assert.Null(EpicArcOffers.Pick(Catalog(genericSwitch: false), 5001, [], T0));
        Assert.Equal(9101, EpicArcOffers.Pick(Catalog(genericSwitch: true), 5001, [], T0));
    }

    [Fact]
    public void Only_the_current_step_is_offered_and_the_next_follows_its_completion()
    {
        var catalog = Catalog(genericSwitch: true);
        Assert.Equal(9101, EpicArcOffers.Pick(catalog, 5001, [], T0));
        Assert.Equal(9102, EpicArcOffers.Pick(catalog, 5001, Done(9101), T0));
        Assert.Equal(9104, EpicArcOffers.Pick(catalog, 5001, Done(9101, 9102), T0 + TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Once_one_successor_of_a_fork_is_completed_the_other_is_not_offered()
    {
        var catalog = Catalog(genericSwitch: true);
        Assert.Equal(9103, EpicArcOffers.Pick(catalog, 5002, Done(9101), T0));
        Assert.Null(EpicArcOffers.Pick(catalog, 5002, Done(9101, 9102), T0));
    }

    [Fact]
    public void A_missing_site_is_never_offered_even_with_the_switch()
    {
        var catalog = Catalog(genericSwitch: true, new Dictionary<long, string>
        {
            [9101] = MissionSiteStatus.Generic, [9102] = MissionSiteStatus.Missing, [9050] = MissionSiteStatus.Missing,
        });
        Assert.Null(EpicArcOffers.Pick(catalog, 5001, Done(9101), T0));
    }

    [Fact]
    public void A_finished_arc_cools_down_for_its_restart_interval_then_runs_again_from_the_entry()
    {
        var catalog = Catalog(genericSwitch: true);
        var finished = Done(9101, 9102, 9103, 9104);
        Assert.Null(EpicArcOffers.Pick(catalog, 5001, finished, T0 + TimeSpan.FromDays(1)));
        Assert.Equal(9101, EpicArcOffers.Pick(catalog, 5001, finished, T0 + TimeSpan.FromDays(91)));
    }

    [Fact]
    public void A_quit_or_declined_step_is_offered_again_because_it_was_not_completed()
    {
        var catalog = Catalog(genericSwitch: true);
        Assert.Equal(9101, EpicArcOffers.Pick(catalog, 5001, [], T0));
        Assert.Equal(9101, EpicArcOffers.Pick(catalog, 5001, [], T0 + TimeSpan.FromHours(5)));
    }

    [Fact]
    public void Among_offerable_steps_the_lowest_mission_id_is_offered()
    {
        var catalog = Catalog(genericSwitch: true, new Dictionary<long, string> { [9101] = MissionSiteStatus.Generic });
        Assert.Equal(9050, EpicArcOffers.Pick(catalog, 5001, [], T0));
        Assert.Equal(9101, EpicArcOffers.Pick(Catalog(genericSwitch: true), 5001, [], T0));
    }

    [Fact]
    public void Nothing_is_offered_without_a_loaded_build()
    {
        Assert.Null(EpicArcOffers.Pick(MissionStaticCatalog.Unavailable("no build"), 5001, [], T0));
    }

    [Fact]
    public void The_committed_site_statuses_hold_back_the_generic_arc_29_site_and_the_missing_arc_132_sites()
    {
        Assert.Equal(MissionSiteStatus.Generic, MissionSiteStatus.Committed[14117]);
        foreach (var missionId in new long[] { 17183, 17184, 17185, 17186 })
            Assert.Equal(MissionSiteStatus.Missing, MissionSiteStatus.Committed[missionId]);
        Assert.Equal(182, MissionSiteStatus.Committed.Count);
        Assert.Equal(0, MissionSiteStatus.Committed.Count(pair => pair.Value == MissionSiteStatus.Specific));
    }

    // 9301 leads to 9302, whose failMissionID is 9301: a failure of 9302 restarts the run at 9301.
    private const string RestartArc = """
        {"_key": 9003, "name": {"en": "Restart Arc"}, "arcRestartInterval": 129600,
         "missions": [
           {"_key": 9301, "agentID": 5001, "nextMissions": [9302]},
           {"_key": 9302, "agentID": 5001, "nextMissions": [], "failMissionID": 9301}]}
        """;

    [Fact]
    public void A_failure_whose_failMissionID_is_an_earlier_step_restarts_the_run_there()
    {
        var catalog = MissionStaticCatalog.FromRows(3405148, [], [Row(9003, RestartArc)], includeUnavailableArcs: true);
        Assert.Equal(9302, EpicArcOffers.Pick(catalog, 5001, Done(9301), T0));
        Assert.Equal(9301, EpicArcOffers.Pick(catalog, 5001, Done(9301), T0 + TimeSpan.FromHours(2),
            new[] { (9302L, T0 + TimeSpan.FromHours(1)) }));
    }
}
