using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>The simulation as a test plays it: it records what the host asks and says what the test tells it to.</summary>
internal sealed class FakeSim : ISimulation
{
    public bool IsConnected { get; set; } = true;

    public event Action<SimMessage>? Received;

    public event Action? Lost;

    public System.Collections.Concurrent.ConcurrentQueue<SimMessage> Heard { get; } = new();

    /// <summary>What an <c>Enter</c> is answered with: null (done) or a reason.</summary>
    public string? EnterAnswer { get; set; }

    public CommandReply DockAnswer { get; set; } = new(true, 134351527136350000, null);

    /// <summary>What a warp is answered with.</summary>
    public CommandReply WarpAnswer { get; set; } = new(true, 134351527136350000, null);

    /// <summary>What a jump is answered with.</summary>
    public CommandReply JumpAnswer { get; set; } = new(true, 0, null, null, 30100034, 50016839);

    /// <summary>What a cynosural jump (the spool) is answered with.</summary>
    public CommandReply CynoJumpAnswer { get; set; } = new(true, 0, null, null, 30100034, 50016839);

    /// <summary>What a spawn is answered with: the session of the NPC, or a reason.</summary>
    public SimSpawnReply? SpawnAnswer { get; set; }

    /// <summary>When set, a spawn fails as it does when the link to the simulation breaks.</summary>
    public bool SpawnUnavailable { get; set; }

    public Task<SimSpawnReply> SpawnAsync(SimSpawn spawn, CancellationToken cancellationToken = default)
    {
        if (SpawnUnavailable)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        Heard.Enqueue(spawn);
        return Task.FromResult(SpawnAnswer is { } answer ? answer with { RequestId = spawn.RequestId } : new SimSpawnReply(spawn.RequestId, (ulong)Heard.Count + (1UL << 62), null));
    }

    /// <summary>What a launched drone is answered with: the session it is known by, or a reason.</summary>
    public SimSpawnReply? LaunchAnswer { get; set; }

    public Task<SimSpawnReply> LaunchDroneAsync(SimLaunchDrone launch, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        Heard.Enqueue(launch);
        return Task.FromResult(LaunchAnswer is { } answer ? answer with { RequestId = launch.RequestId } : new SimSpawnReply(launch.RequestId, (ulong)Heard.Count + (1UL << 62), null));
    }

    /// <summary>What an order to drones is answered with: every drone takes it, unless the test says otherwise.</summary>
    public Func<ulong, byte, long, IReadOnlyList<long>, CommandReply>? DroneAnswer { get; set; }

    public Task<CommandReply> DroneCommandAsync(ulong sessionId, byte command, long targetId, IReadOnlyList<long> droneIds, double controlRange, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        Heard.Enqueue(new SimDroneCommand(sessionId, command, targetId, droneIds, controlRange));
        return Task.FromResult(DroneAnswer?.Invoke(sessionId, command, targetId, droneIds)
            ?? new CommandReply(true, 0, null) { Drones = [.. droneIds.Select(id => new SimDroneResult(id, ""))] });
    }

    /// <summary>What a launched fighter squadron is answered with: the session it is known by, or a reason.</summary>
    public SimSpawnReply? FighterLaunchAnswer { get; set; }

    public Task<SimSpawnReply> LaunchFighterAsync(SimLaunchFighter launch, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        Heard.Enqueue(launch);
        return Task.FromResult(FighterLaunchAnswer is { } answer ? answer with { RequestId = launch.RequestId } : new SimSpawnReply(launch.RequestId, (ulong)Heard.Count + (1UL << 62), null));
    }

    /// <summary>What an order to fighter squadrons is answered with: every squadron takes it, unless the test says otherwise.</summary>
    public Func<ulong, byte, long, IReadOnlyList<long>, CommandReply>? FighterAnswer { get; set; }

    public Task<CommandReply> FighterOrderAsync(ulong sessionId, byte command, long targetId, IReadOnlyList<long> fighterIds, double range, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        Heard.Enqueue(new SimFighterOrder(sessionId, command, targetId, fighterIds, range));
        return Task.FromResult(FighterAnswer?.Invoke(sessionId, command, targetId, fighterIds)
            ?? new CommandReply(true, 0, null) { Fighters = [.. fighterIds.Select(id => new SimFighterResult(id, ""))] });
    }

