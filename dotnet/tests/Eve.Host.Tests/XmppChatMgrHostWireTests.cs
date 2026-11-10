using System.Text;
using Eve.Host;
using Eve.Host.Xmpp;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Xmpp_chat_mgr_calls_keep_client_wire_shapes_through_host_dispatch()
    {
        var sessions = new ConnectedSessions();
        var hub = new XmppChatHub(sessions, new XmppChatSettings("chat.example", "xmpp-gateway.example"));
        var service = new XmppChatMgrService(hub);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = 42;
                session.Set("solarsystemid2", new PyLong(31_000_001));
                session.Set("allianceid", new PyLong(99));
                session.Set("corpid", new PyLong(100));
                session.Set("fleetid", new PyLong(101));
                session.Set("warfactionid", new PyLong(500_001));
                session.Set("corprole", new PyLong(Eve.Corporations.CorporationRoles.Director));
                sessions.Add(session);
            });

        var (front, open) = await OpenFrontAsync();

        Assert.Equal("xmpp-gateway.example", Encoding.ASCII.GetString(Assert.IsType<PyBytes>(
            await HostRpcAsync(front, open, 1, "XmppChatMgr", "GetDeprecatedPrefsFallback")).Value.Span));
        Assert.Equal("system_263238_263259", Encoding.ASCII.GetString(Assert.IsType<PyBytes>(
            await HostRpcAsync(front, open, 2, "XmppChatMgr", "GetRookieChannel")).Value.Span));
        Assert.Equal("private_42_77", Encoding.ASCII.GetString(Assert.IsType<PyBytes>(
            await HostRpcAsync(front, open, 3, "XmppChatMgr", "CreatePrivateChannel", new PyLong(77))).Value.Span));

        var channels = Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 4, "XmppChatMgr", "ResyncSystemChannelAccess"));
        Assert.Equal(
            ["wormhole_31000001", "alliance_99", "corp_100", "fleet_101", "faction_500001"],
            channels.Items.Select(channel => Encoding.ASCII.GetString(Assert.IsType<PyBytes>(channel).Value.Span)));

        var militiaLink = Assert.IsType<XmppLink>(hub.Bind(42, "militia", _ => { }, () => { }));
        Assert.True(hub.TryJoinRoom(militiaLink, "faction_500001", "Pilot", "", out _));

        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 5, "XmppChatMgr", "CreatePlayerOwnedChannel", new PyStr("private room")));
        Assert.Equal(PyBool.False,
            await HostRpcAsync(front, open, 6, "XmppChatMgr", "AddCharacterToChannel",
                new PyStr("player_42"), new PyInt(77), new PyStr("invite")));
        Assert.Equal(PyBool.False,
            await HostRpcAsync(front, open, 7, "XmppChatMgr", "GrantChannelOwnership", new PyStr("player_42")));
        Assert.Equal(PyBool.True,
            await HostRpcAsync(front, open, 8, "XmppChatMgr", "SetMotd",
                new PyStr("corp_100@conference.chat.example"), new PyStr("A new MOTD")));
        Assert.Equal(PyBool.True,
            await HostRpcAsync(front, open, 9, "XmppChatMgr", "SetMotd",
                new PyStr("alliance_99@conference.chat.example"), new PyStr("Alliance notice")));
        Assert.Equal(PyBool.False,
            await HostRpcAsync(front, open, 10, "XmppChatMgr", "SetMotd",
                new PyStr("alliance_99@conference.other.example"), new PyStr("Wrong host")));
        Assert.Equal("resourcewars_987654321", Encoding.ASCII.GetString(Assert.IsType<PyBytes>(
            await HostRpcAsync(front, open, 11, "XmppChatMgr", "EnsureResourceWarsChannelExists", new PyLong(987654321))).Value.Span));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 12, "XmppChatMgr", "EnsureResourceWarsChannelExists", new PyInt(0)));
        var stanzas = new List<string>();
        var link = Assert.IsType<XmppLink>(hub.Bind(42, "resource-wars", stanzas.Add, () => { }));
        Assert.True(hub.TryJoinRoom(link, "resourcewars_987654321", "Pilot", "", out var occupants));
        Assert.Empty(occupants);
        Assert.Contains(stanzas, stanza => stanza.Contains("resourcewars_987654321@conference.chat.example/Pilot", StringComparison.Ordinal));
        hub.LeaveRoom(link, "resourcewars_987654321");
        Assert.False(hub.TryJoinRoom(link, "resourcewars_987654322", "Pilot", "", out _));
        Assert.False(hub.TryJoinRoom(link, "resourcewars_987654321", "Pilot", "", out _));
        Assert.Equal("resourcewars_987654321", Encoding.ASCII.GetString(Assert.IsType<PyBytes>(
            await HostRpcAsync(front, open, 13, "XmppChatMgr", "EnsureResourceWarsChannelExists", new PyLong(987654321))).Value.Span));
        Assert.True(hub.TryJoinRoom(link, "resourcewars_987654321", "Pilot", "", out _));
    }
}
