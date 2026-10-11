namespace Eve.Space;

/// <summary>
/// The answer to a command the caller waits for (a <c>Dock</c>, a warp): accepted (with the time it was), refused (with why), or, for a dock from
/// too far, not possible from here: the ship approaches the station and <paramref name="ApproachDistance"/> is how far off it was. A jump that
/// was accepted says where the ship is going (<paramref name="DestinationSystem"/>, <paramref name="DestinationGate"/>).
/// </summary>
public sealed record CommandReply(bool Accepted, long At, string? Reason, double? ApproachDistance = null, int? DestinationSystem = null, long DestinationGate = 0)
{
    /// <summary>The answer to a targeting order: what the ship has locked (or who has it locked) and whether a lock is going.</summary>
    public IReadOnlyList<long> Targets { get; init; } = [];

    public bool Pending { get; init; }

    /// <summary>What the ship has taken (the answer to <see cref="SimCommands.GetCondition"/>).</summary>
    public SimCondition? Condition { get; init; }

    /// <summary>What a refused targeting order says of the target: its type, and (out of range only) the range and the distance.</summary>
    public int TargetType { get; init; }

    public double DesiredRange { get; init; }

    public double ActualDistance { get; init; }

    /// <summary>Where a can that was placed lies and how it faces (the answer to a <see cref="SimPlaceContainer"/>).</summary>
    public IReadOnlyList<double> Position { get; init; } = [];

    public IReadOnlyList<double> Direction { get; init; } = [];

    /// <summary>What each drone did with an order to drones (<see cref="SimDroneResult.Reason"/> is empty for one that took it).</summary>
    public IReadOnlyList<SimDroneResult> Drones { get; init; } = [];

    /// <summary>What the scene holds that a probe scan can find (command 27, protocol 52).</summary>
    public IReadOnlyList<SimScanTarget> ScanTargets { get; init; } = [];

    /// <summary>What the scene has that a directional scan can see (command 28, protocol 54).</summary>
    public IReadOnlyList<SimDirectionalTarget> DirectionalTargets { get; init; } = [];

    /// <summary>What each fighter squadron did with an order to fighters (<see cref="SimFighterResult.Reason"/> is empty for one that took it).</summary>
    public IReadOnlyList<SimFighterResult> Fighters { get; init; } = [];

    /// <summary>How far a wreck or can is, measured to its surface (the answer to <see cref="SimCommands.Measure"/>; null: there is no such wreck).</summary>
    public double? Distance { get; init; }
}

/// <summary>The simulation cannot be reached (it is not running, or the link broke).</summary>
public sealed class SimUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What the service host asks of the space simulation and hears back from it.</summary>
public interface ISimulation
{
    /// <summary>Whether the simulation is reachable now.</summary>
    bool IsConnected { get; }

    /// <summary>Something the simulation says on its own: a notification for a client, a finished docking, a refused command.</summary>
    event Action<SimMessage>? Received;

    /// <summary>The link broke: every ship the simulation held is gone.</summary>
    event Action? Lost;

    /// <summary>Puts a ship in a solar system. Returns null when it was done and the reason when it was not; throws <see cref="SimUnavailableException"/> when the simulation cannot be reached.</summary>
    Task<string?> EnterAsync(SimEnter enter, CancellationToken cancellationToken = default);

    Task SetHeatProfileAsync(ulong sessionId, SimHeatProfile profile, CancellationToken cancellationToken = default);

    Task SetModuleOverheatAsync(ulong sessionId, long moduleId, bool active, CancellationToken cancellationToken = default);

    Task OfflineModuleAsync(ulong sessionId, long moduleId, CancellationToken cancellationToken = default);

    Task SetRackOverheatAsync(ulong sessionId, int rack, bool active, CancellationToken cancellationToken = default);

    void Leave(ulong sessionId, LeaveWhy why);

    void Bind(ulong sessionId);

    void UpdateState(ulong sessionId);