    public Task<CommandReply> FighterGotoPointAsync(ulong sessionId, IReadOnlyList<long> fighterIds, IReadOnlyList<double> point, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        Heard.Enqueue(new SimFighterGotoPoint(sessionId, fighterIds, point));
        return Task.FromResult(new CommandReply(true, 0, null) { Fighters = [.. fighterIds.Select(id => new SimFighterResult(id, ""))] });
    }

    /// <summary>What a respawn is answered with: null (done) or a reason.</summary>
    public string? RespawnAnswer { get; set; }

    /// <summary>When set, a respawn is not answered until the test completes it.</summary>
    public TaskCompletionSource? RespawnGate { get; set; }

    public async Task<string?> RespawnAsync(SimRespawn respawn, CancellationToken cancellationToken = default)
    {
        Heard.Enqueue(respawn);
        if (RespawnGate is { } gate)
        {
            await gate.Task;
        }
        return RespawnAnswer;
    }

    public void PlaceWreck(SimPlaceWreck wreck) => Heard.Enqueue(wreck);

    public void PlaceAsteroid(SimPlaceAsteroid asteroid) => Heard.Enqueue(asteroid);

    public void DroneWorkResult(SimDroneWorkResult result) => Heard.Enqueue(result);

    public void RemoteRepairCycleResult(SimRemoteRepairCycleResult result) => Heard.Enqueue(result);

    public void ModuleWorkResult(SimModuleWorkResult result) => Heard.Enqueue(result);

    /// <summary>The buff states the host sent, in order (a command burst's, protocol 43).</summary>
    public List<SimBuffState> BuffStates { get; } = [];

    public void BuffState(ulong sessionId, long shipId, IReadOnlyList<SimBuffEntry> entries) => BuffStates.Add(new SimBuffState(sessionId, shipId, entries));

    /// <summary>What a can that is placed is answered with: where it lies (the test's own place when none is set), or why not.</summary>
    public CommandReply? PlaceContainerAnswer { get; set; }

    public Task<CommandReply> PlaceContainerAsync(SimPlaceContainer can, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        Heard.Enqueue(can);
        return Task.FromResult(PlaceContainerAnswer ?? new CommandReply(true, 0, null) { Position = [1000.0, 2000.0, 3000.0], Direction = [1.0, 0.0, 0.0] });
    }

    /// <summary>What a change of the rounds of a gun is answered with (the default: the gun does not run, the host has its rounds).</summary>
    public List<SimUpdateEffect> Updates { get; } = [];

    public Task UpdateEffectAsync(SimUpdateEffect request, CancellationToken cancellationToken = default)
    {
        Updates.Add(request);
        return Task.CompletedTask;
    }

    public Func<SimAdjustCharge, SimChargeAdjusted> AdjustAnswer { get; set; } = request => new SimChargeAdjusted(request.RequestId, false, 0, 0);

    /// <summary>What the simulation tells right after it has answered a change of rounds (a count of the gun, as it goes on shooting).</summary>
    public Action<SimAdjustCharge>? AfterAnswer { get; set; }

    public Task<SimChargeAdjusted> AdjustChargeAsync(SimAdjustCharge request, Action<SimChargeAdjusted>? inOrder = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        Heard.Enqueue(request);
        var answer = AdjustAnswer(request) with { RequestId = request.RequestId };
        inOrder?.Invoke(answer);
        AfterAnswer?.Invoke(request);
        return Task.FromResult(answer);
    }

    public Task<CommandReply> ChangeWreckAsync(ulong sessionId, long wreckId, bool isEmpty, CancellationToken cancellationToken = default)
    {
        Heard.Enqueue(new SimChangeWreck(sessionId, wreckId, isEmpty));
        return Task.FromResult(new CommandReply(true, 0, null));
    }

    public void RemoveWreck(long wreckId, int effect) => Heard.Enqueue(new SimRemoveWreck(wreckId, effect));

    public void RestoreDeployed(SimDeployed deployed) => Heard.Enqueue(deployed);

    public void RemoveDeployed(long deployableId) => Heard.Enqueue(new SimRemoveDeployed(deployableId));

