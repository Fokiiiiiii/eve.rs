using System.Collections.Concurrent;
using System.Net;
using System.Linq;
using System.Text;
using Eve.Services;

namespace Eve.Host.PublicGateway;

/// <summary>The V24.01 public-gateway Local chat requests and notices (chat/client/local_chat/messenger.py).</summary>
internal sealed class LocalChatGateway
{
    private enum ChannelKind : ulong
    {
        Local = 0,
        Corporation = 1,
        Alliance = 2,
        Fleet = 3,
        Wing = 4,
        Squad = 5,
    }

    private readonly record struct ChannelAddress(ChannelKind Kind, long Id, long ParentId = 0, long RootId = 0);
    private const string Prefix = "eve_public.chat.local.api.";
    private const string GetMembersRequest = Prefix + "GetMembershipListRequest";
    private const string GetMembersResponse = Prefix + "GetMembershipListResponse";
    private const string BroadcastRequest = Prefix + "BroadcastMessageRequest";
    private const string BroadcastResponse = Prefix + "BroadcastMessageResponse";
    private const string MembershipNotice = Prefix + "MembershipListNotice";
    private const string JoinNotice = Prefix + "JoinNotice";
    private const string LeaveNotice = Prefix + "LeaveNotice";
    private const string MemberRefreshNotice = Prefix + "MembershipRefreshedNotice";
    private const string MessageNotice = Prefix + "MessageBroadcastNotice";
    private const int MaxMessageBytes = 1024;
    private const int MaxMessagesPerWindow = 3;
    private const int MaxMessagesPerChannelWindow = 30;
    private static readonly TimeSpan FloodWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleMemberRemovalTime = TimeSpan.FromSeconds(900);

    private readonly ConnectedSessions _sessions;
    private readonly NoticeHub _notices;
    private readonly ConcurrentDictionary<ClientSession, Queue<DateTimeOffset>> _messageTimes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ChannelAddress, Queue<DateTimeOffset>> _channelMessageTimes = [];
    private readonly object _gate = new();
    private readonly Dictionary<long, HashSet<long>> _speakersBySystem = [];
    private readonly Dictionary<(long SystemId, long CharacterId), DateTimeOffset> _lastSpoke = [];
    private readonly Dictionary<long, long> _systemByCharacter = [];
    private readonly Dictionary<ClientSession, long> _characterBySession = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ClientSession, Dictionary<ChannelKind, ChannelAddress>> _groupChannelsBySession = new(ReferenceEqualityComparer.Instance);

    public LocalChatGateway(ConnectedSessions sessions, NoticeHub notices)
    {
        _sessions = sessions;
        _notices = notices;
        sessions.Added += Track;
        sessions.Removed += Untrack;
        foreach (var session in sessions.Snapshot()) Track(session);
    }

    public IReadOnlyDictionary<string, GatewayHandler> Handlers() => new Dictionary<string, GatewayHandler>(StringComparer.Ordinal)
    {
        [GetMembersRequest] = GetMembershipListAsync,
        [BroadcastRequest] = BroadcastAsync,
    };

    private Task<GatewayAnswer> GetMembershipListAsync(GatewayRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var characterId = request.CharacterId;
        var session = ActiveCharacter(characterId);
        if (session is null)
            return Task.FromResult(new GatewayAnswer(409, "the active character is not connected", GetMembersResponse, []));

        if (!TryReadChannelKind(request.Payload, 1, out var kind))
            return Task.FromResult(new GatewayAnswer(400, "unknown chat channel", GetMembersResponse, []));
        var channel = ChannelForSession(session, kind);
        if (channel is null)
            return Task.FromResult(new GatewayAnswer(409, "the character is not a member of that chat channel", GetMembersResponse, []));
        var members = VisibleMembers(channel.Value);
        var writer = new ProtoWriter();
        foreach (var member in members)
            writer.Message(1, EncodeCharacter(member));
        WriteChannel(writer, channel.Value, 3);
        return Task.FromResult(new GatewayAnswer(200, "", GetMembersResponse, writer.ToArray()));
    }

