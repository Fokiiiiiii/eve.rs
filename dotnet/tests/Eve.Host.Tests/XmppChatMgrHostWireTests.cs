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
        var service = new XmppChatMgrService(new XmppChatHub(
            sessions,
            new XmppChatSettings("chat.example", "xmpp-gateway.example")));
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = 42;
                session.Set("solarsystemid2", new PyLong(31_000_001));
                session.Set("allianceid", new PyLong(99));
                session.Set("corpid", new PyLong(100));
                session.Set("fleetid", new PyLong(101));
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
            ["wormhole_31000001", "alliance_99", "corp_100", "fleet_101"],
            channels.Items.Select(channel => Encoding.ASCII.GetString(Assert.IsType<PyBytes>(channel).Value.Span)));

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
    }
}
