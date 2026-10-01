using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>The simulation as a test plays it: it records what the host asks and says what the test tells it to.</summary>
internal sealed class FakeSim : ISimulation
{
    public bool IsConnected { get; set; } = true;

    public event Action<SimMessage>? Received;

    public event Action? Lost;

    public List<SimMessage> Heard { get; } = [];

    /// <summary>What an <c>Enter</c> is answered with: null (done) or a reason.</summary>
    public string? EnterAnswer { get; set; }

    public CommandReply DockAnswer { get; set; } = new(true, 134351527136350000, null);

    /// <summary>What a warp is answered with.</summary>
    public CommandReply WarpAnswer { get; set; } = new(true, 134351527136350000, null);

    /// <summary>What a jump is answered with.</summary>
    public CommandReply JumpAnswer { get; set; } = new(true, 0, null, null, 30100034, 50016839);

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
        Heard.Add(spawn);
        return Task.FromResult(SpawnAnswer is { } answer ? answer with { RequestId = spawn.RequestId } : new SimSpawnReply(spawn.RequestId, (ulong)Heard.Count + (1UL << 62), null));
    }

    /// <summary>What a respawn is answered with: null (done) or a reason.</summary>
    public string? RespawnAnswer { get; set; }

    /// <summary>When set, a respawn is not answered until the test completes it.</summary>
    public TaskCompletionSource? RespawnGate { get; set; }

    public async Task<string?> RespawnAsync(SimRespawn respawn, CancellationToken cancellationToken = default)
    {
        Heard.Add(respawn);
        if (RespawnGate is { } gate)
        {
            await gate.Task;
        }
        return RespawnAnswer;
    }

    public void PlaceWreck(SimPlaceWreck wreck) => Heard.Add(wreck);

    /// <summary>What a can that is placed is answered with: where it lies (the test's own place when none is set), or why not.</summary>
    public CommandReply? PlaceContainerAnswer { get; set; }

    public Task<CommandReply> PlaceContainerAsync(SimPlaceContainer can, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        Heard.Add(can);
        return Task.FromResult(PlaceContainerAnswer ?? new CommandReply(true, 0, null) { Position = [1000.0, 2000.0, 3000.0], Direction = [1.0, 0.0, 0.0] });
    }

    /// <summary>What a change of the rounds of a gun is answered with (the default: the gun does not run, the host has its rounds).</summary>
    public Func<SimAdjustCharge, SimChargeAdjusted> AdjustAnswer { get; set; } = request => new SimChargeAdjusted(request.RequestId, false, 0, 0);

    /// <summary>What the simulation tells right after it has answered a change of rounds (a count of the gun, as it goes on shooting).</summary>
    public Action<SimAdjustCharge>? AfterAnswer { get; set; }

    public Task<SimChargeAdjusted> AdjustChargeAsync(SimAdjustCharge request, Action<SimChargeAdjusted>? inOrder = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        Heard.Add(request);
        var answer = AdjustAnswer(request) with { RequestId = request.RequestId };
        inOrder?.Invoke(answer);
        AfterAnswer?.Invoke(request);
        return Task.FromResult(answer);
    }

    public Task<CommandReply> ChangeWreckAsync(ulong sessionId, long wreckId, bool isEmpty, CancellationToken cancellationToken = default)
    {
        Heard.Add(new SimChangeWreck(sessionId, wreckId, isEmpty));
        return Task.FromResult(new CommandReply(true, 0, null));
    }

    public void RemoveWreck(long wreckId, int effect) => Heard.Add(new SimRemoveWreck(wreckId, effect));

    public void Destroy(long shipId, SimCredit credit) => Heard.Add(new SimDestroy(shipId, credit));

    public Task<string?> EnterAsync(SimEnter enter, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new SimUnavailableException("the simulation is not running");
        }
        Heard.Add(enter);
        return Task.FromResult(EnterAnswer);
    }

    /// <summary>What a targeting order is answered with.</summary>
    public CommandReply TargetAnswer { get; set; } = new(true, 0, null) { Pending = true };

    /// <summary>When set, a targeting order fails as it does when the link to the simulation breaks.</summary>
    public bool TargetsUnavailable { get; set; }

    /// <summary>When set, a targeting order is not answered until the test completes it.</summary>
    public TaskCompletionSource? TargetGate { get; set; }

    public Task<CommandReply> CommandAsync(ulong sessionId, byte code, double[] args, CancellationToken cancellationToken = default)
    {
        Heard.Add(new SimCommand(sessionId, code, args));
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
        return Task.FromResult(code switch { SimCommands.WarpTo => WarpAnswer, SimCommands.Jump => JumpAnswer, _ => DockAnswer });
    }

    /// <summary>How far a wreck or can is said to be (null: there is none).</summary>
    public double? MeasureAnswer { get; set; } = 100;

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
        Heard.Add(new SimActivate(sessionId, effect));
        return ActivateGate is { } gate ? gate.Task.ContinueWith(_ => ActivateAnswer, cancellationToken) : Task.FromResult(ActivateAnswer);
    }

    public void Leave(ulong sessionId, LeaveWhy why) => Heard.Add(new SimLeave(sessionId, why));

    public void Bind(ulong sessionId) => Heard.Add(new SimBind(sessionId));

    public void UpdateState(ulong sessionId) => Heard.Add(new SimUpdateState(sessionId));

    public void Command(ulong sessionId, byte code, params double[] args) => Heard.Add(new SimCommand(sessionId, code, args));

    public void ShipChanged(ulong sessionId, SimShip ship) => Heard.Add(new SimShipChanged(sessionId, ship));

    public void Say(SimMessage message) => Received?.Invoke(message);

    public void Lose() => Lost?.Invoke();
}
