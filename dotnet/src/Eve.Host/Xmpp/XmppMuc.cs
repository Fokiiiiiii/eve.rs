using System.Text;
using Eve.Services;

namespace Eve.Host.Xmpp;

public sealed partial class XmppChatHub
{
    private const int MaxRoomNameLength = 96;
    private const int MaxNicknameLength = 64;
    private const int MaxRoomMessageBytes = 2048;
    private const int MaxRoomJoinsPerConnection = 32;
    private const string MucNamespace = "http://jabber.org/protocol/muc";
    private const string MucUserNamespace = "http://jabber.org/protocol/muc#user";
    private const string DiscoItemsNamespace = "http://jabber.org/protocol/disco#items";
    private const string DiscoInfoNamespace = "http://jabber.org/protocol/disco#info";

    private readonly Dictionary<(string Room, long CharacterId), XmppOccupant> _occupants = [];
    private readonly Dictionary<long, DateTimeOffset> _lastRoomMessage = [];
    private readonly HashSet<(string Room, long CharacterId)> _visibleLocalSpeakers = [];

    internal string ConferenceDomain => $"conference.{Settings.Domain}";

    internal bool IsConferenceDomain(string? domain) =>
        string.Equals(domain, ConferenceDomain, StringComparison.OrdinalIgnoreCase);

    internal IReadOnlyList<string> DiscoverLocalRooms(XmppLink link)
    {
        var session = SessionOf(link.CharacterId);
        return session is null
            ? []
            : StaticAccessChannels(session).Where(name => name.StartsWith("local_", StringComparison.Ordinal) || name.StartsWith("wormhole_", StringComparison.Ordinal)).ToArray();
    }

    internal bool TryJoinLocalRoom(XmppLink link, string room, string nickname, out IReadOnlyList<XmppOccupant> occupants)
    {
        occupants = [];
        if (!ValidRoomName(room) || !ValidNickname(nickname) || SessionOf(link.CharacterId) is not { } session
            || !StaticAccessChannels(session).Contains(room, StringComparer.Ordinal))
            return false;

        var delayed = Eve.Host.PublicGateway.LocalChatGateway.IsDelayedLocalChannel(room);
        lock (_gate)
        {
            var key = (room, link.CharacterId);
            var linkedRooms = _occupants.Keys.Where(item => item.CharacterId == link.CharacterId).Select(item => item.Room).Distinct(StringComparer.Ordinal).Count();
            if (linkedRooms >= MaxRoomJoinsPerConnection && !_occupants.ContainsKey(key))
                return false;
            if (_occupants.TryGetValue(key, out var current))
            {
                if (!ReferenceEquals(current.Link, link))
                    return false;
                nickname = current.Nickname;
                return true;
            }
            occupants = _occupants.Where(pair => pair.Key.Room == room
                && (!delayed || _visibleLocalSpeakers.Contains(pair.Key))).Select(pair => pair.Value).ToArray();
            _occupants[key] = new XmppOccupant(link, nickname);
        }

        var self = new XmppOccupant(link, nickname);
        if (!delayed)
        {
            foreach (var occupant in occupants)
                occupant.Link.Send(Presence(room, self, occupant.Link.Jid, selfPresence: false));
        }
        link.Send(Presence(room, self, link.Jid, selfPresence: true));
        foreach (var occupant in occupants)
            link.Send(Presence(room, occupant, link.Jid, selfPresence: false));
        return true;
    }

    internal void LeaveLocalRoom(XmppLink link, string room)
    {
        XmppOccupant? leaving;
        XmppOccupant[] remaining;
        bool visible;
        lock (_gate)
        {
            var key = (room, link.CharacterId);
            if (!_occupants.TryGetValue(key, out leaving) || !ReferenceEquals(leaving.Link, link))
                return;
            _occupants.Remove(key);
            visible = !Eve.Host.PublicGateway.LocalChatGateway.IsDelayedLocalChannel(room) || _visibleLocalSpeakers.Remove(key);
            remaining = _occupants.Where(pair => pair.Key.Room == room).Select(pair => pair.Value).ToArray();
        }

        var from = RoomJid(room, leaving.Nickname);
        if (visible)
        {
            foreach (var occupant in remaining)
                occupant.Link.Send($"<presence from='{XmppXml.Escape(from)}' to='{XmppXml.Escape(occupant.Link.Jid)}' type='unavailable'/>");
        }
        link.Send($"<presence from='{XmppXml.Escape(from)}' to='{XmppXml.Escape(link.Jid)}' type='unavailable'/>");
    }

