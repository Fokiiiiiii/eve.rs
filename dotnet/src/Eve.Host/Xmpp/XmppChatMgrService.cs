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

    [Method("ResyncSystemChannelAccess")]
    public PyValue ResyncSystemChannelAccess(ServiceCall call) =>
        Eve.Services.Py.List(hub.StaticAccessChannels(call.Session).Select(channel => (PyValue)new PyStr(channel)).ToArray());
}
