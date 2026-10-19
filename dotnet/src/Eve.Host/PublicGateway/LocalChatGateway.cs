using System.Collections.Concurrent;
using System.Net;
using System.Linq;
using System.Text;
using Eve.Services;

namespace Eve.Host.PublicGateway;

/// <summary>The V24.01 public-gateway Local chat requests and notices (chat/client/local_chat/messenger.py).</summary>
internal sealed class LocalChatGateway
{
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
    private readonly Dictionary<long, Queue<DateTimeOffset>> _channelMessageTimes = [];
    private readonly object _gate = new();
    private readonly Dictionary<long, HashSet<long>> _speakersBySystem = [];
    private readonly Dictionary<(long SystemId, long CharacterId), DateTimeOffset> _lastSpoke = [];
    private readonly Dictionary<long, long> _systemByCharacter = [];
    private readonly Dictionary<ClientSession, long> _characterBySession = new(ReferenceEqualityComparer.Instance);

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

        var systemId = CurrentSystem(session);
        var members = VisibleMembers(systemId);
        var writer = new ProtoWriter();
        foreach (var member in members)
            writer.Message(1, EncodeCharacter(member));
        writer.Message(2, EncodeIdentifier(systemId));
        return Task.FromResult(new GatewayAnswer(200, "", GetMembersResponse, writer.ToArray()));
    }

    private Task<GatewayAnswer> BroadcastAsync(GatewayRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = ActiveCharacter(request.CharacterId);
        if (session is null)
            return Task.FromResult(new GatewayAnswer(409, "the active character is not connected", BroadcastResponse, []));
        var message = ReadString(request.Payload, 1).Trim();
        if (message.Length == 0 || Encoding.UTF8.GetByteCount(message) > MaxMessageBytes)
            return Task.FromResult(new GatewayAnswer(400, "chat message is empty or too long", BroadcastResponse, []));
        var sentAt = DateTimeOffset.UtcNow;
        if (!PermitMessage(session, sentAt) || !PermitChannelMessage(CurrentSystem(session), sentAt))
            return Task.FromResult(new GatewayAnswer(429, "chat message rate limit exceeded", BroadcastResponse, []));

        var characterId = session.CharacterId!.Value;
        var systemId = CurrentSystem(session);
        var safeMessage = WebUtility.HtmlEncode(message);
        bool joined;
        lock (_gate)
        {
            if (!_speakersBySystem.TryGetValue(systemId, out var speakers))
                _speakersBySystem[systemId] = speakers = [];
            joined = speakers.Add(characterId);
            _lastSpoke[(systemId, characterId)] = DateTimeOffset.UtcNow;
        }

        if (joined && IsDelayed(systemId))
        {
            var join = new ProtoWriter();
            join.Message(1, EncodeCharacter(session));
            join.Message(2, EncodeIdentifier(systemId));
            PublishToSystem(systemId, JoinNotice, join.ToArray());
            PublishMembershipList(systemId);
        }

        var notice = new ProtoWriter();
        notice.Message(1, EncodeIdentifier(characterId));
        notice.Message(2, EncodeIdentifier(systemId));
        notice.String(3, safeMessage);
        PublishToSystem(systemId, MessageNotice, notice.ToArray());
        return Task.FromResult(new GatewayAnswer(200, "", BroadcastResponse, []));
    }

    private void Track(ClientSession session)
    {
        session.AttributesChanged += OnAttributesChanged;
        session.Closed += () => Untrack(session);
        if (session.CharacterId is { } characterId)
            EnterSystem(session, characterId, CurrentSystem(session));
    }

    private void Untrack(ClientSession session)
    {
        session.AttributesChanged -= OnAttributesChanged;
        if (session.CharacterId is { } characterId)
            LeaveSystem(session, characterId);
        _messageTimes.TryRemove(session, out _);
    }

    private void OnAttributesChanged(ClientSession session, IReadOnlyList<string> changed)
    {
        var characterChanged = changed.Contains("charid", StringComparer.Ordinal);
        var systemChanged = changed.Contains("solarsystemid2", StringComparer.Ordinal)
            || changed.Contains("solarsystemid", StringComparer.Ordinal);
        var affiliationChanged = changed.Any(name => name is "corpid" or "allianceid" or "warfactionid" or "accountRole" or "role");
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

    private void PublishMembershipList(long systemId)
    {
        var notice = new ProtoWriter();
        foreach (var member in VisibleMembers(systemId))
            notice.Message(1, EncodeCharacter(member));
        notice.Message(2, EncodeIdentifier(systemId));
        PublishToSystem(systemId, MembershipNotice, notice.ToArray());
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

    private IReadOnlyList<ClientSession> VisibleMembers(long systemId)
    {
        var current = _sessions.Snapshot()
            .Where(s => s.CharacterId is > 0 && CurrentSystem(s) == systemId)
            .GroupBy(s => s.CharacterId!.Value)
            .Select(g => g.First())
            .ToDictionary(s => s.CharacterId!.Value);
        if (!IsDelayed(systemId))
            return [.. current.Values];
        lock (_gate)
        {
            if (!_speakersBySystem.TryGetValue(systemId, out var speakers)) return [];
            var now = DateTimeOffset.UtcNow;
            speakers.RemoveWhere(id =>
            {
                var stale = !_lastSpoke.TryGetValue((systemId, id), out var lastSpoke)
                    || now - lastSpoke >= IdleMemberRemovalTime;
                if (stale) _lastSpoke.Remove((systemId, id));
                return stale || !current.ContainsKey(id);
            });
            if (speakers.Count == 0) _speakersBySystem.Remove(systemId);
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

    private bool PermitChannelMessage(long systemId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_channelMessageTimes.TryGetValue(systemId, out var times))
                _channelMessageTimes[systemId] = times = new Queue<DateTimeOffset>();
            while (times.Count > 0 && now - times.Peek() >= FloodWindow)
                times.Dequeue();
            if (times.Count >= MaxMessagesPerChannelWindow) return false;
            times.Enqueue(now);
            return true;
        }
    }

    private ClientSession? ActiveCharacter(long? characterId) => characterId is { } id
        ? _sessions.Snapshot().FirstOrDefault(s => s.CharacterId == id && CurrentSystem(s) > 0)
        : null;

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
