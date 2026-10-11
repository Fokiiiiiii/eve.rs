using System.Text.Json;
using Eve.Sde;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>The basic kill pool of the security agents, on the committed level tags (missions 3318, 3319 at level 5; 891 at level 4).</summary>
public class BasicKillOffersTests
{
    private static SdeRow Kill(long id, long dungeon) => new(id, JsonDocument.Parse(
        "{\"_key\": " + id + ", \"name\": {\"en\": \"Kill " + id + "\"}, \"nameID\": " + (id + 100000) +
        ", \"killMission\": {\"dungeonID\": " + dungeon + ", \"objectiveQuantity\": 0}}").RootElement.Clone());

    private static readonly MissionStaticCatalog Catalog = MissionStaticCatalog.FromRows(
        3405148, [Kill(3318, 2147), Kill(3319, 2189), Kill(891, 216)], []);

    private static AgentMissionAgentData Agent(long level, long type = 2, long division = 24, string kind = "encounter") => new(
        AgentId: 3008001, AgentTypeId: type, DivisionId: division, Level: level, StationId: 60000001, CorporationId: 1000001,
        IsLocatorAgent: false, IsInSpace: false, CareerId: null, ImportantMission: false, MissionKind: kind,
        MissionTypeLabel: "", SpecialityId: null, StationTypeId: null, BloodlineId: null, SolarSystemId: 30000001,
        DungeonId: null, SpawnPointId: null, TypeId: null);

    private static readonly Func<long, bool> AllStart = _ => true;

    [Fact]
    public void A_level_5_security_agent_offers_the_lowest_level_5_basic_kill_mission()
    {
        Assert.Equal(3318, BasicKillOffers.Pick(Catalog, Agent(5), new HashSet<long>(), AllStart));
    }

    [Fact]
    public void A_mission_whose_dungeon_cannot_start_is_not_offered()
    {
        Assert.Equal(3319, BasicKillOffers.Pick(Catalog, Agent(5), new HashSet<long>(), dungeon => dungeon != 2147));
    }

    [Fact]
    public void A_completed_mission_is_not_offered_again()
    {
        Assert.Equal(3319, BasicKillOffers.Pick(Catalog, Agent(5), new HashSet<long> { 3318 }, AllStart));
    }

    [Fact]
    public void A_level_4_agent_offers_a_level_4_mission_and_levels_3_or_below_are_not_offered()
    {
        Assert.Equal(891, BasicKillOffers.Pick(Catalog, Agent(4), new HashSet<long>(), AllStart));
        Assert.Null(BasicKillOffers.Pick(Catalog, Agent(3), new HashSet<long>(), AllStart));
    }

    [Fact]
    public void Only_basic_security_agents_offer_these_missions()
    {
        Assert.Null(BasicKillOffers.Pick(Catalog, Agent(5, type: 9), new HashSet<long>(), AllStart));
        Assert.Null(BasicKillOffers.Pick(Catalog, Agent(5, division: 22, kind: "courier"), new HashSet<long>(), AllStart));
        Assert.Null(BasicKillOffers.Pick(MissionStaticCatalog.Unavailable("no build"), Agent(5), new HashSet<long>(), AllStart));
    }
}