    public void PlaceProbe(SimPlaceProbe probe) => Heard.Enqueue(probe);

    public void RemoveProbe(long probeId) => Heard.Enqueue(new SimRemoveProbe(probeId));

    public void PlaceStructure(SimPlaceStructure structure) => Heard.Enqueue(structure);

    public void RemoveStructure(long structureId) => Heard.Enqueue(new SimRemoveStructure(structureId));

    public void SetStructureState(SimSetStructureState state) => Heard.Enqueue(state);

    public void PlaceDisruptor(SimPlaceDisruptor disruptor) => Heard.Enqueue(disruptor);

    public void SetDisruptorActive(long deployableId, bool active) => Heard.Enqueue(new SimSetDisruptorActive(deployableId, active));

    public void PlaceAnalysisBeacon(SimPlaceAnalysisBeacon beacon) => Heard.Enqueue(beacon);

    public void SetAnalysisBeaconState(long deployableId, SimAnalysisBeaconState state) => Heard.Enqueue(new SimSetAnalysisBeaconState(deployableId, state));

    public void PlaceInhibitor(SimPlaceInhibitor inhibitor) => Heard.Enqueue(inhibitor);
    public void PlaceCynoBeacon(SimPlaceCynoBeacon beacon) => Heard.Enqueue(beacon);

    public void Destroy(long shipId, SimCredit credit) => Heard.Enqueue(new SimDestroy(shipId, credit));

    public void Report(SimMessage message) => Received?.Invoke(message);

    public Task SetHeatProfileAsync(ulong sessionId, SimHeatProfile profile, CancellationToken cancellationToken = default)
    {
        Heard.Enqueue(new SimSetHeatProfile(sessionId, profile));
        return Task.CompletedTask;
    }

    public Task SetModuleOverheatAsync(ulong sessionId, long moduleId, bool active, CancellationToken cancellationToken = default)
    {
        Heard.Enqueue(new SimSetModuleOverheat(sessionId, moduleId, active));
        return Task.CompletedTask;
    }

    public Task OfflineModuleAsync(ulong sessionId, long moduleId, CancellationToken cancellationToken = default)
    {
        Heard.Enqueue(new SimSetModuleOffline(sessionId, moduleId));
        return Task.CompletedTask;
    }

    public Task SetRackOverheatAsync(ulong sessionId, int rack, bool active, CancellationToken cancellationToken = default)
    {
        Heard.Enqueue(new SimSetRackOverheat(sessionId, rack, active));
        return Task.CompletedTask;
    }

    public Task<string?> EnterAsync(SimEnter enter, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        Heard.Enqueue(enter);
        return Task.FromResult(EnterAnswer);
    }

    /// <summary>What a targeting order is answered with.</summary>
    public CommandReply TargetAnswer { get; set; } = new(true, 0, null) { Pending = true };

    /// <summary>When set, a targeting order fails as it does when the link to the simulation breaks.</summary>
    public bool TargetsUnavailable { get; set; }

    /// <summary>When set, a targeting order is not answered until the test completes it.</summary>
    public TaskCompletionSource? TargetGate { get; set; }

    /// <summary>What a scan-target query (command 27) is answered with: the targets the scene holds.</summary>
    public IReadOnlyList<SimScanTarget> ScanTargetsAnswer { get; set; } = [];

    /// <summary>What a directional scan (command 28) is answered with: what the scene has that a cone can see.</summary>
    public IReadOnlyList<SimDirectionalTarget> DirectionalAnswer { get; set; } = [];

