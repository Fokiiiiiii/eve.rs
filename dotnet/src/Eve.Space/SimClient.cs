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
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<string?>> _entering = new();
    private readonly ConcurrentDictionary<(ulong Session, byte Code), TaskCompletionSource<CommandReply>> _answered = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SimSpawnReply>> _spawning = new();
    private readonly ConcurrentDictionary<ulong, (TaskCompletionSource<SimChargeAdjusted> Completion, Action<SimChargeAdjusted>? InOrder)> _adjusting = new();
    private Task? _run;
    private volatile bool _connected;
    private TimeSpan _retryDelay = RetryDelay;

    public SimClient(Func<CancellationToken, Task<Stream>> connect, Action<string>? log = null)
    {
        _connect = connect;
        _log = log;
    }

    /// <summary>A client of the simulation listening on a Unix socket.</summary>
    public static SimClient ToUnixSocket(string path, Action<string>? log = null) => new(async cancellationToken =>
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
    }, log);

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
            if (_connected)
            {
                _connected = false;
                FailPending();
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
        _connected = false;
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
        // whatever was queued for a link that broke is stale
        while (_outbox.Reader.TryRead(out _))
        {
        }
        _connected = true;

        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
    }

    private void Send(SimMessage message)
    {
        if (!_connected)
        {
            return; // nobody is there to hear it
        }
        _outbox.Writer.TryWrite(SimCodec.Encode(message));
    }

    private async Task<T> AwaitAsync<TKey, T>(ConcurrentDictionary<TKey, TaskCompletionSource<T>> pending, TKey session, SimMessage request, CancellationToken cancellationToken)
        where TKey : notnull
    {
        if (!_connected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(session, completion))
        {
            throw new InvalidOperationException($"session {session} already has a request of this kind waiting");
        }
        using var registration = cancellationToken.Register(() =>
        {
            pending.TryRemove(session, out _);
            completion.TrySetCanceled(cancellationToken);
        });
        Send(request);
        return await completion.Task;
    }

    public Task<string?> EnterAsync(SimEnter enter, CancellationToken cancellationToken = default) =>
        AwaitAsync(_entering, enter.SessionId, enter, cancellationToken);

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
        if (!_connected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        var completion = new TaskCompletionSource<SimChargeAdjusted>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_adjusting.TryAdd(request.RequestId, (completion, inOrder)))
        {
            throw new InvalidOperationException($"request {request.RequestId} is already waiting");
        }
        using var registration = cancellationToken.Register(() =>
        {
            _adjusting.TryRemove(request.RequestId, out _);
            completion.TrySetCanceled(cancellationToken);
        });
        Send(request);
        return await completion.Task;
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

    public void BuffState(ulong sessionId, long shipId, IReadOnlyList<SimBuffEntry> entries) => Send(new SimBuffState(sessionId, shipId, entries));

    public Task<CommandReply> PlaceContainerAsync(SimPlaceContainer can, CancellationToken cancellationToken = default) =>
        AwaitAsync(_answered, (can.SessionId, SimCommands.PlaceContainer), can, cancellationToken);

    public Task<CommandReply> ChangeWreckAsync(ulong sessionId, long wreckId, bool isEmpty, CancellationToken cancellationToken = default) =>
        AwaitAsync(_answered, (sessionId, SimCommands.ChangeWreck), new SimChangeWreck(sessionId, wreckId, isEmpty), cancellationToken);

    public void RemoveWreck(long wreckId, int effect) => Send(new SimRemoveWreck(wreckId, effect));

    public void RestoreDeployed(SimDeployed deployed) => Send(deployed);

    public void RemoveDeployed(long deployableId) => Send(new SimRemoveDeployed(deployableId));

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

    public void PlaceInhibitor(SimPlaceInhibitor inhibitor) => Send(inhibitor);

    public void PlaceCynoBeacon(SimPlaceCynoBeacon beacon) => Send(beacon);

    public void Destroy(long shipId, SimCredit credit) => Send(new SimDestroy(shipId, credit));

    public void Leave(ulong sessionId, LeaveWhy why) => Send(new SimLeave(sessionId, why));

    public void Bind(ulong sessionId) => Send(new SimBind(sessionId));

    public void UpdateState(ulong sessionId) => Send(new SimUpdateState(sessionId));

    public void Command(ulong sessionId, byte code, params double[] args) => Send(new SimCommand(sessionId, code, args));

    public void ShipChanged(ulong sessionId, SimShip ship) => Send(new SimShipChanged(sessionId, ship));

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
