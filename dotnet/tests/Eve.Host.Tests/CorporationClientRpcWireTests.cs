using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;
using Eve.Characters;
using Eve.Corporations;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Alliance_creation_notification_sends_all_columns_and_relationship_changes_keep_the_wire_field()
    {
        const long characterId = 980_301;
        var sessions = new CharacterSessions();
        Start(configure: registry => registry.Register(new AllianceNotificationProbe(sessions)),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                sessions.Enter(characterId, session);
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<(string Name, string IdType, PyValue[] Args)> CallAndRead(string method)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, method == "Create" ? 1 : 2, "allianceNotificationProbe", method, Py.Tuple([])))));
            Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(
                Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
            return ReadCorporationNotification(Assert.IsType<Send>(await front.ReceiveAsync()));
        }

        var created = await CallAndRead("Create");
        Assert.Equal(("OnAllianceChanged", "ownerid"), (created.Name, created.IdType));
        var creationPayload = Assert.IsType<PyTuple>(Assert.Single(created.Args)).Items;
        Assert.Equal(98_000_001, Number(creationPayload[0]));
        var fields = Assert.IsType<PyDict>(creationPayload[1]);
        Assert.Equal(18, fields.Entries.Count);
        foreach (var entry in fields.Entries)
        {
            var pair = Assert.IsType<PyList>(entry.Value);
            Assert.Equal(2, pair.Items.Count);
            Assert.Same(PyNone.Instance, pair.Items[0]);
        }
        Assert.Equal(new PyValue[] { PyNone.Instance, PyNone.Instance },
            Assert.IsType<PyList>(Assert.Single(fields.Entries, entry => T(entry.Key) == "warFactionID").Value).Items);

        var relationship = await CallAndRead("Relationship");
        Assert.Equal(("OnAllianceRelationshipChanged", "ownerid"),
            (relationship.Name, relationship.IdType));
        var relationshipPayload = Assert.IsType<PyTuple>(Assert.Single(relationship.Args)).Items;
        var relationshipChanges = Assert.IsType<PyDict>(relationshipPayload[2]);
        Assert.Equal(new PyValue[] { PyNone.Instance, new PyReal(4.5) },
            Assert.IsType<PyList>(Assert.Single(relationshipChanges.Entries,
                entry => T(entry.Key) == "relationship").Value).Items);
    }

    [Fact]
    public async Task Corporation_member_changes_reach_both_sessions_after_the_rpc_reply()
    {
        const long corporationId = 980_100;
        const long actorId = 980_101;
        const long memberId = 980_102;
        var sessions = new CharacterSessions();
        var opened = 0;
        Start(
            configure: registry => registry.Register(new CorporationMemberChangeProbe(sessions)),
            sessionOpened: session =>
            {
                var characterId = Interlocked.Increment(ref opened) == 1 ? actorId : memberId;
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
                session.Set("corpid", new PyLong(corporationId));
                sessions.Enter(characterId, session);
            });

        var actorFront = await Handshaken(await ConnectAsync());
        var actorOpen = Open(1_790_646_377_000UL * 15, 52);
        await actorFront.SendAsync(actorOpen);
        await actorFront.ReceiveAsync();
        var memberFront = await Handshaken(await ConnectAsync());
        var memberOpen = Open(1_790_646_377_000UL * 15 + 1, 53);
        await memberFront.SendAsync(memberOpen);
        await memberFront.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var callId = 1L;
        async Task<(string IdType, PyValue[] Args)> CallAndReadActorNotification(string method)
        {
            await actorFront.SendAsync(new Packet(actorOpen.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(actorOpen, callId++, "corporationMemberChangeProbe", method,
                    new PyTuple([]), kwargs))));
            Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(
                Assert.IsType<Send>(await actorFront.ReceiveAsync()).Payload)));
            var actorNotice = ReadCorporationNotification(Assert.IsType<Send>(await actorFront.ReceiveAsync()));
            Assert.Equal("OnCorporationMemberChanged", actorNotice.Name);
            return (actorNotice.IdType, actorNotice.Args);
        }

        var roleNotice = await CallAndReadActorNotification("RoleChange");
        Assert.Equal("corpid", roleNotice.IdType);
        AssertMemberChange(roleNotice.Args, corporationId, memberId, "roles",
            new PyLong(0), new PyLong(17));
        var roleOnOther = ReadCorporationNotification(Assert.IsType<Send>(await memberFront.ReceiveAsync()));
        Assert.Equal(("OnCorporationMemberChanged", "corpid"), (roleOnOther.Name, roleOnOther.IdType));
        AssertMemberChange(roleOnOther.Args, corporationId, memberId, "roles",
            new PyLong(0), new PyLong(17));

        var joinNotice = await CallAndReadActorNotification("Join");
        Assert.Equal("corpid", joinNotice.IdType);
        AssertMemberChange(joinNotice.Args, corporationId, memberId, "corporationID",
            PyNone.Instance, new PyInt(corporationId));
        var joinedMember = ReadCorporationNotification(Assert.IsType<Send>(await memberFront.ReceiveAsync()));
        Assert.Equal(("OnCorporationMemberChanged", "clientID"), (joinedMember.Name, joinedMember.IdType));
        AssertMemberChange(joinedMember.Args, corporationId, memberId, "corporationID",
            PyNone.Instance, new PyInt(corporationId));

        var kickNotice = await CallAndReadActorNotification("Kick");
        Assert.Equal("corpid", kickNotice.IdType);
        AssertMemberChange(kickNotice.Args, corporationId, memberId, "corporationID",
            new PyInt(corporationId), PyNone.Instance);
        var kickedMember = ReadCorporationNotification(Assert.IsType<Send>(await memberFront.ReceiveAsync()));
        Assert.Equal(("OnCorporationMemberChanged", "clientID"), (kickedMember.Name, kickedMember.IdType));
        AssertMemberChange(kickedMember.Args, corporationId, memberId, "corporationID",
            new PyInt(corporationId), PyNone.Instance);

        void AssertMemberChange(PyValue[] args, long expectedCorporation, long expectedMember,
            string key, PyValue oldValue, PyValue newValue)
        {
            Assert.Equal(3, args.Length);
            Assert.Equal(expectedCorporation, Number(args[0]));
            Assert.Equal(expectedMember, Number(args[1]));
            var changes = Assert.IsType<PyDict>(args[2]);
            var pair = Assert.IsType<PyList>(Assert.Single(changes.Entries,
                entry => T(entry.Key) == key).Value);
            Assert.Equal(new PyValue[] { oldValue, newValue }, pair.Items);
        }
    }

    private static (string Name, string IdType, PyValue[] Args) ReadCorporationNotification(Send send)
    {
        var state = ((PyTuple)((PyObject)MarshalCodec.Decode(send.Payload)).Args).Items;
        Assert.Equal(MachoMessage.Notification, Number(state[0]));
        var destination = ((PyTuple)((PyObject)state[2]).Args).Items;
        var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
        var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
        var args = ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items.ToArray();
        return (T(destination[1])!, T(destination[3])!, args);
    }

    private sealed class CorporationMemberChangeProbe(CharacterSessions sessions) : IService
    {
        public string Name => "corporationMemberChangeProbe";
        public bool HasMethod(string method) => method is "RoleChange" or "Join" or "Kick";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            const long corporationId = 980_100;
            const long otherCorporationId = 980_200;
            const long memberId = 980_102;
            Member Snapshot(long roles = 0) =>
                new(memberId, corporationId, 1_790_000_000_000, "", new RoleMasks(Roles: roles),
                    null, 0, false, 0);

            Member? before;
            Member? after;
            switch (call.Method)
            {
                case "RoleChange":
                    before = Snapshot();
                    after = Snapshot(17);
                    break;
                case "Join":
                    sessions.Of(memberId)!.Set("corpid", new PyLong(otherCorporationId));
                    before = null;
                    after = Snapshot();
                    break;
                case "Kick":
                    sessions.Of(memberId)!.Set("corpid", new PyLong(otherCorporationId));
                    before = Snapshot();
                    after = null;
                    break;
                default:
                    throw new InvalidOperationException(call.Method);
            }

            CorporationNotificationAdapter.MemberChanged(
                sessions, call, corporationId, memberId, before, after);
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class AllianceNotificationProbe(CharacterSessions sessions) : IService
    {
        public string Name => "allianceNotificationProbe";
        public bool HasMethod(string method) => method is "Create" or "Relationship";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            const long allianceId = 98_000_001;
            if (call.Method == "Relationship")
            {
                CorporationNotificationAdapter.AllianceRelationshipChanged(
                    sessions, call, allianceId, 98_000_002, null, 4.5, []);
                return ValueTask.FromResult<PyValue>(PyNone.Instance);
            }

            var fields = new (string Key, PyValue Value)[]
            {
                ("allianceID", new PyInt(allianceId)), ("allianceName", new PyStr("New Alliance")),
                ("shortName", new PyStr("NEW")), ("executorCorpID", PyNone.Instance),
                ("creatorCorpID", new PyInt(98_000_003)), ("creatorCharID", new PyInt(98_000_004)),
                ("warFactionID", PyNone.Instance), ("description", new PyStr("")), ("url", PyNone.Instance),
                ("startDate", new PyLong(134_273_341_290_000_000)), ("memberCount", new PyInt(1)),
                ("dictatorial", new PyInt(0)), ("allowWar", new PyInt(0)), ("currentCapital", PyNone.Instance),
                ("currentPrimeHour", new PyInt(0)), ("newPrimeHour", new PyInt(0)),
                ("newPrimeHourValidAfter", new PyLong(0)), ("deleted", new PyInt(0)),
            };
            CorporationNotificationAdapter.AllianceChanged(sessions, call, allianceId, null, fields);
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    [Fact]
    public async Task Corporation_read_rpcs_keep_client_shapes_through_host_with_transport_kwargs()
    {
        var readCorporations = new List<long>();
        var corporationManager = new CorpMgrService(
            null!,
            null!,
            readAggressionSettings: (id, _) =>
            {
                readCorporations.Add(id);
                return ValueTask.FromResult<CorpMgrAggressionSettings?>(
                    new CorpMgrAggressionSettings(123, 456));
            });
        var corporationService = new CorporationService(null!, null!);

        Start(configure: registry =>
        {
            registry.Register(corporationManager);
            registry.Register(corporationService);
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

        var corporationIds = Enumerable.Range(1, 500)
            .Select(index => (PyValue)new PyLong(70000000 + index))
            .ToArray();
        var settings = Assert.IsType<PyDict>(await Call(
            1, "corpmgr", "GetAggressionSettingsForCorps", new PyList(corporationIds)));
        Assert.Equal(500, settings.Entries.Count);
        Assert.Equal(Enumerable.Range(1, 500).Select(index => 70000000L + index), readCorporations);
        var firstSettings = Assert.IsType<PyObject>(settings.Entries[0].Value);
        var firstFields = Assert.IsType<PyDict>(firstSettings.Args).Entries
            .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        Assert.Equal(new PyLong(123), firstFields["_enableAfter"]);
        Assert.Equal(new PyLong(456), firstFields["_disableAfter"]);

        Assert.Same(PyNone.Instance, await Call(
            2, "corporationSvc", "GetCorpInfo", new PyInt(0)));

        var recipients = Assert.IsType<PyObject>(await Call(
            3, "corporationSvc", "GetRecipientsOfMedal", new PyInt(700001)));
        var fields = Assert.IsType<PyDict>(recipients.Args).Entries
            .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        Assert.Empty(Assert.IsType<PyList>(fields["lines"]).Items);
    }
}
