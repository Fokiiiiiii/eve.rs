using Eve.Host;
using Eve.Host.Xmpp;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public sealed class XmppChatMgrServiceTests
{
    [Fact]
    public void Hostname_and_fallback_publish_the_configured_xmpp_domain_and_socket_host()
    {
        var hub = new XmppChatHub(new ConnectedSessions(), new XmppChatSettings("chat.example", "192.0.2.10"));
        var service = new XmppChatMgrService(hub);

        Assert.Equal(new PyStr("chat.example"), service.Hostname(null!));
        Assert.Equal(new PyStr("192.0.2.10"), service.GetDeprecatedPrefsFallback(null!));
    }

    [Fact]
    public void Resync_returns_only_the_active_character_static_channel_names()
    {
        const long characterId = 42;
        var sessions = new ConnectedSessions();
        var active = new ClientSession { CharacterId = characterId };
        active.Set("solarsystemid2", new PyLong(31_000_001));
        active.Set("allianceid", new PyLong(99));
        active.Set("corpid", new PyLong(100));
        active.Set("fleetid", new PyLong(101));
        sessions.Add(active);
        var inactive = new ClientSession { CharacterId = 43 };
        inactive.Set("solarsystemid2", new PyLong(30_000_001));

        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "localhost"));
        var service = new XmppChatMgrService(hub);
        var call = new ServiceCall { Method = "ResyncSystemChannelAccess", Args = [], Session = active };
        var inactiveCall = new ServiceCall { Method = "ResyncSystemChannelAccess", Args = [], Session = inactive };

        Assert.Equal(Py.List(new PyStr("wormhole_31000001"), new PyStr("alliance_99"), new PyStr("corp_100"), new PyStr("fleet_101")),
            service.ResyncSystemChannelAccess(call));
        Assert.Equal(Py.List(), service.ResyncSystemChannelAccess(inactiveCall));
    }
}