    /// <summary>A movement command (<see cref="SimCommands"/>); the simulation answers it with <see cref="SimAccepted"/> or <see cref="SimRefused"/> (<see cref="Received"/>).</summary>
    void Command(ulong sessionId, byte code, params double[] args);

    /// <summary>Gives a command that is answered to the caller (a dock, a warp) and waits for the simulation to accept or refuse it.</summary>
    Task<CommandReply> CommandAsync(ulong sessionId, byte code, double[] args, CancellationToken cancellationToken = default);

    /// <summary>Asks the simulation to run an effect; it answers <see cref="SimAccepted"/> or <see cref="SimRefused"/> under <see cref="SimCommands.Activate"/>.</summary>
    Task<CommandReply> ActivateAsync(ulong sessionId, SimEffect effect, CancellationToken cancellationToken = default);

    void ShipChanged(ulong sessionId, SimShip ship);

    /// <summary>Spawns an NPC in the scene of a system and waits for the answer (the session it is known by, or why it could not be done).</summary>
    Task<SimSpawnReply> SpawnAsync(SimSpawn spawn, CancellationToken cancellationToken = default);

    /// <summary>Launches a drone beside the ship of a pilot and waits for the answer (the session the drone is known by, or why it could not be done).</summary>
    Task<SimSpawnReply> LaunchDroneAsync(SimLaunchDrone launch, CancellationToken cancellationToken = default);

    /// <summary>Gives a pilot's drones an order (<see cref="SimDroneCommands"/>) and waits for what each did with it (<see cref="CommandReply.Drones"/>).</summary>
    Task<CommandReply> DroneCommandAsync(ulong sessionId, byte command, long targetId, IReadOnlyList<long> droneIds, double controlRange, CancellationToken cancellationToken = default);

    /// <summary>Launches a pilot's fighter squadron beside its ship (answered with <see cref="SimSpawnReply"/>: the session it is known by, or why not).</summary>
    Task<SimSpawnReply> LaunchFighterAsync(SimLaunchFighter launch, CancellationToken cancellationToken = default);

    /// <summary>Gives a pilot's fighter squadrons an order (<see cref="SimFighterOrders"/>) and waits for what each did with it (<see cref="CommandReply.Fighters"/>).</summary>
    Task<CommandReply> FighterOrderAsync(ulong sessionId, byte command, long targetId, IReadOnlyList<long> fighterIds, double range, CancellationToken cancellationToken = default);

