using System.Text;
using Eve.Marshal;
using Eve.Services;
using Eve.Host.Xmpp;
using Xunit;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class XmppOrganizationRoomTests
{
    [Fact]
    public async Task Corporation_motd_is_authorized_broadcast_and_returned_on_join()
    {
        var sessions = new ConnectedSessions();
        var directorSession = Character(42, "director-token", 100, 200, 300);
        directorSession.Set("corprole", new PyLong(Eve.Corporations.CorporationRoles.Director));
        var memberSession = Character(77, "member-token", 100, 201, 301);
        var laterSession = Character(88, "later-token", 100, 200, 302);
        laterSession.Set("corprole", new PyLong(Eve.Corporations.CorporationRoles.Director));
        sessions.Add(directorSession);
        sessions.Add(memberSession);
        sessions.Add(laterSession);
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"));
        var directorOutput = new List<string>();
        var memberOutput = new List<string>();
        var laterOutput = new List<string>();
        var director = Connect(hub, 42, "director-token", directorOutput);
        var member = Connect(hub, 77, "member-token", memberOutput);
        director.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Director'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        member.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Member'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));

        director.OnStanza(Parse("<message type='groupchat' to='corp_100@conference.chat.example'><subject>Ops schedule</subject></message>"));
        Assert.Contains(memberOutput, item => item.Contains("<subject>Ops schedule</subject>", StringComparison.Ordinal));
        member.OnStanza(Parse("<message type='groupchat' to='corp_100@conference.chat.example'><subject>Denied notice</subject></message>"));
        Assert.Contains(memberOutput, item => item.Contains("not-allowed", StringComparison.Ordinal));
        Assert.DoesNotContain(directorOutput, item => item.Contains("<subject>Denied notice</subject>", StringComparison.Ordinal));

        var later = Connect(hub, 88, "later-token", laterOutput);
        later.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Director'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        director.OnStanza(Parse("<presence to='alliance_200@conference.chat.example/Director'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        later.OnStanza(Parse("<presence to='alliance_200@conference.chat.example/Later'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        Assert.Contains(laterOutput, item => item.Contains("<subject>Ops schedule</subject>", StringComparison.Ordinal));

        var memberService = new XmppChatMgrService(hub);
        var denied = await memberService.SetMotd(new ServiceCall { Method = "SetMotd", Args = [new PyStr("corp_100@conference.chat.example"), new PyStr("Denied")], Session = memberSession });
        Assert.Same(PyBool.False, denied);
        var directorService = new XmppChatMgrService(hub);
        var accepted = await directorService.SetMotd(new ServiceCall { Method = "SetMotd", Args = [new PyStr("corp_100@conference.chat.example"), new PyStr("New notice")], Session = directorSession });
        Assert.Same(PyBool.True, accepted);
        Assert.Contains(memberOutput, item => item.Contains("<subject>New notice</subject>", StringComparison.Ordinal));

        var allianceDenied = await memberService.SetMotd(new ServiceCall { Method = "SetMotd", Args = [new PyStr("alliance_201@conference.chat.example"), new PyStr("Denied")], Session = memberSession });
        Assert.Same(PyBool.False, allianceDenied);
        var allianceAccepted = await directorService.SetMotd(new ServiceCall { Method = "SetMotd", Args = [new PyStr("alliance_200@conference.chat.example"), new PyStr("Alliance notice")], Session = directorSession });
        Assert.Same(PyBool.True, allianceAccepted);
        Assert.Contains(directorOutput, item => item.Contains("<subject>Alliance notice</subject>", StringComparison.Ordinal));
        Assert.Contains(laterOutput, item => item.Contains("<subject>Alliance notice</subject>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Player_room_owner_iq_configures_persists_and_destroys_with_member_presence()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is required for the MUC owner persistence regression");
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        var schema = "xmpp_owner_iq_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var store = await XmppPlayerChannelStore.OpenAsync(source, schema);
            var channel = Assert.IsType<XmppPlayerChannel>(await store.CreateAsync(42, "Pilot room"));
            Assert.True(await store.InviteAsync(channel.RoomName, 77));
            var sessions = new ConnectedSessions();
            var ownerSession = Character(42, "owner-token", 100, 200, 300);
            var memberSession = Character(77, "member-token", 100, 200, 301);
            var guestSession = Character(88, "guest-token", 100, 200, 302);
            sessions.Add(ownerSession);
            sessions.Add(memberSession);
            sessions.Add(guestSession);
            var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"), motdStore: null, playerChannelStore: store);
            var ownerOutput = new List<string>();
            var memberOutput = new List<string>();
            var guestOutput = new List<string>();
            var owner = Connect(hub, 42, "owner-token", ownerOutput);
            var member = Connect(hub, 77, "member-token", memberOutput);
            var guest = Connect(hub, 88, "guest-token", guestOutput);
            owner.OnStanza(Parse($"<presence to='{channel.RoomName}@conference.chat.example/Owner'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
            member.OnStanza(Parse($"<presence to='{channel.RoomName}@conference.chat.example/Member'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));

            await member.OnStanzaAsync(Parse($"<iq type='set' id='member-destroy' to='{channel.RoomName}@conference.chat.example'><query xmlns='http://jabber.org/protocol/muc#owner'><destroy/></query></iq>"), default);
            Assert.Contains(memberOutput, item => item.Contains("id='member-destroy'", StringComparison.Ordinal) && item.Contains("not-allowed", StringComparison.Ordinal));
            Assert.NotNull(store.Get(channel.RoomName));

            owner.OnStanza(Parse($"<iq type='get' id='config' to='{channel.RoomName}@conference.chat.example'><query xmlns='http://jabber.org/protocol/muc#owner'/></iq>"));
            Assert.Contains(ownerOutput, item => item.Contains("id='config'", StringComparison.Ordinal)
                && item.Contains("muc#roomconfig_roomname", StringComparison.Ordinal)
                && item.Contains("members_by_default", StringComparison.Ordinal));

            await owner.OnStanzaAsync(Parse($"<iq type='set' id='configure' to='{channel.RoomName}@conference.chat.example'><query xmlns='http://jabber.org/protocol/muc#owner'><x xmlns='jabber:x:data' type='submit'><field var='FORM_TYPE'><value>http://jabber.org/protocol/muc#roomconfig</value></field><field var='muc#roomconfig_roomname'><value>Saved room</value></field><field var='muc#roomconfig_roomdesc'><value>Room description</value></field><field var='muc#roomconfig_passwordprotectedroom'><value>1</value></field><field var='muc#roomconfig_roomsecret'><value>opensesame</value></field><field var='muc#roomconfig_membersonly'><value>0</value></field><field var='members_by_default'><value>0</value></field></x></query></iq>"), default);
            Assert.Contains(ownerOutput, item => item.Contains("type='result' id='configure'", StringComparison.Ordinal));
            Assert.Contains(memberOutput, item => item.Contains("role='participant'", StringComparison.Ordinal));
            Assert.Equal("Saved room", store.Get(channel.RoomName)?.DisplayName);
            Assert.Equal("Room description", store.Get(channel.RoomName)?.Description);
            Assert.True(store.Get(channel.RoomName)?.PasswordRequired);

            guest.OnStanza(Parse($"<presence to='{channel.RoomName}@conference.chat.example/Guest'><x xmlns='http://jabber.org/protocol/muc'><password>wrong</password></x></presence>"));
            Assert.Contains(guestOutput, item => item.Contains("not-authorized", StringComparison.Ordinal));
            guest.OnStanza(Parse($"<presence to='{channel.RoomName}@conference.chat.example/Guest'><x xmlns='http://jabber.org/protocol/muc'><password>opensesame</password></x></presence>"));
            Assert.Contains(ownerOutput, item => item.Contains("role='visitor'", StringComparison.Ordinal));
            await guest.OnStanzaAsync(Parse($"<message type='groupchat' to='{channel.RoomName}@conference.chat.example'><body>visitor cannot speak</body></message>"), default);
            Assert.DoesNotContain(ownerOutput, item => item.Contains("visitor cannot speak", StringComparison.Ordinal));
            await member.OnStanzaAsync(Parse($"<message type='groupchat' to='{channel.RoomName}@conference.chat.example'><body>member can speak</body></message>"), default);
            Assert.Contains(ownerOutput, item => item.Contains("member can speak", StringComparison.Ordinal));

            var reopened = await XmppPlayerChannelStore.OpenAsync(source, schema);
            var persisted = reopened.Get(channel.RoomName);
            Assert.Equal("Saved room", persisted?.DisplayName);
            Assert.Equal("Room description", persisted?.Description);
            Assert.Equal("opensesame", persisted?.Password);
            Assert.False(persisted?.MembersOnly);
            Assert.False(persisted?.MembersByDefault);

            await owner.OnStanzaAsync(Parse($"<iq type='set' id='destroy' to='{channel.RoomName}@conference.chat.example'><query xmlns='http://jabber.org/protocol/muc#owner'><destroy/></query></iq>"), default);
            Assert.Contains(ownerOutput, item => item.Contains("type='result' id='destroy'", StringComparison.Ordinal));
            Assert.Contains(memberOutput, item => item.Contains("type='unavailable'", StringComparison.Ordinal));
            Assert.Contains(guestOutput, item => item.Contains("type='unavailable'", StringComparison.Ordinal));
            Assert.Null((await XmppPlayerChannelStore.OpenAsync(source, schema)).Get(channel.RoomName));
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Corporation_motd_survives_store_reopen_and_is_sent_on_join()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is required for the MOTD persistence regression");
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        var schema = "xmpp_motd_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var firstStore = await XmppMotdStore.OpenAsync(source, schema);
            var sessions = new ConnectedSessions();
            var directorSession = Character(42, "director-token", 100, 200, 300);
            directorSession.Set("corprole", new PyLong(Eve.Corporations.CorporationRoles.Director));
            sessions.Add(directorSession);
            var firstHub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"), motdStore: firstStore);
            var service = new XmppChatMgrService(firstHub);
            var result = await service.SetMotd(new ServiceCall
            {
                Method = "SetMotd",
                Args = [new PyStr("corp_100@conference.chat.example"), new PyStr("Persisted notice")],
                Session = directorSession,
            });
            Assert.Same(PyBool.True, result);
            var allianceResult = await service.SetMotd(new ServiceCall
            {
                Method = "SetMotd",
                Args = [new PyStr("alliance_200@conference.chat.example"), new PyStr("Persisted alliance notice")],
                Session = directorSession,
            });
            Assert.Same(PyBool.True, allianceResult);

            var firstOutput = new List<string>();
            var firstClient = Connect(firstHub, 42, "director-token", firstOutput);
            firstClient.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Director'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
            firstClient.OnStanza(Parse("<presence to='alliance_200@conference.chat.example/Director'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
            await firstClient.OnStanzaAsync(Parse("<message to='corp_100@conference.chat.example/Director' type='groupchat'><subject>Subject notice</subject></message>"), default);
            Assert.Contains(firstOutput, item => item.Contains("<subject>Subject notice</subject>", StringComparison.Ordinal));
            Assert.Contains(firstOutput, item => item.Contains("<subject>Persisted alliance notice</subject>", StringComparison.Ordinal));

            var restartedStore = await XmppMotdStore.OpenAsync(source, schema);
            var restartedHub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"), motdStore: restartedStore);
            var output = new List<string>();
            var client = Connect(restartedHub, 42, "director-token", output);
            client.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Director'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
            client.OnStanza(Parse("<presence to='alliance_200@conference.chat.example/Director'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));

            Assert.Contains(output, item => item.Contains("<subject>Subject notice</subject>", StringComparison.Ordinal));
            Assert.Contains(output, item => item.Contains("<subject>Persisted alliance notice</subject>", StringComparison.Ordinal));
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Player_channel_rpc_creates_invites_and_transfers_ownership_across_store_reopen()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is required for the player channel persistence regression");
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        var schema = "xmpp_player_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var store = await XmppPlayerChannelStore.OpenAsync(source, schema);
            var sessions = new ConnectedSessions();
            var creatorSession = Character(42, "creator-token", 100, 200, 300);
            var staffSession = Character(43, "staff-token", 100, 200, 300);
            staffSession.Set("accountRole", new PyLong(Eve.Services.GameMasterRoles.ChatAdministrator));
            var invitedSession = Character(77, "invited-token", 100, 200, 301);
            sessions.Add(creatorSession);
            sessions.Add(staffSession);
            sessions.Add(invitedSession);
            var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"),
                motdStore: null, readBlockedOwners: null, playerChannelStore: store);
            var service = new XmppChatMgrService(hub);
            var created = await service.CreatePlayerOwnedChannel(new ServiceCall
            {
                Method = "CreatePlayerOwnedChannel",
                Args = [new PyStr("Pilot room")],
                Session = creatorSession,
            });
            var room = Assert.IsType<PyStr>(created).Value;
            Assert.StartsWith("player_", room, StringComparison.Ordinal);
            var invalidName = await service.CreatePlayerOwnedChannel(new ServiceCall
            {
                Method = "CreatePlayerOwnedChannel",
                Args = [new PyStr(new string('x', XmppPlayerChannelStore.MaxDisplayNameBytes + 1))],
                Session = creatorSession,
            });
            Assert.Same(PyNone.Instance, invalidName);

            var staffOutput = new List<string>();
            var invitedOutput = new List<string>();
            var staff = Connect(hub, 43, "staff-token", staffOutput);
            var invited = Connect(hub, 77, "invited-token", invitedOutput);
            Assert.Same(PyBool.True, await service.GrantChannelOwnership(new ServiceCall
            {
                Method = "GrantChannelOwnership",
                Args = [new PyStr(room)],
                Session = staffSession,
            }));
            Assert.Same(PyBool.True, await service.AddCharacterToChannel(new ServiceCall
            {
                Method = "AddCharacterToChannel",
                Args = [new PyStr(room), new PyLong(77), new PyStr("Welcome")],
                Session = staffSession,
            }));
            Assert.Same(PyBool.False, await service.AddCharacterToChannel(new ServiceCall
            {
                Method = "AddCharacterToChannel",
                Args = [new PyStr(room), new PyLong(99), new PyStr("Denied")],
                Session = creatorSession,
            }));
            Assert.Same(PyBool.False, await service.GrantChannelOwnership(new ServiceCall
            {
                Method = "GrantChannelOwnership",
                Args = [new PyStr(room)],
                Session = creatorSession,
            }));
            Assert.Contains(invitedOutput, item => item.Contains("<invite", StringComparison.Ordinal)
                && item.Contains("Welcome", StringComparison.Ordinal));

            staff.OnStanza(Parse($"<presence to='{room}@conference.chat.example/Staff'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
            invited.OnStanza(Parse($"<presence to='{room}@conference.chat.example/Invited'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
            await staff.OnStanzaAsync(Parse($"<message type='groupchat' to='{room}@conference.chat.example'><body>Joined room</body></message>"), default);
            Assert.Contains(invitedOutput, item => item.Contains("<body>Joined room</body>", StringComparison.Ordinal));

            var reopened = await XmppPlayerChannelStore.OpenAsync(source, schema);
            Assert.True(reopened.CanJoin(room, 43));
            Assert.True(reopened.CanJoin(room, 77));
            Assert.False(reopened.CanJoin(room, 99));
            Assert.Equal("Pilot room", reopened.Get(room)?.DisplayName);
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Player_room_owner_affiliation_transfers_through_muc_admin_and_persists()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is required for the player channel persistence regression");
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        var schema = "xmpp_owner_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var store = await XmppPlayerChannelStore.OpenAsync(source, schema);
            var ownerSession = Character(42, "owner-token", 100, 200, 300);
            var memberSession = Character(77, "member-token", 100, 200, 301);
            var sessions = new ConnectedSessions();
            sessions.Add(ownerSession);
            sessions.Add(memberSession);
            var channel = Assert.IsType<XmppPlayerChannel>(await store.CreateAsync(42, "Pilot room"));
            Assert.True(await store.InviteAsync(channel.RoomName, 77));
            var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"),
                motdStore: null, readBlockedOwners: null, playerChannelStore: store);
            var ownerOutput = new List<string>();
            var memberOutput = new List<string>();
            var owner = Connect(hub, 42, "owner-token", ownerOutput);
            var member = Connect(hub, 77, "member-token", memberOutput);
            owner.OnStanza(Parse($"<presence to='{channel.RoomName}@conference.chat.example/Owner'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
            member.OnStanza(Parse($"<presence to='{channel.RoomName}@conference.chat.example/Member'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));

            await owner.OnStanzaAsync(Parse($"<iq type='set' id='bad-jid' to='{channel.RoomName}@conference.chat.example'><query xmlns='http://jabber.org/protocol/muc#admin'><item jid='77@elsewhere.example' affiliation='owner'/></query></iq>"), default);
            Assert.Contains(ownerOutput, item => item.Contains("id='bad-jid'", StringComparison.Ordinal)
                && item.Contains("not-allowed", StringComparison.Ordinal));
            await member.OnStanzaAsync(Parse($"<iq type='set' id='denied' to='{channel.RoomName}@conference.chat.example'><query xmlns='http://jabber.org/protocol/muc#admin'><item jid='77@chat.example' affiliation='owner'/></query></iq>"), default);
            Assert.Contains(memberOutput, item => item.Contains("id='denied'", StringComparison.Ordinal)
                && item.Contains("not-allowed", StringComparison.Ordinal));
            Assert.Equal(42, store.Get(channel.RoomName)?.OwnerCharacterId);

            await owner.OnStanzaAsync(Parse($"<iq type='set' id='transfer' to='{channel.RoomName}@conference.chat.example'><query xmlns='http://jabber.org/protocol/muc#admin'><item jid='77@chat.example' affiliation='owner'><reason>character</reason></item></query></iq>"), default);
            Assert.Contains(ownerOutput, item => item.Contains("type='result' id='transfer'", StringComparison.Ordinal));
            Assert.Contains(memberOutput, item => item.Contains("affiliation='owner'", StringComparison.Ordinal));
            Assert.Contains(ownerOutput, item => item.Contains("type='unavailable'", StringComparison.Ordinal));
            member.OnStanza(Parse($"<iq type='get' id='owners' to='{channel.RoomName}@conference.chat.example'><query xmlns='http://jabber.org/protocol/muc#admin'><item affiliation='owner'/></query></iq>"));
            Assert.Contains(memberOutput, item => item.Contains("id='owners'", StringComparison.Ordinal)
                && item.Contains("jid='77@chat.example'", StringComparison.Ordinal));

            var reopened = await XmppPlayerChannelStore.OpenAsync(source, schema);
            Assert.Equal(77, reopened.Get(channel.RoomName)?.OwnerCharacterId);
            Assert.False(reopened.CanJoin(channel.RoomName, 42));
            Assert.True(reopened.CanJoin(channel.RoomName, 77));
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void Groupchat_delivery_respects_the_persisted_block_list_of_each_recipient()
    {
        var sessions = new ConnectedSessions();
        sessions.Add(Character(42, "sender-token", 100, 200, 300));
        sessions.Add(Character(77, "blocked-token", 100, 200, 301));
        sessions.Add(Character(88, "allowed-token", 100, 200, 302));
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"),
            (characterId, _) => ValueTask.FromResult<IReadOnlyList<long>>(characterId == 77 ? [42] : []));
        var senderOutput = new List<string>();
        var blockedOutput = new List<string>();
        var allowedOutput = new List<string>();
        var sender = Connect(hub, 42, "sender-token", senderOutput);
        var blocked = Connect(hub, 77, "blocked-token", blockedOutput);
        var allowed = Connect(hub, 88, "allowed-token", allowedOutput);
        sender.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Sender'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        blocked.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Blocked'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        allowed.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Allowed'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));

        sender.OnStanza(Parse("<message type='groupchat' to='corp_100@conference.chat.example'><body>message to members</body></message>"));

        Assert.DoesNotContain(blockedOutput, item => item.Contains("<body>message to members</body>", StringComparison.Ordinal));
        Assert.Contains(allowedOutput, item => item.Contains("<body>message to members</body>", StringComparison.Ordinal));
    }

    [Fact]
    public void Groupchat_rejects_oversized_bodies_and_enforces_the_per_character_cadence()
    {
        var sessions = new ConnectedSessions();
        sessions.Add(Character(42, "sender-token", 100, 200, 300));
        sessions.Add(Character(77, "recipient-token", 100, 200, 301));
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"));
        var senderOutput = new List<string>();
        var recipientOutput = new List<string>();
        var sender = Connect(hub, 42, "sender-token", senderOutput);
        var recipient = Connect(hub, 77, "recipient-token", recipientOutput);
        sender.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Sender'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        recipient.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Recipient'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));

        var oversized = new string('x', 2049);
        sender.OnStanza(Parse($"<message type='groupchat' to='corp_100@conference.chat.example'><body>{oversized}</body></message>"));
        Assert.Contains(senderOutput, item => item.Contains("resource-constraint", StringComparison.Ordinal));
        Assert.DoesNotContain(recipientOutput, item => item.Contains(oversized, StringComparison.Ordinal));

        sender.OnStanza(Parse("<message type='groupchat' to='corp_100@conference.chat.example'><body>first</body></message>"));
        sender.OnStanza(Parse("<message type='groupchat' to='corp_100@conference.chat.example'><body>too soon</body></message>"));
        Assert.Contains(recipientOutput, item => item.Contains("<body>first</body>", StringComparison.Ordinal));
        Assert.DoesNotContain(recipientOutput, item => item.Contains("<body>too soon</body>", StringComparison.Ordinal));
        Assert.Contains(senderOutput, item => item.Contains("resource-constraint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Corporation_alliance_and_fleet_rooms_follow_live_session_membership()
    {
        var sessions = new ConnectedSessions();
        var firstSession = Character(42, "first-token", 100, 200, 300);
        var secondSession = Character(77, "second-token", 100, 201, 301);
        var thirdSession = Character(78, "third-token", 101, 200, 302);
        sessions.Add(firstSession);
        sessions.Add(secondSession);
        sessions.Add(thirdSession);
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"));
        var firstOutput = new List<string>();
        var secondOutput = new List<string>();
        var thirdOutput = new List<string>();
        var first = Connect(hub, 42, "first-token", firstOutput);
        var second = Connect(hub, 77, "second-token", secondOutput);
        var third = Connect(hub, 78, "third-token", thirdOutput);

        first.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Alpha'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        second.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Beta'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        third.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Gamma'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        Assert.Contains(thirdOutput, item => item.Contains("not-authorized", StringComparison.Ordinal));

        first.OnStanza(Parse("<presence to='alliance_200@conference.chat.example/Alpha'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        third.OnStanza(Parse("<presence to='alliance_200@conference.chat.example/Gamma'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        first.OnStanza(Parse("<presence to='fleet_300@conference.chat.example/Alpha'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        third.OnStanza(Parse("<presence to='fleet_300@conference.chat.example/Gamma'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        Assert.Contains(thirdOutput, item => item.Contains("not-authorized", StringComparison.Ordinal));

        first.OnStanza(Parse("<message type='groupchat' to='corp_100@conference.chat.example'><body>corp</body></message>"));
        Assert.Contains(secondOutput, item => item.Contains("<body>corp</body>", StringComparison.Ordinal));
        await Task.Delay(TimeSpan.FromMilliseconds(800));
        first.OnStanza(Parse("<message type='groupchat' to='alliance_200@conference.chat.example'><body>alliance</body></message>"));
        Assert.Contains(thirdOutput, item => item.Contains("<body>alliance</body>", StringComparison.Ordinal));

        firstSession.Set("corpid", new PyLong(101));
        firstSession.Set("allianceid", new PyLong(201));
        firstSession.Set("fleetid", new PyLong(301));
        var service = new XmppChatMgrService(hub);
        var call = new ServiceCall { Method = "ResyncSystemChannelAccess", Args = [], Session = firstSession };
        service.ResyncSystemChannelAccess(call);
        Assert.Contains(secondOutput, item => item.Contains("type='unavailable'", StringComparison.Ordinal)
            && item.Contains("corp_100@conference.chat.example/Alpha", StringComparison.Ordinal));
        Assert.Contains(thirdOutput, item => item.Contains("type='unavailable'", StringComparison.Ordinal)
            && item.Contains("alliance_200@conference.chat.example/Alpha", StringComparison.Ordinal));
        first.OnStanza(Parse("<message type='groupchat' to='corp_100@conference.chat.example'><body>old corp</body></message>"));
        Assert.DoesNotContain(secondOutput, item => item.Contains("<body>old corp</body>", StringComparison.Ordinal));
    }

    private static ClientSession Character(long characterId, string token, long corporationId, long allianceId, long fleetId)
    {
        var session = new ClientSession { CharacterId = characterId };
        session.Set("chatAuthenticationToken", new PyStr(token));
        session.Set("solarsystemid2", new PyLong(30_000_001));
        session.Set("corpid", new PyLong(corporationId));
        session.Set("allianceid", new PyLong(allianceId));
        session.Set("fleetid", new PyLong(fleetId));
        return session;
    }

    private static XmppClientSession Connect(XmppChatHub hub, long characterId, string token, List<string> output)
    {
        var client = new XmppClientSession(hub, output.Add, _ => { });
        client.OnStanza(Auth(characterId, token));
        client.OnStanza(Bind($"client-{characterId}"));
        return client;
    }

    private static XmppElement Auth(long characterId, string token)
    {
        var auth = new XmppElement("auth");
        auth.Attributes["mechanism"] = "PLAIN";
        auth.AppendText(Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{characterId}\0{token}")));
        return auth;
    }

    private static XmppElement Bind(string resource)
    {
        var iq = new XmppElement("iq");
        iq.Attributes["id"] = "bind";
        iq.Attributes["type"] = "set";
        var bind = new XmppElement("bind");
        var resourceElement = new XmppElement("resource");
        resourceElement.AppendText(resource);
        bind.Children.Add(resourceElement);
        iq.Children.Add(bind);
        return iq;
    }

    private static XmppElement Parse(string stanza)
    {
        var parser = new XmppStreamParser();
        parser.Feed(Encoding.UTF8.GetBytes("<stream:stream xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams'>"));
        return Assert.IsType<XmppStanza>(parser.Feed(Encoding.UTF8.GetBytes(stanza)).Single()).Element;
    }
}
