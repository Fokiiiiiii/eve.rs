using System.Security.Cryptography;
using System.Text;
using Eve.Services;
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

    internal XmppLink(long characterId, string resource, string domain, Action<string> send, Action close)
    {
        CharacterId = characterId;
        Resource = resource;
        Domain = domain;
        _send = send;
        _close = close;
    }

    public long CharacterId { get; }
    public string Resource { get; }
    public string Domain { get; }

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
    private readonly XmppMotdStore? _motdStore;
    private readonly object _gate = new();
    private readonly Dictionary<long, XmppLink> _linkByCharacter = [];

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
        Func<long, CancellationToken, ValueTask<IReadOnlyList<long>>>? readBlockedOwners = null)
    {
        _sessions = sessions;
        Settings = settings;
        _readBlockedOwners = readBlockedOwners;
        _motdStore = motdStore;
        if (motdStore is not null)
        {
            foreach (var (room, motd) in motdStore.RoomMotds)
                _roomMotd[room] = motd;
        }
    }

    public XmppChatSettings Settings { get; }

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
        lock (_gate)
        {
            if (SessionOf(characterId) is null)
                return null;
            var link = new XmppLink(characterId, resource, Settings.Domain, send, close);
            if (_linkByCharacter.Remove(characterId, out var previous))
            {
                previous.Send($"<stream:error><conflict xmlns='{XmppXml.StreamsNamespace}'/></stream:error>");
                previous.Close();
            }
            _linkByCharacter[characterId] = link;
            return link;
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
        return channels;
    }
}
