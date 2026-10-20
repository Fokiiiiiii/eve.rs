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
        Assert.Contains(laterOutput, item => item.Contains("<subject>Ops schedule</subject>", StringComparison.Ordinal));

        var memberService = new XmppChatMgrService(hub);
        var denied = await memberService.SetMotd(new ServiceCall { Method = "SetMotd", Args = [new PyStr("corp_100"), new PyStr("Denied")], Session = memberSession });
        Assert.Same(PyBool.False, denied);
        var directorService = new XmppChatMgrService(hub);
        var accepted = await directorService.SetMotd(new ServiceCall { Method = "SetMotd", Args = [new PyStr("corp_100"), new PyStr("New notice")], Session = directorSession });
        Assert.Same(PyBool.True, accepted);
        Assert.Contains(memberOutput, item => item.Contains("<subject>New notice</subject>", StringComparison.Ordinal));
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
                Args = [new PyStr("corp_100"), new PyStr("Persisted notice")],
                Session = directorSession,
            });
            Assert.Same(PyBool.True, result);

            var restartedStore = await XmppMotdStore.OpenAsync(source, schema);
            var restartedHub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"), motdStore: restartedStore);
            var output = new List<string>();
            var client = Connect(restartedHub, 42, "director-token", output);
            client.OnStanza(Parse("<presence to='corp_100@conference.chat.example/Director'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));

            Assert.Contains(output, item => item.Contains("<subject>Persisted notice</subject>", StringComparison.Ordinal));
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
