using System.Text.Json.Nodes;
using Eve.Macho;
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

    [Fact]
    public async Task ReadMissionInfoItemsAsync_projects_an_accepted_mission_for_mission_tracker_rpcs()
    {
        const long characterId = 90000004;
        const long agentId = 7004;
        var state = JsonNode.Parse("""
            {
              "missionsByAgentID": {
                "7004": {
                  "runtimeStatus": "accepted",
                  "missionNameID": 123458,
                  "missionTypeLabel": "UI/Agents/StandardMission/Encounter",
                  "expiresAtFileTime": "133801234567890123",
                  "objectiveMode": "agent",
                  "contentID": "mission-4",
                  "bookmarkIDsByRole": {}
                }
              }
            }
            """)!.AsObject();
        var service = new AgentMissionService(
            (id, _) => ValueTask.FromResult<JsonObject?>(id == characterId ? state : null),
            (_, ids, _) =>
            {
                Assert.Empty(ids);
                return ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>());
            },
            (_, _) => ValueTask.FromResult<JsonArray?>(null));
        var session = new ClientSession();
        session.Set("charid", new PyInt(characterId));

        var result = Assert.IsType<PyList>(await service.ReadMissionInfoItemsAsync(
            characterId, agentId, session, CancellationToken.None));

        Assert.Equal(10, result.Items.Count);
        Assert.Equal(new PyInt(2), result.Items[0]);
        Assert.Equal(new PyStr("UI/Agents/StandardMission/Encounter"), result.Items[2]);
        Assert.Equal(new PyInt(123458), result.Items[3]);
        Assert.Equal(new PyInt(agentId), result.Items[4]);
        Assert.Equal(new PyStr("mission-4"), result.Items[9]);
        Assert.Null(await service.ReadMissionInfoItemsAsync(
            characterId, agentId + 1, session, CancellationToken.None));
    }
    [Fact]
    public async Task MissionTrackerMgrService_uses_the_existing_agent_mission_reader()
    {
        const long characterId = 90000005;
        const long agentId = 7005;
        var state = JsonNode.Parse("""
            {
              "missionsByAgentID": {
                "7005": {
                  "runtimeStatus": "accepted",
                  "missionNameID": 123459,
                  "missionTypeLabel": "UI/Agents/StandardMission/Encounter",
                  "expiresAtFileTime": "133801234567890123",
                  "objectiveMode": "agent",
                  "contentID": "mission-5",
                  "bookmarkIDsByRole": {}
                }
              }
            }
            """)!.AsObject();
        var agentMissions = new AgentMissionService(
            (id, _) => ValueTask.FromResult<JsonObject?>(id == characterId ? state : null),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>()),
            (_, _) => ValueTask.FromResult<JsonArray?>(null));
        var session = new ClientSession();
        session.Set("charid", new PyInt(characterId));
        var tracker = (IService)new MissionTrackerMgrService(agentMissions.ReadMissionInfoItemsAsync);

        var info = Assert.IsType<PyList>(await tracker.CallAsync(new ServiceCall
        {
            Method = "GetMissionInfoItems",
            Args = [new PyInt(agentId)],
            Session = session,
        }));

        Assert.Equal(new PyInt(2), info.Items[0]);
        Assert.Equal(new PyInt(agentId), info.Items[4]);
        Assert.Equal(new PyStr("mission-5"), info.Items[9]);
    }
    [Fact]
    public void Agent_mgr_advertises_the_seven_reference_rpcs()
    {
        var service = new AgentMissionService(
            (_, _) => ValueTask.FromResult<JsonObject?>(null),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>()),
            (_, _) => ValueTask.FromResult<JsonArray?>(null));
        string[] methods =
        [
            "GetAgents",
            "GetCompletedCareerAgentIDs",
            "GetDisabledMissions",
            "GetMessagesForEpicArcMissions",
            "GetMyEpicArcStatus",
            "GetPlausibleMissionIDs",
            "GetSolarSystemOfAgent",
        ];

        Assert.All(methods, method => Assert.True(service.HasMethod(method), method));
    }

    [Fact]
    public async Task Binding_an_agent_gives_a_bound_object_for_that_agent_until_it_is_released()
    {
        var agent = new AgentMissionAgentData(
            7004, 8, 24, 3, null, 1000036, false, true, 17, false, "", "",
            18, 1531, 1, 30000165, 416, 4239, 20520);
        var service = CreateAgentRpcService(rpcCallbacks: new AgentMissionRpcCallbacks(
            ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>([agent])));
        var session = new ClientSession();

        var answer = await service.CallAsync(RpcCall("MachoBindObject", session, new PyInt(7004), PyNone.Instance));

        var registration = Assert.Single(Eve.Macho.BoundObjects.Registrations(answer));
        Assert.Equal(7004, service.BoundAgentId(new ServiceCall { Method = "Bound", Args = [], BoundObjectId = registration.Oid, Session = session, CallId = new PyInt(1) }));
        Assert.Equal(0, service.BoundAgentId(new ServiceCall { Method = "Bound", Args = [], BoundObjectId = "N=1:1", Session = session, CallId = new PyInt(1) }));
        service.OnBoundObjectReleased(registration.Oid, session);
        Assert.Equal(0, service.BoundAgentId(new ServiceCall { Method = "Bound", Args = [], BoundObjectId = registration.Oid, Session = session, CallId = new PyInt(1) }));
    }

    [Fact]
    public async Task Binding_an_agent_the_catalog_does_not_hold_is_refused_and_a_nested_method_nobody_binds_is_not_answered()
    {
        var agent = new AgentMissionAgentData(
            7004, 8, 24, 3, null, 1000036, false, true, 17, false, "", "",
            18, 1531, 1, 30000165, 416, 4239, 20520);
        var service = CreateAgentRpcService(rpcCallbacks: new AgentMissionRpcCallbacks(
            ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>([agent])));
        var session = new ClientSession();

        await Assert.ThrowsAsync<MachoWrappedException>(() =>
            service.CallAsync(RpcCall("MachoBindObject", session, new PyInt(9999), PyNone.Instance)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CallAsync(RpcCall("MachoBindObject", session, new PyInt(7004),
                new PyTuple([new PyStr("DoAction"), new PyTuple([new PyInt(3)]), PyNone.Instance]))).AsTask());
    }

    [Fact]
    public async Task Agent_mgr_agents_and_location_match_the_reference_rowset_and_scalar()
    {
        var agent = new AgentMissionAgentData(
            7004, 8, 24, 3, null, 1000036, false, true, 17, false, "", "",
            18, 1531, 1, 30000165, 416, 4239, 20520);
        var service = CreateAgentRpcService(rpcCallbacks: new AgentMissionRpcCallbacks(
            ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>([agent]),
            ReadSolarSystemOfAgent: (agentId, _) =>
                ValueTask.FromResult<long?>(agentId == agent.AgentId ? agent.SolarSystemId : null)));
        var session = new ClientSession();

        var rowset = await service.CallAsync(RpcCall("GetAgents", session));
        Assert.Equal(Py.Rowset(
        [
            "agentID", "agentTypeID", "divisionID", "level", "stationID", "corporationID",
            "isLocatorAgent", "isInSpace", "careerID", "importantMission", "missionKind",
            "missionTypeLabel", "specialityID", "stationTypeID", "bloodlineID", "solarSystemID",
            "dungeonID", "spawnPointID", "typeID",
        ],
        [
            new PyTuple([
                new PyInt(7004), new PyInt(8), new PyInt(24), new PyInt(3), PyNone.Instance,
                new PyInt(1000036), PyBool.False, PyBool.True, new PyInt(17), PyBool.False,
                new PyStr(""), new PyStr(""), new PyInt(18), new PyInt(1531), new PyInt(1),
                new PyInt(30000165), new PyInt(416), new PyInt(4239), new PyInt(20520),
            ]),
        ]), rowset);
        Assert.Equal(new PyInt(30000165),
            await service.CallAsync(RpcCall("GetSolarSystemOfAgent", session, new PyInt(7004))));
    }

    [Fact]
    public async Task Agent_mgr_career_completion_and_mission_lists_keep_the_reference_shapes()
    {
        const long characterId = 90000021;
        long requestedAgentId = 0;
        var state = JsonNode.Parse("""
            {"completedCareerAgentIDs":{"7001":true,"7002":false,"7003":1}}
            """)!.AsObject();
        var service = CreateAgentRpcService(
            (id, _) => ValueTask.FromResult<JsonObject?>(id == characterId ? state : null),
            new AgentMissionRpcCallbacks(
                ReadDisabledMissions: _ => ValueTask.FromResult<IReadOnlyList<long>>([5101, 5102]),
                ReadPlausibleMissionIds: (agentId, _) =>
                {
                    requestedAgentId = agentId;
                    return ValueTask.FromResult<IReadOnlyList<long>>([6101, 6102]);
                }));
        var session = new ClientSession();
        session.Set("charid", new PyInt(characterId));

        Assert.Equal(NumericDict(
                (7001, PyBool.True),
                (7002, PyBool.False),
                (7003, PyBool.False)),
            await service.CallAsync(RpcCall(
                "GetCompletedCareerAgentIDs",
                session,
                new PyList([new PyInt(7001), new PyInt(7002), new PyInt(7003), new PyInt(7001), new PyInt(0)]))));
        Assert.Equal(new PyList([new PyInt(5101), new PyInt(5102)]),
            await service.CallAsync(RpcCall("GetDisabledMissions", session)));
        Assert.Equal(new PyList([new PyInt(6101), new PyInt(6102)]),
            await service.CallAsync(RpcCall("GetPlausibleMissionIDs", session, new PyInt(7004))));
        Assert.Equal(7004, requestedAgentId);
    }

    [Fact]
    public async Task Agent_mgr_epic_arc_status_and_message_maps_use_numeric_nested_keys()
    {
        const long characterId = 90000022;
        const long acceptedDate = 133801234567890123;
        const long completedDate = 133802345678901234;
        var state = JsonNode.Parse("""
            {
              "epicArcProgress": {
                "missionStatusByArcID": {
                  "1200": {
                    "430001": {
                      "acceptedDate": "133801234567890123",
                      "completedDate": "",
                      "quitDate": "",
                      "nameID": 123456
                    }
                  }
                },
                "completedArcsByID": {
                  "arc-1201": {
                    "epicArcID": 1201,
                    "completedMissionID": 430002,
                    "completedAtFileTime": "133802345678901234"
                  }
                }
              },
              "missionsByAgentID": {
                "7004": {
                  "runtimeStatus": "accepted",
                  "acceptedAtFileTime": "133801234567890123",
                  "missionNameID": 123457,
                  "clientMissionRecord": {
                    "isEpicArc": true,
                    "epicArcID": 1202,
                    "missionID": 430003
                  }
                }
              }
            }
            """)!.AsObject();
        var messages = JsonNode.Parse("""
            {"start":{"430001":"UI/Agent/EpicArc/Start","430002":123}}
            """)!.AsObject();
        var service = CreateAgentRpcService(
            (id, _) => ValueTask.FromResult<JsonObject?>(id == characterId ? state : null),
            new AgentMissionRpcCallbacks(
                ReadEpicArcMessages: _ => ValueTask.FromResult<JsonObject?>(messages)));
        var session = new ClientSession();
        session.Set("charid", new PyInt(characterId));

        var firstArcMissions = NumericDict((430001, Py.KeyVal(
            ("acceptedDate", Py.FileTimeLong(acceptedDate)),
            ("completedDate", PyNone.Instance),
            ("quitDate", PyNone.Instance),
            ("nameID", new PyInt(123456)))));
        var completedArcMissions = NumericDict((430002, Py.KeyVal(
            ("acceptedDate", PyNone.Instance),
            ("completedDate", Py.FileTimeLong(completedDate)),
            ("quitDate", PyNone.Instance),
            ("nameID", PyNone.Instance))));
        var activeArcMissions = NumericDict((430003, Py.KeyVal(
            ("acceptedDate", Py.FileTimeLong(acceptedDate)),
            ("completedDate", PyNone.Instance),
            ("quitDate", PyNone.Instance),
            ("nameID", new PyInt(123457)))));
        var expectedArcStatus = NumericDict(
            (1200, firstArcMissions),
            (1201, completedArcMissions),
            (1202, activeArcMissions));
        Assert.Equal(expectedArcStatus, await service.CallAsync(RpcCall("GetMyEpicArcStatus", session)));

        Assert.Equal(new PyDict([
            new KeyValuePair<PyValue, PyValue>(
                new PyStr("start"),
                NumericDict(
                    (430001, new PyStr("UI/Agent/EpicArc/Start")),
                    (430002, new PyInt(123)))
            ),
        ]), await service.CallAsync(RpcCall("GetMessagesForEpicArcMissions", session)));
    }

    private static AgentMissionService CreateAgentRpcService(
        Func<long, CancellationToken, ValueTask<JsonObject?>>? readMissionState = null,
        AgentMissionRpcCallbacks? rpcCallbacks = null) =>
        new(
            readMissionState ?? ((_, _) => ValueTask.FromResult<JsonObject?>(null)),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(new Dictionary<long, JsonObject>()),
            (_, _) => ValueTask.FromResult<JsonArray?>(null),
            rpcCallbacks: rpcCallbacks);

    private static ServiceCall RpcCall(string method, ClientSession session, params PyValue[] args) =>
        new() { Method = method, Args = args, Session = session };

    private static PyDict NumericDict(params (long Key, PyValue Value)[] entries) =>
        new(entries.Select(entry => new KeyValuePair<PyValue, PyValue>(
            new PyInt(entry.Key), entry.Value)).ToArray());

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
