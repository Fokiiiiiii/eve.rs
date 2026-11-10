using Eve.Crimewatch;
using Eve.Data;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Crimewatch;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private sealed class CrimewatchWirePersistence : ICrimewatchPersistenceCallbacks
    {
        public Task<IReadOnlyList<SecurityStatusTransaction>> SecurityStatusTransactionsOfAsync(long characterId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long?> StartDuelChallengeAsync(long challengerCharacterId, long targetCharacterId, long challengerCorporationId, long challengerAllianceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LimitedEngagement>> LimitedEngagementsAsync(long nowUnixMs, long? characterId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LimitedEngagement>>([]);

        public Task UpsertLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expiresAtUnixMs, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ExpireLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expectedExpiresAtUnixMs, long nowUnixMs, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RespondToDuelChallengeAsync(long responderCharacterId, long challengerCharacterId, long challengeExpiresAtFileTime, bool accept, long? engagementExpiresAtUnixMs, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CrimewatchWireEngagementPersistence : ICrimewatchPersistenceCallbacks
    {
        private readonly Dictionary<(long First, long Second), long> _engagements = [];

        public Task<IReadOnlyList<SecurityStatusTransaction>> SecurityStatusTransactionsOfAsync(long characterId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SecurityStatusTransaction>>([]);

        public Task<long?> StartDuelChallengeAsync(long challengerCharacterId, long targetCharacterId, long challengerCorporationId, long challengerAllianceId, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(null);

        public Task<IReadOnlyList<LimitedEngagement>> LimitedEngagementsAsync(long nowUnixMs, long? characterId, CancellationToken cancellationToken)
        {
            IReadOnlyList<LimitedEngagement> rows = [.. _engagements
                .Where(entry => entry.Value > nowUnixMs &&
                    (characterId is null || entry.Key.First == characterId || entry.Key.Second == characterId))
                .Select(entry => new LimitedEngagement(entry.Key.First, entry.Key.Second, entry.Value))];
            return Task.FromResult(rows);
        }

        public Task UpsertLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expiresAtUnixMs, CancellationToken cancellationToken)
        {
            _engagements[(Math.Min(firstCharacterId, secondCharacterId), Math.Max(firstCharacterId, secondCharacterId))] = expiresAtUnixMs;
            return Task.CompletedTask;
        }

        public Task<bool> ExpireLimitedEngagementAsync(long firstCharacterId, long secondCharacterId, long expectedExpiresAtUnixMs, long nowUnixMs, CancellationToken cancellationToken)
        {
            var key = (Math.Min(firstCharacterId, secondCharacterId), Math.Max(firstCharacterId, secondCharacterId));
            var expired = _engagements.TryGetValue(key, out var until) && until == expectedExpiresAtUnixMs && until <= nowUnixMs;
            if (expired) _engagements.Remove(key);
            return Task.FromResult(expired);
        }

        public Task<bool> RespondToDuelChallengeAsync(long responderCharacterId, long challengerCharacterId, long challengeExpiresAtFileTime, bool accept, long? engagementExpiresAtUnixMs, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private static NpgsqlDataSource CrimewatchTestDataSource() => NpgsqlDataSource.Create(
        PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL") ??
            "Host=127.0.0.1;Database=eve;Username=postgres;Timeout=1;Command Timeout=1"));

    [Fact]
    public async Task Crimewatch_bound_get_character_security_status_uses_the_registered_host_route()
    {
        const long characterId = 90000001;
        const long targetId = 90000002;
        await using var dataSource = CrimewatchTestDataSource();
        await PgCrimewatchStore.MigrateAsync(dataSource);
        var service = new CrimewatchService(
            new PgCrimewatchStore(dataSource), null!, () => 1_800_000_000_000,
            (id, _) => Task.FromResult(id == targetId ? 2.5 : 0d));
        Start(strictRpc: true,
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "crimewatch", "MachoBindObject",
                new PyTuple([new PyTuple([new PyInt(30000142), new PyInt(1)]), PyNone.Instance]), kwargs))));
        var bindResponse = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        var binding = Assert.IsType<PyTuple>(Result(bindResponse));
        var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, 2, oid, "GetCharacterSecurityStatus", new PyTuple([new PyLong(targetId)]), kwargs))));
        var reply = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(reply)).Type);
        Assert.Equal(new PyReal(2.5), Result(reply));
    }

    [Fact]
    public async Task Crimewatch_bound_get_my_security_status_returns_fractional_recovery_over_the_wire()
    {
        const long characterId = 90000001;
        await using var dataSource = CrimewatchTestDataSource();
        await PgCrimewatchStore.MigrateAsync(dataSource);
        var service = new CrimewatchService(
            new PgCrimewatchStore(dataSource), null!, () => 1_800_000_000_000,
            (id, _) => Task.FromResult(id == characterId ? 0.03125 : 0d));
        Start(strictRpc: true,
            configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = characterId);

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "crimewatch", "MachoBindObject",
                new PyTuple([new PyTuple([new PyInt(30000142), new PyInt(1)]), PyNone.Instance]), kwargs))));
        var bind = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        var binding = Assert.IsType<PyTuple>(Result(bind));
        var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, 2, oid, "GetMySecurityStatus", new PyTuple([]), kwargs))));
        var reply = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(reply)).Type);
        Assert.Equal(new PyReal(0.03125), Result(reply));
    }

    [PgFact]
    public async Task Crimewatch_host_persists_and_notifies_the_retail_security_loss_at_a_negative_status()
    {
        const long characterId = 90000021;
        const long targetId = 90000022;
        const long shipId = 90000023;
        const long systemId = 30000142;
        const long now = 1_800_000_000_000;
        const double retailPenaltyResult = -0.4699;
        var crimewatchSchema = "cw_" + Guid.NewGuid().ToString("N")[..12];
        var relationSchema = "rel_" + Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "sch_" + Guid.NewGuid().ToString("N")[..12];
        var eventSchema = "evt_" + Guid.NewGuid().ToString("N")[..12];
        await using var dataSource = CrimewatchTestDataSource();
        await PgCrimewatchStore.MigrateAsync(dataSource, crimewatchSchema);
        await Eve.Scheduler.DurableScheduler.MigrateAsync(dataSource, schedulerSchema);
        await Eve.Events.EventBus.MigrateAsync(dataSource, eventSchema);
        await Eve.Relations.RelationStore.MigrateAsync(dataSource, relationSchema);
        await using var scheduler = new Eve.Scheduler.DurableScheduler(dataSource, () => now, schema: schedulerSchema);
        var events = new Eve.Events.EventBus(dataSource, scheduler, schema: eventSchema);
        var relations = new Eve.Relations.RelationStore(dataSource, scheduler, events, relationSchema);
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await relations.SetAsync(connection, transaction,
                    Eve.Relations.RelationKinds.SecurityStatus.Name, characterId,
                    Eve.Relations.RelationStore.World, -0.25, "test-seed");
                await transaction.CommitAsync();
            }

            var store = new PgCrimewatchStore(dataSource, crimewatchSchema);
            var sessions = new Eve.Services.CharacterSessions();
            using var timerService = new Eve.Scheduler.LocalTimers(
                delay: static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
            using var runtime = new CrimewatchRuntime(store, sessions, () => now,
                new CrimewatchWirePersistence(), localTimers: timerService);
            var securityStatusOf = (long id, CancellationToken token) => relations.ValueAsync(
                Eve.Relations.RelationKinds.SecurityStatus.Name, id,
                Eve.Relations.RelationStore.World, 0, cancellationToken: token);
            new SecurityStatusPenalties(
                runtime,
                sessions,
                securityStatusOf,
                (_, _) => Task.FromResult<int?>(587),
                async (change, connection, transaction, token) =>
                {
                    var attrs = new Dictionary<string, object?>
                    {
                        ["eventTypeID"] = change.EventTypeID,
                        ["otherOwnerID"] = change.OtherOwnerID,
                        ["otherTypeID"] = change.OtherTypeID,
                        ["locationID"] = change.LocationID,
                        ["modification"] = change.Modification,
                        ["targetShipItemID"] = change.TargetShipItemId,
                    };
                    await relations.SetAsync(connection, transaction,
                        Eve.Relations.RelationKinds.SecurityStatus.Name,
                        change.CharacterId, Eve.Relations.RelationStore.World,
                        change.NextStatus, change.Reason, attrs, cancellationToken: token);
                },
                () => now).Attach();
            var service = new CrimewatchService(store, runtime, () => now, securityStatusOf);
            Start(strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    session.Set("shipid", new PyLong(shipId));
                    session.Set("solarsystemid", new PyLong(systemId));
                    sessions.Enter(characterId, session);
                });

            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, 1, "crimewatch", "MachoBindObject",
                    new PyTuple([new PyTuple([new PyInt(systemId), new PyInt(1)]), PyNone.Instance]), kwargs))));
            var bind = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            var binding = Assert.IsType<PyTuple>(Result(bind));
            var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;
            Assert.Equal(new PyReal(-0.25), await HostBoundRpcAsync(
                front, open, 2, oid, "GetMySecurityStatus", new PyTuple([]), kwargs));

            var aggression = await runtime.RecordAggressionAsync(new AggressionRequest(
                characterId, targetId, 90000024, systemId, 0.9,
                Eve.Universe.SecurityClass.HighSec, TargetIsCapsule: false));
            Assert.Equal(AggressionReason.Criminal, aggression.Outcome.Reason);

            MachoPacket DecodeNotification(Send message) =>
                Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(message.Payload)));
            var notifications = new List<MachoPacket>();
            for (var index = 0; index < 6; index++)
            {
                var notification = DecodeNotification(Assert.IsType<Send>(await front.ReceiveAsync()));
                Assert.Equal(MachoMessage.Notification, notification.Type);
                notifications.Add(notification);
            }
            Assert.Equal(
                ["OnModuleAttributeChanges", "OnSecurityStatusUpdate", "OnWeaponsTimerUpdate",
                    "OnPvpTimerUpdate", "OnCriminalTimerUpdate", "OnSystemCriminalFlagUpdates"],
                notifications.Select(notification => notification.Dest.BroadcastId));
            Assert.Equal(new PyReal(retailPenaltyResult), NotificationArguments(notifications[1]).Single());

            Assert.Equal(retailPenaltyResult, await securityStatusOf(characterId, CancellationToken.None));
            var penalty = Assert.Single(await relations.HistoryAsync(
                Eve.Relations.RelationKinds.SecurityStatus.Name, characterId,
                Eve.Relations.RelationStore.World, 1));
            Assert.Equal("aggression", penalty.Reason);
            Assert.Equal(retailPenaltyResult, penalty.NewValue);
            Assert.Equal(new PyReal(retailPenaltyResult), await HostBoundRpcAsync(
                front, open, 3, oid, "GetMySecurityStatus", new PyTuple([]), kwargs));
        }
        finally
        {
            foreach (var schema in new[] { crimewatchSchema, relationSchema, schedulerSchema, eventSchema })
            {
                await using var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task Crimewatch_bound_rpcs_return_the_client_shapes_through_the_registered_host_route()
    {
        await using var dataSource = CrimewatchTestDataSource();
        await PgCrimewatchStore.MigrateAsync(dataSource);
        var service = new CrimewatchService(
            new PgCrimewatchStore(dataSource), null!, () => 1_800_000_000_000,
            (id, _) => Task.FromResult(id == 90000002 ? 2.5 : 0d));
        Start(strictRpc: true, configure: registry => registry.Register(service));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "crimewatch", "MachoBindObject",
                new PyTuple([new PyTuple([new PyInt(30000142), new PyInt(1)]), PyNone.Instance]), kwargs))));
        var bind = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        var binding = Assert.IsType<PyTuple>(Result(bind));
        var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;

        long callId = 1;
        async Task<PyValue> Call(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.BoundCall(open, ++callId, oid, method, new PyTuple(args), kwargs))));
            var decoded = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);
            return Result(decoded)!;
        }

        var states = Assert.IsType<PyTuple>(await Call("GetClientStates"));
        Assert.Equal(4, states.Items.Count);
        var timers = Assert.IsType<PyTuple>(states.Items[0]);
        Assert.Equal(5, timers.Items.Count);
        Assert.All(timers.Items, timer => Assert.Equal(2, Assert.IsType<PyTuple>(timer).Items.Count));
        Assert.Empty(Assert.IsType<PyDict>(states.Items[1]).Entries);
        var flagged = Assert.IsType<PyTuple>(states.Items[2]);
        Assert.Equal(2, flagged.Items.Count);
        Assert.Equal(new PyInt(2), states.Items[3]);
        Assert.Equal(new PyInt(0), await Call("GetMySecurityStatus"));
        Assert.Equal(new PyReal(2.5), await Call("GetCharacterSecurityStatus", new PyLong(90000002)));
        Assert.IsType<PyObjectEx>(await Call("GetSecurityStatusTransactions"));
        Assert.Equal(new PyInt(2), await Call("GetSafetyLevel"));
        Assert.Equal(new PyInt(2), await Call("SetSafetyLevel", new PyInt(0)));
        Assert.Equal(PyNone.Instance, await Call("StartDuelChallenge", new PyLong(90000002)));
        Assert.Equal(PyNone.Instance, await Call("RespondToDuelChallenge", new PyLong(90000002), new PyLong(0), PyBool.True));
    }

    [Fact]
    public async Task Crimewatch_bound_get_client_states_returns_a_persisted_active_receiver_timer()
    {
        const long characterId = 90000001;
        const long now = 1_800_000_000_000;
        const long pvpExpiry = now + 900_000;
        await using var dataSource = CrimewatchTestDataSource();
        await PgCrimewatchStore.MigrateAsync(dataSource);
        var store = new PgCrimewatchStore(dataSource);
        await store.UpdateAsync(characterId, state => state with { PvpUntilMs = pvpExpiry }, now);
        using var runtime = new CrimewatchRuntime(store, new Eve.Services.CharacterSessions(), () => now, new CrimewatchWirePersistence());
        var service = new CrimewatchService(store, runtime, () => now);
        Start(strictRpc: true,
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "crimewatch", "MachoBindObject",
                new PyTuple([new PyTuple([new PyInt(30000142), new PyInt(1)]), PyNone.Instance]), kwargs))));
        var bind = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        var binding = Assert.IsType<PyTuple>(Result(bind));
        var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, 2, oid, "GetClientStates", new PyTuple([]), kwargs))));
        var reply = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(reply)).Type);
        var states = Assert.IsType<PyTuple>(Result(reply));
        var timers = Assert.IsType<PyTuple>(states.Items[0]);
        Assert.Equal([new PyInt(100), PyNone.Instance], Assert.IsType<PyTuple>(timers.Items[0]).Items);
        Assert.Equal([new PyInt(202), new PyLong(Eve.Services.Py.FileTimeFromUnixMs(pvpExpiry))], Assert.IsType<PyTuple>(timers.Items[1]).Items);
    }

    [Fact]
    public async Task Registered_crimewatch_route_returns_active_module_states_and_ongoing_engagement_then_the_stopped_expiries()
    {
        const long characterId = 90000011;
        const long targetId = 90000012;
        const long moduleId = 90000013;
        const long now = 1_800_000_000_000;
        const long systemId = 30000142;
        const long targetShipId = 90000014;
        var schema = "cw_" + Guid.NewGuid().ToString("N")[..12];
        await using var dataSource = CrimewatchTestDataSource();
        await PgCrimewatchStore.MigrateAsync(dataSource, schema);
        var store = new PgCrimewatchStore(dataSource, schema);
        var sessions = new Eve.Services.CharacterSessions();
        var timerService = new Eve.Scheduler.LocalTimers(delay: static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var runtime = new CrimewatchRuntime(store, sessions, () => now, new CrimewatchWireEngagementPersistence(), localTimers: timerService);
        try
        {
            await store.UpdateAsync(targetId,
                state => state.FlagAsSuspect(now) with { SolarSystemId = systemId }, now);
            var service = new CrimewatchService(store, runtime, () => now);
            Start(strictRpc: true,
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    session.Set("solarsystemid", new PyLong(systemId));
                    sessions.Enter(characterId, session);
                });

            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, 1, "crimewatch", "MachoBindObject",
                    new PyTuple([new PyTuple([new PyInt(systemId), new PyInt(1)]), PyNone.Instance]), kwargs))));
            var bind = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            var binding = Assert.IsType<PyTuple>(Result(bind));
            var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;

            await runtime.RecordModuleAggressionAsync(
                new AggressionRequest(characterId, targetId, targetShipId, systemId, 0.9,
                    Eve.Universe.SecurityClass.HighSec, TargetIsCapsule: false),
                moduleId);

            static MachoPacket Decode(Send message) =>
                Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(message.Payload)));
            var activityNotifications = new List<MachoPacket>();
            for (var i = 0; i < 5; i++)
            {
                var notification = Decode(Assert.IsType<Send>(await front.ReceiveAsync()));
                Assert.Equal(MachoMessage.Notification, notification.Type);
                activityNotifications.Add(notification);
            }
            var activityNames = activityNotifications.Select(notification => notification.Dest.BroadcastId).ToList();
            Assert.True(activityNames.IndexOf("OnCrimewatchEngagementCreated") >= 0);
            Assert.True(activityNames.IndexOf("OnCrimewatchEngagementStopTimeout") > activityNames.IndexOf("OnCrimewatchEngagementCreated"));
            Assert.Contains("OnWeaponsTimerUpdate", activityNames);
            Assert.Contains("OnPvpTimerUpdate", activityNames);

            var active = Assert.IsType<PyTuple>(await HostBoundRpcAsync(
                front, open, 2, oid, "GetClientStates", new PyTuple([]), kwargs));
            var activeTimers = Assert.IsType<PyTuple>(active.Items[0]);
            Assert.Equal([new PyInt(101), PyNone.Instance], Assert.IsType<PyTuple>(activeTimers.Items[0]).Items);
            Assert.Equal([new PyInt(201), PyNone.Instance], Assert.IsType<PyTuple>(activeTimers.Items[1]).Items);
            var activeEngagement = Assert.Single(Assert.IsType<PyDict>(active.Items[1]).Entries);
            Assert.Equal(InventoryWire.Id(targetId), activeEngagement.Key);
            Assert.Equal(new PyLong(CrimewatchRuntime.LimitedEngagementTimeoutOngoing), activeEngagement.Value);

            await runtime.EndModuleCombatActivityAsync(characterId, moduleId);
            var stoppedNames = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var notification = Decode(Assert.IsType<Send>(await front.ReceiveAsync()));
                Assert.Equal(MachoMessage.Notification, notification.Type);
                stoppedNames.Add(notification.Dest.BroadcastId!);
            }
            Assert.True(stoppedNames.IndexOf("OnCrimewatchEngagementStartTimeout") >= 0);
            Assert.Contains("OnWeaponsTimerUpdate", stoppedNames);
            Assert.Contains("OnPvpTimerUpdate", stoppedNames);

            var stopped = Assert.IsType<PyTuple>(await HostBoundRpcAsync(
                front, open, 3, oid, "GetClientStates", new PyTuple([]), kwargs));
            var stoppedTimers = Assert.IsType<PyTuple>(stopped.Items[0]);
            Assert.Equal([new PyInt(102), new PyLong(Eve.Services.Py.FileTimeFromUnixMs(now + CombatState.WeaponsDurationMs))],
                Assert.IsType<PyTuple>(stoppedTimers.Items[0]).Items);
            Assert.Equal([new PyInt(202), new PyLong(Eve.Services.Py.FileTimeFromUnixMs(now + CombatState.PvpDurationMs))],
                Assert.IsType<PyTuple>(stoppedTimers.Items[1]).Items);
            var stoppedEngagement = Assert.Single(Assert.IsType<PyDict>(stopped.Items[1]).Entries);
            Assert.Equal(new PyLong(Eve.Services.Py.FileTimeFromUnixMs(now + CrimewatchRuntime.LimitedEngagementDurationMs)),
                stoppedEngagement.Value);
            Assert.Equal(now + CombatState.PvpDurationMs, (await store.StateOfAsync(targetId)).PvpUntilMs);
        }
        finally
        {
            runtime.Dispose();
            timerService.Dispose();
            await using var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Crimewatch_apply_tags_is_bound_and_preserves_the_client_argument_order()
    {
        const long characterId = 90000001;
        (double Target, double Current)? applied = null;
        Eve.Services.ClientSession? connectedSession = null;
        var service = new CrimewatchService(null!, null!, () => 1_800_000_000_000)
        {
            ApplySecurityTagsAsync = (_, target, current, _) =>
            {
                applied = (target, current);
                return ValueTask.CompletedTask;
            },
        };
        Start(strictRpc: true,
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                connectedSession = session;
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "crimewatch", "MachoResolveObject",
                new PyTuple([new PyTuple([new PyInt(30000142), new PyInt(1)])]), kwargs))));
        var resolve = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.IsType<PyInt>(Result(resolve));

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 2, "crimewatch", "MachoBindObject",
                new PyTuple([new PyTuple([new PyInt(30000142), new PyInt(1)]), PyNone.Instance]), kwargs))));
        var bind = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        var binding = Assert.IsType<PyTuple>(Result(bind));
        var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, 3, oid, "ApplyTagsForSecStatus",
                new PyTuple([new PyReal(-4.5), new PyReal(-5.0)]), kwargs))));
        var reply = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(reply)).Type);
        Assert.Equal(PyNone.Instance, Result(reply));
        Assert.Equal((-4.5, -5.0), applied);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, 4, oid, "ApplyTagsForSecStatus",
                new PyTuple([new PyInt(-4), new PyLong(-5)]), kwargs))));
        var numericReply = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(PyNone.Instance, Result(numericReply));
        Assert.Equal((-4.0, -5.0), applied);
        var invalidStatus = await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyTagsForSecStatus(new Eve.Services.ServiceCall
        {
            Method = "ApplyTagsForSecStatus",
            Args = [PyNone.Instance, new PyReal(-5)],
            Session = connectedSession!,
        }).AsTask());
        Assert.Equal("Security tag exchange requires finite target and current security statuses.", invalidStatus.Message);

        Assert.Equal(Eve.Crimewatch.SecurityStatusEventTypeIds.HandInTags,
            Eve.Crimewatch.SecurityStatusEventTypeIds.FromHistoryReason("hand_in_tags"));

        var crossing = SecurityTagRequirements.Calculate(-8.25, -7.0);
        Assert.Equal(1, crossing.Single(requirement => requirement.TypeId == 33138).Quantity);
        Assert.Equal(2, crossing.Single(requirement => requirement.TypeId == 33139).Quantity);
        Assert.Equal(1, SecurityTagRequirements.Calculate(-0.1, 0).Single(requirement => requirement.TypeId == 33141).Quantity);
        Assert.Throws<ArgumentOutOfRangeException>(() => SecurityTagRequirements.Calculate(0, 1));
    }
}
