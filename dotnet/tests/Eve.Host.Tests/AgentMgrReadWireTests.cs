using System.Text.Json.Nodes;
using Eve.Agents;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private static PyValue Get(PyDict dictionary, PyValue key) =>
        dictionary.Entries.Single(entry =>
            entry.Key == key ||
            TextKey(entry.Key) is { } actual && actual == TextKey(key)).Value;

    private static string? TextKey(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
        _ => null,
    };

    private const long AgentRpcPilot = 90_000_041;
    private const long AgentRpcFirstAgent = 7_004;
    private const long AgentRpcSecondAgent = 7_005;
    private const long AgentRpcBookmark = 99_001;

    [Fact]
    public async Task Agent_mgr_read_rpcs_are_registered_on_the_host_and_return_client_consumed_shapes()
    {
        const long acceptedAt = 133801234567890123;
        var state = JsonNode.Parse("""
            {
              "completedCareerAgentIDs": { "7004": true, "7005": false },
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
                "completedArcsByID": {}
              },
              "missionsByAgentID": {
                "7004": {
                  "agentID": 7004,
                  "runtimeStatus": "accepted",
                  "missionTitle": "Courier Assignment",
                  "missionTypeLabel": "UI/Agents/StandardMission/Courier",
                  "importantMission": true,
                  "expiresAtFileTime": "133801234567890123",
                  "objectiveMode": "transport",
                  "contentID": "courier-12",
                  "bookmarkIDsByRole": { "destination": 99001 },
                  "remoteCompletable": false,
                  "clientMissionRecord": { "remoteOfferable": true }
                },
                "7005": {
                  "agentID": 7005,
                  "runtimeStatus": "accepted",
                  "missionTitle": "Encounter Assignment",
                  "missionTypeLabel": "UI/Agents/StandardMission/Encounter",
                  "importantMission": false,
                  "expiresAtFileTime": "133801234567890123",
                  "objectiveMode": "dungeon",
                  "contentID": "123456",
                  "bookmarkIDsByRole": {},
                  "remoteCompletable": false,
                  "clientMissionRecord": { "remoteOfferable": true }
                }
              }
            }
            """)!.AsObject();
        var agent = new AgentMissionAgentData(
            AgentRpcFirstAgent, 8, 24, 3, 60_000_001, 1_000_036, false, false, 17, false,
            "courier", "courier", 18, 1_531, 1, 30_000_165, null, null, null);
        var bookmark = JsonNode.Parse("""
            {
              "itemID": 600001,
              "typeID": 5,
              "locationID": 30000142,
              "creatorID": 7004,
              "created": "133801234500000000",
              "x": 1.25,
              "y": 2.5,
              "z": 3.75,
              "metadata": {
                "agentID": 7004,
                "hint": "Mission destination",
                "locationType": "destination",
                "role": "destination",
                "solarsystemID": 30000142
              }
            }
            """)!.AsObject();
        var missionMessages = JsonNode.Parse("""
            { "start": { "430001": "UI/Agent/EpicArc/Start" } }
            """)!.AsObject();
        var missionContentId = 93031L;
        var missionTime = DateTimeOffset.FromFileTime(acceptedAt);
        var heldMission = new HeldMissionView(
            new MissionStatic(
                missionContentId, "Courier Assignment", true, 60, null, null, null, null,
                new MissionStaticCourier(34, 3, false),
                new MissionStaticReward(29, 1500, null, 0, null),
                null, 0, [], new Dictionary<string, string>(), 123456),
            new AgentMissionRecord(
                AgentRpcPilot, AgentRpcFirstAgent, AgentMissionState.Accepted,
                new AgentMissionContent(missionContentId, 123456, "Courier Assignment", "courier", true),
                missionTime, missionTime, missionTime, null, null, null),
            987654,
            Courier: new CourierObjectiveView(34, 3, 0.1, true, 60_000_001, 60_000_002, 1_000_036));
        var service = new AgentMissionService(
            (characterId, _) => ValueTask.FromResult<JsonObject?>(
                characterId == AgentRpcPilot ? state : null),
            (characterId, bookmarkIds, _) =>
            {
                Assert.Equal(AgentRpcPilot, characterId);
                Assert.Equal([AgentRpcBookmark], bookmarkIds);
                return ValueTask.FromResult<IReadOnlyDictionary<long, JsonObject>>(
                    new Dictionary<long, JsonObject> { [AgentRpcBookmark] = bookmark });
            },
            (characterId, _) => ValueTask.FromResult<JsonArray?>(
                characterId == AgentRpcPilot
                    ? JsonNode.Parse("[[7004,3339,24.5,3.25,5,0,600001],[7005,3340,10,0,3,0,null]]")!.AsArray()
                    : null),
            rpcCallbacks: new AgentMissionRpcCallbacks(
                ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>(
                    [agent, agent with { AgentId = AgentRpcSecondAgent, MissionKind = "encounter" }]),
                ReadDisabledMissions: _ => ValueTask.FromResult<IReadOnlyList<long>>([5101, 5102]),
                ReadEpicArcMessages: _ => ValueTask.FromResult<JsonObject?>(missionMessages),
                ReadHeldMission: (characterId, agentId, _) => ValueTask.FromResult<HeldMissionView?>(
                    characterId == AgentRpcPilot && agentId == AgentRpcFirstAgent ? heldMission : null),
                ReadSolarSystemOfAgent: (agentId, _) => ValueTask.FromResult<long?>(
                    agentId == AgentRpcFirstAgent ? 30_000_165 : null)));

        var machoVersion = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        Assert.False(service.HasMethod("Index"));
        Assert.False(service.HasMethod("get"));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyInt(AgentRpcPilot)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> CallAsync(long callId, string method, PyValue args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "agentMgr", method, args, machoVersion))));
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(send.Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            Assert.Equal(MachoMessage.CallRsp, packet.Type);
            return Result(decoded)!;
        }

        async Task<PyValue> BoundCallAsync(long callId, string oid, string method, PyTuple args, PyDict? kwargs = null)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.BoundCall(open, callId, oid, method, args, kwargs ?? machoVersion))));
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(send.Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            Assert.Equal(MachoMessage.CallRsp, packet.Type);
            return Result(decoded)!;
        }

        var agents = await CallAsync(1, "GetAgents", new PyTuple([]));
        var expectedAgents = MarshalCodec.Decode(MarshalCodec.Encode(Py.Rowset(
            [
                "agentID", "agentTypeID", "divisionID", "level", "stationID", "corporationID",
                "isLocatorAgent", "isInSpace", "careerID", "importantMission", "missionKind",
                "missionTypeLabel", "specialityID", "stationTypeID", "bloodlineID",
            ],
            [
                new PyList([
                    new PyInt(AgentRpcFirstAgent), new PyInt(8), new PyInt(24), new PyInt(3),
                    new PyInt(60_000_001), new PyInt(1_000_036), PyBool.False, PyBool.False,
                    new PyInt(17), PyBool.False, new PyStr("courier"), new PyStr("courier"),
                    new PyInt(18), new PyInt(1_531), new PyInt(1),
                ]),
                new PyList([
                    new PyInt(AgentRpcSecondAgent), new PyInt(8), new PyInt(24), new PyInt(3),
                    new PyInt(60_000_001), new PyInt(1_000_036), PyBool.False, PyBool.False,
                    new PyInt(17), PyBool.False, new PyStr("encounter"), new PyStr("courier"),
                    new PyInt(18), new PyInt(1_531), new PyInt(1),
                ]),
            ])));
        Assert.Equal(expectedAgents, agents);

        var completed = Assert.IsType<PyDict>(await CallAsync(
            2, "GetCompletedCareerAgentIDs",
            new PyTuple([new PyList([new PyInt(AgentRpcFirstAgent), new PyInt(AgentRpcSecondAgent)])])));
        Assert.Equal(PyBool.True, Get(completed, new PyInt(AgentRpcFirstAgent)));
        Assert.Equal(PyBool.False, Get(completed, new PyInt(AgentRpcSecondAgent)));
        Assert.Equal(new PyList([new PyInt(5101), new PyInt(5102)]),
            await CallAsync(3, "GetDisabledMissions", new PyTuple([])));
        var messages = Assert.IsType<PyDict>(
            await CallAsync(4, "GetMessagesForEpicArcMissions", new PyTuple([])));
        var starts = Assert.IsType<PyDict>(Get(messages, new PyBytes("start"u8.ToArray())));
        Assert.Equal("UI/Agent/EpicArc/Start", T(Get(starts, new PyInt(430001))));

        var arcStatus = Assert.IsType<PyDict>(
            await CallAsync(5, "GetMyEpicArcStatus", new PyTuple([])));
        var arcMissions = Assert.IsType<PyDict>(Get(arcStatus, new PyInt(1200)));
        var arcMission = Assert.IsType<PyObject>(Get(arcMissions, new PyInt(430001)));
        var arcMissionFields = Assert.IsType<PyDict>(arcMission.Args);
        Assert.Equal(Py.FileTimeLong(acceptedAt), Get(arcMissionFields, new PyBytes("acceptedDate"u8.ToArray())));
        Assert.Equal(PyNone.Instance, Get(arcMissionFields, new PyBytes("completedDate"u8.ToArray())));
        Assert.Equal(new PyInt(123456), Get(arcMissionFields, new PyBytes("nameID"u8.ToArray())));

        var journal = Assert.IsType<PyTuple>(
            await CallAsync(6, "GetMyJournalDetails", new PyTuple([])));
        var allMissions = Assert.IsType<PyList>(journal.Items[0]);
        Assert.Equal(2, allMissions.Items.Count);
        var firstMission = Assert.IsType<PyTuple>(allMissions.Items[0]);
        var missionBookmarks = Assert.IsType<PyList>(firstMission.Items[6]);
        var returnedBookmark = Assert.IsType<PyObject>(Assert.Single(missionBookmarks.Items));
        Assert.Equal(new PyInt(600001), Get(Assert.IsType<PyDict>(returnedBookmark.Args), new PyBytes("itemID"u8.ToArray())));
        var research = Assert.IsType<PyList>(journal.Items[1]);
        Assert.Equal(2, research.Items.Count);
        Assert.Equal(new PyInt(AgentRpcFirstAgent), Assert.IsType<PyTuple>(research.Items[0]).Items[0]);

        var binding = Assert.IsType<PyTuple>(await CallAsync(
            7, "MachoBindObject", new PyTuple([new PyTuple([new PyLong(AgentRpcFirstAgent)])])));
        var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;
        var boundJournal = Assert.IsType<PyTuple>(
            await BoundCallAsync(8, oid, "GetMyJournalDetails", new PyTuple([])));
        var boundMissions = Assert.IsType<PyList>(boundJournal.Items[0]);
        var onlyAgentMission = Assert.IsType<PyTuple>(Assert.Single(boundMissions.Items));
        Assert.Equal(new PyInt(AgentRpcFirstAgent), onlyAgentMission.Items[4]);
        var boundResearch = Assert.IsType<PyList>(boundJournal.Items[1]);
        Assert.Equal(new PyInt(AgentRpcFirstAgent), Assert.IsType<PyTuple>(Assert.Single(boundResearch.Items)).Items[0]);

        Assert.Equal(new PyInt(30_000_165),
            await CallAsync(9, "GetSolarSystemOfAgent", new PyTuple([new PyInt(AgentRpcFirstAgent)])));

        var missionInfo = Assert.IsType<PyDict>(
            await BoundCallAsync(10, oid, "GetMissionJournalInfo", new PyTuple([])));
        Assert.Equal(new PyInt(missionContentId), Get(missionInfo, new PyBytes("contentID"u8.ToArray())));
        Assert.Equal(new PyInt(123456), Get(missionInfo, new PyBytes("missionNameID"u8.ToArray())));
        Assert.Equal(new PyInt((int)AgentMissionState.Accepted), Get(missionInfo, new PyBytes("missionState"u8.ToArray())));
        Assert.IsType<PyList>(Get(missionInfo, new PyBytes("bookmarks"u8.ToArray())));
        var objectives = Assert.IsType<PyDict>(Get(missionInfo, new PyBytes("objectives"u8.ToArray())));
        Assert.Equal(new PyInt(missionContentId), Get(objectives, new PyBytes("contentID"u8.ToArray())));
        var objective = Assert.IsType<PyTuple>(
            Assert.Single(Assert.IsType<PyList>(Get(objectives, new PyBytes("objectives"u8.ToArray()))).Items));
        Assert.Equal(new PyBytes("transport"u8.ToArray()), objective.Items[0]);
        var transport = Assert.IsType<PyTuple>(objective.Items[1]);
        Assert.Equal(PyBool.True,
            Get(Assert.IsType<PyDict>(transport.Items[4]), new PyBytes("hasCargo"u8.ToArray())));

        var objectiveInfo = Assert.IsType<PyDict>(
            await BoundCallAsync(15, oid, "GetMissionObjectiveInfo", new PyTuple([])));
        Assert.Equal(new PyInt(missionContentId), Get(objectiveInfo, new PyBytes("contentID"u8.ToArray())));
        Assert.IsType<PyList>(Get(objectiveInfo, new PyBytes("objectives"u8.ToArray())));
        var objectiveKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("charID"), new PyLong(AgentRpcPilot)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("ignoreLocateCheck"), PyBool.True),
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        Assert.Equal(objectiveInfo, await BoundCallAsync(16, oid, "GetMissionObjectiveInfo",
            new PyTuple([]), objectiveKwargs));

        Assert.Equal(missionInfo, await BoundCallAsync(11, oid, "GetMissionJournalInfo",
            new PyTuple([PyNone.Instance, PyNone.Instance])));
        Assert.Equal(missionInfo, await BoundCallAsync(12, oid, "GetMissionJournalInfo",
            new PyTuple([new PyInt(AgentRpcPilot), new PyLong(missionContentId)])));

        var noMissionBinding = Assert.IsType<PyTuple>(await CallAsync(
            13, "MachoBindObject", new PyTuple([new PyTuple([new PyInt(AgentRpcSecondAgent)])])));
        var noMissionOid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(noMissionBinding.Items[0]))!;
        Assert.Equal(PyNone.Instance,
            await BoundCallAsync(14, noMissionOid, "GetMissionJournalInfo", new PyTuple([])));
    }

    [Fact]
    public void Courier_mission_hasCargo_counts_only_active_ship_cargo_or_dropoff_hangar()
    {
        const int typeId = 34;
        const long required = 3;
        const long ownerId = 90_000_041;
        const long activeShipId = 500_001;
        const long otherShipId = 500_002;
        const long pickupStationId = 60_000_001;
        const long dropoffStationId = 60_000_002;

        Item Stack(long id, long locationId, int flag, long quantity, int stackTypeId = typeId) =>
            new(id, stackTypeId, ownerId, locationId, flag, quantity, false, null);

        Assert.False(Eve.Host.CharacterServicesComposition.HasCourierCargo(
            [
                Stack(1, pickupStationId, ItemFlags.Hangar, required),
                Stack(2, otherShipId, ItemFlags.Cargo, required),
            ],
            typeId,
            required,
            activeShipId,
            dropoffStationId));

        Assert.False(Eve.Host.CharacterServicesComposition.HasCourierCargo(
            [Stack(3, activeShipId, ItemFlags.Cargo, required - 1)],
            typeId,
            required,
            activeShipId,
            dropoffStationId));

        Assert.True(Eve.Host.CharacterServicesComposition.HasCourierCargo(
            [
                Stack(4, activeShipId, ItemFlags.Cargo, 1),
                Stack(5, activeShipId, ItemFlags.Cargo, required - 1),
            ],
            typeId,
            required,
            activeShipId,
            dropoffStationId));

        Assert.True(Eve.Host.CharacterServicesComposition.HasCourierCargo(
            [Stack(6, dropoffStationId, ItemFlags.Hangar, required)],
            typeId,
            required,
            activeShipId,
            dropoffStationId));

        Assert.False(Eve.Host.CharacterServicesComposition.HasCourierCargo(
            [Stack(7, activeShipId, ItemFlags.Cargo, required, typeId + 1)],
            typeId,
            required,
            activeShipId,
            dropoffStationId));
    }
}
