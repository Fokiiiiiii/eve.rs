using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class LoginInitializationGameStoreTests
{
    [Fact]
    public async Task Achievement_and_operations_progress_use_the_raw_character_row()
    {
        var character = JsonNode.Parse("""
            {
              "achievementState": {
                "eventDict": { "client_spinCamera": 7 },
                "completedDict": { "9": "134273341290000000" },
                "hasEverWarped": false
              },
              "operationProgress": {
                "32": [[158, 0, [[347, 10, 7]]]]
              }
            }
            """)!.AsObject();

        var achievementState = LoginInitializationGameStoreProjection.ReadAchievementState(character);
        var achievement = new AchievementTrackerMgrService(
            readState: (_, _) => ValueTask.FromResult(achievementState),
            hasEverWarped: (_, _) => ValueTask.FromResult(
                LoginInitializationGameStoreProjection.ReadHasEverWarped(character) ?? true));
        var session = new ClientSession { CharacterId = 90000001 };
        var achievementResult = Assert.IsType<PyDict>(await Call(
            achievement, session, "GetCompletedAchievementsAndClientEventCount"));
        var eventCounts = Assert.IsType<PyDict>(Field(achievementResult, "eventDict"));
        Assert.Equal(new PyInt(7), Field(eventCounts, new PyStr("client_spinCamera")));
        var completed = Assert.IsType<PyDict>(Field(achievementResult, "completedDict"));
        Assert.Equal(new PyLong(134273341290000000), Field(completed, new PyInt(9)));
        Assert.Same(PyBool.False, await Call(achievement, session, "HasEverWarped"));

        var operations = new OperationsManagerService(
            readCharacterProgress: (_, _) => ValueTask.FromResult(
                LoginInitializationGameStoreProjection.ReadOperationsProgress(character)));
        var progress = Assert.IsType<PyDict>(await Call(operations, session, "get_character_progress"));
        var category = Assert.IsType<PyList>(Field(progress, new PyInt(32)));
        var operation = Assert.IsType<PyList>(Assert.Single(category.Items));
        Assert.Equal(new PyInt(158), operation.Items[0]);
        var tasks = Assert.IsType<PyList>(operation.Items[2]);
        var task = Assert.IsType<PyList>(Assert.Single(tasks.Items));
        Assert.Equal(new PyInt(347), task.Items[0]);
        Assert.Equal(new PyInt(7), task.Items[2]);
    }

    [Fact]
    public void Mission_objectives_project_saved_rows_and_transport_state()
    {
        var savedRows = JsonNode.Parse("""
            {
              "missionsByAgentID": {
                "7001": {
                  "agentID": 7001,
                  "runtimeStatus": "accepted",
                  "objectiveRows": [
                    { "type": "tuple", "items": ["TravelTo", 30000142] },
                    ["AllObjectivesComplete", 7001]
                  ]
                }
              }
            }
            """)!.AsObject();
        var projected = MissionTrackerGameStoreProjection.ReadAllObjectives(savedRows, 7001, false);
        Assert.Collection(projected.Items,
            row =>
            {
                var tuple = Assert.IsType<PyTuple>(row);
                Assert.Equal(new PyStr("TravelTo"), tuple.Items[0]);
                Assert.Equal(new PyInt(30000142), tuple.Items[1]);
            },
            row =>
            {
                var list = Assert.IsType<PyList>(row);
                Assert.Equal(new PyStr("AllObjectivesComplete"), list.Items[0]);
            });

        var transport = JsonNode.Parse("""
            {
              "agentID": 7001,
              "runtimeStatus": "accepted",
              "objectiveMode": "transport",
              "cargo": { "typeID": 34, "quantity": 10 },
              "dropoffLocation": { "locationID": 600001 }
            }
            """)!.AsObject();
        var objectives = MissionTrackerGameStoreProjection.ReadAllObjectives(transport, 7001, false);
        Assert.Collection(objectives.Items,
            row => Assert.Equal(new PyStr("TransportItemsMissing"), Assert.IsType<PyList>(row).Items[0]),
            row => Assert.Equal(new PyStr("TransportItemsPresent"), Assert.IsType<PyList>(row).Items[0]),
            row => Assert.Equal(new PyStr("AllObjectivesComplete"), Assert.IsType<PyList>(row).Items[0]));
        Assert.Equal(new PyInt(600001), Assert.IsType<PyList>(objectives.Items[1]).Items[2]);

        var missionWithDungeon = JsonNode.Parse("""
            {
              "missionsByAgentID": {
                "7001": {
                  "agentID": 7001,
                  "runtimeStatus": "accepted",
                  "dungeonInstanceID": 81
                }
              }
            }
            """)!.AsObject();
        var dungeonState = JsonNode.Parse("""
            {
              "instancesByID": {
                "81": {
                  "objectiveRows": [
                    { "type": "tuple", "items": ["GoToGate", 8001] }
                  ]
                }
              }
            }
            """)!.AsObject();
        var dungeonObjectives = MissionTrackerGameStoreProjection.ReadAllObjectives(
            missionWithDungeon, 7001, true, dungeonState);
        var gate = Assert.IsType<PyTuple>(Assert.Single(dungeonObjectives.Items));
        Assert.Equal(new PyStr("GoToGate"), gate.Items[0]);
        Assert.Equal(new PyInt(8001), gate.Items[1]);
    }

    [Fact]
    public void Active_mission_dungeon_uses_saved_instance_and_ship_position()
    {
        var mission = JsonNode.Parse("""
            {
              "runtimeStatus": "accepted",
              "missionSystemID": 30000142,
              "missionPosition": { "x": 100.0, "y": 200.0, "z": 300.0 },
              "dungeonInstanceID": 81
            }
            """)!.AsObject();
        var dungeon = JsonNode.Parse("""
            {
              "instancesByID": {
                "81": {
                  "instanceID": 81,
                  "lifecycleState": "active",
                  "solarSystemID": 30000142,
                  "characterID": 90000001,
                  "shipID": 900001
                }
              }
            }
            """)!.AsObject();

        Assert.True(MissionTrackerGameStoreProjection.IsInActiveMissionDungeon(
            mission, dungeon, 90000001, 30000142, 900001, 110, 210, 310, false));
        Assert.False(MissionTrackerGameStoreProjection.IsInActiveMissionDungeon(
            mission, dungeon, 90000001, 30000143, 900001, 110, 210, 310, false));
        Assert.False(MissionTrackerGameStoreProjection.IsInActiveMissionDungeon(
            mission, dungeon, 90000001, 30000142, 900001, 700000, 200, 300, false));
        Assert.Null(MissionTrackerGameStoreProjection.IsInActiveMissionDungeon(
            mission, dungeon, 90000001, 30000142, 900001, 110, 210, 310, true));
    }

    private static Task<PyValue> Call(IService service, ClientSession session, string method, params PyValue[] args) =>
        service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session }).AsTask();

    private static PyValue Field(PyDict dict, string key) => Field(dict, new PyStr(key));

    private static PyValue Field(PyDict dict, PyValue key) =>
        dict.Entries.Single(entry => entry.Key == key).Value;
}
