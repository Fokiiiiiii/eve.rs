using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Xmpp;

[Service("XmppChatMgr")]
public sealed partial class XmppChatMgrService(XmppChatHub hub)
{
    private const string RookieHelpChannel = "system_263238_263259";

    [Method("Hostname")]
    public PyValue Hostname(ServiceCall call) => new PyStr(hub.Settings.Domain);

    [Method("GetDeprecatedPrefsFallback")]
    public PyValue GetDeprecatedPrefsFallback(ServiceCall call) => new PyStr(hub.Settings.ConnectHost);

    [Method("GetRookieChannel")]
    public PyValue GetRookieChannel(ServiceCall call) => new PyStr(RookieHelpChannel);

    [Method("GetHelpChannel")]
    public PyValue GetHelpChannel(ServiceCall call)
    {
        var language = call.Arg(0) is PyStr text ? text.Value.Split('-', '_')[0].ToLowerInvariant() : "en";
        var room = language switch
        {
            "de" => "system_263238_263267",
            "ru" => "system_263238_263301",
            "ja" => "system_263238_263302",
            "fr" => "system_263238_504075",
            "ko" => "system_263238_553782",
            _ => "system_263238_263262",
        };
        return new PyStr(room);
    }

    [Method("SetMotd")]
    public async Task<PyValue> SetMotd(ServiceCall call)
    {
        var room = call.Arg(0) is PyStr roomName ? roomName.Value : "";
        var motd = call.Arg(1) is PyStr text ? text.Value : "";
        return await hub.SetCorporationMotdAsync(call.Session, room, motd, call.Cancellation).ConfigureAwait(false)
            ? PyBool.True
            : PyBool.False;
    }

    [Method("CreatePlayerOwnedChannel")]
    public async Task<PyValue> CreatePlayerOwnedChannel(ServiceCall call)
    {
        var name = call.Arg(0) is PyStr displayName ? displayName.Value : "";
        var room = await hub.CreatePlayerChannelAsync(call.Session, name, call.Cancellation).ConfigureAwait(false);
        return room is null ? PyNone.Instance : new PyStr(room);
    }

    [Method("AddCharacterToChannel")]
    public async Task<PyValue> AddCharacterToChannel(ServiceCall call)
    {
        var room = call.Arg(0) is PyStr roomName ? roomName.Value.Split('@')[0].Trim() : "";
        var characterId = PositiveId(call.Arg(1));
        var reason = call.Arg(2) is PyStr text ? text.Value : "";
        return await hub.AddCharacterToChannelAsync(call.Session, room, characterId, reason, call.Cancellation).ConfigureAwait(false)
            ? PyBool.True
            : PyBool.False;
    }

    [Method("GrantChannelOwnership")]
    public async Task<PyValue> GrantChannelOwnership(ServiceCall call)
    {
        var room = call.Arg(0) is PyStr roomName ? roomName.Value.Split('@')[0].Trim() : "";
        return await hub.GrantPlayerChannelOwnershipAsync(call.Session, room, call.Cancellation).ConfigureAwait(false)
            ? PyBool.True
            : PyBool.False;
    }

    [Method("CreatePrivateChannel")]
    public PyValue CreatePrivateChannel(ServiceCall call)
    {
        var targetCharacterId = PositiveId(call.Arg(0));
        var room = hub.PrivateRoomName(call.Session, targetCharacterId);
        return room is null ? PyNone.Instance : new PyStr(room);
    }

    [Method("ResyncSystemChannelAccess")]
    public PyValue ResyncSystemChannelAccess(ServiceCall call) =>
        Eve.Services.Py.List(hub.ResyncChannels(call.Session).Select(channel => (PyValue)new PyStr(channel)).ToArray());

    private static long PositiveId(PyValue value) => value switch
    {
        PyInt number when number.Value > 0 => number.Value,
        PyLong number when number.Value > 0 => number.Value,
        _ => 0,
    };
}
