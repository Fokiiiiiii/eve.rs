using System.Text;
using Eve.Marshal;
using Eve.Services;
using Eve.Host.Xmpp;
using Xunit;

namespace Eve.Host.Tests;

public sealed class XmppLocalRoomTests
{
[Fact]
    public void Public_help_rooms_accept_group_messages()
    {
        var sessions = new ConnectedSessions();
        sessions.Add(Character(42, "pilot-token", 30_000_142));
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"));
        var output = new List<string>();
        var client = Connect(hub, 42, "pilot-token", output);
        const string room = "system_263238_263262";

        client.OnStanza(Parse($"<presence to='{room}@conference.chat.example/Pilot'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        client.OnStanza(Parse($"<message type='groupchat' to='{room}@conference.chat.example'><body>help room check</body></message>"));

        Assert.Contains(output, item => item.Contains("help room check", StringComparison.Ordinal));
    }

    [Fact]
    public void Active_characters_in_one_system_discover_join_and_exchange_local_messages()
    {
        const long systemId = 30_000_142;
        var sessions = new ConnectedSessions();
        var firstSession = Character(42, "first-token", systemId);
        var secondSession = Character(43, "second-token", systemId);
        sessions.Add(firstSession);
        sessions.Add(secondSession);
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"));
        var firstOutput = new List<string>();
        var secondOutput = new List<string>();
        var first = Connect(hub, 42, "first-token", firstOutput);
        var second = Connect(hub, 43, "second-token", secondOutput);

        first.OnStanza(Parse("<iq id='items' type='get' to='conference.chat.example'><query xmlns='http://jabber.org/protocol/disco#items'/></iq>"));
        Assert.Contains(firstOutput, item => item.Contains("local_30000142@conference.chat.example", StringComparison.Ordinal));

        first.OnStanza(Parse("<presence to='local_30000142@conference.chat.example/Alpha'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        second.OnStanza(Parse("<presence to='local_30000142@conference.chat.example/Beta'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        Assert.Contains(secondOutput, item => item.Contains("local_30000142@conference.chat.example/Alpha", StringComparison.Ordinal));
        Assert.Contains(firstOutput, item => item.Contains("local_30000142@conference.chat.example/Beta", StringComparison.Ordinal));

        first.OnStanza(Parse("<message type='groupchat' to='local_30000142@conference.chat.example'><body>Hello &amp; welcome</body></message>"));
        Assert.Contains(secondOutput, item => item.Contains("type='groupchat'", StringComparison.Ordinal)
            && item.Contains("Hello &amp; welcome", StringComparison.Ordinal));
    }

    [Fact]
    public void Local_room_join_is_rejected_when_session_is_in_another_system()
    {
        var sessions = new ConnectedSessions();
        sessions.Add(Character(42, "token", 30_000_142));
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"));
        var output = new List<string>();
        var client = Connect(hub, 42, "token", output);

        client.OnStanza(Parse("<presence from='42@chat.example/r' to='local_30000143@conference.chat.example/Alpha'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));

        Assert.Contains(output, item => item.Contains("type='error'", StringComparison.Ordinal)
            && item.Contains("not-authorized", StringComparison.Ordinal));
    }

    [Fact]
    public void Reconnected_session_starts_a_fresh_room_message_budget()
    {
        const long systemId = 30_000_142;
        var sessions = new ConnectedSessions();
        sessions.Add(Character(42, "first-token", systemId));
        sessions.Add(Character(43, "second-token", systemId));
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"));
        var firstOutput = new List<string>();
        var secondOutput = new List<string>();
        var first = Connect(hub, 42, "first-token", firstOutput);
        var second = Connect(hub, 43, "second-token", secondOutput);
        first.OnStanza(Parse("<presence to='local_30000142@conference.chat.example/Alpha'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        second.OnStanza(Parse("<presence to='local_30000142@conference.chat.example/Beta'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        first.OnStanza(Parse("<message type='groupchat' to='local_30000142@conference.chat.example'><body>before reconnect</body></message>"));
        Assert.Contains(secondOutput, item => item.Contains("before reconnect", StringComparison.Ordinal));

        first.OnClosed();
        firstOutput.Clear();
        first = Connect(hub, 42, "first-token", firstOutput);
        first.OnStanza(Parse("<presence to='local_30000142@conference.chat.example/Alpha'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        first.OnStanza(Parse("<message type='groupchat' to='local_30000142@conference.chat.example'><body>after reconnect</body></message>"));

        Assert.Contains(secondOutput, item => item.Contains("after reconnect", StringComparison.Ordinal));
    }

    [Fact]
    public void Wormhole_local_hides_silent_occupants_until_they_speak()
    {
        const long systemId = 31_000_142;
        var sessions = new ConnectedSessions();
        sessions.Add(Character(42, "first-token", systemId));
        sessions.Add(Character(43, "second-token", systemId));
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"));
        var firstOutput = new List<string>();
        var secondOutput = new List<string>();
        var first = Connect(hub, 42, "first-token", firstOutput);
        var second = Connect(hub, 43, "second-token", secondOutput);

        first.OnStanza(Parse("<presence to='wormhole_31000142@conference.chat.example/Alpha'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        second.OnStanza(Parse("<presence to='wormhole_31000142@conference.chat.example/Beta'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        Assert.DoesNotContain(secondOutput, item => item.Contains("wormhole_31000142@conference.chat.example/Alpha", StringComparison.Ordinal));

        first.OnStanza(Parse("<message type='groupchat' to='wormhole_31000142@conference.chat.example'><body>Present</body></message>"));
        Assert.Contains(secondOutput, item => item.Contains("wormhole_31000142@conference.chat.example/Alpha", StringComparison.Ordinal)
            && item.Contains("<presence", StringComparison.Ordinal));
        Assert.Contains(secondOutput, item => item.Contains("<body>Present</body>", StringComparison.Ordinal));
    }

    [Fact]
    public void Staff_and_invisible_presence_uses_server_account_roles()
    {
        const long systemId = 30_000_142;
        var sessions = new ConnectedSessions();
        sessions.Add(Character(42, "pilot-token", systemId));
        var staffSession = Character(43, "staff-token", systemId);
        staffSession.Set("accountRole", new PyLong(Eve.Services.GameMasterRoles.ChatAdministrator));
        sessions.Add(staffSession);
        var hiddenSession = Character(44, "hidden-token", systemId);
        hiddenSession.Set("accountRole", new PyLong(Eve.Services.GameMasterRoles.Gml | Eve.Services.GameMasterRoles.ChtInvisible));
        sessions.Add(hiddenSession);
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"));
        var pilotOutput = new List<string>();
        var staffOutput = new List<string>();
        var hiddenOutput = new List<string>();
        var pilot = Connect(hub, 42, "pilot-token", pilotOutput);
        var staff = Connect(hub, 43, "staff-token", staffOutput);
        var hidden = Connect(hub, 44, "hidden-token", hiddenOutput);
        pilot.OnStanza(Parse(" <presence to='local_30000142@conference.chat.example/Pilot'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        staff.OnStanza(Parse("<presence to='local_30000142@conference.chat.example/Staff'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        hidden.OnStanza(Parse("<presence to='local_30000142@conference.chat.example/Hidden'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));

        Assert.Contains(pilotOutput, item => item.Contains("local_30000142@conference.chat.example/Staff", StringComparison.Ordinal)
            && item.Contains("affiliation='admin'", StringComparison.Ordinal) && item.Contains("role='moderator'", StringComparison.Ordinal));
        Assert.DoesNotContain(pilotOutput, item => item.Contains("local_30000142@conference.chat.example/Hidden", StringComparison.Ordinal));
        Assert.Contains(hiddenOutput, item => item.Contains("local_30000142@conference.chat.example/Hidden", StringComparison.Ordinal)
            && item.Contains("status code='110'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_connection_cannot_join_more_than_thirty_two_rooms()
    {
        var sessions = new ConnectedSessions();
        var session = Character(45, "many-rooms-token", 10_000);
        sessions.Add(session);
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"));
        var output = new List<string>();
        var client = Connect(hub, 45, "many-rooms-token", output);

        for (var index = 0; index < 32; index++)
        {
            var systemId = 10_000 + index;
            session.Set("solarsystemid2", new PyInt(systemId));
            client.OnStanza(Parse($"<presence to='local_{systemId}@conference.chat.example/Pilot'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
            Assert.Contains(output, item => item.Contains($"local_{systemId}@conference.chat.example/Pilot", StringComparison.Ordinal));
        }

        const long excessSystem = 20_000;
        session.Set("solarsystemid2", new PyInt(excessSystem));
        client.OnStanza(Parse($"<presence to='local_{excessSystem}@conference.chat.example/Pilot'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        Assert.DoesNotContain(output, item => item.Contains($"local_{excessSystem}@conference.chat.example/Pilot", StringComparison.Ordinal));
    }

    private static ClientSession Character(long characterId, string token, long systemId)
    {
        var session = new ClientSession { CharacterId = characterId };
        session.Set("chatAuthenticationToken", new PyStr(token));
        session.Set("solarsystemid2", new PyInt(systemId));
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
