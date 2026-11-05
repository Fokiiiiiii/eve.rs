using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Eve.Space;

/// <summary>
/// The service host's end of the link to the simulation process: it connects to the simulation's socket (and again, after a
/// break), says hello, sends what the host asks and hands back what the simulation says. Requests that are answered (an
/// <c>Enter</c>, a <c>Dock</c>) are awaited; the rest are sent and forgotten.
/// </summary>
public sealed class SimClient : ISimulation, IAsyncDisposable
{
    private static long _nextCorrelatedRequestId;

    /// <summary>Allocates a process-wide ID for correlated simulation requests.</summary>
    public static ulong NextRequestId() => checked((ulong)Interlocked.Increment(ref _nextCorrelatedRequestId));
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly Action<string>? _log;
    private readonly object _pendingGate = new();
    private readonly Action? _afterConnectionCheck;
    private readonly CancellationTokenSource _stop = new();
    // MaxFrame bounds each frame; 64 queued frames cap retained payload at about 64 MiB.
    internal const int OutboundQueueCapacity = 64;
    private readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboundQueueCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private CancellationTokenSource? _activeSession;
    private SimUnavailableException? _outboundFailure;
    private bool _overloadedLinkLostPending;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<string?>> _entering = new();
    private readonly ConcurrentDictionary<(Type ReplyType, ulong RequestId), TaskCompletionSource<string?>> _abyssal = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimEnsureAbyssalTracePublishedReply>> _publishingAbyssalTraces = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimRemoveAbyssalTraceReply>> _removingAbyssalTraces = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimRemoveAbyssalParticipantReply>> _removingAbyssalParticipants = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimGetAbyssalParticipantPoseReply>> _abyssalParticipantPoses = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimGetSceneBodyReply>> _sceneBodies = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimReleaseTournamentCutoffReply>> _cutoffReleases = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimRegisterTournamentCutoffReply>> _cutoffRegistrations = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimReadTournamentCutoffReply>> _cutoffReads = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimRegisterTournamentMetricsReply>> _metricsRegistrations = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimReadTournamentMetricsReply>> _metricsReads = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimReadTournamentMetricsV2Reply>> _metricsV2Reads = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimReleaseTournamentMetricsReply>> _metricsReleases = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimQueryTournamentWreckReply>> _tournamentWreckQueries = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimRemoveTournamentWreckReply>> _tournamentWreckRemovals = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimEnterPublicParticipantReply>> _publicEnters = new();
    private readonly ConcurrentDictionary<ulong, Action<SimEnterPublicParticipantReply>> _publicEnterInOrder = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimMovePublicParticipantReply>> _publicMoves = new();
    private readonly ConcurrentDictionary<ulong, Action<SimMovePublicParticipantReply>> _publicMoveInOrder = new();
    private readonly ConcurrentDictionary<ulong, Action<SimMoveAbyssalParticipantReply>> _abyssalMoveInOrder = new();
    private readonly ConcurrentDictionary<(ulong Session, byte Code), TaskCompletionSource<CommandReply>> _answered = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimSpawnReply>> _spawning = new();
    private readonly ConcurrentDictionary<ulong, (TaskCompletionSource<SimChargeAdjusted> Completion, Action<SimChargeAdjusted>? InOrder)> _adjusting = new();
    private Task? _run;
    private volatile bool _connected;
    private TimeSpan _retryDelay = RetryDelay;

    public SimClient(Func<CancellationToken, Task<Stream>> connect, Action<string>? log = null)
        : this(connect, log, null)
    {
    }

    internal SimClient(Func<CancellationToken, Task<Stream>> connect, Action<string>? log, Action? afterConnectionCheck)
    {
        _connect = connect;
        _log = log;
        _afterConnectionCheck = afterConnectionCheck;
    }

    /// <summary>A client of the simulation listening on a Unix socket.</summary>
    public static SimClient ToUnixSocket(string path, Action<string>? log = null) => ToUnixSocket(path, log, null);

