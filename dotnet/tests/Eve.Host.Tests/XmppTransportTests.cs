using System.Text;
using Eve.Host;
using Eve.Host.Xmpp;
using Eve.Services;
using Eve.Marshal;

namespace Eve.Host.Tests;

public sealed class XmppTransportTests
{
    [Fact]
    public void Parser_handles_partial_and_coalesced_stream_stanzas()
    {
        var parser = new XmppStreamParser();
        var first = parser.Feed(Encoding.UTF8.GetBytes("<stream:stream xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams'><message><body>hel"));
        var second = parser.Feed(Encoding.UTF8.GetBytes("lo</body></message><presence/>"));

        Assert.Collection(first, item => Assert.IsType<XmppStreamOpened>(item));
        Assert.Collection(second,
            item => Assert.Equal("hello", Assert.IsType<XmppStanza>(item).Element.Child("body")?.Text),
            item => Assert.Equal("presence", Assert.IsType<XmppStanza>(item).Element.Name));
    }

    [Theory]
    [InlineData("<!DOCTYPE x [<!ENTITY e 'expanded'>]><x>&e;</x>")]
    [InlineData("<x><y></x>")]
    public void Parser_rejects_restricted_or_malformed_xml(string stanza)
    {
        var parser = new XmppStreamParser();
        parser.Feed(Encoding.UTF8.GetBytes("<stream:stream xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams'>"));

        Assert.Throws<XmppStreamException>(() => parser.Feed(Encoding.UTF8.GetBytes(stanza)));
    }

    [Fact]
    public void Session_authenticates_token_binds_resource_and_releases_link()
    {
        const long characterId = 42;
        const string token = "session-token";
        var sessions = new ConnectedSessions();
        var character = new ClientSession { CharacterId = characterId };
        character.Set("chatAuthenticationToken", new PyStr(token));
        sessions.Add(character);

        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "127.0.0.1"));
        var sent = new List<string>();
        string? closeCondition = "pending";
        var closeCount = 0;
        var client = new XmppClientSession(hub, sent.Add, condition => closeCondition = condition);
        client.OnStreamOpened();
        client.OnStanza(Auth(characterId, "wrong-token"));
        Assert.Contains(sent, item => item.Contains("<failure", StringComparison.Ordinal));
        Assert.Null(closeCondition);

        sent.Clear();
        closeCondition = "pending";
        client = new XmppClientSession(hub, sent.Add, condition => closeCondition = condition);
        client.OnStreamOpened();
        client.OnStanza(Auth(characterId, token));
        Assert.Contains(sent, item => item.Contains("<success", StringComparison.Ordinal));

        client.OnStanza(Bind("room-client"));
        Assert.Contains(sent, item => item.Contains("42@chat.example/room-client", StringComparison.Ordinal));

        client.OnClosed();
        Assert.NotNull(hub.Bind(characterId, "reconnected", sent.Add, () => closeCount++));
        Assert.Equal(0, closeCount);
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
        iq.Attributes["id"] = "bind-1";
        iq.Attributes["type"] = "set";
        var bind = new XmppElement("bind");
        var resourceElement = new XmppElement("resource");
        resourceElement.AppendText(resource);
        bind.Children.Add(resourceElement);
        iq.Children.Add(bind);
        return iq;
    }
}
