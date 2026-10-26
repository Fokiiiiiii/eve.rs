using Eve.Host;
using Eve.Host.Xmpp;
using Eve.Marshal;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Help_channel_uses_the_authenticated_session_language_over_host_dispatch()
    {
        var service = new XmppChatMgrService(new XmppChatHub(
            new ConnectedSessions(),
            new XmppChatSettings("chat.example", "127.0.0.1")));
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("languageID", new PyStr("JA")));

        var (front, open) = await OpenFrontAsync();

        Assert.Equal(new PyStr("system_263238_263302"),
            await HostRpcAsync(front, open, 1, "XmppChatMgr", "GetHelpChannel"));
    }
}