    internal void LeaveAllLocalRooms(XmppLink link)
    {
        string[] rooms;
        lock (_gate)
            rooms = _occupants.Keys.Where(key => key.CharacterId == link.CharacterId).Select(key => key.Room).ToArray();
        foreach (var room in rooms)
            LeaveLocalRoom(link, room);
    }

    internal bool SendLocalRoomMessage(XmppLink link, string room, string body)
    {
        if (Encoding.UTF8.GetByteCount(body) is 0 or > MaxRoomMessageBytes
            || body.Any(character => char.IsControl(character) && character is not '\n' and not '\t'))
            return false;
        XmppOccupant sender;
        XmppOccupant[] recipients;
        XmppOccupant[] knownSpeakers;
        bool delayed;
        bool firstMessage;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_occupants.TryGetValue((room, link.CharacterId), out sender!))
                return false;
            if (_lastRoomMessage.TryGetValue(link.CharacterId, out var last) && now - last < TimeSpan.FromMilliseconds(750))
                return false;
            _lastRoomMessage[link.CharacterId] = now;
            recipients = _occupants.Where(pair => pair.Key.Room == room).Select(pair => pair.Value).ToArray();
            delayed = Eve.Host.PublicGateway.LocalChatGateway.IsDelayedLocalChannel(room);
            firstMessage = delayed && _visibleLocalSpeakers.Add((room, link.CharacterId));
            knownSpeakers = delayed
                ? _occupants.Where(pair => pair.Key.Room == room && _visibleLocalSpeakers.Contains(pair.Key) && pair.Key.CharacterId != link.CharacterId).Select(pair => pair.Value).ToArray()
                : [];
        }

        if (delayed && firstMessage)
        {
            foreach (var speaker in knownSpeakers)
                link.Send(Presence(room, speaker, link.Jid, selfPresence: false));
            foreach (var recipient in recipients)
                recipient.Link.Send(Presence(room, sender, recipient.Link.Jid, ReferenceEquals(recipient.Link, link)));
        }
        var from = RoomJid(room, sender.Nickname);
        var safeBody = XmppXml.Escape(body);
        foreach (var recipient in recipients)
            recipient.Link.Send($"<message from='{XmppXml.Escape(from)}' to='{XmppXml.Escape(recipient.Link.Jid)}' type='groupchat'><body>{safeBody}</body></message>");
        return true;
    }

    internal string LocalRoomItemsXml(XmppLink link)
    {
        var items = DiscoverLocalRooms(link).Select(room =>
            $"<item jid='{XmppXml.Escape(room + "@" + ConferenceDomain)}' name='{XmppXml.Escape(room)}'/>");
        return string.Concat(items);
    }

    internal string LocalRoomOccupantsXml(XmppLink link, string room)
    {
        lock (_gate)
            return string.Concat(_occupants.Where(pair => pair.Key.Room == room
                && (!Eve.Host.PublicGateway.LocalChatGateway.IsDelayedLocalChannel(room) || _visibleLocalSpeakers.Contains(pair.Key))).Select(pair =>
                $"<item jid='{XmppXml.Escape(RoomJid(room, pair.Value.Nickname))}' name='{XmppXml.Escape(pair.Value.Nickname)}'/>").ToArray());
    }

    internal static string LocalRoomInfoXml(string room) =>
        $"<identity category='conference' type='text' name='{XmppXml.Escape(room)}'/><feature var='{DiscoItemsNamespace}'/><feature var='{DiscoInfoNamespace}'/><feature var='{MucNamespace}'/><feature var='muc_temporary'/><feature var='muc_open'/>";

    private string RoomJid(string room, string nickname) => $"{room}@{ConferenceDomain}/{nickname}";

    private static string Presence(string room, XmppOccupant occupant, string to, bool selfPresence)
    {
        var from = $"{room}@conference.{occupant.Link.Domain}/{occupant.Nickname}";
        var self = selfPresence ? "<status code='110'/>" : "";
        return $"<presence from='{XmppXml.Escape(from)}' to='{XmppXml.Escape(to)}'><x xmlns='{MucUserNamespace}'><item affiliation='member' role='participant'/>{self}</x></presence>";
    }

    private static bool ValidRoomName(string room) => room.Length is > 0 and <= MaxRoomNameLength
        && room.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool ValidNickname(string nickname) => nickname.Length is > 0 and <= MaxNicknameLength
        && nickname.All(character => !char.IsControl(character) && character is not '/' and not '@');

    internal sealed record XmppOccupant(XmppLink Link, string Nickname);
}
