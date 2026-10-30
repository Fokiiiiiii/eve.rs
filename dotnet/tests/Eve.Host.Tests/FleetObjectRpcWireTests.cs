using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Space;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Fleet_object_rpcs_are_dispatched_through_host_with_transport_kwargs()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder(
            "Host=127.0.0.1;Port=1;Database=eve;Username=postgres").Build();
        var fleet = new FleetObjectService(new FleetStore(dataSource), () => FixedNowMs);

        Start(configure: registry => registry.Register(fleet));

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(long callId, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "fleetObjectHandler", method, new PyTuple(args), kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload))!;
        }

        var nextCallId = 1L;
        foreach (var method in new[]
        {
            "GetFleetMaxSize", "GetInitState", "GetJoinRequests", "GetMotd",
            "GetWings", "GetWings", "GetWings", "GetWings", "GetWings", "GetWings",
            "Init", "KickMember", "LeaveFleet", "LoadFleetSetup", "MakeLeader",
            "MassMoveMembers", "MoveMember", "RejectJoinRequest", "SendBroadcast",
            "SetAcceptsConduitJumpsValue", "SetAcceptsRegroupValue", "SetAutoJoinSquadID",
            "SetFleetMaxSize", "SetMotdEx", "SetOptions"
        })
        {
            var args = method switch
            {
                "Init" => new PyValue[] { PyNone.Instance, PyNone.Instance },
                "KickMember" or "MakeLeader" or "RejectJoinRequest" => [new PyLong(90_000_002)],
                "LoadFleetSetup" or "SetMotdEx" => [new PyWString("setup")],
                "MassMoveMembers" => [new PyList([new PyLong(90_000_002)]), new PyInt(-1), new PyInt(-1), new PyInt(4)],
                "MoveMember" => [new PyLong(90_000_002), new PyInt(-1), new PyInt(-1), new PyInt(4)],
                "SendBroadcast" => [new PyStr("EnemySpotted"), new PyInt(3), PyNone.Instance, PyNone.Instance],
                "SetAcceptsConduitJumpsValue" or "SetAcceptsRegroupValue" => [PyBool.True],
                "SetAutoJoinSquadID" or "SetFleetMaxSize" => [new PyInt(1)],
                "SetOptions" => [Py.KeyVal([("isFreeMove", PyBool.True)])],
                _ => []
            };

            var result = await Call(nextCallId++, method, args);
            if (method == "GetWings")
                Assert.IsType<PyDict>(result);
            else if (method == "MassMoveMembers")
                Assert.IsType<PyList>(result);
            else if (method == "MoveMember")
                Assert.Equal(PyBool.False, result);
            else
                Assert.Same(PyNone.Instance, result);
        }
    }

    [Fact]
    public async Task Voluntary_fleet_leave_notifies_the_other_member_without_echoing_to_the_leaver()
    {
        const long fleetId = 900_100;
        const long leaderId = 900_101;
        const long leaverId = 900_102;
        await using var dataSource = new NpgsqlDataSourceBuilder(
            "Host=127.0.0.1;Port=1;Database=eve;Username=postgres").Build();
        var connected = new Dictionary<long, ClientSession>();
        var fleet = new FleetObjectService(new FleetStore(dataSource), () => FixedNowMs,
            sessionForCharacter: id => connected.GetValueOrDefault(id));
        var before = new FleetSnapshot(fleetId, null,
            [
                new FleetMemberSnapshot(leaderId, FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Leader, FleetStore.CreatorJob),
                new FleetMemberSnapshot(leaverId, FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Member, 0),
            ], [], []);
        var after = new FleetSnapshot(fleetId, null,
            [new FleetMemberSnapshot(leaderId, FleetStore.NoUnit, FleetStore.NoUnit, FleetStore.Leader, FleetStore.CreatorJob)],
            [], []);
        var opened = 0;
        Start(
            configure: registry => registry.Register(new VoluntaryFleetLeaveProbe(fleet, before, after)),
            sessionOpened: session =>
            {
                var characterId = Interlocked.Increment(ref opened) == 1 ? leaderId : leaverId;
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
                connected[characterId] = session;
            });

        var leaderFront = await Handshaken(await ConnectAsync());
        var leaderOpen = Open(1_790_646_377_000UL * 15, 42);
        await leaderFront.SendAsync(leaderOpen);
        await leaderFront.ReceiveAsync();
        var leaverFront = await Handshaken(await ConnectAsync());
        var leaverOpen = Open(1_790_646_377_000UL * 15 + 1, 43);
        await leaverFront.SendAsync(leaverOpen);
        await leaverFront.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await leaverFront.SendAsync(new Packet(leaverOpen.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(leaverOpen, 1, "fleetLeaveNotificationProbe", "Leave",
                new PyTuple([]), kwargs))));

        var reply = Assert.IsType<Send>(await leaverFront.ReceiveAsync());
        Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(reply.Payload)));
        var notification = ReadNotification(Assert.IsType<Send>(await leaderFront.ReceiveAsync()));
        Assert.Equal("OnFleetLeave", notification.Name);
        Assert.Single(notification.Args);
        Assert.Equal(leaverId, Number(notification.Args[0]));
    }

    private sealed class VoluntaryFleetLeaveProbe(
        FleetObjectService fleet,
        FleetSnapshot before,
        FleetSnapshot after) : IService
    {
        public string Name => "fleetLeaveNotificationProbe";
        public bool HasMethod(string method) => method == "Leave";

        public async ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            await fleet.PublishVoluntaryLeaveChangesAsync(call.Session, before.FleetId, before, after, call.Cancellation);
            return PyNone.Instance;
        }
    }

    [Fact]
    public async Task Fleet_join_kick_role_and_broadcast_notifications_keep_the_client_wire_shapes()
    {
        const long fleetId = 900_200;
        const long leaderId = 900_201;
        const long memberId = 900_202;
        const long solarSystemId = 30_000_141;
        const long itemId = 600_001;
        const long typeId = 587;
        await using var dataSource = new NpgsqlDataSourceBuilder(
            "Host=127.0.0.1;Port=1;Database=eve;Username=postgres").Build();
        var connected = new Dictionary<long, ClientSession>();
        var fleet = new FleetObjectService(new FleetStore(dataSource), () => FixedNowMs,
            sessionForCharacter: id => connected.GetValueOrDefault(id),
            memberSkillsForCharacter: (_, _) => Task.FromResult<IReadOnlyList<int>>([0, 0, 0]));
        var leader = new FleetMemberSnapshot(leaderId, FleetStore.NoUnit, FleetStore.NoUnit,
            FleetStore.Leader, FleetStore.CreatorJob);
        var member = new FleetMemberSnapshot(memberId, FleetStore.NoUnit, FleetStore.NoUnit,
            FleetStore.Member, 0);
        var before = new FleetSnapshot(fleetId, null, [leader, member], [], []);
        var joinBefore = new FleetSnapshot(fleetId, null, [leader], [], []);
        var joinAfter = new FleetSnapshot(fleetId, null, [leader, member], [], []);
        var roleAfter = new FleetSnapshot(fleetId, null,
            [leader, member with { Role = FleetStore.WingCommander }], [], []);
        var kickAfter = new FleetSnapshot(fleetId, null, [leader], [], []);
        var opened = 0;
        Start(
            configure: registry => registry.Register(new FleetNotificationProbe(
                fleet, before, joinBefore, joinAfter, roleAfter, kickAfter)),
            sessionOpened: session =>
            {
                var characterId = Interlocked.Increment(ref opened) == 1 ? leaderId : memberId;
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
                session.Set("solarsystemid2", new PyLong(solarSystemId));
                connected[characterId] = session;
            });

        var leaderFront = await Handshaken(await ConnectAsync());
        var leaderOpen = Open(1_790_646_377_000UL * 15, 44);
        await leaderFront.SendAsync(leaderOpen);
        await leaderFront.ReceiveAsync();
        var memberFront = await Handshaken(await ConnectAsync());
        var memberOpen = Open(1_790_646_377_000UL * 15 + 1, 45);
        await memberFront.SendAsync(memberOpen);
        await memberFront.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var callId = 1L;

        async Task<((string Name, string IdType, PyValue[] Args)[] Notifications, PyValue? Result)> Call(
            string method, params PyValue[] args)
        {
            await leaderFront.SendAsync(new Packet(leaderOpen.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(leaderOpen, callId++, "fleetNotificationProbe", method,
                    new PyTuple(args), kwargs))));
            var notifications = new List<(string Name, string IdType, PyValue[] Args)>();
            while (true)
            {
                var send = Assert.IsType<Send>(await leaderFront.ReceiveAsync());
                var decoded = MarshalCodec.Decode(send.Payload);
                var values = ((PyTuple)((PyObject)decoded).Args).Items;
                if (Number(values[0]) == MachoMessage.Notification)
                    notifications.Add(ReadFleetNotification(send));
                else
                    return ([.. notifications], Result(decoded));
            }
        }

        var join = await Call("Join");
        Assert.Same(PyNone.Instance, join.Result);
        var joined = Assert.Single(join.Notifications);
        Assert.Equal(("OnFleetJoin", "fleetid"), (joined.Name, joined.IdType));
        Assert.Single(joined.Args);
        AssertJoinedMember(joined.Args[0], memberId);
        var joinSessionChange = ReadFleetSessionChanges(Assert.IsType<Send>(await memberFront.ReceiveAsync()));
        Assert.Equal(new[] { "fleetid", "fleetrole" }, joinSessionChange.Entries.Select(entry => T(entry.Key)));
        Assert.Equal(new PyValue[] { PyNone.Instance, new PyLong(fleetId) },
            Assert.IsType<PyTuple>(joinSessionChange.Entries[0].Value).Items);
        Assert.Equal(new PyValue[] { PyNone.Instance, new PyInt(FleetStore.Member) },
            Assert.IsType<PyTuple>(joinSessionChange.Entries[1].Value).Items);
        var joinOnMember = ReadFleetNotification(Assert.IsType<Send>(await memberFront.ReceiveAsync()));
        Assert.Equal(("OnFleetJoin", "fleetid"), (joinOnMember.Name, joinOnMember.IdType));
        Assert.Single(joinOnMember.Args);
        AssertJoinedMember(joinOnMember.Args[0], memberId);

        var roleChange = await Call("Role");
        Assert.Same(PyNone.Instance, roleChange.Result);
        var changed = Assert.Single(roleChange.Notifications);
        Assert.Equal(("OnFleetMemberChanged", "fleetid"), (changed.Name, changed.IdType));
        Assert.Equal(13, changed.Args.Length);
        Assert.Equal(memberId, Number(changed.Args[0]));
        Assert.Equal(fleetId, Number(changed.Args[1]));
        Assert.Equal(FleetStore.Member, Number(changed.Args[4]));
        Assert.Equal(FleetStore.WingCommander, Number(changed.Args[9]));
        Assert.Same(PyBool.False, changed.Args[12]);
        var memberChange = ReadFleetNotification(Assert.IsType<Send>(await memberFront.ReceiveAsync()));
        Assert.Equal(("OnFleetMemberChanged", "fleetid"), (memberChange.Name, memberChange.IdType));
        Assert.Equal(13, memberChange.Args.Length);
        var moved = ReadFleetNotification(Assert.IsType<Send>(await memberFront.ReceiveAsync()));
        Assert.Equal(("OnFleetMove", "fleetid"), (moved.Name, moved.IdType));
        Assert.Empty(moved.Args);

        var kick = await Call("Kick");
        Assert.Same(PyNone.Instance, kick.Result);
        var kicked = Assert.Single(kick.Notifications);
        Assert.Equal(("OnFleetLeave", "fleetid"), (kicked.Name, kicked.IdType));
        Assert.Equal(new PyValue[] { new PyLong(memberId) }, kicked.Args);
        var kickSessionChange = ReadFleetSessionChanges(Assert.IsType<Send>(await memberFront.ReceiveAsync()));
        Assert.Equal(new[] { "fleetid", "fleetrole" }, kickSessionChange.Entries.Select(entry => T(entry.Key)));
        Assert.Equal(new PyValue[] { new PyLong(fleetId), PyNone.Instance },
            Assert.IsType<PyTuple>(kickSessionChange.Entries[0].Value).Items);
        Assert.Equal(new PyValue[] { new PyInt(FleetStore.Member), PyNone.Instance },
            Assert.IsType<PyTuple>(kickSessionChange.Entries[1].Value).Items);
        var removed = ReadFleetNotification(Assert.IsType<Send>(await memberFront.ReceiveAsync()));
        Assert.Equal(("OnFleetLeave", "fleetid"), (removed.Name, removed.IdType));
        Assert.Equal(new PyValue[] { new PyLong(memberId) }, removed.Args);

        var broadcast = await Call("Broadcast",
            new PyStr("EnemySpotted"), new PyInt(3), new PyLong(itemId), new PyInt((int)typeId));
        Assert.Same(PyNone.Instance, broadcast.Result);
        var sentBroadcast = Assert.Single(broadcast.Notifications);
        Assert.Equal(("OnFleetBroadcast", "fleetid"), (sentBroadcast.Name, sentBroadcast.IdType));
        Assert.Equal(6, sentBroadcast.Args.Length);
        Assert.Equal("EnemySpotted", T(sentBroadcast.Args[0]));
        Assert.Equal(3, Number(sentBroadcast.Args[1]));
        Assert.Equal(leaderId, Number(sentBroadcast.Args[2]));
        Assert.Equal(solarSystemId, Number(sentBroadcast.Args[3]));
        Assert.Equal(itemId, Number(sentBroadcast.Args[4]));
        Assert.Equal(typeId, Number(sentBroadcast.Args[5]));
        var receivedBroadcast = ReadFleetNotification(Assert.IsType<Send>(await memberFront.ReceiveAsync()));
        Assert.Equal(("OnFleetBroadcast", "fleetid"), (receivedBroadcast.Name, receivedBroadcast.IdType));
        Assert.Equal(sentBroadcast.Args, receivedBroadcast.Args);
    }

    private static void AssertJoinedMember(PyValue value, long characterId)
    {
        var fields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries
            .ToDictionary(entry => T(entry.Key)!, entry => entry.Value);
        Assert.Equal(characterId, Number(fields["charID"]));
        Assert.Equal(FleetStore.Member, Number(fields["role"]));
        Assert.Equal(FleetStore.NoUnit, Number(fields["wingID"]));
        Assert.Equal(FleetStore.NoUnit, Number(fields["squadID"]));
        Assert.Equal(new PyValue[] { new PyInt(0), new PyInt(0), new PyInt(0) },
            Assert.IsType<PyList>(fields["skills"]).Items);
        Assert.IsType<PyInt>(fields["job"]);
        Assert.IsType<PyObject>(fields["memberOptOuts"]);
    }

    private static PyDict ReadFleetSessionChanges(Send send)
    {
        var state = ((PyTuple)((PyObject)MarshalCodec.Decode(send.Payload)).Args).Items;
        Assert.Equal(MachoMessage.SessionChangeNotification, Number(state[0]));
        var sessionChange = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(state[4]).Items[1]);
        return Assert.IsType<PyDict>(sessionChange.Items[1]);
    }

    private static (string Name, string IdType, PyValue[] Args) ReadFleetNotification(Send send)
    {
        var state = ((PyTuple)((PyObject)MarshalCodec.Decode(send.Payload)).Args).Items;
        var destination = ((PyTuple)((PyObject)state[2]).Args).Items;
        var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
        var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
        return (T(destination[1])!, T(destination[3])!,
            [.. ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items]);
    }

    private sealed class FleetNotificationProbe(
        FleetObjectService fleet,
        FleetSnapshot before,
        FleetSnapshot joinBefore,
        FleetSnapshot joinAfter,
        FleetSnapshot roleAfter,
        FleetSnapshot kickAfter) : IService
    {
        public string Name => "fleetNotificationProbe";
        public bool HasMethod(string method) => method is "Join" or "Role" or "Kick" or "Broadcast" or "Structure" or "MoveResults";

        public async ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            switch (call.Method)
            {
                case "Join":
                    await fleet.PublishMemberChangesAsync(call.Session, before.FleetId,
                        joinBefore, joinAfter, call.Cancellation);
                    break;
                case "Role":
                    await fleet.PublishMemberChangesAsync(call.Session, before.FleetId,
                        before, roleAfter, call.Cancellation);
                    break;
                case "Kick":
                    await fleet.PublishMemberChangesAsync(call.Session, before.FleetId,
                        before, kickAfter, call.Cancellation);
                    break;
                case "Broadcast":
                    fleet.PublishBroadcast(call, before);
                    break;
                case "Structure":
                    fleet.NotifyFleetMembers(before, call.Session, "OnFleetWingAdded", new PyLong(101));
                    fleet.NotifyFleetMembers(before, call.Session, "OnFleetSquadAdded", new PyLong(101), new PyLong(201));
                    fleet.NotifyFleetMembers(before, call.Session, "OnFleetWingDeleted", new PyLong(101));
                    fleet.NotifyFleetMembers(before, call.Session, "OnFleetSquadDeleted", new PyLong(201));
                    fleet.NotifyFleetMembers(before, call.Session, "OnFleetWingNameChanged", new PyLong(101), new PyWString("Wing"));
                    fleet.NotifyFleetMembers(before, call.Session, "OnFleetSquadNameChanged", new PyLong(201), new PyWString("Squad"));
                    fleet.NotifyFleetOptionsChanged(before.FleetId, before, before with { AutoJoinSquadId = null }, call.Session);
                    break;
                case "MoveResults":
                    return new PyTuple([
                        FleetObjectService.MoveMemberResult(true),
                        FleetObjectService.MoveMemberResult(false),
                        FleetObjectService.MassMoveMembersResult([900_302, 900_303]),
                        FleetObjectService.MassMoveMembersResult([])
                    ]);
                default:
                    throw new InvalidOperationException(call.Method);
            }
            return PyNone.Instance;
        }
    }

    [Fact]
    public async Task Fleet_finder_rpcs_are_dispatched_through_host_with_transport_kwargs()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder(
            "Host=127.0.0.1;Port=1;Database=eve;Username=postgres").Build();
        var store = new FleetStore(dataSource);
        var fleet = new FleetObjectService(store, () => FixedNowMs);
        var proxy = new FleetProxyService(store);

        Start(configure: registry =>
        {
            registry.Register(fleet);
            registry.Register(proxy);
        });

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(long callId, string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, service, method, new PyTuple(args), kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload))!;
        }

        Assert.Same(PyNone.Instance, await Call(1, "fleetObjectHandler", "SetTakesFleetWarp", PyBool.True));
        Assert.Same(PyNone.Instance, await Call(2, "fleetObjectHandler", "UpdateMemberInfo", new PyInt(587)));

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 3, "fleetProxy", "ApplyToJoinFleet",
                new PyTuple([new PyLong(90_000_001), PyBool.False]), kwargs))));
        var refusal = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.IsType<PyObject>(MarshalCodec.Decode(refusal.Payload));

        var adverts = Assert.IsType<PyDict>(await Call(4, "fleetProxy", "GetAvailableFleetAds"));
        Assert.Empty(adverts.Entries);
        Assert.Same(PyNone.Instance, await Call(5, "fleetProxy", "AddFleetFinderAdvert",
            Py.KeyVal([("fleetName", new PyWString("Fleet")), ("description", new PyWString("Open"))])));
        Assert.Same(PyNone.Instance, await Call(6, "fleetProxy", "UpdateFleetAdvertWithNewLeader", Py.KeyVal()));
        Assert.Same(PyNone.Instance, await Call(7, "fleetProxy", "RemoveFleetFinderAdvert"));
        Assert.Same(PyNone.Instance, await Call(8, "fleetProxy", "GetMyFleetFinderAdvert"));
        Assert.Same(PyNone.Instance, await Call(9, "fleetProxy", "UpdateAdvertInfo",
            new PyInt(1), Py.KeyVal()));
        Assert.Same(PyNone.Instance, await Call(10, "fleetProxy", "UpdateAdvertAllowedEntities", Py.KeyVal()));
    }

    [Fact]
    public async Task Fleet_broadcast_names_and_member_notifications_keep_client_wire_shapes()
    {
        const long fleetId = 900_300;
        const long leaderId = 900_301;
        const long memberId = 900_302;
        const long solarSystemId = 30_000_141;
        await using var dataSource = new NpgsqlDataSourceBuilder(
            "Host=127.0.0.1;Port=1;Database=eve;Username=postgres").Build();
        var connected = new Dictionary<long, ClientSession>();
        var fleet = new FleetObjectService(new FleetStore(dataSource), () => FixedNowMs,
            sessionForCharacter: id => connected.GetValueOrDefault(id),
            sameBubble: (_, _) => true);
        var snapshot = new FleetSnapshot(fleetId, 201,
            [
                new FleetMemberSnapshot(leaderId, FleetStore.NoUnit, FleetStore.NoUnit,
                    FleetStore.Leader, FleetStore.CreatorJob),
                new FleetMemberSnapshot(memberId, FleetStore.NoUnit, FleetStore.NoUnit,
                    FleetStore.Member, 0)
            ], [], []);
        var opened = 0;
        Start(configure: registry =>
        {
            registry.Register(fleet);
            registry.Register(new FleetNotificationProbe(
                fleet, snapshot, snapshot, snapshot, snapshot, snapshot));
        }, sessionOpened: session =>
        {
            var characterId = Interlocked.Increment(ref opened) == 1 ? leaderId : memberId;
            session.CharacterId = characterId;
            session.Set("charid", new PyLong(characterId));
            session.Set("solarsystemid2", new PyLong(solarSystemId));
            session.Set("ballparkid", new PyLong(77));
            connected[characterId] = session;
        });

        var leaderFront = await Handshaken(await ConnectAsync());
        var leaderOpen = Open(1_790_646_377_000UL * 15, 46);
        await leaderFront.SendAsync(leaderOpen);
        await leaderFront.ReceiveAsync();
        var memberFront = await Handshaken(await ConnectAsync());
        var memberOpen = Open(1_790_646_377_000UL * 15 + 1, 47);
        await memberFront.SendAsync(memberOpen);
        await memberFront.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var callId = 1L;

        async Task<((string Name, string IdType, PyValue[] Args)[] Notifications, PyValue? Result)> Call(
            string method, params PyValue[] args)
        {
            await leaderFront.SendAsync(new Packet(leaderOpen.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(leaderOpen, callId++, "fleetNotificationProbe", method,
                    new PyTuple(args), kwargs))));
            var notifications = new List<(string Name, string IdType, PyValue[] Args)>();
            while (true)
            {
                var send = Assert.IsType<Send>(await leaderFront.ReceiveAsync());
                var decoded = MarshalCodec.Decode(send.Payload);
                var values = ((PyTuple)((PyObject)decoded).Args).Items;
                if (Number(values[0]) == MachoMessage.Notification)
                    notifications.Add(ReadFleetNotification(send));
                else
                    return ([.. notifications], Result(decoded));
            }
        }

        string[] clientBroadcasts =
        [
            "EnemySpotted", "NeedBackup", "HoldPosition", "InPosition", "TravelTo", "JumpBeacon",
            "Location", "Target", "HealTarget", "HealArmor", "HealShield", "HealCapacitor",
            "WarpTo", "AlignTo", "JumpTo"
        ];
        Assert.Equal(clientBroadcasts.OrderBy(name => name, StringComparer.Ordinal),
            FleetBroadcasts.Names.OrderBy(name => name, StringComparer.Ordinal));
        foreach (var name in clientBroadcasts)
        {
            var (sent, result) = await Call("Broadcast", new PyStr(name), new PyInt(3),
                new PyLong(600_001), new PyInt(587));
            Assert.Same(PyNone.Instance, result);
            var sentBroadcast = Assert.Single(sent);
            Assert.Equal(("OnFleetBroadcast", "fleetid"), (sentBroadcast.Name, sentBroadcast.IdType));
            Assert.Equal(6, sentBroadcast.Args.Length);
            Assert.Equal(name, T(sentBroadcast.Args[0]));
            Assert.Equal(3, Number(sentBroadcast.Args[1]));
            Assert.Equal(leaderId, Number(sentBroadcast.Args[2]));
            Assert.Equal(solarSystemId, Number(sentBroadcast.Args[3]));
            Assert.Equal(600_001, Number(sentBroadcast.Args[4]));
            Assert.Equal(587, Number(sentBroadcast.Args[5]));
            var receivedBroadcast = ReadFleetNotification(Assert.IsType<Send>(await memberFront.ReceiveAsync()));
            Assert.Equal(sentBroadcast.Name, receivedBroadcast.Name);
            Assert.Equal(sentBroadcast.IdType, receivedBroadcast.IdType);
            Assert.Equal(sentBroadcast.Args, receivedBroadcast.Args);
        }

        var (topology, topologyResult) = await Call("Structure");
        Assert.Same(PyNone.Instance, topologyResult);
        Assert.Equal(new[]
        {
            "OnFleetWingAdded", "OnFleetSquadAdded", "OnFleetWingDeleted", "OnFleetSquadDeleted",
            "OnFleetWingNameChanged", "OnFleetSquadNameChanged", "OnFleetOptionsChanged"
        }, topology.Select(notification => notification.Name));
        Assert.All(topology, notification => Assert.Equal("fleetid", notification.IdType));
        Assert.Equal(new PyValue[] { new PyLong(101) }, topology[0].Args);
        Assert.Equal(new PyValue[] { new PyLong(101), new PyLong(201) }, topology[1].Args);
        Assert.Equal(new PyValue[] { new PyLong(101) }, topology[2].Args);
        Assert.Equal(new PyValue[] { new PyLong(201) }, topology[3].Args);
        Assert.Equal(new PyValue[] { new PyLong(101), new PyWString("Wing") }, topology[4].Args);
        Assert.Equal(new PyValue[] { new PyLong(201), new PyWString("Squad") }, topology[5].Args);
        Assert.Equal(2, topology[6].Args.Length);
        var oldOptions = Assert.IsType<PyDict>(Assert.IsType<PyObject>(topology[6].Args[0]).Args)
            .Entries.ToDictionary(entry => T(entry.Key)!, entry => entry.Value);
        var newOptions = Assert.IsType<PyDict>(Assert.IsType<PyObject>(topology[6].Args[1]).Args)
            .Entries.ToDictionary(entry => T(entry.Key)!, entry => entry.Value);
        Assert.Equal(new PyLong(201), oldOptions["autoJoinSquadID"]);
        Assert.Same(PyNone.Instance, newOptions["autoJoinSquadID"]);
        Assert.Same(PyBool.False, oldOptions["isFreeMove"]);
        Assert.Same(PyBool.False, newOptions["isFreeMove"]);
        var memberNotifications = new List<(string Name, string IdType, PyValue[] Args)>();
        for (var i = 0; i < topology.Length; i++)
            memberNotifications.Add(ReadFleetNotification(Assert.IsType<Send>(await memberFront.ReceiveAsync())));
        Assert.Equal(topology, memberNotifications);

        var (_, moveResult) = await Call("MoveResults");
        var moveValues = Assert.IsType<PyTuple>(moveResult);
        Assert.Same(PyBool.True, moveValues.Items[0]);
        Assert.Same(PyBool.False, moveValues.Items[1]);
        Assert.Equal(new PyValue[] { new PyLong(900_302), new PyLong(900_303) },
            Assert.IsType<PyList>(moveValues.Items[2]).Items);
        Assert.Empty(Assert.IsType<PyList>(moveValues.Items[3]).Items);
    }
}
