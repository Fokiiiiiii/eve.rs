using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class LoginInitializationRpcServicesTests
{
    [Fact]
    public async Task Login_services_register_the_reference_routes_and_return_golden_shapes()
    {
        var achievement = new AchievementTrackerMgrService();
        var operations = new OperationsManagerService();
        var missions = new MissionTrackerMgrService();
        var abyssal = new AbyssalMgrService((_, _) => ValueTask.FromResult<PyValue>(PyNone.Instance));
        var voidSpace = new VoidSpaceMgrService();
        var airNpe = new AirNpeService(
            () => "disabled",
            (_, _) => Task.FromResult<AirNpeCharacterState?>(null),
            (_, _, _) => Task.FromResult<int?>(null));

        Assert.Equal("achievementTrackerMgr", achievement.Name);
        Assert.True(achievement.HasMethod("GetCompletedAchievementsAndClientEventCount"));
        Assert.Equal("operationsManager", operations.Name);
        Assert.True(operations.HasMethod("get_character_progress"));
        Assert.True(operations.HasMethod("get_active_category_id"));
        Assert.True(operations.HasMethod("get_operations_completed_at_least_once"));
        Assert.True(operations.HasMethod("start_site"));
        Assert.True(operations.HasMethod("unblock_task"));
        Assert.Equal("missionTrackerMgr", missions.Name);
        Assert.True(missions.HasMethod("UpdateAllMissions"));
        Assert.True(missions.HasMethod("GetMissionInfoItems"));
        Assert.Equal("abyssalMgr", abyssal.Name);
        Assert.True(abyssal.HasMethod("ClientIsReady"));
        Assert.Equal("voidSpaceMgr", voidSpace.Name);
        Assert.True(voidSpace.HasMethod("ClientIsReady"));
        Assert.Equal("air_npe", airNpe.Name);
        Assert.True(airNpe.HasMethod("is_air_npe_enabled"));
        Assert.True(airNpe.HasMethod("get_air_npe_state"));
        Assert.True(airNpe.HasMethod("skip_air_npe"));

        var achievementState = Assert.IsType<PyDict>(await achievement.CallAsync(Call(
            "GetCompletedAchievementsAndClientEventCount", new ClientSession())));
        // a character with no tracker record has empty maps (no fixture): the client starts from its own defaults
        Assert.Empty(Assert.IsType<PyDict>(Field(achievementState, "eventDict")).Entries);
        Assert.Empty(Assert.IsType<PyDict>(Field(achievementState, "completedDict")).Entries);

        var progress = Assert.IsType<PyDict>(await operations.CallAsync(Call("get_character_progress", new ClientSession())));
        var category = Assert.IsType<PyList>(Field(progress, new PyInt(32)));
        var operation = Assert.IsType<PyList>(Assert.Single(category.Items));
        Assert.Equal(new PyInt(158), operation.Items[0]);
        Assert.Equal(new PyInt(0), operation.Items[1]);
        var taskStates = Assert.IsType<PyList>(operation.Items[2]);
        Assert.Collection(taskStates.Items,
            task => AssertTask(task, 347),
            task => AssertTask(task, 348));
        Assert.Same(PyNone.Instance, await operations.CallAsync(Call("get_active_category_id", new ClientSession())));
        Assert.Equal(new PyList([]), await operations.CallAsync(Call("get_operations_completed_at_least_once", new ClientSession())));
        Assert.Same(PyNone.Instance, await operations.CallAsync(Call("start_site", new ClientSession())));
        Assert.Same(PyNone.Instance, await operations.CallAsync(Call("unblock_task", new ClientSession())));
        Assert.Same(PyNone.Instance, await missions.CallAsync(Call("GetMissionInfoItems", new ClientSession(), new PyInt(7001))));
        Assert.Same(PyNone.Instance, await abyssal.CallAsync(Call("ClientIsReady", new ClientSession())));
        Assert.Same(PyNone.Instance, await voidSpace.CallAsync(Call("ClientIsReady", new ClientSession())));
        Assert.Equal(PyBool.False, await airNpe.CallAsync(Call("is_air_npe_enabled", new ClientSession())));
        Assert.Equal(new PyInt(2), await airNpe.CallAsync(Call("get_air_npe_state", new ClientSession())));
        Assert.Same(PyNone.Instance, await airNpe.CallAsync(Call("skip_air_npe", new ClientSession())));
    }

    [Fact]
    public async Task Air_npe_uses_character_state_and_notifies_only_for_a_persisted_skip_transition()
    {
        var sink = new PacketSink();
        var session = new ClientSession(sink) { ClientId = 7, UserId = 11, CharacterId = 42 };
        var skipCalls = 0;
        var service = new AirNpeService(
            () => "tutorial",
            (characterId, _) =>
            {
                Assert.Equal(42, characterId);
                return Task.FromResult<AirNpeCharacterState?>(new AirNpeCharacterState("tutorial", 1));
            },
            (characterId, tutorialEnabled, _) =>
            {
                Assert.Equal(42, characterId);
                Assert.True(tutorialEnabled);
                skipCalls++;
                return Task.FromResult<int?>(skipCalls == 1 ? 3 : null);
            });

        Assert.Equal(PyBool.True, await service.CallAsync(Call("is_air_npe_enabled", session)));
        Assert.Equal(new PyInt(1), await service.CallAsync(Call("get_air_npe_state", session)));
        Assert.Same(PyNone.Instance, await service.CallAsync(Call("skip_air_npe", session)));
        Assert.Same(PyNone.Instance, await service.CallAsync(Call("skip_air_npe", session)));
        Assert.Equal(2, skipCalls);

        var packet = Assert.IsType<PyObject>(Assert.Single(sink.Packets));
        Assert.Equal(new PyStr("carbon.common.script.net.machoNetPacket.Notification"), packet.Name);
        var packetItems = Assert.IsType<PyTuple>(packet.Args).Items;
        var envelope = Assert.IsType<PyTuple>(packetItems[4]);
        var notificationBody = Assert.IsType<PyTuple>(Assert.Single(envelope.Items));
        var notification = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(
            Assert.IsType<PyBuffer>(notificationBody.Items[1]).Value));
        var invocation = Assert.IsType<PyTuple>(notification.Items[1]);
        var args = Assert.IsType<PyTuple>(invocation.Items[1]);
        Assert.Equal(new PyInt(42), args.Items[0]);
        Assert.Equal(new PyInt(3), args.Items[1]);
    }

    [Fact]
    public async Task Air_npe_cinematic_mode_is_enabled_and_reports_completed_state()
    {
        var service = new AirNpeService(
            () => "cinematic",
            (_, _) => Task.FromResult<AirNpeCharacterState?>(
                new AirNpeCharacterState("cinematic_intro_overlay", 2)),
            (_, _, _) => Task.FromResult<int?>(null));
        var session = new ClientSession { CharacterId = 42 };

        Assert.Equal(PyBool.True, await service.CallAsync(Call("is_air_npe_enabled", session)));
        Assert.Equal(new PyInt(2), await service.CallAsync(Call("get_air_npe_state", session)));
    }

    [Fact]
    public async Task Air_npe_rpcs_refuse_client_selected_characters_and_keywords()
    {
        var reads = 0;
        var writes = 0;
        var service = new AirNpeService(
            () => "tutorial",
            (_, _) =>
            {
                reads++;
                return Task.FromResult<AirNpeCharacterState?>(new AirNpeCharacterState("tutorial", 1));
            },
            (_, _, _) =>
            {
                writes++;
                return Task.FromResult<int?>(3);
            });
        var session = new ClientSession { CharacterId = 42 };
        var calls = new[]
        {
            Call("get_air_npe_state", session, new PyLong(9001)),
            Call("skip_air_npe", session, new PyLong(9001)),
            new ServiceCall { Method = "is_air_npe_enabled", Args = [], Kwargs = new PyDict([]), Session = session },
        };

        foreach (var call in calls)
        {
            var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Equal(MachoErrors.UserErrorClass, refusal.Error.ClassName);
            Assert.Equal("AIR_NPE_ARGUMENTS_INVALID", refusal.Error.UserErrorMessage);
        }

        Assert.Equal(0, reads);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Operations_tutorial_methods_reflect_shared_air_npe_state()
    {
        var mode = "tutorial";
        AirNpeCharacterState state = new("tutorial", 1);
        var service = new OperationsManagerService(
            newCharacterExperienceMode: () => mode,
            readTutorialState: (characterId, _) =>
            {
                Assert.Equal(42, characterId);
                return Task.FromResult<AirNpeCharacterState?>(state);
            });
        var session = new ClientSession { CharacterId = 42 };

        Assert.Equal(PyBool.True, await service.CallAsync(Call("can_character_play_the_tutorial", session)));
        Assert.Equal(new PyInt(21), await service.CallAsync(Call("get_tutorial_state", session)));
        Assert.Equal(PyBool.False, await service.CallAsync(Call("is_main_tutorial_finished", session)));
        Assert.Equal(PyBool.False, await service.CallAsync(Call("has_skipped_tutorial", session)));

        state = new AirNpeCharacterState("tutorial", 3);
        Assert.Equal(PyBool.False, await service.CallAsync(Call("can_character_play_the_tutorial", session)));
        Assert.Equal(new PyInt(23), await service.CallAsync(Call("get_tutorial_state", session)));
        Assert.Equal(PyBool.True, await service.CallAsync(Call("is_main_tutorial_finished", session)));
        Assert.Equal(PyBool.True, await service.CallAsync(Call("has_skipped_tutorial", session)));

        state = new AirNpeCharacterState("disabled", 2);
        Assert.Equal(new PyInt(22), await service.CallAsync(Call("get_tutorial_state", session)));
        Assert.Equal(PyBool.False, await service.CallAsync(Call("has_skipped_tutorial", session)));
    }

    [Fact]
    public async Task Existing_state_callbacks_feed_the_registered_wire_shapes()
    {
        var session = new ClientSession { CharacterId = 42 };
        long? achievementCharacter = null;
        var eventCounts = new PyDict([Entry(new PyStr("client_openMap"), new PyInt(3))]);
        var completed = new PyDict([Entry(new PyInt(9), new PyLong(134273341290000000))]);
        var achievement = new AchievementTrackerMgrService((id, _) =>
        {
            achievementCharacter = id;
            return ValueTask.FromResult<AchievementTrackerPersistedState?>(new(eventCounts, completed));
        });

        var achievementResult = Assert.IsType<PyDict>(await achievement.CallAsync(
            Call("GetCompletedAchievementsAndClientEventCount", session)));
        Assert.Equal(42, achievementCharacter);
        Assert.Equal(eventCounts, Field(achievementResult, "eventDict"));
        Assert.Equal(completed, Field(achievementResult, "completedDict"));

        var operationCalls = new List<string>();
        var operations = new OperationsManagerService(
            (_, _) => ValueTask.FromResult<PyDict?>(new PyDict([Entry(new PyInt(32), new PyList([]))])),
            (_, _) => ValueTask.FromResult<PyValue>(new PyInt(32)),
            (_, _) => ValueTask.FromResult(new PyList([new PyInt(158)])),
            _ => { operationCalls.Add("start_site"); return ValueTask.CompletedTask; },
            _ => { operationCalls.Add("unblock_task"); return ValueTask.CompletedTask; });

        Assert.Equal(new PyDict([Entry(new PyInt(32), new PyList([]))]),
            await operations.CallAsync(Call("get_character_progress", session)));
        Assert.Equal(new PyInt(32), await operations.CallAsync(Call("get_active_category_id", session)));
        Assert.Equal(new PyList([new PyInt(158)]),
            await operations.CallAsync(Call("get_operations_completed_at_least_once", session, new PyInt(32))));
        Assert.Same(PyNone.Instance, await operations.CallAsync(Call("start_site", session, new PyInt(32), new PyInt(158))));
        Assert.Same(PyNone.Instance, await operations.CallAsync(Call("unblock_task", session, new PyInt(32), new PyInt(158), new PyInt(347))));
        Assert.Equal(["start_site", "unblock_task"], operationCalls);

        var readyCalls = 0;
        var abyssal = new AbyssalMgrService((call, _) =>
        {
            Assert.Equal("ClientIsReady", call.Method);
            readyCalls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        });
        var voidSpace = new VoidSpaceMgrService(_ => { readyCalls++; return ValueTask.CompletedTask; });
        Assert.Same(PyNone.Instance, await abyssal.CallAsync(Call("ClientIsReady", session)));
        Assert.Same(PyNone.Instance, await voidSpace.CallAsync(Call("ClientIsReady", session)));
        Assert.Equal(2, readyCalls);
    }

    [Fact]
    public async Task Mission_tracker_uses_distinct_positive_agent_ids_and_sends_one_reference_notification()
    {
        var sink = new PacketSink();
        var session = new ClientSession(sink) { ClientId = 7, UserId = 11, CharacterId = 42 };
        var requested = new List<(long CharacterId, long AgentId)>();
        var service = new MissionTrackerMgrService((characterId, agentId, _, _) =>
        {
            requested.Add((characterId, agentId));
            return ValueTask.FromResult<PyValue?>(new PyTuple([new PyInt(2), new PyStr("123456")]));
        });
        var ids = new PyList([new PyInt(7001), new PyInt(0), new PyInt(7001), new PyLong(7002)]);

        Assert.Same(PyNone.Instance, await service.CallAsync(Call("UpdateAllMissions", session, ids)));

        Assert.Equal([(42L, 7001L), (42L, 7002L)], requested);
        var packet = Assert.IsType<PyObject>(Assert.Single(sink.Packets));
        Assert.Equal(new PyStr("carbon.common.script.net.machoNetPacket.Notification"), packet.Name);
        var packetItems = Assert.IsType<PyTuple>(packet.Args).Items;
        Assert.Equal(new PyInt(11), packetItems[3]);
        var notificationEnvelope = Assert.IsType<PyTuple>(packetItems[4]);
        var notificationBody = Assert.IsType<PyTuple>(Assert.Single(notificationEnvelope.Items));
        var notificationBuffer = Assert.IsType<PyBuffer>(notificationBody.Items[1]);
        var notification = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(notificationBuffer.Value));
        Assert.Equal(new PyInt(0), notification.Items[0]);
        var invocation = Assert.IsType<PyTuple>(notification.Items[1]);
        Assert.Equal(new PyInt(1), invocation.Items[0]);
        var notificationArgs = Assert.IsType<PyTuple>(invocation.Items[1]);
        var actualUpdates = Assert.IsType<PyList>(Assert.Single(notificationArgs.Items));
        Assert.Collection(actualUpdates.Items,
            item =>
            {
                var update = Assert.IsType<PyDict>(item);
                Assert.Equal(new PyInt(7001), Field(update, "agentID"));
                var info = Assert.IsType<PyList>(Field(update, "info"));
                Assert.Equal(new PyInt(2), info.Items[0]);
                Assert.Equal("123456", Eve.Macho.JsSemantics.StringValue(info.Items[1]));
            },
            item =>
            {
                var update = Assert.IsType<PyDict>(item);
                Assert.Equal(new PyInt(7002), Field(update, "agentID"));
                var info = Assert.IsType<PyList>(Field(update, "info"));
                Assert.Equal(new PyInt(2), info.Items[0]);
                Assert.Equal("123456", Eve.Macho.JsSemantics.StringValue(info.Items[1]));
            });
        Assert.Equal(new PyInt(0), Field(Assert.IsType<PyDict>(packetItems[5]), "sn"));

        var missionInfo = await service.CallAsync(Call("GetMissionInfoItems", session, new PyInt(7001)));
        Assert.Equal(new PyList([new PyInt(2), new PyStr("123456")]), missionInfo);
    }

    [Fact]
    public async Task Mission_tracker_preserves_nested_list_items_in_get_and_update_wire_shapes()
    {
        var missionRows = new PyList([new PyTuple([new PyInt(2), new PyStr("Courier")])]);
        var researchRows = new PyList([new PyTuple([new PyInt(8001), new PyInt(3339)])]);
        var service = new MissionTrackerMgrService((_, _, _, _) =>
            ValueTask.FromResult<PyValue?>(new PyTuple([missionRows, researchRows])));
        var info = Assert.IsType<PyList>(await service.CallAsync(
            Call("GetMissionInfoItems", new ClientSession { CharacterId = 42 }, new PyInt(7001))));
        Assert.Collection(info.Items,
            item => AssertSingleRowList(item, new PyInt(2), new PyStr("Courier")),
            item => AssertSingleRowList(item, new PyInt(8001), new PyInt(3339)));

        var sink = new PacketSink();
        var session = new ClientSession(sink) { ClientId = 7, UserId = 11, CharacterId = 42 };
        Assert.Same(PyNone.Instance, await service.CallAsync(Call(
            "UpdateAllMissions", session, new PyList([new PyInt(7001)]))));
        var packet = Assert.IsType<PyObject>(Assert.Single(sink.Packets));
        var packetItems = Assert.IsType<PyTuple>(packet.Args).Items;
        var envelope = Assert.IsType<PyTuple>(packetItems[4]);
        var notificationBody = Assert.IsType<PyTuple>(Assert.Single(envelope.Items));
        var notification = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(
            Assert.IsType<PyBuffer>(notificationBody.Items[1]).Value));
        var invocation = Assert.IsType<PyTuple>(notification.Items[1]);
        var notificationArgs = Assert.IsType<PyTuple>(invocation.Items[1]);
        var updates = Assert.IsType<PyList>(Assert.Single(notificationArgs.Items));
        var update = Assert.IsType<PyDict>(Assert.Single(updates.Items));
        var updatedInfo = Assert.IsType<PyList>(Field(update, "info"));
        Assert.Collection(updatedInfo.Items,
            item => AssertSingleRowList(item, new PyInt(2), new PyStr("Courier")),
            item => AssertSingleRowList(item, new PyInt(8001), new PyInt(3339)));
    }

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session,
    };

    private static PyValue Field(PyDict dict, string name) =>
        dict.Entries.Single(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == name).Value;

    private static PyValue Field(PyDict dict, PyValue key) =>
        dict.Entries.Single(entry => entry.Key == key).Value;

    private static void AssertTask(PyValue value, long taskId)
    {
        var fields = Assert.IsType<PyList>(value);
        Assert.Collection(fields.Items,
            id => Assert.Equal(new PyInt(taskId), id),
            progress => Assert.Equal(new PyInt(10), progress),
            complete => Assert.Equal(new PyInt(0), complete));
    }

    private static KeyValuePair<PyValue, PyValue> Entry(PyValue key, PyValue value) => new(key, value);

    private static void AssertSingleRowList(PyValue value, PyValue first, PyValue second)
    {
        var row = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(value).Items));
        Assert.Collection(row.Items,
            item => Assert.Equal(first, item),
            item =>
            {
                if (second is PyStr text)
                    Assert.Equal(text.Value, Eve.Macho.JsSemantics.StringValue(item));
                else
                    Assert.Equal(second, item);
            });
    }

    private sealed class PacketSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];

        public void Send(PyValue packet) => Packets.Add(packet);
    }
}