    private Task<GatewayAnswer> BroadcastAsync(GatewayRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = ActiveCharacter(request.CharacterId);
        if (session is null)
            return Task.FromResult(new GatewayAnswer(409, "the active character is not connected", BroadcastResponse, []));
        if (!TryReadChannelKind(request.Payload, 2, out var kind))
            return Task.FromResult(new GatewayAnswer(400, "unknown chat channel", BroadcastResponse, []));
        var channel = ChannelForSession(session, kind);
        if (channel is null)
            return Task.FromResult(new GatewayAnswer(409, "the character is not a member of that chat channel", BroadcastResponse, []));
        var message = ReadString(request.Payload, 1).Trim();
        if (message.Length == 0 || Encoding.UTF8.GetByteCount(message) > MaxMessageBytes)
            return Task.FromResult(new GatewayAnswer(400, "chat message is empty or too long", BroadcastResponse, []));
        var sentAt = DateTimeOffset.UtcNow;
        if (!PermitMessage(session, sentAt) || !PermitChannelMessage(channel.Value, sentAt))
            return Task.FromResult(new GatewayAnswer(429, "chat message rate limit exceeded", BroadcastResponse, []));

        var characterId = session.CharacterId!.Value;
        var safeMessage = WebUtility.HtmlEncode(message);
        if (kind == ChannelKind.Local)
        {
            bool joined;
            lock (_gate)
            {
                if (!_speakersBySystem.TryGetValue(channel.Value.Id, out var speakers))
                    _speakersBySystem[channel.Value.Id] = speakers = [];
                joined = speakers.Add(characterId);
                _lastSpoke[(channel.Value.Id, characterId)] = DateTimeOffset.UtcNow;
            }

            if (joined && IsDelayed(channel.Value.Id))
            {
                var join = new ProtoWriter();
                join.Message(1, EncodeCharacter(session));
                WriteChannel(join, channel.Value, 3);
                PublishToChannel(channel.Value, JoinNotice, join.ToArray());
                PublishMembershipList(channel.Value);
            }
        }

        var notice = new ProtoWriter();
        notice.Message(1, EncodeIdentifier(characterId));
        WriteChannel(notice, channel.Value, 4);
        notice.String(3, safeMessage);
        PublishToChannel(channel.Value, MessageNotice, notice.ToArray());
        return Task.FromResult(new GatewayAnswer(200, "", BroadcastResponse, []));
    }

    private void Track(ClientSession session)
    {
        session.AttributesChanged += OnAttributesChanged;
        session.Closed += () => Untrack(session);
        if (session.CharacterId is { } characterId)
            EnterSystem(session, characterId, CurrentSystem(session));
        RefreshGroupChannels(session);
    }

    private void Untrack(ClientSession session)
    {
        session.AttributesChanged -= OnAttributesChanged;
        if (session.CharacterId is { } characterId)
            LeaveSystem(session, characterId);
        LeaveGroupChannels(session);
        _messageTimes.TryRemove(session, out _);
    }

    private void OnAttributesChanged(ClientSession session, IReadOnlyList<string> changed)
    {
        var characterChanged = changed.Contains("charid", StringComparer.Ordinal);
        var systemChanged = changed.Contains("solarsystemid2", StringComparer.Ordinal)
            || changed.Contains("solarsystemid", StringComparer.Ordinal);
        var affiliationChanged = changed.Any(name => name is "corpid" or "allianceid" or "warfactionid" or "accountRole" or "role" or "fleetid" or "wingid" or "squadid");
        if (!characterChanged && !systemChanged && !affiliationChanged)
            return;

        long? oldCharacter;
        lock (_gate)
            oldCharacter = _characterBySession.GetValueOrDefault(session);
        var newCharacter = session.CharacterId;
        var nextSystem = CurrentSystem(session);
        if (oldCharacter != newCharacter)
        {
            if (oldCharacter is > 0) LeaveSystem(session, oldCharacter.Value);
            if (newCharacter is > 0 && nextSystem > 0) EnterSystem(session, newCharacter.Value, nextSystem);
            RefreshGroupChannels(session);
            return;
        }
        if (newCharacter is not { } characterId) return;

        long? previousSystem;
        lock (_gate)
            previousSystem = _systemByCharacter.GetValueOrDefault(characterId);
        if (previousSystem != nextSystem)
        {
            if (previousSystem is > 0) LeaveSystem(session, characterId);
            if (nextSystem > 0) EnterSystem(session, characterId, nextSystem);
        }
        else if (affiliationChanged && nextSystem > 0 && VisibleMembers(nextSystem).Any(member => member.CharacterId == characterId))
        {
            var notice = new ProtoWriter();
            notice.Message(1, EncodeCharacter(session));
            notice.Message(2, EncodeIdentifier(nextSystem));
            PublishToSystem(nextSystem, MemberRefreshNotice, notice.ToArray());
        }
        if (affiliationChanged)
            RefreshGroupChannels(session);
    }

