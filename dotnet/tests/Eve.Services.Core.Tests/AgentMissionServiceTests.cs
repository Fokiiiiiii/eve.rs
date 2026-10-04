using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class AgentMissionServiceTests
{
    [Fact]
    public async Task GetMyJournalDetails_projects_saved_missions_bookmarks_and_research_to_the_wire_shape()
    {
        const long characterId = 90000001;
        const long firstAgentId = 7001;
        const long secondAgentId = 7002;
        const long bookmarkId = 99001;
        const long missionFileTime = 133801234567890123;
        var state = JsonNode.Parse("""
            {
              "missionsByAgentID": {
                "7002": {
                  "agentID": 7002,
                  "runtimeStatus": "accepted",
                  "missionNameID": 123456,
                  "missionTypeLabel": "UI/Agents/StandardMission/Encounter",
                  "importantMission": false,
                  "expiresAtFileTime": "133801234567890123",
                  "objectiveMode": "dungeon",
                  "contentID": "123456",
                  "bookmarkIDsByRole": {},
                  "remoteCompletable": false,
                  "clientMissionRecord": { "remoteOfferable": true }
                },
                "7001": {
                  "agentID": 7001,
                  "runtimeStatus": "offered",
                  "missionTitle": "Courier Assignment",
                  "missionTypeLabel": "UI/Agents/StandardMission/Courier",
                  "importantMission": true,
                  "expiresAtFileTime": "133801234567890123",
                  "objectiveMode": "transport",
                  "contentID": "courier-12",
                  "bookmarkIDsByRole": { "destination": 99001 },
                  "remoteCompletable": true,
                  "clientMissionRecord": { "remoteOfferable": false }
                }
              }
            }
            """)!.AsObject();
        var originalState = state.ToJsonString();
        var bookmarks = new Dictionary<long, JsonObject>
        {
            [bookmarkId] = JsonNode.Parse("""
                {
                  "itemID": 600001,
                  "typeID": 5,
                  "locationID": 30000142,
                  "creatorID": 7001,
                  "created": "133801234500000000",
                  "x": 1.25,
                  "y": 2.5,
                  "z": 3.75,
                  "metadata": {
                    "agentID": 7001,
                    "hint": "Mission destination",
                    "locationType": "destination",
                    "role": "destination",
                    "solarsystemID": 30000142
                  }
                }
                """)!.AsObject(),
        };
        IReadOnlyList<long>? requestedBookmarkIds = null;
        long? missionStateCharacter = null;
        long? researchCharacter = null;
        var service = new AgentMissionService(
            (id, _) =>
            {
                missionStateCharacter = id;
                return ValueTask.FromResult<JsonObject?>(id == characterId ? state : null);
            },
            (id, ids, _) =>
            {
                Assert.Equal(characterId, id);
                requestedBookmarkIds = ids;
                return ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(bookmarks);
            },
            (id, _) =>
            {
                researchCharacter = id;
                return ValueTask.FromResult<JsonArray?>(JsonNode.Parse("""
                    [[8001, 3339, 24.5, 3.25, 5, 0, 600001]]
                    """)!.AsArray());
            });

        var result = await service.CallAsync(Call(characterId));

        var journal = Assert.IsType<PyTuple>(result);
        Assert.Equal(2, journal.Items.Count);
        Assert.Equal(characterId, missionStateCharacter);
        Assert.Equal(characterId, researchCharacter);
        Assert.Equal([bookmarkId], requestedBookmarkIds);

        var missions = Assert.IsType<PyList>(journal.Items[0]);
        Assert.Equal(2, missions.Items.Count);
        var offered = Assert.IsType<PyTuple>(missions.Items[0]);
        Assert.Equal(10, offered.Items.Count);
        Assert.Equal(new PyInt(1), offered.Items[0]);
        Assert.Equal(new PyInt(1), offered.Items[1]);
        Assert.Equal(new PyStr("UI/Agents/StandardMission/Courier"), offered.Items[2]);
        Assert.Equal(new PyStr("Courier Assignment"), offered.Items[3]);
        Assert.Equal(new PyInt(firstAgentId), offered.Items[4]);
        Assert.Equal(new PyLong(missionFileTime), offered.Items[5]);
        Assert.Equal(new PyInt(0), offered.Items[7]);
        Assert.Equal(new PyInt(1), offered.Items[8]);
        Assert.Equal(new PyStr("courier-12"), offered.Items[9]);

        var missionBookmarks = Assert.IsType<PyList>(offered.Items[6]);
        var bookmark = Assert.IsType<PyObject>(Assert.Single(missionBookmarks.Items));
        Assert.Equal(new PyStr("util.KeyVal"), bookmark.Name);
        Assert.Equal(new PyInt(600001), Field(bookmark, "itemID"));
        Assert.Equal(new PyInt(7001), Field(bookmark, "agentID"));
        Assert.Equal(new PyStr("Mission destination"), Field(bookmark, "hint"));
        Assert.Equal(new PyLong(133801234500000000), Field(bookmark, "created"));
        Assert.Equal(new PyReal(1.25), Field(bookmark, "x"));

        var accepted = Assert.IsType<PyTuple>(missions.Items[1]);
        Assert.Equal(new PyInt(2), accepted.Items[0]);
        Assert.Equal(new PyInt(0), accepted.Items[1]);
        Assert.Equal(new PyInt(secondAgentId), accepted.Items[4]);
        Assert.Equal(new PyInt(123456), accepted.Items[9]);
        Assert.Empty(Assert.IsType<PyList>(accepted.Items[6]).Items);

        var research = Assert.IsType<PyList>(journal.Items[1]);
        var researchProject = Assert.IsType<PyTuple>(Assert.Single(research.Items));
        Assert.Equal(7, researchProject.Items.Count);
        Assert.Equal(new PyInt(8001), researchProject.Items[0]);
        Assert.Equal(new PyInt(3339), researchProject.Items[1]);
        Assert.Equal(new PyReal(24.5), researchProject.Items[2]);
        Assert.Equal(new PyReal(3.25), researchProject.Items[3]);
        Assert.Equal(new PyInt(5), researchProject.Items[4]);
        Assert.Equal(new PyInt(0), researchProject.Items[5]);
        Assert.Equal(new PyInt(600001), researchProject.Items[6]);
        Assert.Equal(originalState, state.ToJsonString());
    }

    [Fact]
    public async Task GetMyJournalDetails_returns_two_empty_lists_for_an_unselected_character_without_reading()
    {
        var reads = 0;
        var service = new AgentMissionService(
            (_, _) => { reads++; return ValueTask.FromResult<JsonObject?>(null); },
            (_, _, _) => { reads++; return ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>()); },
            (_, _) => { reads++; return ValueTask.FromResult<JsonArray?>(null); });

        var result = await service.CallAsync(Call(0));

        var journal = Assert.IsType<PyTuple>(result);
        Assert.Equal(2, journal.Items.Count);
        Assert.Empty(Assert.IsType<PyList>(journal.Items[0]).Items);
        Assert.Empty(Assert.IsType<PyList>(journal.Items[1]).Items);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task GetMyJournalDetails_keeps_research_rows_when_the_character_has_no_mission_state()
    {
        const long characterId = 90000002;
        var service = new AgentMissionService(
            (_, _) => ValueTask.FromResult<JsonObject?>(null),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>()),
            (id, _) =>
            {
                Assert.Equal(characterId, id);
                return ValueTask.FromResult<JsonArray?>(JsonNode.Parse("[[8002, 3340, 10, 0, 3, 0, null]]")!.AsArray());
            });
        var session = new ClientSession();
        session.Set("charid", new PyInt(characterId));

        var result = Assert.IsType<PyTuple>(await service.CallAsync(new ServiceCall
        {
            Method = "GetMyJournalDetails", Args = [], Session = session,
        }));

        Assert.Empty(Assert.IsType<PyList>(result.Items[0]).Items);
        var research = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(result.Items[1]).Items));
        Assert.Equal(PyNone.Instance, research.Items[6]);
    }

    [Fact]
    public async Task GetMyJournalDetails_disables_remote_completion_when_the_character_is_at_the_agent()
    {
        const long characterId = 90000003;
        const long agentId = 7003;
        var state = JsonNode.Parse("""
            {
              "missionsByAgentID": {
                "7003": {
                  "agentID": 7003,
                  "runtimeStatus": "accepted",
                  "missionNameID": 123457,
                  "missionTypeLabel": "UI/Agents/StandardMission/Encounter",
                  "expiresAtFileTime": "133801234567890123",
                  "contentID": 123457,
                  "bookmarkIDsByRole": {},
                  "remoteCompletable": true
                }
              }
            }
            """)!.AsObject();
        var service = new AgentMissionService(
            (_, _) => ValueTask.FromResult<JsonObject?>(state),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>()),
            (_, _) => ValueTask.FromResult<JsonArray?>(null),
            (character, agent, session) =>
                character == characterId && agent == agentId &&
                session.Get("stationid") is PyInt station && station.Value == 60001234);

        var result = Assert.IsType<PyTuple>(await service.CallAsync(Call(characterId, stationId: 60001234)));
        var mission = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(result.Items[0]).Items));

        Assert.Equal(new PyInt(0), mission.Items[8]);
    }

    private static ServiceCall Call(long characterId, long stationId = 0)
    {
        var session = new ClientSession();
        if (characterId > 0) session.Set("charid", new PyInt(characterId));
        if (stationId > 0) session.Set("stationid", new PyInt(stationId));
        return new ServiceCall { Method = "GetMyJournalDetails", Args = [], Session = session };
    }

    private static PyValue Field(PyObject value, string key) =>
        Assert.Single(Assert.IsType<PyDict>(value.Args).Entries, pair => pair.Key == new PyStr(key)).Value;
}
