using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;
using Eve.Services.Core;

namespace Eve.Host.Xmpp;

/// <summary>What the chat service publishes: the XMPP domain the client builds its JIDs from, and the address it dials.</summary>
public sealed record XmppChatSettings(string Domain, string ConnectHost);

/// <summary>
/// One authenticated XMPP connection of a character. Each character has at most one; a later bind replaces the earlier one.
/// </summary>
public sealed class XmppLink
{
    private readonly Action<string> _send;
    private readonly Action _close;

    internal XmppLink(long characterId, string resource, string domain, long accountRole, CharMgrChatUserData? chatUserData, Action<string> send, Action close)
    {
        CharacterId = characterId;
        Resource = resource;
        Domain = domain;
        AccountRole = accountRole;
        ChatUserData = chatUserData;
        _send = send;
        _close = close;
    }

    public long CharacterId { get; }
    public long AccountRole { get; }
    public string Resource { get; }
    public string Domain { get; }
    internal CharMgrChatUserData? ChatUserData { get; }

    /// <summary>The full JID the client bound (<c>charid@domain/resource</c>).</summary>
    public string Jid => $"{CharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture)}@{Domain}/{Resource}";

    /// <summary>Queues a stanza for the connection. Never blocks: a connection that falls behind is closed.</summary>
    internal void Send(string stanza) => _send(stanza);

    internal void Close() => _close();
}

/// <summary>
/// The chat rooms the client joins. This class owns the character-to-connection registry and the token check; room
/// membership is added on top of it. Only a character with an authenticated XMPP connection is an occupant.
/// </summary>
public sealed partial class XmppChatHub
{
    private readonly ConnectedSessions _sessions;
    private readonly Func<long, CancellationToken, ValueTask<IReadOnlyList<long>>>? _readBlockedOwners;
    private readonly Func<long, CancellationToken, ValueTask<CharMgrChatUserData?>>? _readChatUserData;
    private readonly XmppMotdStore? _motdStore;
    private readonly object _gate = new();
    private readonly Dictionary<long, XmppLink> _linkByCharacter = [];
    private readonly HashSet<ClientSession> _trackedSessions = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<long> _resourceWarsInstances = [];

    public XmppChatHub(
        ConnectedSessions sessions,
        XmppChatSettings settings,
        Func<long, CancellationToken, ValueTask<IReadOnlyList<long>>>? readBlockedOwners = null)
        : this(sessions, settings, null, readBlockedOwners)
    {
    }

    internal XmppChatHub(
        ConnectedSessions sessions,
        XmppChatSettings settings,
        XmppMotdStore? motdStore,
        Func<long, CancellationToken, ValueTask<IReadOnlyList<long>>>? readBlockedOwners = null,
        XmppPlayerChannelStore? playerChannelStore = null,
        Func<long, CancellationToken, ValueTask<CharMgrChatUserData?>>? readChatUserData = null)
    {
        _sessions = sessions;
        Settings = settings;
        _readBlockedOwners = readBlockedOwners;
        _readChatUserData = readChatUserData;
        _motdStore = motdStore;
        PlayerChannels = playerChannelStore;
        _sessions.Added += TrackSession;
        _sessions.Removed += UntrackSession;
        foreach (var session in _sessions.Snapshot())
            TrackSession(session);
        if (motdStore is not null)
        {
            foreach (var (room, motd) in motdStore.RoomMotds)
                _roomMotd[room] = motd;
        }
    }

    public XmppChatSettings Settings { get; }
    internal XmppPlayerChannelStore? PlayerChannels { get; }

