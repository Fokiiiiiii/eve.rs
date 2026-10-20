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

    /// <summary>Maximum queued packets sent to one session.</summary>
    public int MaxQueuedOutboundPacketsPerSession { get; init; } = 512;

    /// <summary>Maximum serialized bytes queued to one session.</summary>
    public long MaxQueuedOutboundBytesPerSession { get; init; } = 8 * 1024 * 1024;

    /// <summary>Maximum queued packets sent through one front connection.</summary>
    public int MaxQueuedOutboundPacketsPerConnection { get; init; } = 8_192;

    /// <summary>Maximum serialized bytes queued through one front connection.</summary>
    public long MaxQueuedOutboundBytesPerConnection { get; init; } = 64 * 1024 * 1024;

    /// <summary>Called with a problem worth logging.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Called for every new session, before its first packet: attach what must be released when it ends.</summary>
    public Action<ClientSession>? SessionOpened { get; init; }
}

/// <summary>
/// The service host: it accepts network fronts, and serves each front's sessions. Packets of one session
/// are handled one at a time, in order; different sessions run in parallel.
/// </summary>
public sealed class HostServer(
    HostOptions options,
    PacketDispatcher dispatcher,
    IAccountDirectory accounts,
    Func<ReadOnlyMemory<byte>> clusterConfig)
{
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
    public async Task ServeAsync(Socket listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(cancellationToken);
                if (socket.AddressFamily != AddressFamily.Unix)
                {
                    socket.NoDelay = true;
                }
                _ = Task.Run(() => RunAsync(new NetworkStream(socket, ownsSocket: true), cancellationToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Serves one front over <paramref name="stream"/> until it disconnects or the link breaks.</summary>
    public async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connections);
        await using var connection = new FrontConnection(stream, options, dispatcher, accounts, clusterConfig);
        _fronts[connection] = 0;
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
        finally
        {
            _fronts.TryRemove(connection, out _);
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

    public FrontConnection(Stream stream, HostOptions options, PacketDispatcher dispatcher, IAccountDirectory accounts, Func<ReadOnlyMemory<byte>> clusterConfig)
    {
        _stream = stream;
        _options = options;
        _dispatcher = dispatcher;
        _accounts = accounts;
        _clusterConfig = clusterConfig;
        if (options.MaxQueuedOutboundPacketsPerSession <= 0 || options.MaxQueuedOutboundBytesPerSession <= 0 || options.MaxQueuedOutboundPacketsPerConnection <= 0 || options.MaxQueuedOutboundBytesPerConnection <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Outbound queue limits must be positive.");
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
                Handle(HostCodec.Decode(frame), ct);
            }
        }
        finally
        {
            foreach (var session in _sessions.Values)
            {
                session.Complete();
            }
            _sessions.Clear();
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

    private void Handle(HostMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case AuthRequest request:
                _ = AuthenticateAsync(request, ct);
                break;
            case SessionOpen open:
                OpenSession(open, ct);
                break;
            case SessionClose close:
                if (_sessions.TryRemove(close.SessionId, out var closing))
                {
                    closing.Complete();
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
        AuthOutcome outcome;
        try
        {
            var result = await _accounts.LoginAsync(new LoginAttempt(request.UserName, request.PasswordHash, request.Language, request.Address), ct);
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
        catch (Exception error)
        {
            _options.Log?.Invoke($"authenticating {request.UserName}: {error}");
            outcome = new Rejected(LoginRefused.Unavailable);
        }
        Post(new AuthReply(request.Corr, outcome));
    }

    private void OpenSession(SessionOpen open, CancellationToken ct)
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
        _options.SessionOpened?.Invoke(session);
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

    public SessionActor(ClientSession session, PacketDispatcher dispatcher, HostOptions options, Action<string> Disconnect, CancellationToken ct)
    {
        _session = session;
        _dispatcher = dispatcher;
        _options = options;
        _disconnect = Disconnect;
        _ct = ct;
    }

    public void Start() => _ = Task.Run(RunAsync, CancellationToken.None);

    public void Post(ReadOnlyMemory<byte> packet)
    {
        if (Interlocked.Increment(ref _pending) > _options.MaxPendingPacketsPerSession)
        {
            _options.Log?.Invoke($"session {_session.SessionId}: {_pending} packets waiting; cutting it off");
            _disconnect("Overloaded");
            return;
        }
        _inbox.Writer.TryWrite(packet);
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
            }
        }
    }
}