    public Task<CommandReply> CommandAsync(ulong sessionId, byte code, double[] args, CancellationToken cancellationToken = default)
    {
        Heard.Enqueue(new SimCommand(sessionId, code, args));
        if (code == SimCommands.DirectionalScan)
        {
            return Task.FromResult(new CommandReply(true, 0, null) { DirectionalTargets = DirectionalAnswer });
        }
        if (code == SimCommands.ScanTargets)
        {
            return Task.FromResult(new CommandReply(true, 0, null) { ScanTargets = ScanTargetsAnswer });
        }
        if (code is >= SimCommands.AddTarget and <= SimCommands.GetTargeters)
        {
            if (TargetsUnavailable)
            {
                throw new SimUnavailableException("the link to the simulation broke");
            }
            return TargetGate is { } gate ? gate.Task.ContinueWith(_ => TargetAnswer, cancellationToken) : Task.FromResult(TargetAnswer);
        }
        if (code == SimCommands.GetCondition)
        {
            return Task.FromResult(new CommandReply(true, 0, null) { Condition = ConditionAnswer });
        }
        if (code == SimCommands.Heal)
        {
            return Task.FromResult(HealAnswer);
        }
        if (code == SimCommands.Measure)
        {
            return Task.FromResult(new CommandReply(true, 0, null) { Distance = MeasureAnswer });
        }
        if (code == SimCommands.ResolveWarpTarget)
        {
            return Task.FromResult(new CommandReply(true, 0, null) { Position = WarpDestinationAnswer });
        }
        if (code == SimCommands.DropPoint)
        {
            return Task.FromResult(new CommandReply(true, 0, null) { Position = DropPointAnswer(sessionId, args[0]) });
        }
        if (code == SimCommands.PositionOf)
        {
            var targetId = (long)args[0];
            return Task.FromResult(new CommandReply(true, 0, null)
            {
                Position = PositionAnswers.GetValueOrDefault(targetId, [0, 0, 0])
            });
        }
        return Task.FromResult(code switch { SimCommands.WarpTo or SimCommands.WarpToPoint => WarpAnswer, SimCommands.Jump => JumpAnswer, SimCommands.CynoJump or SimCommands.OpenBridge or SimCommands.BridgeJump or SimCommands.ConduitJump => CynoJumpAnswer, _ => DockAnswer });
    }

    /// <summary>How far a wreck or can is said to be (null: there is none).</summary>
    public double? MeasureAnswer { get; set; } = 100;

    public IReadOnlyList<double> WarpDestinationAnswer { get; set; } = [1_000_000, 0, 0];

    public Dictionary<long, IReadOnlyList<double>> PositionAnswers { get; } = [];

    /// <summary>The drop point the simulation answers for a distance ahead of the pilot's ship (the default: along +x).</summary>
    public Func<ulong, double, IReadOnlyList<double>> DropPointAnswer { get; set; } = (_, distance) => [distance, 0, 0];

    /// <summary>What the ship is said to have taken.</summary>
    public SimCondition ConditionAnswer { get; set; } = SimCondition.Full;

    /// <summary>What a heal is answered with.</summary>
    public CommandReply HealAnswer { get; set; } = new(true, 134351527136350000, null);

    /// <summary>What an effect that is started is answered with.</summary>
    public CommandReply ActivateAnswer { get; set; } = new(true, 134351527136350000, null);

    /// <summary>When set, starting an effect is not answered until the test completes it.</summary>
    public TaskCompletionSource? ActivateGate { get; set; }

    /// <summary>When set, starting an effect fails as it does when the link to the simulation breaks.</summary>
    public bool ActivateUnavailable { get; set; }

    public Task<CommandReply> ActivateAsync(ulong sessionId, SimEffect effect, CancellationToken cancellationToken = default)
    {
        if (ActivateUnavailable)
        {
            throw new SimUnavailableException("the link to the simulation broke");
        }
        Heard.Enqueue(new SimActivate(sessionId, effect));
        return ActivateGate is { } gate ? gate.Task.ContinueWith(_ => ActivateAnswer, cancellationToken) : Task.FromResult(ActivateAnswer);
    }

    public void Leave(ulong sessionId, LeaveWhy why) => Heard.Enqueue(new SimLeave(sessionId, why));

    public void Bind(ulong sessionId) => Heard.Enqueue(new SimBind(sessionId));

    public void UpdateState(ulong sessionId) => Heard.Enqueue(new SimUpdateState(sessionId));

    public void Command(ulong sessionId, byte code, params double[] args) => Heard.Enqueue(new SimCommand(sessionId, code, args));

    public void ShipChanged(ulong sessionId, SimShip ship) => Heard.Enqueue(new SimShipChanged(sessionId, ship));

    public void Say(SimMessage message) => Received?.Invoke(message);

    public void Lose() => Lost?.Invoke();
}