    private void EnterSystem(ClientSession session, long characterId, long systemId)
    {
        lock (_gate)
        {
            _systemByCharacter[characterId] = systemId;
            _characterBySession[session] = characterId;
        }
        if (!IsDelayed(systemId))
        {
            var notice = new ProtoWriter();
            notice.Message(1, EncodeCharacter(session));
            notice.Message(2, EncodeIdentifier(systemId));
            PublishToSystem(systemId, JoinNotice, notice.ToArray(), characterId);
            PublishMembershipList(systemId);
        }
    }

    private void LeaveSystem(ClientSession session, long characterId)
    {
        long systemId;
        lock (_gate)
        {
            if (!_systemByCharacter.Remove(characterId, out systemId)) return;
            if (_characterBySession.TryGetValue(session, out var activeCharacter) && activeCharacter == characterId)
                _characterBySession.Remove(session);
            if (_speakersBySystem.TryGetValue(systemId, out var speakers))
            {
                speakers.Remove(characterId);
                _lastSpoke.Remove((systemId, characterId));
                if (speakers.Count == 0) _speakersBySystem.Remove(systemId);
            }
        }
        var notice = new ProtoWriter();
        notice.Message(1, EncodeIdentifier(characterId));
        notice.Message(2, EncodeIdentifier(systemId));
        PublishToSystem(systemId, LeaveNotice, notice.ToArray(), characterId);
        PublishMembershipList(systemId);
    }

    private void PublishMembershipList(long systemId) => PublishMembershipList(new ChannelAddress(ChannelKind.Local, systemId));

    private void PublishMembershipList(ChannelAddress channel)
    {
        var notice = new ProtoWriter();
        foreach (var member in VisibleMembers(channel))
            notice.Message(1, EncodeCharacter(member));
        WriteChannel(notice, channel, 3);
        PublishToChannel(channel, MembershipNotice, notice.ToArray());
    }

    private void PublishToSystem(long systemId, string type, byte[] payload, long? exceptCharacter = null)
    {
        foreach (var session in _sessions.Snapshot())
        {
            if (session.CharacterId is not { } characterId || characterId == exceptCharacter || CurrentSystem(session) != systemId)
                continue;
            _notices.Publish(type, payload, character: characterId);
        }
    }

    private void PublishToChannel(ChannelAddress channel, string type, byte[] payload, long? exceptCharacter = null)
    {
        if (channel.Kind == ChannelKind.Local)
        {
            PublishToSystem(channel.Id, type, payload, exceptCharacter);
            return;
        }

        foreach (var session in _sessions.Snapshot())
        {
            if (session.CharacterId is not { } characterId || characterId == exceptCharacter
                || ChannelForSession(session, channel.Kind) != channel)
                continue;
            _notices.Publish(type, payload, character: characterId);
        }
    }

    private IReadOnlyList<ClientSession> VisibleMembers(long systemId) => VisibleMembers(new ChannelAddress(ChannelKind.Local, systemId));

