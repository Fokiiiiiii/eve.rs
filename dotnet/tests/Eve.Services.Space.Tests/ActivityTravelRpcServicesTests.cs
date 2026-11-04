using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;

namespace Eve.Services.Space.Tests;

public sealed class ActivityTravelRpcServicesTests
{
    [Fact]
    public async Task Pvp_catalog_and_event_details_keep_the_EveJS_wire_shapes()
    {
        var catalog = PvpFilamentEventCatalog.LoadDefault();
        var now = new DateTimeOffset(2022, 6, 4, 10, 59, 59, TimeSpan.Zero);
        var service = new PvpFilamentMgrService(catalog, utcNow: () => now);
        Assert.NotNull(catalog.GetEvent(105, 145));
        Assert.Null(catalog.GetEvent(105, 99999));
        Assert.Null(catalog.GetEvent(99999, 145));
        var session = new ClientSession(new PacketSink()) { ClientId = 20 };
        session.UserId = 10;

        Assert.Equal("pvpFilamentMgr", service.Name);
        Assert.True(service.HasMethod("AbyssalPVPEndGateActivation"));
        var allEvents = Assert.IsType<PyDict>(await service.CallAsync(Call("GetAllEvents", session)));
        Assert.Equal(88, allEvents.Entries.Count);
        var sourcedEvent = Assert.IsType<PyObject>(Assert.Single(allEvents.Entries,
            entry => entry.Key == new PyStr("105_145")).Value);
        Assert.Equal(new PyStr("util.KeyVal"), sourcedEvent.Name);
        var eventFields = Assert.IsType<PyDict>(sourcedEvent.Args);
        Assert.Equal(new PyInt(105), Field(eventFields, "matchTypeID"));
        Assert.Equal(new PyInt(145), Field(eventFields, "scheduleID"));
        Assert.Equal(new PyInt(63715), Field(eventFields, "filamentTypeID"));
        Assert.Equal(25, eventFields.Entries.Count);
        Assert.Equal(ExpectedDate(2022, 6, 4), Field(eventFields, "startDate"));
        Assert.Equal(ExpectedDate(2022, 6, 5), Field(eventFields, "endDate"));
        Assert.DoesNotContain(Assert.IsType<PyDict>(await service.CallAsync(Call("GetActiveEvents", session))).Entries,
            entry => entry.Key == new PyStr("105_145"));

        now = new DateTimeOffset(2022, 6, 4, 11, 0, 0, TimeSpan.Zero);
        var active = Assert.IsType<PyDict>(await service.CallAsync(Call("GetActiveEvents", session)));
        Assert.Contains(active.Entries, entry => entry.Key == new PyStr("105_145"));
        Assert.Equal(ExpectedDate(2022, 6, 4), await service.CallAsync(Call("GetNextEventDate", session)));
        now = new DateTimeOffset(2022, 6, 5, 11, 0, 0, TimeSpan.Zero);
        Assert.DoesNotContain(Assert.IsType<PyDict>(await service.CallAsync(Call("GetActiveEvents", session))).Entries,
            entry => entry.Key == new PyStr("105_145"));

        now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var recent = Assert.IsType<PyObject>(await service.CallAsync(Call("GetMostRecentEvent", session)));
        var recentFields = Assert.IsType<PyDict>(recent.Args);
        Assert.Equal(new PyInt(129), Field(recentFields, "matchTypeID"));
        Assert.Equal(new PyInt(171), Field(recentFields, "scheduleID"));
        Assert.Same(PyNone.Instance, await service.CallAsync(Call("GetNextEventDate", session)));
        now = new DateTimeOffset(2020, 7, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.DoesNotContain(Assert.IsType<PyDict>(await service.CallAsync(Call("GetActiveEvents", session))).Entries,
            entry => entry.Key == new PyStr("1_1"));

        var sink = (PacketSink)session.Sink!;
        var leaderboard = new PyDict([
            Entry("matchTypeID", new PyInt(7)),
            Entry("scheduleID", new PyInt(9)),
            Entry("entries", new PyList([])),
        ]);
        Assert.Equal(PyNone.Instance, await service.CallAsync(Call("GetLeaderboard", session, new PyInt(7), new PyInt(9))));
        Assert.Equal(ClientNotification.Build(10, session.NodeId, "OnPVPFilamentsLeaderboard", "clientID", [leaderboard], 0),
            Assert.Single(sink.Packets));

        var stats = new PyDict([
            Entry("matchTypeID", new PyInt(7)),
            Entry("scheduleID", new PyInt(9)),
            Entry("statistics", new PyDict([
                Entry("rank", new PyInt(0)),
                Entry("wins", new PyInt(0)),
                Entry("losses", new PyInt(0)),
                Entry("draws", new PyInt(0)),
            ])),
        ]);
        Assert.Equal(PyNone.Instance, await service.CallAsync(Call("GetCharacterStatistics", session, new PyInt(7), new PyInt(9))));
        Assert.Equal(ClientNotification.Build(10, session.NodeId, "OnPVPFilamentsCharacterStatistics", "clientID", [stats], 1),
            sink.Packets[1]);
        Assert.Equal(PyNone.Instance, await service.CallAsync(Call("LeavePVPQueue", session, new PyInt(44), new PyInt(55))));
    }

    [Fact]
    public async Task Pvp_queue_rejects_invalid_ids_and_only_admits_an_active_sourced_filament()
    {
        var session = new ClientSession { CharacterId = 123 };
        var callbackCount = 0;
        var service = new PvpFilamentMgrService(
            admit: (call, _, _) =>
            {
                callbackCount++;
                var error = TravelRpcWire.Integer(call.Arg(0)) > 0
                    ? null
                    : new PvpFilamentQueueError(PvpFilamentJumpErrorCodes.KeyNotFound, []);
                return ValueTask.FromResult(error is null
                    ? new PvpFilamentAdmissionResult([new PvpFilamentQueueParticipant(123, session)], [])
                    : new PvpFilamentAdmissionResult([], [error]));
            },
            utcNow: () => new DateTimeOffset(2022, 6, 4, 11, 0, 0, TimeSpan.Zero));

        var invalidType = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.CallAsync(Call("JoinPVPQueue", session, new PyInt(44), new PyInt(99999))));
        Assert.Equal(PyList.Of(PyList.Of(new PyInt(33), new PyList([]))), ErrorValue(invalidType, "errors"));
        Assert.Equal(0, callbackCount);

        var invalidItem = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.CallAsync(Call("JoinPVPQueue", session, new PyInt(0), new PyInt(63715))));
        Assert.Equal(PyList.Of(PyList.Of(new PyInt(6), new PyList([]))), ErrorValue(invalidItem, "errors"));
        Assert.Equal(1, callbackCount);

