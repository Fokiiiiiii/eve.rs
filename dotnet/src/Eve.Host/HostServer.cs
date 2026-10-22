using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Eve.Identity;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

public sealed record HostOptions
{
    /// <summary>A session that has this many inbound packets waiting is cut off.</summary>
    public int MaxPendingPacketsPerSession { get; init; } = 10_000;

    /// <summary>Maximum bytes held by one session actor, including the packet being processed.</summary>
    public long MaxPendingBytesPerSession { get; init; } = 16 * 1024 * 1024;

    /// <summary>Maximum queued packets sent to one session.</summary>
    public int MaxQueuedOutboundPacketsPerSession { get; init; } = 512;

    /// <summary>Maximum serialized bytes queued to one session.</summary>
    public long MaxQueuedOutboundBytesPerSession { get; init; } = 8 * 1024 * 1024;

    /// <summary>Maximum queued packets sent through one front connection.</summary>
    public int MaxQueuedOutboundPacketsPerConnection { get; init; } = 8_192;

    /// <summary>Maximum simultaneous accepted front connections.</summary>
    public int MaxFrontConnections { get; init; } = 1_024;

    /// <summary>Maximum serialized bytes queued through one front connection.</summary>
    public long MaxQueuedOutboundBytesPerConnection { get; init; } = 64 * 1024 * 1024;

    /// <summary>Called with a problem worth logging.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Called for every new session, before its first packet: attach what must be released when it ends.</summary>
    public Action<ClientSession>? SessionOpened { get; init; }

    /// <summary>Persist account session start before the initial state is sent.</summary>
    public Func<ClientSession, CancellationToken, Task>? SessionOpenedAsync { get; init; }

    /// <summary>Persist account session end when its actor closes.</summary>
    public Func<ClientSession, Task>? SessionClosedAsync { get; init; }
}

/// <summary>
/// The service host: it accepts network fronts, and serves each front's sessions. Packets of one session
/// are handled one at a time, in order; different sessions run in parallel.
/// </summary>
internal sealed class LoginAttemptLimiter
{
    private const long WindowMilliseconds = 15 * 60 * 1000;
    private const int AccountFailureLimit = 10;
    private const int AddressFailureLimit = 50;
    private const int MaximumKeys = 4096;

