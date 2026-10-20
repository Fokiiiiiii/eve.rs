using System.Text;
using Eve.Marshal;
using Eve.Services;
using Eve.Host.Xmpp;
using Xunit;

namespace Eve.Host.Tests;

public sealed class XmppOrganizationRoomTests
{
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