        Assert.Equal(new PyInt(0), await service.CallAsync(Call("JoinPVPQueue", session, new PyInt(44), new PyInt(63715))));
        Assert.Equal(2, callbackCount);
    }

    [Fact]
    public async Task Pvp_queue_rechecks_the_schedule_after_async_admission()
    {
        var now = new DateTimeOffset(2022, 6, 4, 11, 0, 0, TimeSpan.Zero);
        var session = new ClientSession { CharacterId = 123 };
        var service = new PvpFilamentMgrService(
            admit: (_, _, _) =>
            {
                now = new DateTimeOffset(2022, 6, 5, 11, 0, 0, TimeSpan.Zero);
                return ValueTask.FromResult(new PvpFilamentAdmissionResult(
                    [new PvpFilamentQueueParticipant(123, session)], []));
            },
            utcNow: () => now);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.CallAsync(Call("JoinPVPQueue", session, new PyInt(44), new PyInt(63715))));
        Assert.Equal(PyList.Of(PyList.Of(new PyInt(33), new PyList([]))), ErrorValue(error, "errors"));
    }

    [Fact]
    public async Task Pvp_queue_duplicate_leave_retry_and_notifications_keep_reference_shapes()
    {
        var ownerSink = new PacketSink();
        var memberSink = new PacketSink();
        var owner = new ClientSession(ownerSink) { CharacterId = 123, ClientId = 20 };
        var member = new ClientSession(memberSink) { CharacterId = 456, ClientId = 21 };
        owner.UserId = 10;
        member.UserId = 11;
        var participants = new[]
        {
            new PvpFilamentQueueParticipant(owner.CharacterId!.Value, owner),
            new PvpFilamentQueueParticipant(member.CharacterId!.Value, member),
        };
        var service = new PvpFilamentMgrService(
            admit: (_, _, _) => ValueTask.FromResult(new PvpFilamentAdmissionResult(participants, [])),
            utcNow: () => new DateTimeOffset(2022, 6, 4, 11, 0, 0, TimeSpan.Zero));

        Assert.Equal(new PyInt(0), await service.CallAsync(Call("JoinPVPQueue", owner, new PyInt(44), new PyInt(63715))));
        var duplicate = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.CallAsync(Call("JoinPVPQueue", owner, new PyInt(44), new PyInt(63715))));
        Assert.Equal(PyList.Of(PyList.Of(new PyInt(32), new PyList([]))), ErrorValue(duplicate, "errors"));

        Assert.Equal(PyNone.Instance, await service.CallAsync(Call("LeavePVPQueue", member)));
        var failure = PyList.Of(PyList.Of(new PyInt(22), new PyList([])));
        Assert.Equal(ClientNotification.Build(10, owner.NodeId, "OnMatchMakingFailure", "clientID", [failure], 0),
            Assert.Single(ownerSink.Packets));
        Assert.Equal(ClientNotification.Build(11, member.NodeId, "OnMatchMakingFailure", "clientID", [failure], 0),
            Assert.Single(memberSink.Packets));

        Assert.Equal(PyNone.Instance, await service.CallAsync(Call("LeavePVPQueue", owner)));
        Assert.Single(ownerSink.Packets);
        Assert.Single(memberSink.Packets);
        Assert.Equal(new PyInt(0), await service.CallAsync(Call("JoinPVPQueue", owner, new PyInt(44), new PyInt(63715))));

        owner.Close();
        var disconnected = PyList.Of(PyList.Of(new PyInt(21), new PyList([])));
        Assert.Equal(ClientNotification.Build(10, owner.NodeId, "OnMatchMakingFailure", "clientID", [disconnected], 1),
            ownerSink.Packets[1]);
        Assert.Equal(ClientNotification.Build(11, member.NodeId, "OnMatchMakingFailure", "clientID", [disconnected], 1),
            memberSink.Packets[1]);
    }

    [Fact]
    public async Task Pvp_queue_concurrent_joins_admit_only_one_record()
    {
        var session = new ClientSession { CharacterId = 123 };
        var bothAdmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admissionCount = 0;
        async ValueTask<PvpFilamentAdmissionResult> Admit(ServiceCall call, PvpFilamentEvent eventInfo, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref admissionCount) == 2)
                bothAdmitted.TrySetResult();
            await releaseAdmission.Task;
            return new PvpFilamentAdmissionResult([new PvpFilamentQueueParticipant(123, session)], []);
        }

        var service = new PvpFilamentMgrService(admit: Admit,
            utcNow: () => new DateTimeOffset(2022, 6, 4, 11, 0, 0, TimeSpan.Zero));
        async Task<object> JoinAsync()
        {
            try
            {
                return await service.CallAsync(Call("JoinPVPQueue", session, new PyInt(44), new PyInt(63715)));
            }
            catch (MachoWrappedException error)
            {
                return error;
            }
        }

        var first = Task.Run(JoinAsync);
        var second = Task.Run(JoinAsync);
        await bothAdmitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseAdmission.TrySetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results.OfType<PyInt>(), result => result.Value == 0);
        var duplicate = Assert.IsType<MachoWrappedException>(Assert.Single(results.OfType<MachoWrappedException>()));
        Assert.Equal(PyList.Of(PyList.Of(new PyInt(32), new PyList([]))), ErrorValue(duplicate, "errors"));
    }

    [Fact]
    public async Task Pvp_queue_and_end_gate_errors_match_the_reference_codes()
    {
        var service = new PvpFilamentMgrService();
        var join = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.CallAsync(Call("JoinPVPQueue", new ClientSession(), new PyInt(1), new PyInt(2))));
        Assert.Equal("PVPJumpError", join.Error.UserErrorMessage);
        Assert.Equal(PyList.Of(PyList.Of(new PyInt(33), new PyList([]))), ErrorValue(join, "errors"));

        var endGate = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.CallAsync(Call("AbyssalPVPEndGateActivation", new ClientSession(), new PyInt(1))));
        Assert.Equal("DeniedTargetAttemptFailed", endGate.Error.UserErrorMessage);
    }

    [Fact]
    public async Task Random_jump_validation_and_unavailable_runtime_match_reference_errors()
    {
        var session = new ClientSession();
        session.CharacterId = 123;
        var invalid = new RandomJumpMgrService((id, _) => ValueTask.FromResult<SpaceRpcItem?>(
            new SpaceRpcItem(id, 456, 700, 9, 0)));
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await invalid.CallAsync(Call("ActivateRandomJumpFilament", session, new PyInt(77))));
        Assert.Equal("RandomJumpError", error.Error.UserErrorMessage);
        Assert.Equal(PyList.Of(
            PyList.Of(new PyInt(2), PyList.Of(new PyInt(123))),
            PyList.Of(new PyInt(3), PyList.Of(new PyInt(123))),
            PyList.Of(new PyInt(11), new PyList([]))), ErrorValue(error, "errors"));

        var noItem = new RandomJumpMgrService();
        var missing = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await noItem.CallAsync(Call("ActivateRandomJumpFilament", session, new PyInt(88))));
        Assert.Equal(PyList.Of(PyList.Of(new PyInt(6), new PyList([]))), ErrorValue(missing, "errors"));

        var unavailable = new RandomJumpMgrService((id, _) => ValueTask.FromResult<SpaceRpcItem?>(
            new SpaceRpcItem(id, 123, 5001, 4041, 1)));
        var unknown = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await unavailable.CallAsync(Call("ActivateRandomJumpFilament", session, new PyInt(99))));
        Assert.Equal(PyList.Of(PyList.Of(new PyInt(22), new PyList([]))), ErrorValue(unknown, "errors"));
    }

    [Fact]
    public async Task Random_jump_activation_callback_receives_the_owned_key()
    {
        var session = new ClientSession();
        session.CharacterId = 123;
        SpaceRpcItem? activated = null;
        var service = new RandomJumpMgrService(
            (id, _) => ValueTask.FromResult<SpaceRpcItem?>(new SpaceRpcItem(id, 123, 5001, 4041, 2)),
            (_, item, _) =>
            {
                activated = item;
                return ValueTask.FromResult<PyValue?>(PyNone.Instance);
            });

        Assert.Equal(PyNone.Instance, await service.CallAsync(Call("ActivateRandomJumpFilament", session, new PyInt(51))));
        Assert.Equal(new SpaceRpcItem(51, 123, 5001, 4041, 2), activated);
    }

    [Fact]
    public async Task Random_jump_replay_is_checked_before_the_consumed_item_is_read()
    {
        var session = new ClientSession();
        session.CharacterId = 123;
        var reads = 0;
        var service = new RandomJumpMgrService(
            (id, _) =>
            {
                reads++;
                return ValueTask.FromResult<SpaceRpcItem?>(null);
            },
            replay: (_, itemId, _) => ValueTask.FromResult<PyValue?>(itemId == 51 ? PyNone.Instance : null));

        Assert.Equal(PyNone.Instance, await service.CallAsync(Call("ActivateRandomJumpFilament", session, new PyInt(51))));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task Warp_vector_checks_item_group_owner_and_quantity_before_the_backend()
    {
        var session = new ClientSession();
        session.CharacterId = 123;
        var called = false;
        var wrongType = new WarpVectorMgrService((id, _) => ValueTask.FromResult<SpaceRpcItem?>(
            new SpaceRpcItem(id, 123, 700, 9, 1)));
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await wrongType.CallAsync(Call("UseVector", session, new PyInt(70))));
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        Assert.Equal(new PyStr("The selected item is not warp vector data."), ErrorValue(error, "notify"));

        var wrongOwner = new WarpVectorMgrService((id, _) => ValueTask.FromResult<SpaceRpcItem?>(
            new SpaceRpcItem(id, 456, 700, 4599, 1)));
        var ownerError = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await wrongOwner.CallAsync(Call("UseVector", session, new PyInt(70))));
        Assert.Equal(new PyStr("You do not own this warp vector data."), ErrorValue(ownerError, "notify"));

        var emptyStack = new WarpVectorMgrService((id, _) => ValueTask.FromResult<SpaceRpcItem?>(
            new SpaceRpcItem(id, 123, 700, 4599, 0)));
        var quantityError = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await emptyStack.CallAsync(Call("UseVector", session, new PyInt(70))));
        Assert.Equal(new PyStr("Warp vector data is not available."), ErrorValue(quantityError, "notify"));

        var valid = new WarpVectorMgrService(
            (id, _) => ValueTask.FromResult<SpaceRpcItem?>(new SpaceRpcItem(id, 123, 800, 4599, 1)),
            (_, item, _) =>
            {
                called = item.ItemId == 71;
                return ValueTask.FromResult<PyValue?>(PyNone.Instance);
            });
        Assert.Equal(PyNone.Instance, await valid.CallAsync(Call("UseVector", session, new PyInt(71))));
        Assert.True(called);

        var unavailable = new WarpVectorMgrService((id, _) => ValueTask.FromResult<SpaceRpcItem?>(
            new SpaceRpcItem(id, 123, 800, 4599, 1)));
        var noDestination = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await unavailable.CallAsync(Call("UseVector", session, new PyInt(72))));
        Assert.Equal(new PyStr("No verified warp vector destination is available."), ErrorValue(noDestination, "notify"));
    }

    [Fact]
    public async Task Wormhole_polarization_returns_filetime_and_duration_and_jump_defaults_to_none()
    {
        var session = new ClientSession();
        session.CharacterId = 321;
        var readEndpoint = 0L;
        var readCharacter = 0L;
        var service = new WormholeMgrService(
            spaceSessions: null,
            readPolarization: (endpointId, characterId, _) =>
            {
                readEndpoint = endpointId;
                readCharacter = characterId;
                return ValueTask.FromResult<WormholePolarization?>(new WormholePolarization(1_900_000_000_000, 300));
            });

        Assert.Equal("wormholeMgr", service.Name);
        Assert.True(service.HasMethod("WormholeJump"));
        Assert.Equal(PyList.Of(
            new PyLong(Py.FileTimeFromUnixMs(1_900_000_000_000)), new PyInt(300)),
            await service.CallAsync(Call("GetWormholePolarization", session, new PyInt(654))));
        Assert.Equal(654, readEndpoint);
        Assert.Equal(321, readCharacter);
        Assert.Equal(PyNone.Instance, await service.CallAsync(Call("WormholeJump", session, new PyInt(654))));

        var noState = new WormholeMgrService(null);
        Assert.Equal(PyNone.Instance, await noState.CallAsync(Call("GetWormholePolarization", session, new PyInt(654))));
    }

    [Fact]
    public async Task Wormhole_jump_cancel_notification_requires_the_landing_pending_callback()
    {
        var sink = new PacketSink();
        var session = new ClientSession(sink) { CharacterId = 321 };
        session.UserId = 10;
        var service = new WormholeMgrService(
            spaceSessions: null,
            isPilotWarpLandingPending: call => call.Session.CharacterId == 321);

        Assert.Equal(PyNone.Instance, await service.CallAsync(Call("WormholeJump", session, new PyInt(654))));
        Assert.Equal(
            ClientNotification.Build(10, session.NodeId, "OnWormholeJumpCancel", "clientID", [], 0),
            Assert.Single(sink.Packets));

        var notPending = new WormholeMgrService(spaceSessions: null, isPilotWarpLandingPending: _ => false);
        var noCancelSink = new PacketSink();
        var noCancelSession = new ClientSession(noCancelSink) { CharacterId = 321 };
        Assert.Equal(PyNone.Instance, await notPending.CallAsync(Call("WormholeJump", noCancelSession, new PyInt(654))));
        Assert.Empty(noCancelSink.Packets);
    }

    [Fact]
    public async Task Wormhole_jump_callback_returns_the_EveJS_list_result()
    {
        var session = new ClientSession { CharacterId = 321 };
        var endpointId = 0L;
        var service = new WormholeMgrService(
            spaceSessions: null,
            jump: (_, endpoint, _) =>
            {
                endpointId = endpoint;
                return ValueTask.FromResult<PyValue?>(PyTuple.Of(new PyStr("bound-oid"), new PyLong(638_000_000_000_000_000)));
            });

        Assert.Equal(PyList.Of(new PyStr("bound-oid"), new PyLong(638_000_000_000_000_000)),
            await service.CallAsync(Call("WormholeJump", session, new PyInt(654))));
        Assert.Equal(654, endpointId);
    }

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session,
    };

    private static KeyValuePair<PyValue, PyValue> Entry(string name, PyValue value) => new(new PyStr(name), value);

    private static PyValue Field(PyDict dictionary, string name) =>
        Assert.Single(dictionary.Entries, entry => entry.Key == new PyStr(name)).Value;

    private static PyObjectEx ExpectedDate(int year, byte month, byte day) => new(
        ObjectExKind.Ex1,
        new PyTuple([
            new PyToken("datetime.date"),
            new PyTuple([new PyBytes(new byte[] { (byte)(year >> 8), (byte)year, month, day })]),
        ]),
        [],
        []);

    private static PyValue ErrorValue(MachoWrappedException error, string name)
    {
        var payload = Assert.IsType<PyObjectEx>(error.Error.Payload);
        var header = Assert.IsType<PyTuple>(payload.Header);
        var args = Assert.IsType<PyTuple>(header.Items[1]);
        var values = Assert.IsType<PyDict>(args.Items[1]);
        return Assert.Single(values.Entries, entry => entry.Key == new PyStr(name)).Value;
    }

    private sealed class PacketSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }
}
