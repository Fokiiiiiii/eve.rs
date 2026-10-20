using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public class AgentLocationWrapTests
{
    private static AgentMissionAgentData Agent(long? station, long? stationType, bool inSpace = false, long? dungeon = null, long? spawn = null, long? type = null) => new(
        AgentId: 3019369, AgentTypeId: 10, DivisionId: 23, Level: 1, StationId: station, CorporationId: 1000129,
        IsLocatorAgent: false, IsInSpace: inSpace, CareerId: null, ImportantMission: false, MissionKind: "epicArc",
        MissionTypeLabel: "", SpecialityId: null, StationTypeId: stationType, BloodlineId: null, SolarSystemId: inSpace ? 30005199 : 30004504,
        DungeonId: dungeon, SpawnPointId: spawn, TypeId: type);

    private static Dictionary<string, PyValue> Fields(PyDict dict) =>
        dict.Entries.ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);

    [Fact]
    public void A_station_agent_is_at_its_station_with_the_station_type()
    {
        var fields = Fields(AgentMissionService.AgentLocationWrap(Agent(station: 60012583, stationType: 1531)));
        Assert.Equal(60012583, ((PyInt)fields["locationID"]).Value);
        Assert.Equal(1531, ((PyInt)fields["typeID"]).Value);
        Assert.Equal(30004504, ((PyInt)fields["solarsystemID"]).Value);
        Assert.False(fields.ContainsKey("locationType"));
    }

    [Fact]
    public void A_station_agent_without_a_known_type_is_a_station_of_the_reference_type()
    {
        var fields = Fields(AgentMissionService.AgentLocationWrap(Agent(station: 60012583, stationType: null)));
        Assert.Equal(1531, ((PyInt)fields["typeID"]).Value);
    }

    [Fact]
    public void An_agent_in_space_is_in_its_system_with_the_spawn_data_of_its_row()
    {
        var fields = Fields(AgentMissionService.AgentLocationWrap(Agent(station: 60012583, stationType: 1531, inSpace: true,
            dungeon: 3750, spawn: 5038, type: 30887)));
        Assert.Equal("agentinspace", ((PyStr)fields["locationType"]).Value);
        Assert.Equal(30005199, ((PyInt)fields["solarsystemID"]).Value);
        Assert.Equal(30005199, ((PyInt)fields["locationID"]).Value);
        Assert.Equal(5, ((PyInt)fields["typeID"]).Value);
        Assert.Equal(3750, ((PyInt)fields["dungeonID"]).Value);
        Assert.Equal(5038, ((PyInt)fields["spawnPointID"]).Value);
        Assert.Equal(30887, ((PyInt)fields["agentInSpaceTypeID"]).Value);
    }
}