    private IReadOnlyList<ClientSession> VisibleMembers(ChannelAddress channel)
    {
        var current = _sessions.Snapshot()
            .Where(s => s.CharacterId is > 0 && ChannelForSession(s, channel.Kind) == channel)
            .GroupBy(s => s.CharacterId!.Value)
            .Select(g => g.First())
            .ToDictionary(s => s.CharacterId!.Value);
        if (channel.Kind != ChannelKind.Local || !IsDelayed(channel.Id))
            return [.. current.Values];
        lock (_gate)
        {
            if (!_speakersBySystem.TryGetValue(channel.Id, out var speakers)) return [];
            var now = DateTimeOffset.UtcNow;
            speakers.RemoveWhere(id =>
            {
                var stale = !_lastSpoke.TryGetValue((channel.Id, id), out var lastSpoke)
                    || now - lastSpoke >= IdleMemberRemovalTime;
                if (stale) _lastSpoke.Remove((channel.Id, id));
                return stale || !current.ContainsKey(id);
            });
            if (speakers.Count == 0) _speakersBySystem.Remove(channel.Id);
            return [.. speakers.Where(current.ContainsKey).Select(id => current[id])];
        }
    }

    private bool PermitMessage(ClientSession session, DateTimeOffset now)
    {
        var times = _messageTimes.GetOrAdd(session, _ => new Queue<DateTimeOffset>());
        lock (times)
        {
            while (times.Count > 0 && now - times.Peek() >= FloodWindow)
                times.Dequeue();
            if (times.Count >= MaxMessagesPerWindow) return false;
            times.Enqueue(now);
            return true;
        }
    }