    private sealed class Failures(int limit)
    {
        public int Limit { get; } = limit;
        public Queue<long> Times { get; } = new();
        public long LastFailure { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Failures> _failures = new(StringComparer.Ordinal);

    public bool IsAllowed(string accountName, string address)
    {
        var now = Environment.TickCount64;
        var accountKey = AccountKey(accountName);
        var addressKey = AddressKey(address);
        lock (_gate)
            return (accountKey is null || Count(accountKey, now) < AccountFailureLimit)
                && Count(addressKey, now) < AddressFailureLimit;
    }

    public void RecordFailure(string accountName, string address)
    {
        var now = Environment.TickCount64;
        var accountKey = AccountKey(accountName);
        var addressKey = AddressKey(address);
        lock (_gate)
        {
            if (accountKey is not null) Add(accountKey, AccountFailureLimit, now);
            Add(addressKey, AddressFailureLimit, now);
        }
    }

    public void ClearAccountFailures(string accountName)
    {
        var accountKey = AccountKey(accountName);
        if (accountKey is null) return;
        lock (_gate) _failures.Remove(accountKey);
    }

    private int Count(string key, long now)
    {
        if (!_failures.TryGetValue(key, out var failures)) return 0;
        while (failures.Times.TryPeek(out var timestamp) && now - timestamp >= WindowMilliseconds)
            failures.Times.Dequeue();
        if (failures.Times.Count == 0)
        {
            _failures.Remove(key);
            return 0;
        }
        return failures.Times.Count;
    }

    private void Add(string key, int limit, long now)
    {
        _ = Count(key, now);
        if (!_failures.TryGetValue(key, out var failures))
        {
            if (_failures.Count >= MaximumKeys)
            {
                var oldest = _failures.MinBy(entry => entry.Value.LastFailure).Key;
                _failures.Remove(oldest);
            }
            failures = new Failures(limit);
            _failures.Add(key, failures);
        }
        failures.Times.Enqueue(now);
        failures.LastFailure = now;
    }

    private static string? AccountKey(string accountName) =>
        Eve.Identity.AccountNames.IsAcceptable(accountName)
            ? Digest("account:", accountName.ToUpperInvariant())
            : null;

    private static string AddressKey(string address)
    {
        var identity = System.Net.IPEndPoint.TryParse(address, out var endpoint)
            ? endpoint.Address.MapToIPv6().ToString()
            : System.Net.IPAddress.TryParse(address, out var ip)
                ? ip.MapToIPv6().ToString()
                : address;
        return Digest("address:", identity);
    }

    private static string Digest(string prefix, string value) =>
        prefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}

public sealed class HostServer(
    HostOptions options,
    PacketDispatcher dispatcher,
    IAccountDirectory accounts,
    Func<ReadOnlyMemory<byte>> clusterConfig)
{
    private readonly int _maxFrontConnections = options.MaxFrontConnections > 0
        ? options.MaxFrontConnections
        : throw new ArgumentOutOfRangeException(nameof(options), "MaxFrontConnections must be positive.");
    private int _connections;
    private readonly ConcurrentDictionary<FrontConnection, byte> _fronts = new();

    public int ConnectedFronts => Volatile.Read(ref _connections);

    /// <summary>
    /// Sends every connected front the cluster configuration again (a front keeps the one it was sent when it connected,
    /// and the handshake's <c>config_vals</c> comes from it). Call it after the configuration changed.
    /// </summary>
    public void PublishClusterConfig()
    {
        foreach (var front in _fronts.Keys)
        {
            front.PublishClusterConfig();
        }
    }

    /// <summary>Accepts fronts on <paramref name="listener"/> until cancelled.</summary>
private readonly LoginAttemptLimiter _loginAttemptLimiter = new();

    public async Task ServeAsync(Socket listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(cancellationToken);
                if (!TryReserveConnection())
                {
                    socket.Dispose();
                    continue;
                }
                if (socket.AddressFamily != AddressFamily.Unix)
                {
                    socket.NoDelay = true;
                }
                _ = Task.Run(() => RunReservedAsync(new NetworkStream(socket, ownsSocket: true), cancellationToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Serves one front over <paramref name="stream"/> until it disconnects or the link breaks.</summary>
    public async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (!TryReserveConnection())
        {
            await stream.DisposeAsync();
            return;
        }
        await RunReservedAsync(stream, cancellationToken);
    }

    private bool TryReserveConnection()
    {
        while (true)
        {
            var current = Volatile.Read(ref _connections);
            if (current >= _maxFrontConnections)
                return false;
            if (Interlocked.CompareExchange(ref _connections, current + 1, current) == current)
                return true;
        }
    }

    private async Task RunReservedAsync(Stream stream, CancellationToken cancellationToken)
    {
        FrontConnection? activeConnection = null;
        try
        {
            var connection = new FrontConnection(stream, options, dispatcher, accounts, clusterConfig, _loginAttemptLimiter);
            activeConnection = connection;
            _fronts[connection] = 0;
            await using (connection)
            {
                try
                {
                    await connection.RunAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception error) when (error is HostLinkException or IOException or ObjectDisposedException)
                {
                    options.Log?.Invoke($"front link ended: {error.Message}");
                }
            }
        }
        finally
        {
            if (activeConnection is not null)
            {
                _fronts.TryRemove(activeConnection, out _);
            }
            else
            {
                await stream.DisposeAsync();
            }
            Interlocked.Decrement(ref _connections);
        }
    }
}

/// <summary>One front's link: reads its frames, keeps its sessions, and writes what they send.</summary>
internal sealed class FrontConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly HostOptions _options;
    private readonly PacketDispatcher _dispatcher;
    private readonly IAccountDirectory _accounts;
    private readonly Func<ReadOnlyMemory<byte>> _clusterConfig;
    private readonly Channel<QueuedFrame> _outgoing;
    private readonly ConcurrentDictionary<ulong, QueueBudget> _sessionQueueBudgets = new();
    private readonly QueueBudget _connectionQueueBudget = new();
    private readonly ConcurrentDictionary<ulong, SessionActor> _sessions = new();
    private readonly object _handshakeGate = new();
    private bool _handshaken;
    private readonly CancellationTokenSource _stop = new();
    private Task _writer = Task.CompletedTask;

private readonly LoginAttemptLimiter _loginAttemptLimiter;

    public FrontConnection(Stream stream, HostOptions options, PacketDispatcher dispatcher, IAccountDirectory accounts, Func<ReadOnlyMemory<byte>> clusterConfig, LoginAttemptLimiter loginAttemptLimiter)
    {
        _stream = stream;
        _options = options;
        _dispatcher = dispatcher;
        _accounts = accounts;
        _clusterConfig = clusterConfig;
        _loginAttemptLimiter = loginAttemptLimiter;
        if (options.MaxFrontConnections <= 0 || options.MaxPendingPacketsPerSession <= 0 || options.MaxPendingBytesPerSession <= 0 || options.MaxQueuedOutboundPacketsPerSession <= 0 || options.MaxQueuedOutboundBytesPerSession <= 0 || options.MaxQueuedOutboundPacketsPerConnection <= 0 || options.MaxQueuedOutboundBytesPerConnection <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Front connection, session and outbound queue limits must be positive.");
        _outgoing = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(options.MaxQueuedOutboundPacketsPerConnection)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var ct = linked.Token;
        _writer = Task.Run(() => WriteLoopAsync(ct), CancellationToken.None);
        try
        {
            if (await ReadFrameAsync(ct) is not { } first || HostCodec.Decode(first) is not Hello hello)
            {
                throw new HostLinkException(HostLinkError.UnknownKind, "the front did not start with Hello");
            }
            if (hello.Version != HostCodec.ProtocolVersion)
            {
                throw new HostLinkException(HostLinkError.BadStatus, $"the front speaks version {hello.Version}, this host {HostCodec.ProtocolVersion}");
            }
            // The front's first two messages are the acknowledgement and the configuration: a publish waits for them.
            lock (_handshakeGate)
            {
                Post(new HelloAck(HostCodec.ProtocolVersion));
                Post(new ClusterConfig(_clusterConfig()));
                _handshaken = true;
            }

            while (await ReadFrameAsync(ct) is { } frame)
            {
                await HandleAsync(HostCodec.Decode(frame), ct);
            }
        }
        finally
        {
            var closingSessions = _sessions.Values.ToArray();
            _sessions.Clear();
            foreach (var session in closingSessions)
                session.Complete();
            await Task.WhenAll(closingSessions.Select(session => session.Completion));
            _outgoing.Writer.TryComplete();
            try
            {
                await _writer;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    private async Task HandleAsync(HostMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case AuthRequest request:
                _ = AuthenticateAsync(request, ct);
                break;
            case SessionOpen open:
                await OpenSessionAsync(open, ct);
                break;
            case SessionClose close:
                if (_sessions.TryRemove(close.SessionId, out var closing))
                {
                    closing.Complete();
                    await closing.Completion;
                }
                break;
            case Packet packet:
                if (_sessions.TryGetValue(packet.SessionId, out var session))
                {
                    session.Post(packet.Payload);
                }
                break;
            default:
                throw new HostLinkException(HostLinkError.UnknownKind, $"a front may not send {message.GetType().Name}");
        }
    }

    private async Task AuthenticateAsync(AuthRequest request, CancellationToken ct)
    {
        if (!_loginAttemptLimiter.IsAllowed(request.UserName, request.Address))
        {
            Post(new AuthReply(request.Corr, new Rejected(LoginRefused.BadCredentials)));
            return;
        }

        AuthOutcome outcome;
        try
        {
            var result = await _accounts.LoginAsync(new LoginAttempt(request.UserName, request.PasswordHash, request.Language, request.Address), ct);
            if (result is LoggedIn)
                _loginAttemptLimiter.ClearAccountFailures(request.UserName);
            else if (result is LoginRefused { Reason: LoginRefused.BadCredentials })
                _loginAttemptLimiter.RecordFailure(request.UserName, request.Address);
            outcome = result switch
            {
                LoggedIn login => new Accepted(login.AccountId, login.Role, login.CountryCode),
                LoginRefused refused => new Rejected(refused.Reason),
                _ => new Rejected(LoginRefused.Unavailable),
            };
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            _options.Log?.Invoke("account authentication failed due to a host error");
            outcome = new Rejected(LoginRefused.Unavailable);
        }
        Post(new AuthReply(request.Corr, outcome));
    }

    private async Task OpenSessionAsync(SessionOpen open, CancellationToken ct)
    {
        var session = new ClientSession { SessionId = unchecked((long)open.SessionId), ClientId = unchecked((long)open.ClientId) };
        session.Sink = new LinkSink(this, open.SessionId);
        session.AttachDisconnectClient(reason => Disconnect(open.SessionId, reason));
        session.UserId = open.UserId;
        session.Set("clientID", new PyLong(unchecked((long)open.ClientId)));
        // The account's role is kept apart: the client's own `role` only comes with a character (SelectCharacterID).
        session.Set("accountRole", new PyLong(unchecked((long)open.Role)));
        session.Set("hqID", new PyInt(0));
        session.Set("address", new PyStr(open.Address));
        session.Set("languageID", new PyStr(open.Language));
        session.Set("countryCode", new PyStr(open.CountryCode));

        var actor = new SessionActor(session, _dispatcher, _options, Disconnect: reason => Disconnect(open.SessionId, reason), ct);
        if (!_sessions.TryAdd(open.SessionId, actor))
        {
            throw new HostLinkException(HostLinkError.BadStatus, $"session {open.SessionId} is already open");
        }
        try
        {
            _options.SessionOpened?.Invoke(session);
            if (_options.SessionOpenedAsync is { } opened)
                await opened(session, ct);
        }
        catch
        {
            _sessions.TryRemove(open.SessionId, out _);
            actor.Complete();
            session.Close();
            throw;
        }
        // The client waits for this before anything else; it goes out ahead of every reply.
        session.Send(SessionInitialState.Build(open.SessionId, open.ClientId, open.UserId, open.Role, open.Address, open.Language, open.CountryCode));
        actor.Start();
    }

    public void Disconnect(ulong sessionId, string reason)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.Complete();
        }
        _sessionQueueBudgets.TryRemove(sessionId, out _);
        Post(new Disconnect(sessionId, reason));
    }

    /// <summary>Queues a frame for the front.</summary>
    public void Post(HostMessage message)
    {
        var (sessionId, isSessionPacket) = message is Send send ? (send.SessionId, true) : (0UL, false);
        if (isSessionPacket && !_sessions.ContainsKey(sessionId))
            return;

        var frame = HostCodec.Encode(message);
        QueueBudget? sessionBudget = null;
        if (isSessionPacket)
        {
            sessionBudget = _sessionQueueBudgets.GetOrAdd(sessionId, _ => new QueueBudget());
            if (!sessionBudget.TryReserve(frame.Length, _options.MaxQueuedOutboundPacketsPerSession, _options.MaxQueuedOutboundBytesPerSession))
            {
                Disconnect(sessionId, "OutgoingOverloaded");
                return;
            }
        }

        if (!_connectionQueueBudget.TryReserve(frame.Length, _options.MaxQueuedOutboundPacketsPerConnection, _options.MaxQueuedOutboundBytesPerConnection))
        {
            sessionBudget?.Release(frame.Length);
            if (isSessionPacket)
                Disconnect(sessionId, "OutgoingOverloaded");
            return;
        }

        if (!_outgoing.Writer.TryWrite(new QueuedFrame(frame, sessionBudget, _connectionQueueBudget)))
        {
            sessionBudget?.Release(frame.Length);
            _connectionQueueBudget.Release(frame.Length);
            if (isSessionPacket)
                Disconnect(sessionId, "OutgoingOverloaded");
        }
    }

    private sealed record QueuedFrame(byte[] Bytes, QueueBudget? SessionBudget, QueueBudget ConnectionBudget);

    private sealed class QueueBudget
    {
        private int _packets;
        private long _bytes;

        public bool TryReserve(int bytes, int maxPackets, long maxBytes)
        {
            lock (this)
            {
                if (_packets >= maxPackets || bytes > maxBytes - _bytes)
                    return false;
                _packets++;
                _bytes += bytes;
                return true;
            }
        }

        public void Release(int bytes)
        {
            lock (this)
            {
                _packets--;
                _bytes -= bytes;
            }
        }
    }

    /// <summary>Sends the front the cluster configuration as it is now, once the front has its first two messages.</summary>
    public void PublishClusterConfig()
    {
        lock (_handshakeGate)
        {
            if (_handshaken)
            {
                Post(new ClusterConfig(_clusterConfig()));
            }
        }
    }

    private sealed class LinkSink(FrontConnection connection, ulong sessionId) : IPacketSink
    {
        public void Send(PyValue packet) => connection.Post(new Send(sessionId, MarshalCodec.Encode(packet)));
    }

    // ── framing ─────────────────────────────────────────────────────────────

    /// <summary>The next frame's payload, or null when the front closed cleanly between frames.</summary>
    private async Task<byte[]?> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[4];
        var read = 0;
        while (read < header.Length)
        {
            var count = await _stream.ReadAsync(header.AsMemory(read), ct);
            if (count == 0)
            {
                return read == 0 ? null : throw new HostLinkException(HostLinkError.Truncated, "the front closed in the middle of a frame");
            }
            read += count;
        }
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > HostCodec.MaxFrame)
        {
            throw new HostLinkException(HostLinkError.FrameTooLarge, $"a frame of {length} bytes exceeds {HostCodec.MaxFrame}");
        }
        var payload = new byte[length];
        try
        {
            await _stream.ReadExactlyAsync(payload, ct);
        }
        catch (EndOfStreamException)
        {
            throw new HostLinkException(HostLinkError.Truncated, "the front closed in the middle of a frame");
        }
        return payload;
    }

    /// <summary>Writes queued frames, batching whatever has piled up into one write.</summary>
    private async Task WriteLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var used = 0;
        var reader = _outgoing.Reader;
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var queued))
            {
                var frame = queued.Bytes;
                queued.SessionBudget?.Release(frame.Length);
                queued.ConnectionBudget.Release(frame.Length);
                if (used > 0 && used + frame.Length > buffer.Length)
                {
                    await _stream.WriteAsync(buffer.AsMemory(0, used), ct);
                    used = 0;
                }
                if (frame.Length > buffer.Length)
                {
                    await _stream.WriteAsync(frame, ct);
                    continue;
                }
                frame.CopyTo(buffer.AsSpan(used));
                used += frame.Length;
            }
            if (used > 0)
            {
                await _stream.WriteAsync(buffer.AsMemory(0, used), ct);
                used = 0;
            }
            await _stream.FlushAsync(ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _writer;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        _stop.Dispose();
        await _stream.DisposeAsync();
    }
}

