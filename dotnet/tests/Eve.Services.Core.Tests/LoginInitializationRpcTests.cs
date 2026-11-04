using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class LoginInitializationRpcTests
{
    [Fact]
    public async Task Operations_manager_exposes_reference_routes_and_default_wire_shapes()
    {
        var service = (IService)new OperationsManagerService();
        string[] methods =
        [
            "can_character_play_the_tutorial", "get_tutorial_state", "is_main_tutorial_finished",
            "has_skipped_tutorial", "get_character_progress", "get_active_category_id", "start_site",
            "process_client_event", "activate_operation_in_solar_system", "block_task",
            "complete_category_for_character", "get_category_state", "get_mission_avoidance_systems",
            "get_operations_completed_at_least_once", "is_category_complete", "skip_task", "unblock_task",
            "cancel_current_treatment_operation",
        ];
        Assert.All(methods, method => Assert.True(service.HasMethod(method), method));
        Assert.False(service.HasMethod("unknown"));
        var session = new ClientSession { CharacterId = 90000001 };

        Assert.IsType<PyDict>(await Call(service, session, "get_character_progress"));
        Assert.Same(PyNone.Instance, await Call(service, session, "get_active_category_id"));
        Assert.Equal(new PyList([]), await Call(service, session, "get_mission_avoidance_systems"));
        Assert.Equal(new PyList([]), await Call(service, session, "get_operations_completed_at_least_once"));
        Assert.Same(PyBool.False, await Call(service, session, "skip_task"));
        Assert.Same(PyBool.False, await Call(service, session, "complete_category_for_character"));
        Assert.Same(PyBool.False, await Call(service, session, "activate_operation_in_solar_system"));
        Assert.Equal(new PyInt(20), await Call(service, session, "get_category_state"));
        Assert.Same(PyNone.Instance, await Call(service, session, "process_client_event"));
    }

    [Fact]
    public async Task Achievement_tracker_returns_the_reference_keyed_maps_and_accepts_persisted_state()
    {
        var service = (IService)new AchievementTrackerMgrService();
        string[] methods = ["HasEverWarped", "GetCompletedAchievementsAndClientEventCount", "UpdateClientAchievmentsAndCounters"];
        Assert.All(methods, method => Assert.True(service.HasMethod(method), method));

        var result = Assert.IsType<PyDict>(await Call(service, new ClientSession(), "GetCompletedAchievementsAndClientEventCount"));
        Assert.Equal(["eventDict", "completedDict"], result.Entries.Select(entry => Assert.IsType<PyStr>(entry.Key).Value));
        Assert.IsType<PyDict>(Value(result, "eventDict"));
        Assert.IsType<PyDict>(Value(result, "completedDict"));
        Assert.Same(PyBool.False, await Call(service, new ClientSession(), "HasEverWarped"));
        Assert.Same(PyNone.Instance, await Call(service, new ClientSession(), "UpdateClientAchievmentsAndCounters", new PyDict([]), new PyDict([])));

        var knownWarped = (IService)new AchievementTrackerMgrService(
            hasEverWarped: (_, _) => ValueTask.FromResult(true));
        Assert.Same(PyBool.True, await Call(knownWarped, Session(90000001), "HasEverWarped"));
        var warpedSession = Session(90000002);
        warpedSession.Set("hasEverWarped", new PyInt(1));
        Assert.Same(PyBool.True, await Call(service, warpedSession, "HasEverWarped"));

        var events = new PyDict([new(new PyStr("event"), new PyInt(7))]);
        var completed = new PyDict([new(new PyInt(9), new PyLong(1234))]);
        var wired = (IService)new AchievementTrackerMgrService((_, _) =>
            ValueTask.FromResult<AchievementTrackerPersistedState?>(new(events, completed)));
        var wiredResult = Assert.IsType<PyDict>(await Call(wired, Session(90000001), "GetCompletedAchievementsAndClientEventCount"));
        Assert.Same(events, Value(wiredResult, "eventDict"));
        Assert.Same(completed, Value(wiredResult, "completedDict"));
    }

    [Fact]
    public async Task Facility_manager_routes_match_reference_defaults_and_accept_a_wire_callback()
    {
        var service = (IService)new FacilityManagerService();
        string[] methods = ["GetFacilities", "GetFacility", "GetFacilitiesByID", "GetMaxActivityModifiers", "GetFacilityTaxes", "SetFacilityTaxes", "GetFacilityLocations"];
        Assert.All(methods, method => Assert.True(service.HasMethod(method), method));
        var session = new ClientSession();
        Assert.Equal(new PyList([]), await Call(service, session, "GetFacilities"));
        Assert.Equal(new PyList([]), await Call(service, session, "GetFacilitiesByID", new PyList([new PyInt(1)])));
        Assert.Equal(new PyList([]), await Call(service, session, "GetFacilityLocations", new PyInt(1), new PyInt(2)));
        Assert.IsType<PyDict>(await Call(service, session, "GetMaxActivityModifiers"));
        Assert.IsType<PyObject>(await Call(service, session, "GetFacilityTaxes", new PyInt(1)));
        await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await Call(service, session, "SetFacilityTaxes", new PyInt(1), new PyInt(2), new PyDict([])));
        await Assert.ThrowsAsync<MachoWrappedException>(async () => await Call(service, session, "GetFacility", new PyInt(999)));

        var facility = new PyObject(new PyStr("util.KeyVal"), new PyDict([]));
        var wired = (IService)new FacilityManagerService((call, _) =>
            ValueTask.FromResult<PyValue?>(call.Method == "GetFacility" ? facility : null));
        Assert.Same(facility, await Call(wired, session, "GetFacility", new PyInt(123)));
    }

    [Fact]
    public async Task Mission_tracker_reads_existing_mission_data_and_notifies_updates()
    {
        var read = new List<long>();
        var sink = new RecordingSink();
        var session = new ClientSession(sink) { CharacterId = 90000001 };
        var info = new PyList([new PyInt(2), new PyInt(300)]);
        var service = (IService)new MissionTrackerMgrService((characterId, agentId, _, _) =>
        {
            Assert.Equal(90000001, characterId);
            read.Add(agentId);
            return ValueTask.FromResult<PyValue?>(info);
        });

        Assert.True(service.HasMethod("GetMissionInfoItems"));
        Assert.True(service.HasMethod("UpdateAllMissions"));
        Assert.True(service.HasMethod("GetAllMissionObjectives"));
        Assert.True(service.HasMethod("IsInActiveDungeonID"));
        Assert.Equal(new PyList([]), await Call(service, session, "GetAllMissionObjectives", new PyInt(42)));
        Assert.Same(PyBool.False, await Call(service, session, "IsInActiveDungeonID", new PyInt(42)));
        Assert.Equal(info, await Call(service, session, "GetMissionInfoItems", new PyInt(42)));
        Assert.Same(PyNone.Instance, await Call(service, session, "UpdateAllMissions", new PyList([new PyInt(42), new PyInt(43)])));
        Assert.Equal([42, 42, 43], read);
        Assert.Single(sink.Packets);
        Assert.Equal("OnMissionsUpdated", NotificationName(sink.Packets[0]));
    }

    [Theory]
    [InlineData("abyssalMgr", "ClientIsReady")]
    [InlineData("abyssalMgr", "AbyssalEntranceDeployment")]
    [InlineData("abyssalMgr", "AbyssalEntranceGateActivation")]
    [InlineData("abyssalMgr", "AbyssalGateActivation")]
    [InlineData("abyssalMgr", "AbyssalEndGateActivation")]
    [InlineData("voidSpaceMgr", "ClientIsReady")]
    [InlineData("voidSpaceMgr", "VoidSpaceEndGateActivation")]
    [InlineData("voidSpaceMgr", "VoidSpacePlayerJump")]
    public async Task Space_lifecycle_services_expose_evejs_routes_and_future_callbacks(string serviceName, string method)
    {
        var session = new ClientSession();
        var expected = new PyInt(4);
        if (serviceName == "abyssalMgr")
        {
            var calls = 0;
            var abyssal = new AbyssalMgrService((call, _) =>
            {
                Assert.Equal(method, call.Method);
                calls++;
                return ValueTask.FromResult<PyValue>(expected);
            });
            Assert.True(abyssal.HasMethod(method));
            var args = method switch
            {
                "AbyssalEntranceDeployment" => new PyValue[] { new PyLong(900001), new PyInt(1) },
                "AbyssalEntranceGateActivation" or "AbyssalGateActivation" or "AbyssalEndGateActivation" => [new PyLong(900002)],
                _ => [],
            };
            PyValue kwargs = method == "AbyssalGateActivation"
                ? new PyDict([new(new PyStr("violateSafetyTimer"), PyBool.True)])
                : PyNone.Instance;
            Assert.Same(expected, await abyssal.CallAsync(new ServiceCall
            {
                Method = method, Args = args, Kwargs = kwargs, Session = session,
            }));
            Assert.Equal(1, calls);
            return;
        }

        IService Unwired() => new VoidSpaceMgrService();
        IService Wired(Func<ServiceCall, CancellationToken, ValueTask<PyValue?>> handler) =>
            new VoidSpaceMgrService(rpcHandler: handler);
        var unconnected = Unwired();
        Assert.True(unconnected.HasMethod(method));

        if (method is "VoidSpaceEndGateActivation")
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(unconnected, session, method));
            Assert.Equal(MachoErrors.UserErrorPayload("DeniedTargetAttemptFailed"), error.Error.Payload);
        }
        else if (method == "VoidSpacePlayerJump")
        {
            await Assert.ThrowsAsync<MachoWrappedException>(() => Call(unconnected, session, method));
        }
        else
        {
            Assert.Same(PyNone.Instance, await Call(unconnected, session, method));
        }

        Assert.Same(expected, await Call(Wired((_, _) => ValueTask.FromResult<PyValue?>(expected)), session, method));
    }

    [Theory]
    [InlineData("abyssalMgr", "AbyssalGateActivation")]
    [InlineData("abyssalMgr", "AbyssalEndGateActivation")]
    [InlineData("voidSpaceMgr", "VoidSpaceEndGateActivation")]
    [InlineData("voidSpaceMgr", "VoidSpacePlayerJump")]
    public async Task Gate_runtime_callback_receives_the_client_call_and_returns_its_response(string serviceName, string method)
    {
        PyValue[] args = method == "VoidSpacePlayerJump"
            ? [new PyInt(123), new PyInt(456)]
            : [new PyInt(123)];
        PyValue kwargs = method == "AbyssalGateActivation"
            ? new PyDict([new(new PyStr("violateSafetyTimer"), PyBool.True)])
            : PyNone.Instance;
        var call = new ServiceCall { Method = method, Args = args, Kwargs = kwargs, Session = new ClientSession() };
        ServiceCall? observed = null;
        var expected = new PyInt(44);
        ValueTask<PyValue?> VoidHandler(ServiceCall received, CancellationToken _)
        {
            observed = received;
            return ValueTask.FromResult<PyValue?>(expected);
        }
        ValueTask<PyValue> AbyssalHandler(ServiceCall received, CancellationToken _)
        {
            observed = received;
            return ValueTask.FromResult<PyValue>(expected);
        }

        IService service = serviceName == "abyssalMgr"
            ? new AbyssalMgrService(AbyssalHandler)
            : new VoidSpaceMgrService(rpcHandler: VoidHandler);

        Assert.Same(expected, await service.CallAsync(call));
        Assert.Same(call, observed);
        Assert.Equal(args, observed!.Args);
        Assert.Equal(kwargs, observed.Kwargs);
    }

    [Fact]
    public async Task Void_space_player_jump_uses_evejs_unknown_error_wrapped_payload_when_runtime_is_unavailable()
    {
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            Call(new VoidSpaceMgrService(), new ClientSession(), "VoidSpacePlayerJump", new PyInt(123), new PyInt(456)));
        var errorList = new PyList([new PyList([new PyInt(22), new PyList([])])]);
        var values = new[] { new KeyValuePair<PyValue, PyValue>(new PyStr("errors"), errorList) };

        Assert.Equal(MachoErrors.UserErrorPayload("VoidSpaceJumpError", values), error.Error.Payload);
    }

    private static ClientSession Session(long characterId) => new() { CharacterId = characterId };

    private static Task<PyValue> Call(IService service, ClientSession session, string method, params PyValue[] args) =>
        service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session }).AsTask();

    private static PyValue Value(PyDict dict, string key) =>
        dict.Entries.Single(entry => entry.Key == new PyStr(key)).Value;

    private static string NotificationName(PyValue value)
    {
        var packet = Assert.IsType<PyObject>(value);
        var fields = Assert.IsType<PyTuple>(packet.Args).Items;
        var destination = Assert.IsType<PyObject>(fields[2]);
        var addressFields = Assert.IsType<PyTuple>(destination.Args).Items;
        return Assert.IsType<PyStr>(addressFields[1]).Value;
    }

    private sealed class RecordingSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }
}
