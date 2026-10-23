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
    private readonly Dictionary<long, DateTimeOffset> _lastPrivateInvite = [];
    private readonly HashSet<(string Room, long CharacterId)> _visibleLocalSpeakers = [];
    private readonly Dictionary<string, string> _roomMotd = new(StringComparer.Ordinal);

    internal string ConferenceDomain => $"conference.{Settings.Domain}";

    internal bool IsConferenceDomain(string? domain) =>
        string.Equals(domain, ConferenceDomain, StringComparison.OrdinalIgnoreCase);

    private static bool IsPublicHelpChannel(string room) => room is
        "system_263238_263259" or "system_263238_263262" or "system_263238_263267"
        or "system_263238_263301" or "system_263238_263302" or "system_263238_504075"
        or "system_263238_553782";

    private static string StaticChannelDisplayName(string room) => room switch
    {
        "system_263238_263259" => "Rookie Help",
        "system_263238_263262" => "English Help",
        "system_263238_263267" => "German Help",
        "system_263238_263301" => "Russian Help",
        "system_263238_263302" => "Japanese Help",
        "system_263238_504075" => "French Help",
        "system_263238_553782" => "Korean Help",
        _ => room,
    };

    internal IReadOnlyList<string> DiscoverLocalRooms(XmppLink link)
    {
        var session = SessionOf(link.CharacterId);
        if (session is null) return [];
        return StaticAccessChannels(session)
            .Concat(["system_263238_263259", "system_263238_263262", "system_263238_263267", "system_263238_263301", "system_263238_263302", "system_263238_504075", "system_263238_553782"])
            .Concat(PlayerChannels?.AvailableTo(link.CharacterId).Select(channel => channel.RoomName) ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(room => room, StringComparer.Ordinal)
            .ToArray();
    }

private bool CanAccessRoom(ClientSession? session, long characterId, string room) => session is not null
        && (StaticAccessChannels(session).Contains(room, StringComparer.Ordinal)
            || IsPublicHelpChannel(room) || HasPrivateRoomAccess(session, room) || PlayerChannels?.CanJoin(room, characterId) == true);

    internal bool TryJoinRoom(XmppLink link, string room, string nickname, out IReadOnlyList<XmppOccupant> occupants)
    {
        occupants = [];
        var session = SessionOf(link.CharacterId);
        var canJoin = CanAccessRoom(session, link.CharacterId, room);
        if (!ValidRoomName(room) || !ValidNickname(nickname) || !canJoin)
            return false;

        PruneRoomMembers(room);
        var delayed = Eve.Host.PublicGateway.LocalChatGateway.IsDelayedLocalChannel(room);
        var hidden = (link.AccountRole & Eve.Services.GameMasterRoles.ChtInvisible) != 0;
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
                && (!delayed || _visibleLocalSpeakers.Contains(pair.Key))
                && (pair.Value.Link.AccountRole & Eve.Services.GameMasterRoles.ChtInvisible) == 0).Select(pair => pair.Value).ToArray();
            _occupants[key] = new XmppOccupant(link, nickname);
        }

        var self = new XmppOccupant(link, nickname);
        if (!hidden && !delayed)
        {
            foreach (var occupant in occupants)
                occupant.Link.Send(Presence(room, self, occupant.Link.Jid, selfPresence: false));
        }
        link.Send(Presence(room, self, link.Jid, selfPresence: true));
        foreach (var occupant in occupants)
            link.Send(Presence(room, occupant, link.Jid, selfPresence: false));
        var motd = RoomMotd(room);
        if (motd.Length > 0)
            link.Send(RoomSubject(room, "admin", link.Jid, motd));
        return true;
    }

    internal void LeaveRoom(XmppLink link, string room)
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
            visible = (!Eve.Host.PublicGateway.LocalChatGateway.IsDelayedLocalChannel(room) || _visibleLocalSpeakers.Remove(key))
                && (link.AccountRole & Eve.Services.GameMasterRoles.ChtInvisible) == 0;
            remaining = _occupants.Where(pair => pair.Key.Room == room
                && (pair.Value.Link.AccountRole & Eve.Services.GameMasterRoles.ChtInvisible) == 0).Select(pair => pair.Value).ToArray();
        }

        var from = RoomJid(room, leaving.Nickname);
        if (visible)
        {
            foreach (var occupant in remaining)
                occupant.Link.Send($"<presence from='{XmppXml.Escape(from)}' to='{XmppXml.Escape(occupant.Link.Jid)}' type='unavailable'/>");
        }
        link.Send($"<presence from='{XmppXml.Escape(from)}' to='{XmppXml.Escape(link.Jid)}' type='unavailable'/>");
    }

    internal void LeaveAllRooms(XmppLink link)
    {
        string[] rooms;
        lock (_gate)
            rooms = _occupants.Keys.Where(key => key.CharacterId == link.CharacterId).Select(key => key.Room).ToArray();
        foreach (var room in rooms)
            LeaveRoom(link, room);
    }

    internal bool SendRoomMessage(XmppLink link, string room, string body) =>
        SendRoomMessageAsync(link, room, body, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    internal async ValueTask<bool> SendRoomMessageAsync(XmppLink link, string room, string body, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(body) is 0 or > MaxRoomMessageBytes
            || body.Any(character => char.IsControl(character) && character is not '\n' and not '\t'))
            return false;
        PruneRoomMembers(room);
        var session = SessionOf(link.CharacterId);
        if (!CanAccessRoom(session, link.CharacterId, room))
            return false;

        XmppOccupant sender;
        XmppOccupant[] recipients;
        XmppOccupant[] knownSpeakers;
        bool delayed;
        bool firstMessage;
        var visible = (link.AccountRole & Eve.Services.GameMasterRoles.ChtInvisible) == 0;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_occupants.TryGetValue((room, link.CharacterId), out sender!)
                || !ReferenceEquals(sender.Link, link))
                return false;
            if (_lastRoomMessage.TryGetValue(link.CharacterId, out var last) && now - last < TimeSpan.FromMilliseconds(750))
                return false;
            _lastRoomMessage[link.CharacterId] = now;
            recipients = _occupants.Where(pair => pair.Key.Room == room).Select(pair => pair.Value).ToArray();
            delayed = Eve.Host.PublicGateway.LocalChatGateway.IsDelayedLocalChannel(room);
            firstMessage = delayed && visible && _visibleLocalSpeakers.Add((room, link.CharacterId));
            knownSpeakers = delayed
                ? _occupants.Where(pair => pair.Key.Room == room && _visibleLocalSpeakers.Contains(pair.Key) && pair.Key.CharacterId != link.CharacterId
                    && (pair.Value.Link.AccountRole & Eve.Services.GameMasterRoles.ChtInvisible) == 0).Select(pair => pair.Value).ToArray()
                : [];
        }

        if (recipients.Length > 1)
        {
            var allowed = new List<XmppOccupant>(recipients.Length);
            foreach (var recipient in recipients)
            {
                if (ReferenceEquals(recipient.Link, link)
                    || !await IsBlockedByAsync(recipient.Link.CharacterId, link.CharacterId, cancellationToken))
                    allowed.Add(recipient);
            }
            recipients = allowed.ToArray();
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

    internal async ValueTask<bool> SetRoomMotdAsync(XmppLink link, string room, string motd, CancellationToken cancellationToken)
    {
        var session = SessionOf(link.CharacterId);
        if (session is null || !ReferenceEquals(SessionOf(link.CharacterId), session))
            return false;
        lock (_gate)
        {
            if (!_occupants.TryGetValue((room, link.CharacterId), out var occupant) || !ReferenceEquals(occupant.Link, link))
                return false;
        }
        return await SetCorporationMotdAsync(session, room, motd, cancellationToken).ConfigureAwait(false);
    }

    internal bool SetCorporationMotd(ClientSession session, string room, string motd)
    {
        if (session.GetInt("corpid") is not { } corporationId || corporationId <= 0
            || room != $"corp_{corporationId}"
            || !IsCorporationDirector(session)
            || Encoding.UTF8.GetByteCount(motd) > 1024
            || motd.Any(character => char.IsControl(character) && character is not '\n' and not '\t')
            || session.CharacterId is not { } characterId
            || !ReferenceEquals(SessionOf(characterId), session))
            return false;

        XmppOccupant[] recipients;
        lock (_gate)
        {
            _roomMotd[room] = motd;
            recipients = _occupants.Where(pair => pair.Key.Room == room).Select(pair => pair.Value).ToArray();
        }
        foreach (var recipient in recipients)
            recipient.Link.Send(RoomSubject(room, "admin", recipient.Link.Jid, motd));
        return true;
    }

    internal async ValueTask<bool> SetCorporationMotdAsync(ClientSession session, string room, string motd, CancellationToken cancellationToken)
    {
        if (session.GetInt("corpid") is not { } corporationId || corporationId <= 0
            || room != $"corp_{corporationId}"
            || !IsCorporationDirector(session)
            || Encoding.UTF8.GetByteCount(motd) > 1024
            || motd.Any(character => char.IsControl(character) && character is not '\n' and not '\t')
            || session.CharacterId is not { } characterId
            || !ReferenceEquals(SessionOf(characterId), session))
            return false;

        if (_motdStore is not null)
            await _motdStore.SetAsync(room, motd, cancellationToken).ConfigureAwait(false);
        return SetCorporationMotd(session, room, motd);
    }

    private static bool IsCorporationDirector(ClientSession session) =>
        ((long)(session.GetInt("corprole") ?? 0) & Eve.Corporations.CorporationRoles.Director) != 0;

    private string RoomMotd(string room)
    {
        lock (_gate)
            return _roomMotd.GetValueOrDefault(room, "");
    }

    private string RoomSubject(string room, string nickname, string to, string motd) =>
        $"<message from='{XmppXml.Escape(RoomJid(room, nickname))}' to='{XmppXml.Escape(to)}' type='groupchat'><subject>{XmppXml.Escape(motd)}</subject></message>";

    internal string LocalRoomItemsXml(XmppLink link)
    {
        var items = DiscoverLocalRooms(link).Select(room =>
        {
            var channel = PlayerChannels?.Get(room);
            var displayName = channel?.DisplayName ?? StaticChannelDisplayName(room);
            var affiliation = channel is null ? "" : channel.OwnerCharacterId == link.CharacterId ? "owner" : "member";
            var node = affiliation.Length == 0 ? "" : $" node='{affiliation}'";
            return $"<item jid='{XmppXml.Escape(room + "@" + ConferenceDomain)}' name='{XmppXml.Escape(displayName)}'{node}/>";
        });
        return string.Concat(items);
    }

    internal string LocalRoomOccupantsXml(XmppLink link, string room)
    {
        lock (_gate)
            return string.Concat(_occupants.Where(pair => pair.Key.Room == room
                && (!Eve.Host.PublicGateway.LocalChatGateway.IsDelayedLocalChannel(room) || _visibleLocalSpeakers.Contains(pair.Key))
                && (pair.Value.Link.AccountRole & Eve.Services.GameMasterRoles.ChtInvisible) == 0).Select(pair =>
                $"<item jid='{XmppXml.Escape(RoomJid(room, pair.Value.Nickname))}' name='{XmppXml.Escape(pair.Value.Nickname)}'/>").ToArray());
    }

    internal static string LocalRoomInfoXml(string room) =>
        $"<identity category='conference' type='text' name='{XmppXml.Escape(room)}'/><feature var='{DiscoItemsNamespace}'/><feature var='{DiscoInfoNamespace}'/><feature var='{MucNamespace}'/><feature var='muc_temporary'/><feature var='muc_open'/>";

    private string RoomJid(string room, string nickname) => $"{room}@{ConferenceDomain}/{nickname}";

    private string Presence(string room, XmppOccupant occupant, string to, bool selfPresence)
    {
        var from = $"{room}@conference.{occupant.Link.Domain}/{occupant.Nickname}";
        var staff = (occupant.Link.AccountRole & (Eve.Services.GameMasterRoles.ChatAdministrator | Eve.Services.GameMasterRoles.Gml)) != 0;
        var owner = PlayerChannels?.Get(room)?.OwnerCharacterId == occupant.Link.CharacterId;
        var affiliation = owner ? "owner" : staff ? "admin" : "member";
        var role = owner || staff ? "moderator" : "participant";
        var self = selfPresence ? "<status code='110'/>" : "";
        return $"<presence from='{XmppXml.Escape(from)}' to='{XmppXml.Escape(to)}'><x xmlns='{MucUserNamespace}'><item affiliation='{affiliation}' role='{role}'/>{self}</x></presence>";
    }

    internal IReadOnlyList<string> ResyncChannels(ClientSession session)
    {
        var channels = StaticAccessChannels(session);
        if (session.CharacterId is not { } characterId || characterId <= 0)
            return channels;
        var active = ReferenceEquals(SessionOf(characterId), session);
        string[] staleRooms;
        lock (_gate)
        {
            staleRooms = _occupants.Keys.Where(key => key.CharacterId == characterId
                && (!active || !CanAccessRoom(session, characterId, key.Room)))
                .Select(key => key.Room).Distinct(StringComparer.Ordinal).ToArray();
        }
        foreach (var room in staleRooms)
        {
            XmppLink? link;
            lock (_gate)
                link = _occupants.TryGetValue((room, characterId), out var occupant) ? occupant.Link : null;
            if (link is not null)
                LeaveRoom(link, room);
        }
        return channels;
    }

    private void PruneRoomMembers(string room)
    {
        XmppOccupant[] occupants;
        lock (_gate)
            occupants = _occupants.Where(pair => pair.Key.Room == room).Select(pair => pair.Value).ToArray();
        foreach (var occupant in occupants)
        {
            var session = SessionOf(occupant.Link.CharacterId);
            if (!CanAccessRoom(session, occupant.Link.CharacterId, room))
                LeaveRoom(occupant.Link, room);
        }
    }

    internal string? PrivateRoomName(ClientSession caller, long targetCharacterId)
    {
        if (caller.CharacterId is not { } callerCharacterId || callerCharacterId <= 0
            || !ReferenceEquals(SessionOf(callerCharacterId), caller))
            return null;
        return PrivateChannelName(callerCharacterId, targetCharacterId);
    }

    internal async Task<string?> CreatePlayerChannelAsync(ClientSession session, string displayName, CancellationToken cancellationToken)
    {
        if (PlayerChannels is null || session.CharacterId is not { } characterId || characterId <= 0
            || !ReferenceEquals(SessionOf(characterId), session))
            return null;
        var channel = await PlayerChannels.CreateAsync(characterId, displayName, cancellationToken).ConfigureAwait(false);
        return channel?.RoomName;
    }

    internal async Task<bool> GrantPlayerChannelOwnershipAsync(ClientSession session, string room, CancellationToken cancellationToken)
    {
        if (PlayerChannels is null || session.CharacterId is not { } characterId || characterId <= 0
            || !ReferenceEquals(SessionOf(characterId), session)
            || !Eve.Services.GameMasterRoles.Has(session, Eve.Services.GameMasterRoles.ChatAdministrator | Eve.Services.GameMasterRoles.Gml))
            return false;
        return await PlayerChannels.SetOwnerAsync(room, characterId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> AddCharacterToChannelAsync(
        ClientSession session,
        string room,
        long targetCharacterId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (PlayerChannels is null || session.CharacterId is not { } inviterId || inviterId <= 0
            || targetCharacterId <= 0 || targetCharacterId == inviterId
            || Encoding.UTF8.GetByteCount(reason) > 256
            || reason.Any(character => char.IsControl(character) && character is not '\n' and not '\t')
            || !ReferenceEquals(SessionOf(inviterId), session)
            || !Eve.Services.GameMasterRoles.Has(session, Eve.Services.GameMasterRoles.ChatAdministrator | Eve.Services.GameMasterRoles.Gml)
            || PlayerChannels.Get(room) is null)
            return false;

        if (SessionOf(targetCharacterId) is null)
            return false;
        XmppLink? target;
        lock (_gate)
        {
            if (!_linkByCharacter.TryGetValue(targetCharacterId, out target))
                return false;
        }
        if (await IsCharacterBlockedByAsync(targetCharacterId, inviterId, cancellationToken).ConfigureAwait(false))
            return false;
        if (!await PlayerChannels.InviteAsync(room, targetCharacterId, cancellationToken).ConfigureAwait(false))
            return false;

        var safeReason = reason.Length == 0 ? "" : $"<reason>{XmppXml.Escape(reason)}</reason>";
        var roomJid = $"{room}@{ConferenceDomain}";
        var inviterJid = $"{inviterId.ToString(System.Globalization.CultureInfo.InvariantCulture)}@{Settings.Domain}";
        target.Send($"<message from='{XmppXml.Escape(roomJid)}' to='{XmppXml.Escape(target.Jid)}'><x xmlns='http://jabber.org/protocol/muc#user'><invite from='{XmppXml.Escape(inviterJid)}'>{safeReason}</invite></x></message>");
        return true;
    }

internal static string? PrivateChannelName(long callerCharacterId, long targetCharacterId)
    {
        if (callerCharacterId <= 0 || targetCharacterId <= 0 || callerCharacterId == targetCharacterId)
            return null;
        var first = Math.Min(callerCharacterId, targetCharacterId);
        var second = Math.Max(callerCharacterId, targetCharacterId);
        return $"private_{first}_{second}";
    }

    internal bool InvitePrivateParticipant(XmppLink inviter, string room, long targetCharacterId, string reason) =>
        InvitePrivateParticipantAsync(inviter, room, targetCharacterId, reason, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    internal async ValueTask<bool> InvitePrivateParticipantAsync(
        XmppLink inviter,
        string room,
        long targetCharacterId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!TryPrivateRoomMembers(room, out var first, out var second)
            || inviter.CharacterId != first && inviter.CharacterId != second
            || targetCharacterId != first && targetCharacterId != second
            || targetCharacterId == inviter.CharacterId
            || Encoding.UTF8.GetByteCount(reason) > 256)
            return false;

        XmppLink? target;
        lock (_gate)
        {
            if (!_occupants.ContainsKey((room, inviter.CharacterId))
                || !_linkByCharacter.TryGetValue(targetCharacterId, out target))
                return false;
            var now = DateTimeOffset.UtcNow;
            if (_lastPrivateInvite.TryGetValue(inviter.CharacterId, out var lastInvite)
                && now - lastInvite < TimeSpan.FromSeconds(1))
                return false;
            _lastPrivateInvite[inviter.CharacterId] = now;
        }

        if (await IsCharacterBlockedByAsync(targetCharacterId, inviter.CharacterId, cancellationToken))
            return false;

        var safeReason = reason.Length == 0 ? "" : $"<reason>{XmppXml.Escape(reason)}</reason>";
        var roomJid = $"{room}@{ConferenceDomain}";
        var inviterJid = $"{inviter.CharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture)}@{Settings.Domain}";
        target.Send($"<message from='{XmppXml.Escape(roomJid)}' to='{XmppXml.Escape(target.Jid)}'><x xmlns='http://jabber.org/protocol/muc#user'><invite from='{XmppXml.Escape(inviterJid)}'>{safeReason}</invite></x></message>");
        return true;
    }

    private bool HasPrivateRoomAccess(ClientSession? session, string room) =>
        session?.CharacterId is { } characterId
        && TryPrivateRoomMembers(room, out var first, out var second)
        && (characterId == first || characterId == second);

    private static bool TryPrivateRoomMembers(string room, out long first, out long second)
    {
        first = 0;
        second = 0;
        var parts = room.Split('_');
        return parts.Length == 3 && parts[0] == "private"
            && long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out first)
            && long.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out second)
            && first > 0 && second > first;
    }

    private static bool ValidRoomName(string room) => room.Length is > 0 and <= MaxRoomNameLength
        && room.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool ValidNickname(string nickname) => nickname.Length is > 0 and <= MaxNicknameLength
        && nickname.All(character => !char.IsControl(character) && character is not '/' and not '@');

    internal sealed record XmppOccupant(XmppLink Link, string Nickname);
}
