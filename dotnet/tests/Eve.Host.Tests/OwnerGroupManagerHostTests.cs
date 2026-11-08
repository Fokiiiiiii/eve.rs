using System.Text;
using Eve.AccessGroups;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Owner_group_summary_RPCs_require_a_role_or_everyone_membership()
    {
        const long adminId = 90000001;
        const long roleManagerId = 90000004;
        const long memberId = 90000002;
        const long unrelatedId = 90000003;
        const long adminCorpId = 98000001;
        const long roleManagerCorpId = 98000004;
        const long memberCorpId = 98000002;
        const long unrelatedCorpId = 98000003;

        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = Npgsql.NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        var schema = $"ag_host_{Guid.NewGuid():N}"[..20];
        try
        {
            await AccessGroupStore.MigrateAsync(source, schema);
            var store = new AccessGroupStore(source, schema, () => FixedNowMs);
            var adminAffiliation = new Affiliation(adminId, adminCorpId, 0);
            var created = await store.CreateAsync(adminAffiliation, "Profile access", "Readers can see this summary.");
            var groupId = created.Result.GroupId;
            await store.AddMembersAsync(adminAffiliation, groupId,
                [
                    new NewMember(roleManagerId, MemberKind.Character, MembershipType.Manager),
                    new NewMember(memberId, MemberKind.Character, MembershipType.Member),
                ]);
            var publicGroup = (await store.CreateAsync(adminAffiliation, "Public profile", "Everyone can read this summary.")).Result.GroupId;
            await store.AddMembersAsync(adminAffiliation, publicGroup,
                [new NewMember(0, MemberKind.Everyone, MembershipType.Member)]);

            var pilots = new Dictionary<long, (long CharacterId, long CorporationId)>
            {
                [101] = (adminId, adminCorpId),
                [102] = (roleManagerId, roleManagerCorpId),
                [103] = (memberId, memberCorpId),
                [104] = (unrelatedId, unrelatedCorpId),
            };
            var service = new OwnerGroupRefusals(new OwnerGroupManagerService(
                store,
                _ => ValueTask.FromResult<MemberKind?>(MemberKind.Character),
                sessions: null));
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    var pilot = pilots[session.SessionId];
                    session.Set("charid", new PyInt((int)pilot.CharacterId));
                    session.Set("corpid", new PyInt((int)pilot.CorporationId));
                    session.Set("allianceid", new PyInt(0));
                });

            async Task<(TestFront Front, SessionOpen Open)> OpenPilot(ulong sessionId, uint userId)
            {
                var front = await Handshaken(await ConnectAsync());
                var open = Open(sessionId, userId);
                await front.SendAsync(open);
                Assert.IsType<Send>(await front.ReceiveAsync());
                return (front, open);
            }

            var (adminFront, adminOpen) = await OpenPilot(101, 42);
            var (managerFront, managerOpen) = await OpenPilot(102, 43);
            var (memberFront, memberOpen) = await OpenPilot(103, 44);
            var (unrelatedFront, unrelatedOpen) = await OpenPilot(104, 45);

            var adminGroup = Result(MarshalCodec.Decode(
                (await OwnerGroupCall(adminFront, adminOpen, 1, "GetGroup", new PyLong(groupId))).Payload));
            AssertOwnerGroupSummary(adminGroup, groupId, adminId, "Profile access", "Readers can see this summary.",
                (int)MembershipType.Admin, adminId);

            var adminMany = Assert.IsType<PyList>(Result(MarshalCodec.Decode(
                (await OwnerGroupCall(adminFront, adminOpen, 2, "GetGroupsMany", new PyList([new PyLong(groupId)]))).Payload)));
            Assert.Single(adminMany.Items);
            AssertOwnerGroupSummary(adminMany.Items[0], groupId, adminId, "Profile access", "Readers can see this summary.",
                (int)MembershipType.Admin, adminId);

            var managerGroup = Result(MarshalCodec.Decode(
                (await OwnerGroupCall(managerFront, managerOpen, 1, "GetGroup", new PyLong(groupId))).Payload));
            AssertOwnerGroupSummary(managerGroup, groupId, adminId, "Profile access", "Readers can see this summary.",
                (int)MembershipType.Manager, adminId);
            var managerMembers = Assert.IsType<PyList>(Result(MarshalCodec.Decode(
                (await OwnerGroupCall(managerFront, managerOpen, 2, "GetMembers", new PyLong(groupId))).Payload)));
            Assert.Equal(3, managerMembers.Items.Count);
            AssertOwnerGroupMember(managerMembers.Items.Single(item =>
                OwnerGroupFields(item)["memberID"].Equals(Eve.Services.InventoryWire.Id(roleManagerId))), roleManagerId, (int)MembershipType.Manager);
            AssertOwnerGroupMember(managerMembers.Items.Single(item =>
                OwnerGroupFields(item)["memberID"].Equals(Eve.Services.InventoryWire.Id(memberId))), memberId, (int)MembershipType.Member);
            Assert.NotEmpty(Assert.IsType<PyList>(Result(MarshalCodec.Decode(
                (await OwnerGroupCall(managerFront, managerOpen, 3, "GetGroupLogs", new PyLong(groupId))).Payload))).Items);

            var memberGroup = Result(MarshalCodec.Decode(
                (await OwnerGroupCall(memberFront, memberOpen, 1, "GetGroup", new PyLong(groupId))).Payload));
            AssertOwnerGroupSummary(memberGroup, groupId, adminId, "Profile access", "Readers can see this summary.",
                (int)MembershipType.Member, adminId);

            var profileGroups = Assert.IsType<PyList>(Result(MarshalCodec.Decode(
                (await OwnerGroupCall(memberFront, memberOpen, 2, "GetGroupsMany",
                    new PyList([new PyLong(groupId), new PyLong(groupId + 999)]))).Payload)));
            Assert.Single(profileGroups.Items);
            AssertOwnerGroupSummary(profileGroups.Items[0], groupId, adminId, "Profile access", "Readers can see this summary.",
                (int)MembershipType.Member, adminId);
            Assert.Empty(Assert.IsType<PyList>(Result(MarshalCodec.Decode(
                (await OwnerGroupCall(memberFront, memberOpen, 3, "GetMembers", new PyLong(groupId))).Payload))).Items);
            AssertOwnerGroupDenied(await OwnerGroupCall(memberFront, memberOpen, 4, "GetGroupLogs", new PyLong(groupId)));

            Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(
                (await OwnerGroupCall(unrelatedFront, unrelatedOpen, 1, "GetGroup", new PyLong(groupId))).Payload)));
            var publicLinkGroup = Result(MarshalCodec.Decode(
                (await OwnerGroupCall(unrelatedFront, unrelatedOpen, 2, "GetGroup", new PyLong(publicGroup))).Payload));
            AssertOwnerGroupSummary(publicLinkGroup, publicGroup, adminId, "Public profile", "Everyone can read this summary.",
                (int)MembershipType.Member, adminId);

            Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(
                (await OwnerGroupCall(unrelatedFront, unrelatedOpen, 3, "GetGroup", new PyLong(groupId + 999))).Payload)));
            var unrelatedProfileGroups = Assert.IsType<PyList>(Result(MarshalCodec.Decode(
                (await OwnerGroupCall(unrelatedFront, unrelatedOpen, 4, "GetGroupsMany",
                    new PyList([new PyLong(groupId), new PyLong(publicGroup), new PyLong(groupId + 999)]))).Payload)));
            Assert.Single(unrelatedProfileGroups.Items);
            AssertOwnerGroupSummary(unrelatedProfileGroups.Items[0], publicGroup, adminId, "Public profile",
                "Everyone can read this summary.", (int)MembershipType.Member, adminId);
            Assert.Empty(Assert.IsType<PyList>(Result(MarshalCodec.Decode(
                (await OwnerGroupCall(unrelatedFront, unrelatedOpen, 5, "GetMembers", new PyLong(groupId))).Payload))).Items);
            Assert.Empty(Assert.IsType<PyDict>(Result(MarshalCodec.Decode(
                (await OwnerGroupCall(unrelatedFront, unrelatedOpen, 6, "GetMembersForMultipleGroups",
                    new PyList([new PyLong(groupId)]))).Payload))).Entries);
            AssertOwnerGroupDenied(await OwnerGroupCall(unrelatedFront, unrelatedOpen, 7, "GetGroupLogs", new PyLong(groupId)));

            var nextCall = 8u;
            async Task DeniedWrite(string method, params PyValue[] args) =>
                AssertOwnerGroupDenied(await OwnerGroupCall(unrelatedFront, unrelatedOpen, nextCall++, method, args));

            await DeniedWrite("UpdateName", new PyLong(groupId), new PyWString("Changed"));
            await DeniedWrite("UpdateDescription", new PyLong(groupId), new PyWString("Changed"));
            await DeniedWrite("AddMembers", new PyLong(groupId), new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyLong(unrelatedId), new PyInt((int)MembershipType.Member)),
            ]));
            await DeniedWrite("DeleteMembers", new PyLong(groupId), new PyList([new PyLong(memberId)]));
            await DeniedWrite("UpdateMembershipTypes", new PyLong(groupId), new PyList([new PyLong(memberId)]),
                new PyInt((int)MembershipType.Excluded));
            await DeniedWrite("Delete", new PyLong(groupId));

            var unchanged = Result(MarshalCodec.Decode(
                (await OwnerGroupCall(adminFront, adminOpen, 5, "GetGroup", new PyLong(groupId))).Payload));
            AssertOwnerGroupSummary(unchanged, groupId, adminId, "Profile access", "Readers can see this summary.",
                (int)MembershipType.Admin, adminId);
            var unchangedMembers = Assert.IsType<PyList>(Result(MarshalCodec.Decode(
                (await OwnerGroupCall(adminFront, adminOpen, 6, "GetMembers", new PyLong(groupId))).Payload)));
            Assert.Equal(3, unchangedMembers.Items.Count);
            AssertOwnerGroupMember(unchangedMembers.Items.Single(item =>
                OwnerGroupFields(item)["memberID"].Equals(Eve.Services.InventoryWire.Id(adminId))), adminId, (int)MembershipType.Admin);
            AssertOwnerGroupMember(unchangedMembers.Items.Single(item =>
                OwnerGroupFields(item)["memberID"].Equals(Eve.Services.InventoryWire.Id(roleManagerId))), roleManagerId,
                (int)MembershipType.Manager);
            AssertOwnerGroupMember(unchangedMembers.Items.Single(item =>
                OwnerGroupFields(item)["memberID"].Equals(Eve.Services.InventoryWire.Id(memberId))), memberId, (int)MembershipType.Member);
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Owner_group_manager_can_change_or_remove_own_manager_entry_over_RPC()
    {
        const long adminId = 90000001;
        const long managerId = 90000004;
        const long otherManagerId = 90000005;
        const long memberId = 90000006;
        const long adminCorpId = 98000001;
        const long managerCorpId = 98000004;

        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = Npgsql.NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        var schema = $"ag_self_{Guid.NewGuid():N}"[..20];
        try
        {
            await AccessGroupStore.MigrateAsync(source, schema);
            var store = new AccessGroupStore(source, schema, () => FixedNowMs);
            var adminAffiliation = new Affiliation(adminId, adminCorpId, 0);
            var managerAffiliation = new Affiliation(managerId, managerCorpId, 0);
            var protectedGroup = (await store.CreateAsync(adminAffiliation, "Protected", "")).Result.GroupId;
            await store.AddMembersAsync(adminAffiliation, protectedGroup,
            [
                new NewMember(managerId, MemberKind.Character, MembershipType.Manager),
                new NewMember(otherManagerId, MemberKind.Character, MembershipType.Manager),
                new NewMember(memberId, MemberKind.Character, MembershipType.Member),
            ]);

            async Task<long> CreateManagerGroup(string name)
            {
                var groupId = (await store.CreateAsync(adminAffiliation, name, "")).Result.GroupId;
                await store.AddMembersAsync(adminAffiliation, groupId,
                    [new NewMember(managerId, MemberKind.Character, MembershipType.Manager)]);
                return groupId;
            }

            var demoteGroup = await CreateManagerGroup("Self demotion");
            var blockGroup = await CreateManagerGroup("Self block");
            var removeGroup = await CreateManagerGroup("Self removal");

            var sessions = new CharacterSessions();
            var pilots = new Dictionary<long, (long CharacterId, long CorporationId)>
            {
                [101] = (adminId, adminCorpId),
                [102] = (managerId, managerCorpId),
            };
            Start(
                configure: registry => registry.Register(new OwnerGroupManagerService(
                    store, _ => ValueTask.FromResult<MemberKind?>(MemberKind.Character), sessions)),
                sessionOpened: session =>
                {
                    var pilot = pilots[session.SessionId];
                    session.CharacterId = pilot.CharacterId;
                    session.Set("corpid", new PyInt((int)pilot.CorporationId));
                    session.Set("allianceid", new PyInt(0));
                    sessions.Enter(pilot.CharacterId, session);
                });

            async Task<(TestFront Front, SessionOpen Open)> OpenPilot(ulong sessionId, uint userId)
            {
                var front = await Handshaken(await ConnectAsync());
                var open = Open(sessionId, userId);
                await front.SendAsync(open);
                Assert.IsType<Send>(await front.ReceiveAsync());
                return (front, open);
            }

            var (adminFront, adminOpen) = await OpenPilot(101, 42);
            var (managerFront, managerOpen) = await OpenPilot(102, 43);
            uint callId = 1;

            async Task DeniedEntry(string method, long targetId, MembershipType? targetType = null)
            {
                var beforeMembers = await store.MembersAsync(adminAffiliation, protectedGroup);
                var beforeLogs = await store.LogsAsync(adminAffiliation, protectedGroup);
                var reply = targetType is { } type
                    ? await OwnerGroupCall(managerFront, managerOpen, callId++, method,
                        new PyLong(protectedGroup), new PyList([new PyLong(targetId)]), new PyInt((int)type))
                    : await OwnerGroupCall(managerFront, managerOpen, callId++, method,
                        new PyLong(protectedGroup), new PyList([new PyLong(targetId)]));
                AssertOwnerGroupMemberFailure(reply, targetId, "NotAllowed");
                Assert.Equal(beforeMembers, await store.MembersAsync(adminAffiliation, protectedGroup));
                Assert.Equal(beforeLogs, await store.LogsAsync(adminAffiliation, protectedGroup));
            }

            await DeniedEntry("UpdateMembershipTypes", otherManagerId, MembershipType.Member);
            await DeniedEntry("UpdateMembershipTypes", adminId, MembershipType.Excluded);
            await DeniedEntry("DeleteMembers", otherManagerId);
            await DeniedEntry("DeleteMembers", adminId);
            await DeniedEntry("UpdateMembershipTypes", managerId, MembershipType.Admin);

            async Task AssertSuccessAndNotifications(long groupId, int actionId, int? newType)
            {
                var beforeLogs = await store.LogsAsync(adminAffiliation, groupId);
                Send reply;
                if (newType is { } type)
                    reply = await OwnerGroupCall(managerFront, managerOpen, callId++, "UpdateMembershipTypes",
                        new PyLong(groupId), new PyList([new PyLong(managerId)]), new PyInt(type));
                else
                    reply = await OwnerGroupCall(managerFront, managerOpen, callId++, "DeleteMembers",
                        new PyLong(groupId), new PyList([new PyLong(managerId)]));

                AssertOwnerGroupSuccess(reply);
                var codes = actionId == AccessGroupChanges.MemberRemoved
                    ? new[] { AccessGroupChanges.MemberRemoved, AccessGroupChanges.ChangedForListener }
                    : new[] { AccessGroupChanges.MemberTypeChanged, AccessGroupChanges.ChangedForListener };
                AssertOwnerGroupNotification(Assert.IsType<Send>(await managerFront.ReceiveAsync()), groupId, codes);
                AssertOwnerGroupNotification(Assert.IsType<Send>(await adminFront.ReceiveAsync()), groupId, codes);

                var members = await store.MembersAsync(adminAffiliation, groupId);
                if (newType is { } savedType)
                    Assert.Contains(members, member => member.MemberId == managerId && (int)member.Type == savedType);
                else
                    Assert.DoesNotContain(members, member => member.MemberId == managerId);

                var logs = await store.LogsAsync(adminAffiliation, groupId);
                Assert.Equal(beforeLogs.Count + 1, logs.Count);
                var log = Assert.Single(logs, entry => entry.CharacterId == managerId);
                Assert.Equal(actionId, log.ActionId);
                Assert.Equal(managerId, log.OwnerId);
                Assert.Equal((int?)MembershipType.Manager, log.OldType);
                Assert.Equal<int?>(newType ?? -1, log.NewType);
            }

            await AssertSuccessAndNotifications(demoteGroup, AccessGroupChanges.MemberTypeChanged, (int)MembershipType.Member);
            await AssertSuccessAndNotifications(blockGroup, AccessGroupChanges.MemberTypeChanged, (int)MembershipType.Excluded);
            await AssertSuccessAndNotifications(removeGroup, AccessGroupChanges.MemberRemoved, null);

            var removedSummary = Result(MarshalCodec.Decode(
                (await OwnerGroupCall(managerFront, managerOpen, callId, "GetGroup", new PyLong(removeGroup))).Payload));
            Assert.Same(PyNone.Instance, removedSummary);
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static void AssertOwnerGroupSuccess(Send reply)
    {
        Assert.Equal(MachoMessage.CallRsp,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(reply.Payload))).Type);
        Assert.Empty(Assert.IsType<PyList>(Result(MarshalCodec.Decode(reply.Payload))).Items);
    }

    private static void AssertOwnerGroupMemberFailure(Send reply, long memberId, string reason)
    {
        Assert.Equal(MachoMessage.CallRsp,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(reply.Payload))).Type);
        var failure = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(Result(MarshalCodec.Decode(reply.Payload))).Items));
        Assert.Equal(Eve.Services.InventoryWire.Id(memberId), failure.Items[0]);
        Assert.Equal(new PyBytes(Encoding.UTF8.GetBytes(reason)), failure.Items[1]);
    }

    private static void AssertOwnerGroupNotification(Send send, long groupId, IReadOnlyList<int> codes)
    {
        var packet = Assert.IsType<PyObject>(MarshalCodec.Decode(send.Payload));
        var state = Assert.IsType<PyTuple>(packet.Args).Items;
        Assert.Equal(new PyInt(MachoMessage.Notification), state[0]);
        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(state[2]).Args).Items;
        Assert.Equal("OnOwnerGroupsUpdated", T(address[1]));
        Assert.Equal("clientID", T(address[3]));
        var body = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(state[4]).Items[0]).Items[1]);
        var inner = Assert.IsType<PyTuple>(MarshalCodec.Decode(body.Value.ToArray()));
        var args = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(inner.Items[1]).Items[1]).Items;
        var changedGroups = Assert.IsType<PyDict>(Assert.Single(args));
        var changed = Assert.Single(changedGroups.Entries);
        Assert.Equal(Eve.Services.InventoryWire.Id(groupId), changed.Key);
        Assert.Equal(codes.Select(code => (PyValue)new PyInt(code)), Assert.IsType<PyList>(changed.Value).Items);
    }

    private static async Task<Send> OwnerGroupCall(TestFront front, SessionOpen open, uint callId, string method,
        params PyValue[] args)
    {
        await front.SendAsync(new Packet(open.SessionId,
            MarshalCodec.Encode(Packets.Call(open, callId, "ownerGroupManager", method, args))));
        return Assert.IsType<Send>(await front.ReceiveAsync());
    }

    private static void AssertOwnerGroupSummary(PyValue? value, long groupId, long creatorId, string name,
        string description, int membershipType, params long[] admins)
    {
        var entries = Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries;
        Assert.Equal(new[] { "groupID", "creatorID", "name", "description", "membershipType", "admins" },
            entries.Select(entry => T(entry.Key)));
        Assert.Equal(6, entries.Count);
        var fields = entries.ToDictionary(entry => T(entry.Key)!, entry => entry.Value);
        Assert.Equal(Eve.Services.InventoryWire.Id(groupId), fields["groupID"]);
        Assert.Equal(Eve.Services.InventoryWire.Id(creatorId), fields["creatorID"]);
        Assert.Equal(name, Assert.IsType<PyWString>(fields["name"]).Value);
        Assert.Equal(description, Assert.IsType<PyWString>(fields["description"]).Value);
        Assert.Equal(new PyInt(membershipType), fields["membershipType"]);
        Assert.Equal(admins.Select(Eve.Services.InventoryWire.Id), Assert.IsType<PyList>(fields["admins"]).Items);
    }

    private static void AssertOwnerGroupMember(PyValue value, long memberId, int membershipType)
    {
        var entries = Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries;
        Assert.Equal(new[] { "memberID", "membershipType" }, entries.Select(entry => T(entry.Key)));
        Assert.Equal(Eve.Services.InventoryWire.Id(memberId), OwnerGroupFields(value)["memberID"]);
        Assert.Equal(new PyInt(membershipType), OwnerGroupFields(value)["membershipType"]);
    }

    private static Dictionary<string, PyValue> OwnerGroupFields(PyValue value) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries
            .ToDictionary(entry => T(entry.Key)!, entry => entry.Value);

    private static void AssertOwnerGroupDenied(Send reply)
    {
        AssertWrappedError(reply, "eveexceptions.UserError", "CustomNotify");
        Assert.Contains("You do not have permission to manage this access group.",
            Encoding.ASCII.GetString(reply.Payload.Span));
    }
}