    internal static SimClient ToUnixSocket(string path, Action<string>? log, Action? afterConnectionCheck) => new(async cancellationToken =>
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }, log, afterConnectionCheck);

    /// <summary>A client of a simulation listening on a local TCP endpoint.</summary>
    public static SimClient ToTcpSocket(string address, Action<string>? log = null) => new(async cancellationToken =>
    {
        if (!System.Net.IPEndPoint.TryParse(address, out var endpoint))
            throw new ArgumentException("Expected a TCP endpoint in address:port form.", nameof(address));
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }, log);

    public bool IsConnected => _connected;

    public event Action<SimMessage>? Received;

    public event Action? Lost;

    /// <summary>How long to wait before trying to connect again.</summary>
    public TimeSpan RetryAfter
    {
        get => _retryDelay;
        set => _retryDelay = value;
    }

    /// <summary>Starts connecting (and keeps at it).</summary>
    public void Start() => _run ??= Task.Run(() => RunAsync(_stop.Token));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var stream = await _connect(cancellationToken);
                await ServeAsync(stream, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is IOException or SocketException or SimLinkException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            {
                _log?.Invoke($"the simulation link: {error.Message}");
            }
            if (FailPendingAfterLinkLoss())
            {
                Lost?.Invoke();
            }
            try
            {
                await Task.Delay(_retryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        FailPending();
    }

    private static async Task<SimMessage> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, cancellationToken);
        var size = BitConverter.ToUInt32(length);
        if (size > SimCodec.MaxFrame)
        {
            throw new SimLinkException(SimLinkError.FrameTooLarge, $"frame of {size} bytes exceeds {SimCodec.MaxFrame}");
        }
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return SimCodec.Decode(payload);
    }

    private async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(SimCodec.Encode(new SimHello(SimCodec.ProtocolVersion)), cancellationToken);
        using (var hello = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            hello.CancelAfter(HelloTimeout);
            var ack = await ReadAsync(stream, hello.Token);
            if (ack is not SimHelloAck { Version: SimCodec.ProtocolVersion })
            {
                throw new SimLinkException(SimLinkError.BadValue, $"the simulation answered hello with {ack.GetType().Name}");
            }
        }
        _log?.Invoke("connected to the simulation");
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_pendingGate)
        {
            // whatever was queued for a link that broke is stale
            while (_outbox.Reader.TryRead(out _))
            {
            }
            _outboundFailure = null;
            _overloadedLinkLostPending = false;
            _activeSession = session;
            _connected = true;
        }
        var writer = Task.Run(async () =>
        {
            await foreach (var frame in _outbox.Reader.ReadAllAsync(session.Token))
            {
                await stream.WriteAsync(frame, session.Token);
            }
        }, session.Token);
        try
        {
            while (true)
            {
                Dispatch(await ReadAsync(stream, session.Token));
                if (writer.IsFaulted)
                {
                    await writer;
                }
            }
        }
        finally
        {
            await session.CancelAsync();
            try
            {
                await writer;
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private void Dispatch(SimMessage message)
    {
        switch (message)
        {
            case SimEnterReply reply:
                if (_entering.TryRemove(reply.SessionId, out var entering))
                {
                    entering.TrySetResult(reply.Error);
                }
                break;
            case SimSpawnReply spawned:
                if (_spawning.TryRemove(spawned.RequestId, out var spawning))
                {
                    spawning.TrySetResult(spawned);
                }
                break;
            case SimEnsureAbyssalSceneReply reply:
                CompleteAbyssal(reply);
                break;
            case SimEnsureAbyssalSceneObjectReply reply:
                CompleteAbyssal(reply);
                break;
            case SimEnsureAbyssalEnvironmentObjectReply reply:
                CompleteAbyssal(reply.RequestId, typeof(SimEnsureAbyssalEnvironmentObjectReply), reply.Error);
                break;
            case SimRefreshAbyssalNpcReply reply:
                CompleteAbyssal(reply.RequestId, typeof(SimRefreshAbyssalNpcReply), reply.Error);
                break;
            case SimEnsureAbyssalRewardCacheReply reply:
                CompleteAbyssal(reply.RequestId, typeof(SimEnsureAbyssalRewardCacheReply), reply.Error);
                break;
            case SimEnsureAbyssalLootContainerReply reply:
                CompleteAbyssal(reply.RequestId, typeof(SimEnsureAbyssalLootContainerReply), reply.Error);
                break;
            case SimMoveAbyssalParticipantReply reply:
                if (_abyssalMoveInOrder.TryGetValue(reply.RequestId, out var beforeMovementNotifications))
                {
                    try
                    {
                        beforeMovementNotifications(reply);
                    }
                    catch (Exception error)
                    {
                        _log?.Invoke($"Abyssal SessionChange for move {reply.RequestId} failed: {error.Message}");
                        reply = reply with { Error = $"ABYSSAL_SESSION_HANDOFF_FAILED: {error.Message}" };
                    }
                }
                CompleteAbyssal(reply);
                break;
            case SimRebindAbyssalSessionReply reply:
                CompleteAbyssal(reply);
                break;
            case SimRemoveAbyssalSceneReply reply:
                CompleteAbyssal(reply);
                break;
            case SimEnsureAbyssalTracePublishedReply reply when _publishingAbyssalTraces.TryRemove(reply.RequestId, out var publishingTrace):
                publishingTrace.TrySetResult(reply);
                break;
            case SimEnsureAbyssalTracePublishedReply:
                break;
            case SimRemoveAbyssalTraceReply reply when _removingAbyssalTraces.TryRemove(reply.RequestId, out var removingTrace):
                removingTrace.TrySetResult(reply);
                break;
            case SimRemoveAbyssalTraceReply:
                break;
            case SimRemoveAbyssalParticipantReply reply when _removingAbyssalParticipants.TryRemove(reply.RequestId, out var removingParticipant):
                removingParticipant.TrySetResult(reply);
                break;
            case SimRemoveAbyssalParticipantReply:
                break;
            case SimEnsureTournamentBeaconReply reply:
                CompleteAbyssal(reply.RequestId, typeof(SimEnsureTournamentBeaconReply), reply.Error); break;
            case SimRemoveTournamentBeaconReply reply:
                CompleteAbyssal(reply.RequestId, typeof(SimRemoveTournamentBeaconReply), reply.Error); break;
            case SimReleaseTournamentCutoffReply reply when _cutoffReleases.TryRemove(reply.RequestId, out var release):
                release.TrySetResult(reply); break;
            case SimReleaseTournamentCutoffReply: break;
            case SimRegisterTournamentCutoffReply reply when _cutoffRegistrations.TryRemove(reply.RequestId, out var registration):
                registration.TrySetResult(reply); break;
            case SimRegisterTournamentCutoffReply: break;
            case SimReadTournamentCutoffReply reply when _cutoffReads.TryRemove(reply.RequestId, out var cutoff):
                cutoff.TrySetResult(reply); break;
            case SimReadTournamentCutoffReply: break;
            case SimRegisterTournamentMetricsReply reply when _metricsRegistrations.TryRemove(reply.RequestId, out var metricRegistration): metricRegistration.TrySetResult(reply); break;
            case SimRegisterTournamentMetricsReply: break;
            case SimReadTournamentMetricsReply reply when _metricsReads.TryRemove(reply.RequestId, out var metricRead): metricRead.TrySetResult(reply); break;
            case SimReadTournamentMetricsReply: break;
            case SimReadTournamentMetricsV2Reply reply when _metricsV2Reads.TryRemove(reply.RequestId, out var metricV2Read): metricV2Read.TrySetResult(reply); break;
            case SimReadTournamentMetricsV2Reply: break;
            case SimReleaseTournamentMetricsReply reply when _metricsReleases.TryRemove(reply.RequestId, out var metricRelease): metricRelease.TrySetResult(reply); break;
            case SimReleaseTournamentMetricsReply: break;
            case SimQueryTournamentWreckReply reply when _tournamentWreckQueries.TryRemove(reply.RequestId, out var wreckQuery):
                wreckQuery.TrySetResult(reply); break;
            case SimQueryTournamentWreckReply: break;
            case SimRemoveTournamentWreckReply reply when _tournamentWreckRemovals.TryRemove(reply.RequestId, out var wreckRemoval):
                wreckRemoval.TrySetResult(reply); break;
            case SimRemoveTournamentWreckReply: break;
            case SimEnterPublicParticipantReply reply when _publicEnters.TryRemove(reply.RequestId, out var publicEnter):
                if (_publicEnterInOrder.TryRemove(reply.RequestId, out var beforeEntryMessages)) beforeEntryMessages(reply);
                publicEnter.TrySetResult(reply); break;
            case SimEnterPublicParticipantReply: break;
            case SimMovePublicParticipantReply reply when _publicMoves.TryRemove(reply.RequestId, out var publicMove):
                if (_publicMoveInOrder.TryRemove(reply.RequestId, out var beforeFollowing)) beforeFollowing(reply);
                publicMove.TrySetResult(reply);
                break;
            case SimMovePublicParticipantReply:
                break;
            case SimGetSceneBodyReply reply when _sceneBodies.TryRemove(reply.RequestId, out var bodyRequest):
                bodyRequest.TrySetResult(reply);
                break;
            case SimGetSceneBodyReply:
                break;
            case SimGetAbyssalParticipantPoseReply reply when _abyssalParticipantPoses.TryRemove(reply.RequestId, out var poseRequest):
                poseRequest.TrySetResult(reply);
                break;
            case SimGetAbyssalParticipantPoseReply:
                break;
            case SimChargeAdjusted adjusted:
                if (_adjusting.TryRemove(adjusted.RequestId, out var adjusting))
                {
                    adjusting.InOrder?.Invoke(adjusted);
                    adjusting.Completion.TrySetResult(adjusted);
                }
                break;
            case SimAccepted accepted when _answered.TryRemove((accepted.SessionId, accepted.Command), out var accepting):
                accepting.TrySetResult(new CommandReply(true, accepted.At, null));
                break;
            case SimPosition position when _answered.TryRemove((position.SessionId, position.Command), out var resolving):
                resolving.TrySetResult(new CommandReply(true, 0, null) { Position = position.Position });
                break;
            case SimPosition:
                break; // nobody is waiting for it
            case SimScanTargets scan when _answered.TryRemove((scan.SessionId, scan.Command), out var scanning):
                scanning.TrySetResult(new CommandReply(true, 0, null) { ScanTargets = scan.Targets });
                break;
            case SimScanTargets:
                break; // nobody is waiting for it
            case SimDirectionalTargets directional when _answered.TryRemove((directional.SessionId, directional.Command), out var sweeping):
                sweeping.TrySetResult(new CommandReply(true, 0, null) { DirectionalTargets = directional.Targets });
                break;
            case SimDirectionalTargets:
                break; // nobody is waiting for it
            case SimRefused refused when _answered.TryRemove((refused.SessionId, refused.Command), out var refusing):
                refusing.TrySetResult(new CommandReply(false, 0, refused.Reason));
                break;
            case SimApproaching approaching when _answered.TryRemove((approaching.SessionId, approaching.Command), out var approach):
                approach.TrySetResult(new CommandReply(false, 0, null, approaching.Distance));
                break;
            case SimApproaching:
                break; // nobody is waiting for it
            case SimJumping jumping when _answered.TryRemove((jumping.SessionId, jumping.Command), out var jump):
                jump.TrySetResult(new CommandReply(true, 0, null, null, jumping.DestinationSystem, jumping.DestinationGate));
                break;
            case SimJumping:
                break; // nobody is waiting for it
            case SimTargets targets when _answered.TryRemove((targets.SessionId, targets.Command), out var answering):
                answering.TrySetResult(new CommandReply(true, 0, null) { Targets = targets.Targets, Pending = targets.Pending });
                break;
            case SimTargets:
                break; // nobody is waiting for it
            case SimConditionReply reply when _answered.TryRemove((reply.SessionId, reply.Command), out var asked):
                asked.TrySetResult(new CommandReply(true, 0, null) { Condition = reply.Condition });
                break;
            case SimConditionReply:
                break; // nobody is waiting for it
            case SimTargetRefused refused when _answered.TryRemove((refused.SessionId, refused.Command), out var refusing):
                refusing.TrySetResult(new CommandReply(false, 0, refused.Reason) { TargetType = refused.TargetType, DesiredRange = refused.DesiredRange, ActualDistance = refused.ActualDistance });
                break;
            case SimTargetRefused:
                break; // nobody is waiting for it
            case SimContainerPlaced placed when _answered.TryRemove((placed.SessionId, SimCommands.PlaceContainer), out var placing):
                placing.TrySetResult(new CommandReply(true, 0, null) { Position = placed.Position, Direction = placed.Direction });
                break;
            case SimContainerPlaced:
                break; // nobody is waiting for it
            case SimDroneResults results when _answered.TryRemove((results.SessionId, SimDroneCommands.Key(results.Command)), out var droning):
                droning.TrySetResult(new CommandReply(true, 0, null) { Drones = results.Results });
                break;
            case SimDroneResults:
                break; // nobody is waiting for it
            case SimFighterResults fighters when _answered.TryRemove((fighters.SessionId, SimFighterOrders.Key(fighters.Command)), out var ordering):
                ordering.TrySetResult(new CommandReply(true, 0, null) { Fighters = fighters.Results });
                break;
            case SimFighterResults:
                break; // nobody is waiting for it
            case SimMeasured measured when _answered.TryRemove((measured.SessionId, measured.Command), out var measuring):
                measuring.TrySetResult(new CommandReply(true, 0, null) { Distance = measured.Distance });
                break;
            case SimMeasured:
                break; // nobody is waiting for it
            default:
                Received?.Invoke(message);
                break;
        }
    }

    private void FailPending()
    {
        lock (_pendingGate)
        {
            _connected = false;
            _activeSession = null;
            FailPendingLocked();
        }
    }

    private bool FailPendingAfterLinkLoss()
    {
        lock (_pendingGate)
        {
            if (!_connected && !_overloadedLinkLostPending)
                return false;
            _connected = false;
            _activeSession = null;
            _overloadedLinkLostPending = false;
            FailPendingLocked();
            return true;
        }
    }

    private void FailPendingLocked()
    {
        foreach (var request in _publishingAbyssalTraces.Keys)
            if (_publishingAbyssalTraces.TryRemove(request, out var publishingTrace)) publishingTrace.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _removingAbyssalTraces.Keys)
            if (_removingAbyssalTraces.TryRemove(request, out var removingTrace)) removingTrace.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _removingAbyssalParticipants.Keys)
            if (_removingAbyssalParticipants.TryRemove(request, out var removingParticipant)) removingParticipant.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _abyssalParticipantPoses.Keys)
            if (_abyssalParticipantPoses.TryRemove(request, out var participantPose)) participantPose.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _publicEnters.Keys)
            if (_publicEnters.TryRemove(request, out var entry)) entry.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _publicMoves.Keys)
            if (_publicMoves.TryRemove(request, out var move)) move.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _cutoffReleases.Keys)
            if (_cutoffReleases.TryRemove(request, out var release)) release.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _cutoffRegistrations.Keys)
            if (_cutoffRegistrations.TryRemove(request, out var registration)) registration.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _cutoffReads.Keys)
            if (_cutoffReads.TryRemove(request, out var cutoff)) cutoff.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _metricsRegistrations.Keys)
            if (_metricsRegistrations.TryRemove(request, out var registration)) registration.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _metricsReads.Keys)
            if (_metricsReads.TryRemove(request, out var read)) read.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _metricsV2Reads.Keys)
            if (_metricsV2Reads.TryRemove(request, out var readV2)) readV2.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _metricsReleases.Keys)
            if (_metricsReleases.TryRemove(request, out var release)) release.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _tournamentWreckQueries.Keys)
            if (_tournamentWreckQueries.TryRemove(request, out var query)) query.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _tournamentWreckRemovals.Keys)
            if (_tournamentWreckRemovals.TryRemove(request, out var removal)) removal.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        foreach (var request in _sceneBodies.Keys)
        {
            if (_sceneBodies.TryRemove(request, out var pending))
                pending.TrySetException(new SimUnavailableException("the link to the simulation broke"));
        }
        foreach (var session in _entering.Keys)
        {
            if (_entering.TryRemove(session, out var pending))
            {
                pending.TrySetException(new SimUnavailableException("the link to the simulation broke"));
            }
        }
        foreach (var request in _spawning.Keys)
        {
            if (_spawning.TryRemove(request, out var pending))
            {
                pending.TrySetException(new SimUnavailableException("the link to the simulation broke"));
            }
        }
        foreach (var key in _answered.Keys)
        {
            if (_answered.TryRemove(key, out var pending))
            {
                pending.TrySetException(new SimUnavailableException("the link to the simulation broke"));
            }
        }
        foreach (var request in _adjusting.Keys)
        {
            if (_adjusting.TryRemove(request, out var pending))
            {
                pending.Completion.TrySetException(new SimUnavailableException("the link to the simulation broke"));
            }
        }
        foreach (var key in _abyssal.Keys)
        {
            if (_abyssal.TryRemove(key, out var pending))
            {
                pending.TrySetException(new SimUnavailableException("the link to the simulation broke"));
            }
        }
    }

    private static byte[] EncodeOutbound(SimMessage message)
    {
        var frame = SimCodec.Encode(message);
        var payloadLength = frame.Length - sizeof(uint);
        if (payloadLength > SimCodec.MaxFrame)
            throw new ArgumentException($"outbound frame of {payloadLength} bytes exceeds {SimCodec.MaxFrame}", nameof(message));
        return frame;
    }

    private SimUnavailableException? TryEnqueueLocked(
        SimMessage message,
        out CancellationTokenSource? sessionToCancel,
        out bool overloaded)
    {
        sessionToCancel = null;
        overloaded = false;
        if (_outboundFailure is { } failed)
            return failed;
        if (_outbox.Writer.TryWrite(EncodeOutbound(message)))
            return null;

        var failure = new SimUnavailableException($"outbound queue is full ({OutboundQueueCapacity} frames)");
        _outboundFailure = failure;
        _connected = false;
        _overloadedLinkLostPending = true;
        sessionToCancel = _activeSession;
        overloaded = true;
        return failure;
    }

    private void FailOverloadedLink(SimUnavailableException failure, CancellationTokenSource? sessionToCancel)
    {
        FailPending();
        try
        {
            sessionToCancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _log?.Invoke($"the simulation link: {failure.Message}");
    }

    private void Send(SimMessage message)
    {
        SimUnavailableException? failure;
        CancellationTokenSource? sessionToCancel;
        bool overloaded;
        lock (_pendingGate)
        {
            if (!_connected)
                return; // nobody is there to hear it
            failure = TryEnqueueLocked(message, out sessionToCancel, out overloaded);
        }
        if (failure is null)
            return;
        // One-way operations have no result channel; Lost is their recovery signal.
        if (overloaded)
            FailOverloadedLink(failure, sessionToCancel);
    }

    private async Task<bool> SendAsync(SimMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frame = EncodeOutbound(message);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationToken sessionToken;
            lock (_pendingGate)
            {
                if (_outboundFailure is { } failure)
                    throw failure;
                var session = _activeSession;
                if (!_connected || session is null || session.IsCancellationRequested)
                    return false;
                if (_outbox.Writer.TryWrite(frame))
                    return true;
                sessionToken = session.Token;
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionToken);
            try
            {
                if (!await _outbox.Writer.WaitToWriteAsync(wait.Token).ConfigureAwait(false))
                    return false;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Recheck the link; it may have reconnected while this send was waiting.
            }
        }
    }

    private static void TryEnqueue<T>(
        object gate,
        CancellationToken cancellationToken,
        TaskCompletionSource<T> completion,
        Action cancelPending,
        Action enqueue)
    {
        lock (gate)
        {
            if (completion.Task.IsCompleted) return;
            if (cancellationToken.IsCancellationRequested)
            {
                cancelPending();
                return;
            }

            enqueue();
        }
    }

    internal static void CancelPending<TKey, TValue, T>(
        object gate, ConcurrentDictionary<TKey, TValue> pending, TKey key, TValue expected,
        TaskCompletionSource<T> completion, CancellationToken cancellationToken) where TKey : notnull
    {
        lock (gate)
        {
            if (TryRemoveExact(pending, key, expected))
                completion.TrySetCanceled(cancellationToken);
        }
    }

    private static bool TryRemoveExact<TKey, TValue>(ConcurrentDictionary<TKey, TValue> pending, TKey key, TValue expected)
        where TKey : notnull =>
        ((ICollection<KeyValuePair<TKey, TValue>>)pending).Remove(new KeyValuePair<TKey, TValue>(key, expected));

    private async Task<T> AwaitAsync<TKey, T>(
        ConcurrentDictionary<TKey, TaskCompletionSource<T>> pending,
        TKey session,
        SimMessage request,
        CancellationToken cancellationToken,
        string? duplicateRequestMessage = null)
        where TKey : notnull
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        SimUnavailableException? failure = null;
        CancellationTokenSource? sessionToCancel = null;
        var overloaded = false;
        try
        {
            lock (_pendingGate)
            {
                if (_outboundFailure is { } linkFailure)
                    throw linkFailure;
                if (!_connected)
                {
                    throw new SimUnavailableException("the simulation is not running");
                }
                _afterConnectionCheck?.Invoke();
                if (!pending.TryAdd(session, completion))
                {
                    throw new InvalidOperationException(duplicateRequestMessage ?? $"session {session} already has a request of this kind waiting");
                }
                try
                {
                    registration = cancellationToken.Register(() =>
                        CancelPending(_pendingGate, pending, session, completion, completion, cancellationToken));
                    TryEnqueue(
                        _pendingGate,
                        cancellationToken,
                        completion,
                        () => CancelPending(_pendingGate, pending, session, completion, completion, cancellationToken),
                        () =>
                        {
                            failure = TryEnqueueLocked(request, out sessionToCancel, out overloaded);
                            if (failure is not null)
                                TryRemoveExact(pending, session, completion);
                        });
                }
                catch
                {
                    TryRemoveExact(pending, session, completion);
                    throw;
                }
            }
            if (failure is not null)
            {
                if (overloaded)
                    FailOverloadedLink(failure, sessionToCancel);
                throw failure;
            }
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
        }
    }

    private void CompleteAbyssal(SimEnsureAbyssalSceneReply reply) =>
        CompleteAbyssal(reply.RequestId, typeof(SimEnsureAbyssalSceneReply), reply.Error);

    private void CompleteAbyssal(SimEnsureAbyssalSceneObjectReply reply) =>
        CompleteAbyssal(reply.RequestId, typeof(SimEnsureAbyssalSceneObjectReply), reply.Error);

    private void CompleteAbyssal(SimMoveAbyssalParticipantReply reply) =>
        CompleteAbyssal(reply.RequestId, typeof(SimMoveAbyssalParticipantReply), reply.Error);

    private void CompleteAbyssal(SimRebindAbyssalSessionReply reply) =>
        CompleteAbyssal(reply.RequestId, typeof(SimRebindAbyssalSessionReply), reply.Error);

    private void CompleteAbyssal(SimRemoveAbyssalSceneReply reply) =>
        CompleteAbyssal(reply.RequestId, typeof(SimRemoveAbyssalSceneReply), reply.Error);

    private void CompleteAbyssal(ulong requestId, Type replyType, string? error)
    {
        if (_abyssal.TryRemove((replyType, requestId), out var pending)) pending.TrySetResult(error);
    }

    private Task<string?> AwaitAbyssalAsync(ulong requestId, Type replyType, SimMessage request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<string?>(cancellationToken);
        lock (_pendingGate)
        {
            if (!_connected)
                throw _outboundFailure ?? new SimUnavailableException("the simulation is not running");
        }
        if (requestId == 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        return AwaitAsync(
            _abyssal,
            (replyType, requestId),
            request,
            cancellationToken,
            $"Abyssal request {requestId} already has a request of this kind waiting");
    }

    public Task<string?> EnterAsync(SimEnter enter, CancellationToken cancellationToken = default) =>
        AwaitAsync(_entering, enter.SessionId, enter, cancellationToken);

    public Task<string?> EnsureAbyssalSceneAsync(SimEnsureAbyssalScene scene, CancellationToken cancellationToken = default) =>
        AwaitAbyssalAsync(scene.RequestId, typeof(SimEnsureAbyssalSceneReply), scene, cancellationToken);

    public Task<string?> EnsureAbyssalSceneObjectAsync(SimEnsureAbyssalSceneObject sceneObject, CancellationToken cancellationToken = default) =>
        AwaitAbyssalAsync(sceneObject.RequestId, typeof(SimEnsureAbyssalSceneObjectReply), sceneObject, cancellationToken);

    public Task<string?> EnsureAbyssalEnvironmentObjectAsync(SimEnsureAbyssalEnvironmentObject environment, CancellationToken cancellationToken = default) =>
        AwaitAbyssalAsync(environment.RequestId, typeof(SimEnsureAbyssalEnvironmentObjectReply), environment, cancellationToken);

    public Task<string?> RefreshAbyssalNpcAsync(SimRefreshAbyssalNpc npc, CancellationToken cancellationToken = default) =>
        AwaitAbyssalAsync(npc.RequestId, typeof(SimRefreshAbyssalNpcReply), npc, cancellationToken);

    public Task<string?> EnsureAbyssalRewardCacheAsync(SimEnsureAbyssalRewardCache cache, CancellationToken cancellationToken = default) =>
        AwaitAbyssalAsync(cache.RequestId, typeof(SimEnsureAbyssalRewardCacheReply), cache, cancellationToken);

    public Task<string?> EnsureAbyssalLootContainerAsync(SimEnsureAbyssalLootContainer container, CancellationToken cancellationToken = default) =>
        AwaitAbyssalAsync(container.RequestId, typeof(SimEnsureAbyssalLootContainerReply), container, cancellationToken);

    public Task<string?> MoveAbyssalParticipantAsync(SimMoveAbyssalParticipant move, CancellationToken cancellationToken = default) =>
        AwaitAbyssalAsync(move.RequestId, typeof(SimMoveAbyssalParticipantReply), move, cancellationToken);

    public async Task<string?> MoveAbyssalParticipantInOrderAsync(
        SimMoveAbyssalParticipant move,
        Action<SimMoveAbyssalParticipantReply> beforeFollowingMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beforeFollowingMessages);
        if (!move.SessionHandoff) throw new ArgumentException("The move does not require a SessionChange handoff.", nameof(move));
        if (!_abyssalMoveInOrder.TryAdd(move.RequestId, beforeFollowingMessages))
            throw new InvalidOperationException($"move {move.RequestId} already has a handoff");
        try
        {
            return await AwaitAbyssalAsync(move.RequestId, typeof(SimMoveAbyssalParticipantReply), move, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _abyssalMoveInOrder.TryRemove(move.RequestId, out _);
        }
    }

    public Task<string?> RebindAbyssalSessionAsync(SimRebindAbyssalSession rebind, CancellationToken cancellationToken = default) =>
        AwaitAbyssalAsync(rebind.RequestId, typeof(SimRebindAbyssalSessionReply), rebind, cancellationToken);

    public Task<string?> RemoveAbyssalSceneAsync(SimRemoveAbyssalScene scene, CancellationToken cancellationToken = default) =>
        AwaitAbyssalAsync(scene.RequestId, typeof(SimRemoveAbyssalSceneReply), scene, cancellationToken);

    public Task<SimEnsureAbyssalTracePublishedReply> EnsureAbyssalTracePublishedAsync(
        SimEnsureAbyssalTracePublished trace, CancellationToken cancellationToken = default) =>
        AwaitAsync(_publishingAbyssalTraces, trace.RequestId, trace, cancellationToken);

    public Task<SimRemoveAbyssalTraceReply> RemoveAbyssalTraceAsync(
        SimRemoveAbyssalTrace trace, CancellationToken cancellationToken = default) =>
        AwaitAsync(_removingAbyssalTraces, trace.RequestId, trace, cancellationToken);

    public Task<SimRemoveAbyssalParticipantReply> RemoveAbyssalParticipantAsync(
        SimRemoveAbyssalParticipant participant, CancellationToken cancellationToken = default) =>
        AwaitAsync(_removingAbyssalParticipants, participant.RequestId, participant, cancellationToken);

    public Task<string?> EnsureTournamentBeaconAsync(SimEnsureTournamentBeacon request, CancellationToken cancellationToken = default) =>
        AwaitAbyssalAsync(request.RequestId, typeof(SimEnsureTournamentBeaconReply), request, cancellationToken);

    public Task<string?> RemoveTournamentBeaconAsync(SimRemoveTournamentBeacon request, CancellationToken cancellationToken = default) =>
        AwaitAbyssalAsync(request.RequestId, typeof(SimRemoveTournamentBeaconReply), request, cancellationToken);

    public async Task<SimEnterPublicParticipantReply> EnterPublicParticipantInOrderAsync(
        SimEnterPublicParticipant request, Action<SimEnterPublicParticipantReply> beforeFollowingMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beforeFollowingMessages);
        if (!_publicEnterInOrder.TryAdd(request.RequestId, beforeFollowingMessages))
            throw new InvalidOperationException($"entry {request.RequestId} already has a handoff");
        try { return await AwaitAsync(_publicEnters, request.RequestId, request, cancellationToken).ConfigureAwait(false); }
        finally { _publicEnterInOrder.TryRemove(request.RequestId, out _); }
    }

    public async Task<SimMovePublicParticipantReply> MovePublicParticipantInOrderAsync(
        SimMovePublicParticipant request, Action<SimMovePublicParticipantReply> beforeFollowingMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beforeFollowingMessages);
        if (!_publicMoveInOrder.TryAdd(request.RequestId, beforeFollowingMessages))
            throw new InvalidOperationException($"move {request.RequestId} already has a handoff");
        try { return await AwaitAsync(_publicMoves, request.RequestId, request, cancellationToken).ConfigureAwait(false); }
        finally { _publicMoveInOrder.TryRemove(request.RequestId, out _); }
    }

    public async Task<SimRegisterTournamentMetricsReply> RegisterTournamentMetricsAsync(SimRegisterTournamentMetrics request, CancellationToken cancellationToken = default)
    {
        var reply=await AwaitAsync(_metricsRegistrations,request.RequestId,request,cancellationToken).ConfigureAwait(false);
        if(reply.RequestId!=request.RequestId || reply.Scene!=request.Scene || reply.MatchId!=request.MatchId || reply.Generation!=request.Generation || reply.Start!=request.Start || !reply.Roster.SequenceEqual(request.Roster))
            throw new SimLinkException(SimLinkError.BadValue,"metrics registration reply does not match its request");
        return reply;
    }

    public async Task<SimReadTournamentMetricsReply> ReadTournamentMetricsAsync(SimReadTournamentMetrics request, CancellationToken cancellationToken = default)
    {
        var reply=await AwaitAsync(_metricsReads,request.RequestId,request,cancellationToken).ConfigureAwait(false);
        if(reply.RequestId!=request.RequestId || reply.Scene!=request.Scene || reply.MatchId!=request.MatchId || reply.Generation!=request.Generation || reply.Start!=request.Start || !reply.Roster.SequenceEqual(request.Roster))
            throw new SimLinkException(SimLinkError.BadValue,"metrics sample reply does not match its request");
        return reply;
    }

    public async Task<SimReadTournamentMetricsV2Reply> ReadTournamentMetricsV2Async(SimReadTournamentMetricsV2 request, CancellationToken cancellationToken = default)
    {
        var reply=await AwaitAsync(_metricsV2Reads,request.RequestId,request,cancellationToken).ConfigureAwait(false);
        if(reply.RequestId!=request.RequestId || reply.Scene!=request.Scene || reply.MatchId!=request.MatchId || reply.Generation!=request.Generation || reply.Start!=request.Start || !reply.Roster.SequenceEqual(request.Roster))
            throw new SimLinkException(SimLinkError.BadValue,"metrics V2 sample reply does not match its request");
        return reply;
    }

    public async Task<SimReleaseTournamentMetricsReply> ReleaseTournamentMetricsAsync(SimReleaseTournamentMetrics request, CancellationToken cancellationToken = default)
    {
        var reply=await AwaitAsync(_metricsReleases,request.RequestId,request,cancellationToken).ConfigureAwait(false);
        if(reply.RequestId!=request.RequestId || reply.Scene!=request.Scene || reply.MatchId!=request.MatchId || reply.Generation!=request.Generation || reply.Start!=request.Start || !reply.Roster.SequenceEqual(request.Roster))
            throw new SimLinkException(SimLinkError.BadValue,"metrics release reply does not match its request");
        return reply;
    }

    public async Task<SimReleaseTournamentCutoffReply> ReleaseTournamentCutoffAsync(SimReleaseTournamentCutoff request, CancellationToken cancellationToken = default)
    {
        var reply = await AwaitAsync(_cutoffReleases, request.RequestId, request, cancellationToken).ConfigureAwait(false);
        if (reply.RequestId != request.RequestId || reply.Scene != request.Scene || reply.MatchId != request.MatchId || reply.Generation != request.Generation || reply.Start != request.Start || reply.Deadline != request.Deadline)
            throw new SimLinkException(SimLinkError.BadValue, "cutoff release reply does not match its request");
        return reply;
    }

    public async Task<SimRegisterTournamentCutoffReply> RegisterTournamentCutoffAsync(SimRegisterTournamentCutoff request, CancellationToken cancellationToken = default)
    {
        var reply = await AwaitAsync(_cutoffRegistrations, request.RequestId, request, cancellationToken).ConfigureAwait(false);
        if (reply.RequestId != request.RequestId || reply.Scene != request.Scene || reply.MatchId != request.MatchId || reply.Generation != request.Generation || reply.Start != request.Start || reply.Deadline != request.Deadline)
            throw new SimLinkException(SimLinkError.BadValue, "cutoff registration reply does not match its request");
        return reply;
    }

    public async Task<SimReadTournamentCutoffReply> ReadTournamentCutoffAsync(SimReadTournamentCutoff request, CancellationToken cancellationToken = default)
    {
        var reply = await AwaitAsync(_cutoffReads, request.RequestId, request, cancellationToken).ConfigureAwait(false);
        if (reply.RequestId != request.RequestId || reply.Scene != request.Scene || reply.MatchId != request.MatchId || reply.Generation != request.Generation || reply.Start != request.Start || reply.Deadline != request.Deadline)
            throw new SimLinkException(SimLinkError.BadValue, "cutoff evidence reply does not match its request");
        return reply;
    }

    public async Task<SimQueryTournamentWreckReply> QueryTournamentWreckAsync(SimQueryTournamentWreck request, CancellationToken cancellationToken = default)
    {
        var reply = await AwaitAsync(_tournamentWreckQueries, request.RequestId, request, cancellationToken).ConfigureAwait(false);
        if (reply.RequestId != request.RequestId || reply.Scene != request.Scene || reply.ItemId != request.ItemId)
            throw new SimLinkException(SimLinkError.BadValue, "tournament wreck reply does not match its request");
        return reply;
    }

    public async Task<SimRemoveTournamentWreckReply> RemoveTournamentWreckAsync(SimRemoveTournamentWreck request, CancellationToken cancellationToken = default)
    {
        var reply = await AwaitAsync(_tournamentWreckRemovals, request.RequestId, request, cancellationToken).ConfigureAwait(false);
        if (reply.RequestId != request.RequestId || reply.Scene != request.Scene || reply.ItemId != request.ItemId)
            throw new SimLinkException(SimLinkError.BadValue, "tournament wreck reply does not match its request");
        return reply;
    }

    public async Task<SimGetSceneBodyReply> GetSceneBodyAsync(SimGetSceneBody request, CancellationToken cancellationToken = default)
    {
        var reply = await AwaitAsync(_sceneBodies, request.RequestId, request, cancellationToken).ConfigureAwait(false);
        if (reply.RequestId != request.RequestId || reply.Scene != request.Scene || reply.ItemId != request.ItemId)
            throw new SimLinkException(SimLinkError.BadValue, "scene body reply does not match its request");
        return reply;
    }

    public Task<SimGetAbyssalParticipantPoseReply> GetAbyssalParticipantPoseAsync(
        SimGetAbyssalParticipantPose request, CancellationToken cancellationToken = default) =>
        AwaitAsync(_abyssalParticipantPoses, request.RequestId, request, cancellationToken);

    public async Task<string?> ControlStructureAsync(SimControlStructure control, CancellationToken cancellationToken = default)
    {
        var reply = await AwaitAsync(_answered, (control.SessionId, SimCommands.ControlStructure), control, cancellationToken);
        return reply.Accepted ? null : reply.Reason ?? "STRUCTURE_CONTROL_FAILED";
    }

    public Task SetHeatProfileAsync(ulong sessionId, SimHeatProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Send(new SimSetHeatProfile(sessionId, profile));
        return Task.CompletedTask;
    }

    public Task UpdateEffectAsync(SimUpdateEffect request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Send(request);
        return Task.CompletedTask;
    }

    public Task SetModuleOverheatAsync(ulong sessionId, long moduleId, bool active, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Send(new SimSetModuleOverheat(sessionId, moduleId, active));
        return Task.CompletedTask;
    }

    public Task OfflineModuleAsync(ulong sessionId, long moduleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Send(new SimSetModuleOffline(sessionId, moduleId));
        return Task.CompletedTask;
    }

    public Task SetRackOverheatAsync(ulong sessionId, int rack, bool active, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Send(new SimSetRackOverheat(sessionId, rack, active));
        return Task.CompletedTask;
    }

    public Task<CommandReply> CommandAsync(ulong sessionId, byte code, double[] args, CancellationToken cancellationToken = default) =>
        AwaitAsync(_answered, (sessionId, code), new SimCommand(sessionId, code, args), cancellationToken);

    public Task<CommandReply> ActivateAsync(ulong sessionId, SimEffect effect, CancellationToken cancellationToken = default) =>
        AwaitAsync(_answered, (sessionId, SimCommands.Activate), new SimActivate(sessionId, effect), cancellationToken);

    public async Task<SimChargeAdjusted> AdjustChargeAsync(SimAdjustCharge request, Action<SimChargeAdjusted>? inOrder = null, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<SimChargeAdjusted>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = (Completion: completion, InOrder: inOrder);
        CancellationTokenRegistration registration = default;
        SimUnavailableException? failure = null;
        CancellationTokenSource? sessionToCancel = null;
        var overloaded = false;
        try
        {
            lock (_pendingGate)
            {
                if (_outboundFailure is { } linkFailure) throw linkFailure;
                if (!_connected) throw new SimUnavailableException("the simulation is not running");
                _afterConnectionCheck?.Invoke();
                if (!_adjusting.TryAdd(request.RequestId, pending))
                    throw new InvalidOperationException($"request {request.RequestId} is already waiting");
                try
                {
                    registration = cancellationToken.Register(() =>
                        CancelPending(_pendingGate, _adjusting, request.RequestId, pending, completion, cancellationToken));
                    TryEnqueue(
                        _pendingGate,
                        cancellationToken,
                        completion,
                        () => CancelPending(_pendingGate, _adjusting, request.RequestId, pending, completion, cancellationToken),
                        () =>
                        {
                            failure = TryEnqueueLocked(request, out sessionToCancel, out overloaded);
                            if (failure is not null)
                                TryRemoveExact(_adjusting, request.RequestId, pending);
                        });
                }
                catch
                {
                    TryRemoveExact(_adjusting, request.RequestId, pending);
                    throw;
                }
            }
            if (failure is not null)
            {
                if (overloaded)
                    FailOverloadedLink(failure, sessionToCancel);
                throw failure;
            }
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
        }
    }

    public Task<SimSpawnReply> SpawnAsync(SimSpawn spawn, CancellationToken cancellationToken = default) =>
        AwaitAsync(_spawning, spawn.RequestId, spawn, cancellationToken);

    public Task<SimSpawnReply> LaunchDroneAsync(SimLaunchDrone launch, CancellationToken cancellationToken = default) =>
        AwaitAsync(_spawning, launch.RequestId, launch, cancellationToken);

    public Task<CommandReply> DroneCommandAsync(ulong sessionId, byte command, long targetId, IReadOnlyList<long> droneIds, double controlRange, CancellationToken cancellationToken = default) =>
        AwaitAsync(_answered, (sessionId, SimDroneCommands.Key(command)), new SimDroneCommand(sessionId, command, targetId, droneIds, controlRange), cancellationToken);

    public Task<SimSpawnReply> LaunchFighterAsync(SimLaunchFighter launch, CancellationToken cancellationToken = default) =>
        AwaitAsync(_spawning, launch.RequestId, launch, cancellationToken);

    public Task<CommandReply> FighterOrderAsync(ulong sessionId, byte command, long targetId, IReadOnlyList<long> fighterIds, double range, CancellationToken cancellationToken = default) =>
        AwaitAsync(_answered, (sessionId, SimFighterOrders.Key(command)), new SimFighterOrder(sessionId, command, targetId, fighterIds, range), cancellationToken);

    public Task<CommandReply> FighterGotoPointAsync(ulong sessionId, IReadOnlyList<long> fighterIds, IReadOnlyList<double> point, CancellationToken cancellationToken = default) =>
        AwaitAsync(_answered, (sessionId, SimFighterOrders.Key(SimFighterOrders.GotoPoint)), new SimFighterGotoPoint(sessionId, fighterIds, point), cancellationToken);

    public Task<string?> RespawnAsync(SimRespawn respawn, CancellationToken cancellationToken = default) =>
        AwaitAsync(_entering, respawn.SessionId, respawn, cancellationToken);

    public void PlaceWreck(SimPlaceWreck wreck) => Send(wreck);

    public void PlaceAsteroid(SimPlaceAsteroid asteroid) => Send(asteroid);

    public void DroneWorkResult(SimDroneWorkResult result) => Send(result);

    public void RemoteRepairCycleResult(SimRemoteRepairCycleResult result) => Send(result);

    public void ModuleWorkResult(SimModuleWorkResult result) => Send(result);

    public void StructureDamageCandidatesResult(SimStructureDamageCandidatesResult result) => Send(result);

    public void BuffState(ulong sessionId, long shipId, IReadOnlyList<SimBuffEntry> entries) => Send(new SimBuffState(sessionId, shipId, entries));

    public Task<CommandReply> PlaceContainerAsync(SimPlaceContainer can, CancellationToken cancellationToken = default) =>
        AwaitAsync(_answered, (can.SessionId, SimCommands.PlaceContainer), can, cancellationToken);

    public Task<CommandReply> ChangeWreckAsync(ulong sessionId, long wreckId, bool? isEmpty, bool? abandoned = null, CancellationToken cancellationToken = default) =>
        AwaitAsync(_answered, (sessionId, SimCommands.ChangeWreck), new SimChangeWreck(sessionId, wreckId, isEmpty, abandoned), cancellationToken);

    public void RemoveWreck(long wreckId, int effect) => Send(new SimRemoveWreck(wreckId, effect));

    public void RestoreDeployed(SimDeployed deployed) => Send(deployed);

    public void RemoveDeployed(long deployableId) => Send(new SimRemoveDeployed(deployableId));

    public Task<bool> RemoveDeployedAsync(long deployableId, CancellationToken cancellationToken = default) =>
        SendAsync(new SimRemoveDeployed(deployableId), cancellationToken);

    public void PlaceProbe(SimPlaceProbe probe) => Send(probe);

    public void RemoveProbe(long probeId) => Send(new SimRemoveProbe(probeId));

    public void PlaceStructure(SimPlaceStructure structure) => Send(structure);

    public void PlaceWormhole(SimPlaceWormhole wormhole) => Send(wormhole);

    public void RemoveWormhole(long wormholeId) => Send(new SimRemoveWormhole(wormholeId));

    public void SetTetherPolicy(SimTetherPolicy policy) => Send(policy);

    public void RemoveStructure(long structureId) => Send(new SimRemoveStructure(structureId));

    public void SetStructureState(SimSetStructureState state) => Send(state);

    public void PlaceDisruptor(SimPlaceDisruptor disruptor) => Send(disruptor);

    public void SetDisruptorActive(long deployableId, bool active) => Send(new SimSetDisruptorActive(deployableId, active));

    public void PlaceAnalysisBeacon(SimPlaceAnalysisBeacon beacon) => Send(beacon);

    public void SetAnalysisBeaconState(long deployableId, SimAnalysisBeaconState state) => Send(new SimSetAnalysisBeaconState(deployableId, state));

    public void SetSlimField(long itemId, string fieldName, SimSlimValue value) => Send(new SimSetSlimField(itemId, fieldName, value));

    public void PlaceInhibitor(SimPlaceInhibitor inhibitor) => Send(inhibitor);

    public void PlaceCynoBeacon(SimPlaceCynoBeacon beacon) => Send(beacon);

    public void PlaceObservatory(SimPlaceObservatory observatory) => Send(observatory);
    public void PlaceControlTower(SimPlaceControlTower tower) => Send(tower);
    public void PlaceDeadspacePocket(SimPlaceDeadspacePocket pocket) => Send(pocket);
    public Task<bool> PlaceDeadspacePocketAsync(SimPlaceDeadspacePocket pocket, CancellationToken cancellationToken = default) =>
        SendAsync(pocket, cancellationToken);
    public void PlaceAccelerationGate(SimPlaceAccelerationGate gate) => Send(gate);
    public Task<bool> PlaceAccelerationGateAsync(SimPlaceAccelerationGate gate, CancellationToken cancellationToken = default) =>
        SendAsync(gate, cancellationToken);
    public void PlaceCapturePoint(SimPlaceCapturePoint point) => Send(point);
    public void PlaceBattlefieldScoreboard(SimPlaceBattlefieldScoreboard scoreboard) => Send(scoreboard);

    public void RemoveCynoBeacon(int solarSystemId, long structureBeaconId) => Send(new SimRemoveCynoBeacon(solarSystemId, structureBeaconId));

    public void RemoteDecloakPing(int solarSystemId, long observatoryId) => Send(new SimRemoteDecloakPing(solarSystemId, observatoryId));

    public void Destroy(long shipId, SimCredit credit) => Send(new SimDestroy(shipId, credit));

    public void Leave(ulong sessionId, LeaveWhy why) => Send(new SimLeave(sessionId, why));

    public void Bind(ulong sessionId) => Send(new SimBind(sessionId));

    public void UpdateState(ulong sessionId) => Send(new SimUpdateState(sessionId));

    public void Command(ulong sessionId, byte code, params double[] args) => Send(new SimCommand(sessionId, code, args));

    public void ShipChanged(ulong sessionId, SimShip ship) => Send(new SimShipChanged(sessionId, ship));

    public void CriminalFlagActive(ulong sessionId) => Send(new SimCriminalFlagActive(sessionId));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            try
            {
                await _run;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _stop.Dispose();
    }
}
