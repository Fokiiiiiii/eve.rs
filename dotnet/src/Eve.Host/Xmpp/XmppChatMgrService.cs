using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Xmpp;

[Service("XmppChatMgr")]
public sealed partial class XmppChatMgrService(XmppChatHub hub)
{
    [Method("Hostname")]
    public PyValue Hostname(ServiceCall call) => new PyStr(hub.Settings.Domain);

    [Method("GetDeprecatedPrefsFallback")]
    public PyValue GetDeprecatedPrefsFallback(ServiceCall call) => new PyStr(hub.Settings.ConnectHost);

    [Method("SetMotd")]
    public PyValue SetMotd(ServiceCall call)
    {
        var room = call.Arg(0) is PyStr roomName ? roomName.Value : "";
        var motd = call.Arg(1) is PyStr text ? text.Value : "";
        return hub.SetCorporationMotd(call.Session, room, motd) ? PyBool.True : PyBool.False;
    }

    [Method("CreatePrivateChannel")]
    public PyValue CreatePrivateChannel(ServiceCall call)
    {
        var targetCharacterId = call.Arg(0) switch
        {
            PyInt number => number.Value,
            PyLong number => number.Value,
            _ => 0,
        };
        var room = hub.PrivateRoomName(call.Session, targetCharacterId);
        return room is null ? PyNone.Instance : new PyStr(room);
    }

    [Method("ResyncSystemChannelAccess")]
    public PyValue ResyncSystemChannelAccess(ServiceCall call) =>
        Eve.Services.Py.List(hub.ResyncChannels(call.Session).Select(channel => (PyValue)new PyStr(channel)).ToArray());
}