    internal string? EnsureResourceWarsChannel(long instanceId)
    {
        if (instanceId <= 0)
            return null;
        lock (_gate)
            _resourceWarsInstances.Add(instanceId);
        return $"resourcewars_{instanceId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private bool HasResourceWarsInstance(long instanceId)
    {
        lock (_gate)
            return _resourceWarsInstances.Contains(instanceId);
    }

    private void RetireEmptyResourceWarsChannel(string room)
    {
        const string prefix = "resourcewars_";
        if (room.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(room.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var instanceId))
            _resourceWarsInstances.Remove(instanceId);
    }

    /// <summary>True only when a connected session of the character holds the token the chat service issued for it.</summary>
    public bool Authenticate(long characterId, string token)
    {
        if (characterId <= 0 || string.IsNullOrEmpty(token))
            return false;
        var session = SessionOf(characterId);
        var expected = session is null ? null : ChatAuthenticationService.StoredToken(session, characterId);
        if (string.IsNullOrEmpty(expected))
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token));
    }

    /// <summary>Registers the connection of an authenticated character. A character's earlier connection is closed with a conflict.</summary>
    internal XmppLink? Bind(long characterId, string resource, Action<string> send, Action close)
    {
        var session = SessionOf(characterId);
        if (session is null)
            return null;
        return Bind(session, resource, SessionChatUserData(session), send, close);
    }

    internal async ValueTask<XmppLink?> BindAsync(long characterId, string resource, Action<string> send, Action close, CancellationToken cancellationToken)
    {
        var session = SessionOf(characterId);
        if (session is null)
            return null;
        var userData = _readChatUserData is null
            ? SessionChatUserData(session)
            : await _readChatUserData(characterId, cancellationToken).ConfigureAwait(false);
        return Bind(session, resource, userData, send, close);
    }

    private XmppLink? Bind(ClientSession session, string resource, CharMgrChatUserData? userData, Action<string> send, Action close)
    {
        var characterId = session.CharacterId ?? 0;
        lock (_gate)
        {
            if (characterId <= 0 || !ReferenceEquals(SessionOf(characterId), session))
                return null;
            var link = new XmppLink(characterId, resource, Settings.Domain, session.GetInt("accountRole") ?? 0, userData, send, close);
            if (_linkByCharacter.Remove(characterId, out var previous))
            {
                previous.Send($"<stream:error><conflict xmlns='{XmppXml.StreamsNamespace}'/></stream:error>");
                previous.Close();
            }
            _linkByCharacter[characterId] = link;
            return link;
        }
    }

    internal async ValueTask<CharMgrChatUserData?> ReadChatUserDataAsync(long characterId, CancellationToken cancellationToken)
    {
        XmppLink? link;
        lock (_gate)
            _linkByCharacter.TryGetValue(characterId, out link);
        if (link?.ChatUserData is { } cached)
            return cached;
        if (_readChatUserData is not null)
            return await _readChatUserData(characterId, cancellationToken).ConfigureAwait(false);
        return SessionOf(characterId) is { } session ? SessionChatUserData(session) : null;
    }

    internal string UserDataXml(long characterId, CharMgrChatUserData? userData = null)
    {
        var session = SessionOf(characterId);
        userData ??= session is null ? null : CachedOrSessionChatUserData(characterId, session);
        if (userData is null)
            return "<eve_user_data corpid='0' role='0' warfactionid='None' allianceid='None'/>";

        var attributes = session?.SnapshotAttributes();
        var corporationId = SessionId(attributes, "corpid", userData.CorporationId);
        var allianceId = SessionId(attributes, "allianceid", userData.AllianceId);
        var warFactionId = SessionId(attributes, "warfactionid", userData.WarFactionId);
        var role = SessionId(attributes, "role", 0);
        var info = JsonSerializer.Serialize(
            new object?[] { userData.CharacterId, userData.CharacterName, userData.TypeId, userData.IsFemale, null },
            ChatUserDataJsonOptions);
        return $"<eve_user_data info='{XmppXml.Escape(info)}' corpid='{Format(corporationId)}' role='{Format(role)}' warfactionid='{FormatOptionalId(warFactionId)}' allianceid='{FormatOptionalId(allianceId)}'/>";
    }

    private static readonly JsonSerializerOptions ChatUserDataJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private CharMgrChatUserData? CachedOrSessionChatUserData(long characterId, ClientSession session)
    {
        lock (_gate)
            if (_linkByCharacter.TryGetValue(characterId, out var link) && link.ChatUserData is { } cached)
                return cached;
        return SessionChatUserData(session);
    }

    private static CharMgrChatUserData? SessionChatUserData(ClientSession session)
    {
        if (session.CharacterId is not { } characterId || characterId <= 0
            || session.Get("characterName") is not PyStr name || string.IsNullOrWhiteSpace(name.Value))
            return null;
        var typeId = session.GetInt("typeID") ?? 1373;
        return new CharMgrChatUserData(
            characterId,
            name.Value,
            typeId is > 0 and <= int.MaxValue ? (int)typeId : 1373,
            (session.GetInt("genderID") ?? session.GetInt("gender") ?? 0) != 0,
            session.GetInt("corpid") ?? 0,
            session.GetInt("allianceid") ?? 0,
            session.GetInt("warfactionid") ?? 0);
    }

    private static long SessionId(IReadOnlyDictionary<string, PyValue>? attributes, string name, long fallback)
    {
        if (attributes is null || !attributes.TryGetValue(name, out var value))
            return fallback;
        return value switch
        {
            PyInt number => number.Value,
            PyLong number => number.Value,
            PyNone => 0,
            _ => fallback,
        };
    }

    private static string Format(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatOptionalId(long value) => value > 0 ? Format(value) : "None";

    private void TrackSession(ClientSession session)
    {
        lock (_gate)
            if (!_trackedSessions.Add(session))
                return;
        session.AttributesChanged += OnSessionAttributesChanged;
    }

    private void UntrackSession(ClientSession session)
    {
        lock (_gate)
            if (!_trackedSessions.Remove(session))
                return;
        session.AttributesChanged -= OnSessionAttributesChanged;
    }

    private void OnSessionAttributesChanged(ClientSession session, IReadOnlyList<string> changed)
    {
        if (session.CharacterId is not { } characterId || characterId <= 0 || !ReferenceEquals(SessionOf(characterId), session))
            return;

        var scopeChanged = changed.Any(name => name is "corpid" or "allianceid" or "fleetid" or "warfactionid");
        if (scopeChanged)
            SynchronizeScopedMembership(session, characterId, changed);

        if (changed.Any(name => name is "corpid" or "allianceid" or "warfactionid" or "role" or "corprole"))
            RefreshRoomPresence(characterId);
    }

    private void SynchronizeScopedMembership(ClientSession session, long characterId, IReadOnlyList<string> changed)
    {
        XmppLink? link;
        lock (_gate)
            _linkByCharacter.TryGetValue(characterId, out link);
        if (link is null)
            return;

        string nickname;
        lock (_gate)
            nickname = _occupants.Values.FirstOrDefault(occupant => ReferenceEquals(occupant.Link, link))?.Nickname
                ?? link.ChatUserData?.CharacterName
                ?? (session.Get("characterName") as PyStr)?.Value
                ?? Format(characterId);

        string[] staleRooms;
        lock (_gate)
            staleRooms = _occupants.Keys.Where(key => key.CharacterId == characterId && IsSessionScopedRoom(key.Room)
                && !CanAccessRoom(session, characterId, key.Room)).Select(key => key.Room).ToArray();
        foreach (var room in staleRooms)
            LeaveRoom(link, room);

        foreach (var (sessionKey, kind, prefix) in new[]
        {
            ("corpid", "corp", "corp_"),
            ("allianceid", "alliance", "alliance_"),
            ("fleetid", "fleet", "fleet_"),
            ("warfactionid", "faction", "faction_"),
        })
        {
            if (!changed.Contains(sessionKey, StringComparer.Ordinal) || session.GetInt(sessionKey) is not { } id || id <= 0)
                continue;
            var room = $"{prefix}{Format(id)}";
            if (CanAccessRoom(session, characterId, room))
                TryJoinRoom(link, room, nickname, "", out _);
        }
    }

    private static bool IsSessionScopedRoom(string room) => room.StartsWith("corp_", StringComparison.Ordinal)
        || room.StartsWith("alliance_", StringComparison.Ordinal)
        || room.StartsWith("fleet_", StringComparison.Ordinal)
        || room.StartsWith("faction_", StringComparison.Ordinal);

    private void RefreshRoomPresence(long characterId)
    {
        (string Room, XmppOccupant Occupant, XmppOccupant[] Recipients)[] targets;
        lock (_gate)
            targets = _occupants.Where(pair => pair.Key.CharacterId == characterId)
                .Select(pair => (pair.Key.Room, pair.Value, _occupants.Where(other => other.Key.Room == pair.Key.Room
                    && ((other.Value.Link.AccountRole & GameMasterRoles.ChtInvisible) == 0
                        || ReferenceEquals(other.Value.Link, pair.Value.Link))).Select(other => other.Value).ToArray()))
                .ToArray();

        foreach (var (room, occupant, recipients) in targets)
        {
            if (Eve.Host.PublicGateway.LocalChatGateway.IsDelayedLocalChannel(room))
            {
                var key = (room, characterId);
                lock (_gate)
                    if (!_visibleLocalSpeakers.Contains(key))
                        continue;
            }
            var visibleRecipients = (occupant.Link.AccountRole & GameMasterRoles.ChtInvisible) != 0
                ? recipients.Where(recipient => ReferenceEquals(recipient.Link, occupant.Link)).ToArray()
                : recipients;
            foreach (var recipient in visibleRecipients)
            {
                recipient.Link.Send(Presence(room, occupant, recipient.Link.Jid, ReferenceEquals(occupant.Link, recipient.Link), available: false));
                recipient.Link.Send(Presence(room, occupant, recipient.Link.Jid, ReferenceEquals(occupant.Link, recipient.Link)));
            }
        }
    }

    /// <summary>Drops a connection when its stream ends. A replaced connection is already gone and changes nothing.</summary>
    internal void Unbind(XmppLink link)
    {
        lock (_gate)
        {
            if (_linkByCharacter.TryGetValue(link.CharacterId, out var current) && ReferenceEquals(current, link))
            {
                _linkByCharacter.Remove(link.CharacterId);
                _lastPrivateInvite.Remove(link.CharacterId);
                _lastRoomMessage.Remove(link.CharacterId);
            }
        }
    }

    private ClientSession? SessionOf(long characterId) =>
        _sessions.Snapshot().FirstOrDefault(session => session.CharacterId == characterId);

    internal async ValueTask<bool> IsBlockedByAsync(long recipientCharacterId, long senderCharacterId, CancellationToken cancellationToken)
    {
        var blocked = await ReadBlockedOwnersAsync(recipientCharacterId, cancellationToken);
        if (blocked.Count == 0)
            return false;
        var sender = SessionOf(senderCharacterId);
        return blocked.Contains(senderCharacterId)
            || sender?.GetInt("corpid") is { } corporationId && corporationId > 0 && blocked.Contains(corporationId)
            || sender?.GetInt("allianceid") is { } allianceId && allianceId > 0 && blocked.Contains(allianceId);
    }

    internal async ValueTask<bool> IsCharacterBlockedByAsync(long recipientCharacterId, long senderCharacterId, CancellationToken cancellationToken)
    {
        var blocked = await ReadBlockedOwnersAsync(recipientCharacterId, cancellationToken);
        return blocked.Contains(senderCharacterId);
    }

    private async ValueTask<IReadOnlyList<long>> ReadBlockedOwnersAsync(long recipientCharacterId, CancellationToken cancellationToken)
    {
        if (_readBlockedOwners is null || recipientCharacterId <= 0)
            return [];
        return await _readBlockedOwners(recipientCharacterId, cancellationToken);
    }

    internal IReadOnlyList<string> StaticAccessChannels(ClientSession session)
    {
        if (session.CharacterId is not { } characterId || characterId <= 0
            || !ReferenceEquals(SessionOf(characterId), session))
            return [];

        var channels = new List<string>(4);
        var systemId = session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0;
        if (systemId > 0)
            channels.Add(Eve.Host.PublicGateway.LocalChatGateway.LocalChannelName(systemId));

        if (session.GetInt("allianceid") is { } allianceId && allianceId > 0)
            channels.Add($"alliance_{allianceId}");
        if (session.GetInt("corpid") is { } corporationId && corporationId > 0)
            channels.Add($"corp_{corporationId}");
        if (session.GetInt("fleetid") is { } fleetId && fleetId > 0)
            channels.Add($"fleet_{fleetId}");
        if (session.GetInt("warfactionid") is { } warFactionId && warFactionId > 0)
            channels.Add($"faction_{warFactionId}");
        return channels;
    }
}
