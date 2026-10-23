using System.Text;
using Eve.Marshal;
using Eve.Services;
using Eve.Host.Xmpp;
using Xunit;

namespace Eve.Host.Tests;

public sealed class XmppPrivateConversationTests
{
    [Fact]
    public void Private_invites_are_rejected_when_the_target_blocks_the_inviter()
    {
        var sessions = new ConnectedSessions();
        sessions.Add(Character(42, "first-token"));
        sessions.Add(Character(77, "second-token"));
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"),
            (characterId, _) => ValueTask.FromResult<IReadOnlyList<long>>(characterId == 77 ? [42] : []));
        var firstOutput = new List<string>();
        var secondOutput = new List<string>();
        var first = Connect(hub, 42, "first-token", firstOutput);
        _ = Connect(hub, 77, "second-token", secondOutput);

        first.OnStanza(Parse("<presence to='private_42_77@conference.chat.example/Alpha'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        first.OnStanza(Parse("<message to='private_42_77@conference.chat.example'><x xmlns='http://jabber.org/protocol/muc#user'><invite to='77@chat.example'/></x></message>"));

        Assert.Contains(firstOutput, item => item.Contains("not-allowed", StringComparison.Ordinal));
        Assert.DoesNotContain(secondOutput, item => item.Contains("<invite", StringComparison.Ordinal));
    }

    [Fact]
    public void Private_room_invitation_and_messages_are_limited_to_the_character_pair()
    {
        var sessions = new ConnectedSessions();
        sessions.Add(Character(42, "first-token"));
        sessions.Add(Character(77, "second-token"));
        sessions.Add(Character(78, "third-token"));
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"));
        var firstOutput = new List<string>();
        var secondOutput = new List<string>();
        var thirdOutput = new List<string>();
        var first = Connect(hub, 42, "first-token", firstOutput);
        var second = Connect(hub, 77, "second-token", secondOutput);
        var third = Connect(hub, 78, "third-token", thirdOutput);
        const string room = "private_42_77";

        first.OnStanza(Parse($"<presence to='{room}@conference.chat.example/Alpha'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        first.OnStanza(Parse($"<message to='{room}@conference.chat.example'><x xmlns='http://jabber.org/protocol/muc#user'><invite to='77@chat.example'><reason>Private chat</reason></invite></x></message>"));
        Assert.Contains(secondOutput, item => item.Contains($"from='{room}@conference.chat.example'", StringComparison.Ordinal)
            && item.Contains("<invite from='42@chat.example'>", StringComparison.Ordinal));
        var inviteCount = secondOutput.Count(item => item.Contains("<invite from='42@chat.example'>", StringComparison.Ordinal));
        first.OnStanza(Parse($"<message to='{room}@conference.chat.example'><x xmlns='http://jabber.org/protocol/muc#user'><invite to='77@chat.example'/></x></message>"));
        Assert.Equal(inviteCount, secondOutput.Count(item => item.Contains("<invite from='42@chat.example'>", StringComparison.Ordinal)));

        second.OnStanza(Parse($"<presence to='{room}@conference.chat.example/Beta'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        first.OnStanza(Parse($"<message type='groupchat' to='{room}@conference.chat.example'><body>meet at the station</body></message>"));
        Assert.Contains(secondOutput, item => item.Contains("<body>meet at the station</body>", StringComparison.Ordinal));

        third.OnStanza(Parse($"<presence to='{room}@conference.chat.example/Gamma'><x xmlns='http://jabber.org/protocol/muc'/></presence>"));
        Assert.Contains(thirdOutput, item => item.Contains("not-authorized", StringComparison.Ordinal));
    }

    private static ClientSession Character(long characterId, string token)
    {
        var session = new ClientSession { CharacterId = characterId };
        session.Set("chatAuthenticationToken", new PyStr(token));
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