    private bool PermitChannelMessage(ChannelAddress channel, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_channelMessageTimes.TryGetValue(channel, out var times))
                _channelMessageTimes[channel] = times = new Queue<DateTimeOffset>();
            while (times.Count > 0 && now - times.Peek() >= FloodWindow)
                times.Dequeue();
            if (times.Count >= MaxMessagesPerChannelWindow) return false;
            times.Enqueue(now);
            return true;
        }
    }

    private ClientSession? ActiveCharacter(long? characterId) => characterId is { } id
        ? _sessions.Snapshot().FirstOrDefault(s => s.CharacterId == id)
        : null;

    private void RefreshGroupChannels(ClientSession session)
    {
        var next = new Dictionary<ChannelKind, ChannelAddress>();
        foreach (var kind in new[] { ChannelKind.Corporation, ChannelKind.Alliance, ChannelKind.Fleet, ChannelKind.Wing, ChannelKind.Squad })
            if (ChannelForSession(session, kind) is { } channel)
                next[kind] = channel;

        Dictionary<ChannelKind, ChannelAddress> previous;
        lock (_gate)
        {
            previous = _groupChannelsBySession.GetValueOrDefault(session) ?? [];
            _groupChannelsBySession[session] = next;
        }

        foreach (var (kind, channel) in previous)
            if (!next.TryGetValue(kind, out var current) || current != channel)
                PublishGroupMembershipChange(session, channel, LeaveNotice, joined: false);
        foreach (var (kind, channel) in next)
            if (!previous.TryGetValue(kind, out var prior) || prior != channel)
                PublishGroupMembershipChange(session, channel, JoinNotice, joined: true);
        foreach (var channel in previous.Values.Concat(next.Values).Distinct())
            PublishMembershipList(channel);
    }

    private void LeaveGroupChannels(ClientSession session)
    {
        Dictionary<ChannelKind, ChannelAddress> previous;
        lock (_gate)
        {
            if (!_groupChannelsBySession.TryGetValue(session, out previous!)) return;
            _groupChannelsBySession.Remove(session);
        }
        foreach (var channel in previous.Values)
        {
            PublishGroupMembershipChange(session, channel, LeaveNotice, joined: false);
            PublishMembershipList(channel);
        }
    }

    private void PublishGroupMembershipChange(ClientSession session, ChannelAddress channel, string type, bool joined)
    {
        if (session.CharacterId is not { } characterId) return;
        var notice = new ProtoWriter();
        if (joined) notice.Message(1, EncodeCharacter(session)); else notice.Message(1, EncodeIdentifier(characterId));
        WriteChannel(notice, channel, 3);
        PublishToChannel(channel, type, notice.ToArray(), joined ? characterId : null);
    }

    private static ChannelAddress? ChannelForSession(ClientSession session, ChannelKind kind)
    {
        var id = kind switch
        {
            ChannelKind.Local => CurrentSystem(session),
            ChannelKind.Corporation => session.GetInt("corpid") ?? 0,
            ChannelKind.Alliance => session.GetInt("allianceid") ?? 0,
            ChannelKind.Fleet => session.GetInt("fleetid") ?? 0,
            ChannelKind.Wing => session.GetInt("wingid") ?? 0,
            ChannelKind.Squad => session.GetInt("squadid") ?? 0,
            _ => 0,
        };
        if (id <= 0) return null;
        var parent = kind switch
        {
            ChannelKind.Wing or ChannelKind.Squad => session.GetInt("fleetid") ?? 0,
            _ => 0,
        };
        var root = kind == ChannelKind.Squad ? session.GetInt("wingid") ?? 0 : 0;
        if (kind is ChannelKind.Wing or ChannelKind.Squad && parent <= 0) return null;
        if (kind == ChannelKind.Squad && root <= 0) return null;
        return new ChannelAddress(kind, id, parent, root);
    }

    private static bool TryReadChannelKind(ReadOnlyMemory<byte> payload, int number, out ChannelKind kind)
    {
        var field = ProtoReader.Fields(payload).FirstOrDefault(f => f.Number == number && f.WireType == 0);
        if (field.Number != number)
        {
            kind = ChannelKind.Local;
            return true;
        }
        if (Enum.IsDefined(typeof(ChannelKind), field.Scalar))
        {
            kind = (ChannelKind)field.Scalar;
            return true;
        }
        kind = ChannelKind.Local;
        return false;
    }

    private static void WriteChannel(ProtoWriter writer, ChannelAddress channel, int kindField)
    {
        writer.Message(2, EncodeIdentifier(channel.Id));
        writer.Uint(kindField, (ulong)channel.Kind);
        if (channel.ParentId > 0) writer.Message(kindField + 1, EncodeIdentifier(channel.ParentId));
        if (channel.RootId > 0) writer.Message(kindField + 2, EncodeIdentifier(channel.RootId));
    }

    private static long CurrentSystem(ClientSession session) => session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0;
    private static bool IsDelayed(long systemId) => systemId is >= 31_000_000 and <= 31_999_999;

    private static string ReadString(ReadOnlyMemory<byte> payload, int number)
    {
        var field = ProtoReader.Fields(payload).FirstOrDefault(f => f.Number == number && f.WireType == 2);
        return field.Number == number ? Encoding.UTF8.GetString(field.Bytes.Span) : "";
    }

    private static byte[] EncodeIdentifier(long sequential)
    {
        var writer = new ProtoWriter();
        writer.Uint(1, (ulong)sequential);
        return writer.ToArray();
    }

    private static byte[] EncodeCharacter(ClientSession session)
    {
        var character = new ProtoWriter();
        character.Message(1, EncodeIdentifier(session.CharacterId ?? 0));
        character.Message(2, EncodeIdentifier(session.GetInt("corpid") ?? 0));
        var alliance = session.GetInt("allianceid") ?? 0;
        if (alliance > 0) character.Message(4, EncodeIdentifier(alliance)); else character.Bool(3, true);
        var faction = session.GetInt("warfactionid") ?? 0;
        if (faction > 0) character.Message(6, EncodeIdentifier(faction)); else character.Bool(5, true);
        character.Uint(7, (ulong)Classification(session));
        return character.ToArray();
    }

    private static int Classification(ClientSession session)
    {
        var role = session.GetInt("accountRole") ?? 0;
        if ((role & GameMasterRoles.Qa) != 0 || (role & GameMasterRoles.Programmer) != 0) return 2;
        if ((role & GameMasterRoles.Admin) != 0) return 3;
        if ((role & (GameMasterRoles.Gml | GameMasterRoles.Gmh | GameMasterRoles.ExtGm1 | GameMasterRoles.ExtGm2 | GameMasterRoles.ExtGm3 | GameMasterRoles.ExtGm4)) != 0) return 4;
        return 0;
    }
}