/// <summary>Runs one session's packets one at a time, in the order they arrived.</summary>
internal sealed class SessionActor
{
    private readonly ClientSession _session;
    private readonly PacketDispatcher _dispatcher;
    private readonly HostOptions _options;
    private readonly Action<string> _disconnect;
    private readonly CancellationToken _ct;
    private readonly Channel<ReadOnlyMemory<byte>> _inbox = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });
    private int _pending;
    private long _pendingBytes;

    public SessionActor(ClientSession session, PacketDispatcher dispatcher, HostOptions options, Action<string> Disconnect, CancellationToken ct)
    {
        _session = session;
        _dispatcher = dispatcher;
        _options = options;
        _disconnect = Disconnect;
        _ct = ct;
    }

    public Task Completion { get; private set; } = Task.CompletedTask;

    public void Start() => Completion = Task.Run(RunAsync, CancellationToken.None);

    public void Post(ReadOnlyMemory<byte> packet)
    {
        var pending = Interlocked.Increment(ref _pending);
        var pendingBytes = Interlocked.Add(ref _pendingBytes, packet.Length);
        if (pending > _options.MaxPendingPacketsPerSession || pendingBytes > _options.MaxPendingBytesPerSession)
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Add(ref _pendingBytes, -packet.Length);
            _options.Log?.Invoke($"session {_session.SessionId}: inbound queue exceeded its packet or byte budget; cutting it off");
            _disconnect("Overloaded");
            return;
        }
        if (!_inbox.Writer.TryWrite(packet))
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Add(ref _pendingBytes, -packet.Length);
        }
    }

    public void Complete() => _inbox.Writer.TryComplete();

    private async Task RunAsync()
    {
        try
        {
            await RunLoopAsync();
        }
        finally
        {
            _session.Close();
            if (_options.SessionClosedAsync is { } closed)
            {
                try
                {
                    await closed(_session);
                }
                catch (Exception error)
                {
                    _options.Log?.Invoke($"session {_session.SessionId}: could not persist session end ({error.GetType().Name})");
                }
            }
        }
    }

    private async Task RunLoopAsync()
    {
        await foreach (var payload in _inbox.Reader.ReadAllAsync())
        {
            try
            {
                if (Environment.GetEnvironmentVariable("EVE_TRACE_LOGIN") == "1"
                    && !payload.IsEmpty
                    && payload.Span[0] == 0x78)
                {
                    _options.Log?.Invoke(
                        $"[login-trace] zlib-inbound session={_session.SessionId} bytes={payload.Length}");
                }
                var value = MarshalCodec.DecodeInbound(payload);
                await _dispatcher.DispatchAsync(value, _session, _ct);
            }
            catch (MarshalException error)
            {
                _options.Log?.Invoke($"session {_session.SessionId}: dropped an undecodable packet: {error.Message}");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                _options.Log?.Invoke($"session {_session.SessionId}: {error}");
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
                Interlocked.Add(ref _pendingBytes, -payload.Length);
            }
        }
    }
}