    /// <summary>Sends a pilot's fighter squadrons to a point (GOTO_POINT) and waits for what each did with it (<see cref="CommandReply.Fighters"/>).</summary>
    Task<CommandReply> FighterGotoPointAsync(ulong sessionId, IReadOnlyList<long> fighterIds, IReadOnlyList<double> point, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives a session whose ship was destroyed another (the capsule the host made), at rest at the place; null when it was done, else why not.
    /// </summary>
    Task<string?> RespawnAsync(SimRespawn respawn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the rounds of a gun that runs and waits for what the simulation says they come to. <paramref name="inOrder"/> is called with the answer on the thread that reads the
    /// link, before anything the simulation told after it is handled: what it does is ordered with every <see cref="SimChargeSpent"/> the simulation told before the answer.
    /// </summary>
    Task<SimChargeAdjusted> AdjustChargeAsync(SimAdjustCharge request, Action<SimChargeAdjusted>? inOrder = null, CancellationToken cancellationToken = default);

    /// <summary>Refreshes a running effect (protocol 38): the simulation applies it at the start of the effect's next cycle. Nothing is answered.</summary>
    Task UpdateEffectAsync(SimUpdateEffect request, CancellationToken cancellationToken = default);

    /// <summary>Puts a wreck the host made in space (where the ship was destroyed), in the scene of its system if there is one.</summary>
    void PlaceWreck(SimPlaceWreck wreck);

    void PlaceAsteroid(SimPlaceAsteroid asteroid);

    void DroneWorkResult(SimDroneWorkResult result);

    /// <summary>Returns the host-calculated repair amount for one pending mutadaptive remote-repair cycle.</summary>
    void RemoteRepairCycleResult(SimRemoteRepairCycleResult result);

    /// <summary>The host's decision for a module work cycle (ADR 0013); the module is parked until it arrives.</summary>
    void ModuleWorkResult(SimModuleWorkResult result);

    /// <summary>The timed buffs of a ship of this session (a command burst's, protocol 43): the simulation tells the client what they are.</summary>
    void BuffState(ulong sessionId, long shipId, IReadOnlyList<SimBuffEntry> entries);

    /// <summary>Puts a can the host made ahead of a pilot's ship and waits for the answer: where it lies (<see cref="CommandReply.Position"/>), or why it could not be done.</summary>
    Task<CommandReply> PlaceContainerAsync(SimPlaceContainer can, CancellationToken cancellationToken = default);

    /// <summary>Tells the scene that what is in a wreck or a can changed (it has something to take, or nothing) and waits until whoever sees it has been told: accepted, or refused when there is no such wreck.</summary>
    Task<CommandReply> ChangeWreckAsync(ulong sessionId, long wreckId, bool isEmpty, CancellationToken cancellationToken = default);

    /// <summary>Takes a wreck or a can out of space, with the destruction effect <paramref name="effect"/> played first when it is not 0.</summary>
    void RemoveWreck(long wreckId, int effect);
    /// <summary>Puts a cynosural field back in space after a restart (what is left of its life is <see cref="SimDeployed.LifetimeMs"/>).</summary>
    void RestoreDeployed(SimDeployed deployed);
    /// <summary>Takes a cynosural field out of space (its record's time is up).</summary>
    void RemoveDeployed(long deployableId);
    /// <summary>Puts a scanner probe in space where it was launched (protocol 45).</summary>
    void PlaceProbe(SimPlaceProbe probe);
    /// <summary>Takes a scanner probe out of space (protocol 45).</summary>
    void RemoveProbe(long probeId);

    /// <summary>The host puts an Upwell structure in space (protocol 59).</summary>
    void PlaceStructure(SimPlaceStructure structure);

    void SetTetherPolicy(SimTetherPolicy policy);

    /// <summary>The host takes a structure out of space (protocol 59).</summary>
    void RemoveStructure(long structureId);

    /// <summary>The host changes a structure's slim state (protocol 59).</summary>
    void SetStructureState(SimSetStructureState state);
    /// <summary>Puts a mobile warp disruptor in space where its pilot dropped it, anchored or not (protocol 48).</summary>
    void PlaceDisruptor(SimPlaceDisruptor disruptor);
    /// <summary>Says a mobile warp disruptor is anchored or not (protocol 48).</summary>
    void SetDisruptorActive(long deployableId, bool active);
    /// <summary>Puts a CRAB the host keeps a record of in space (protocol 61).</summary>
    void PlaceAnalysisBeacon(SimPlaceAnalysisBeacon beacon);
    /// <summary>The host sets a CRAB's state (protocol 61).</summary>
    void SetAnalysisBeaconState(long deployableId, SimAnalysisBeaconState state);
    /// <summary>Puts a Mobile Cynosural Inhibitor in space where its pilot dropped it, anchored or not (protocol 50).</summary>
    void PlaceInhibitor(SimPlaceInhibitor inhibitor);
    /// <summary>Puts a Mobile Cynosural Beacon in space where its pilot dropped it, anchored or not (protocol 58).</summary>
    void PlaceCynoBeacon(SimPlaceCynoBeacon beacon);

    /// <summary>Destroys a ship: the explosion, the wreck (for <paramref name="credit"/> to loot), and (for a player's ship) a <see cref="SimDestroyed"/> that comes back through <see cref="Received"/>.</summary>
    void Destroy(long shipId, SimCredit credit);
}
