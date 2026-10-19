using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Events;
using Eve.Inventory;
using Eve.Scheduler;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Macho;
using Eve.Marshal;
using Eve.Npc;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;
using Eve.Universe;
using ModuleAttributes = Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions : IDisposable, ISpaceTargeting, ISpaceModules, ISpaceHealth, ISpaceHulls, ISpaceNpcs, ISpaceDeath, ISpaceRounds, ISpaceProbes, Eve.Services.ICharacterEntryHook
{
    /// <summary>How long a ship that has undocked cannot be hurt (the reference's: 30 seconds).</summary>
    public const long UndockInvulnerabilityTicks = 300_000_000;
    private const string LoginInventoryBootstrapPending = "loginInventoryBootstrapPending";

    /// <summary>How long a ship that has come through a stargate is cloaked and cannot be hurt (the reference's: 60 seconds).</summary>
    public const long JumpCloakTicks = 600_000_000;

    /// <summary>How long after an undock or a jump the client is told the session change is over (the reference's: 10 seconds).</summary>
    private const long SessionChangeCooldownMs = 10_000;

    /// <summary>The queue of a system the client is told of when its pilot jumps there: how many entries it can hold and how long one is kept.</summary>
    private const int JumpQueueSpace = 1000;
    private const int JumpQueueTimeoutSeconds = 180;

    private sealed class Pilot(ClientSession session, long characterId, long shipId, int typeId, int solarSystemId, long invulnerableUntil, long undockedFrom = 0, bool jumpCloak = false)
    {
        public ClientSession Session { get; } = session;

        public long CharacterId { get; } = characterId;

        public long ShipId { get; } = shipId;

        public int TypeId { get; } = typeId;

        public int SolarSystemId { get; set; } = solarSystemId;


        /// <summary>The scene the simulator currently owns for this ship; unlike SolarSystemId it includes an Abyssal run and room.</summary>
        public SimSceneKey SimulationScene { get; set; } = SimSceneKey.SolarSystem(solarSystemId);

        /// <summary>The domain content id for an active Abyssal participant; null outside a run.</summary>
        public string? AbyssalContentId { get; set; }

        /// <summary>The exact most recent Abyssal scene move, retained for Host receipt retries.</summary>
        public AbyssalMoveReceipt? LastAbyssalMove { get; set; }

        public Task? StructureControlDeath { get; set; }

        public Func<Task>? RetryStructureControlDeath { get; set; }

        public IReadOnlyList<double>? StructureControlCapsulePosition { get; set; }

        public IReadOnlyList<double>? StructureControlCapsuleDirection { get; set; }

        /// <summary>A FILETIME: until when the ship cannot be hurt, unless a command of its pilot breaks it first.</summary>
        public long InvulnerableUntil { get; } = invulnerableUntil;

        /// <summary>The station the ship undocked from (the reference keeps saying so in the custom info of the ship's item).</summary>
        public long UndockedFrom { get; } = undockedFrom;

        /// <summary>Whether the protection is the cloak of a ship that has come through a stargate (its end is told with one more notification).</summary>
        public bool JumpCloak { get; } = jumpCloak;

        /// <summary>Marks the protection ended; false when it had ended already (each ending is told once).</summary>
        public bool ProtectionEnded;
        public bool CynoJumpPending;
        public bool CynoArrival;
        public long TetherDelayUntilMs;
        public long TetherStructureId;
        public long TetherPolicyExpiresMs;
        public long TetherPolicyBlockedUntilMs;
        public IReadOnlyList<SimTetherCandidate> TetherAllowedCandidates = [];
        public bool TetherRepairing;
        public bool BallparkBound;
        public TetherGeometry? TetherGeometry;
        public SemaphoreSlim TetherPolicyGate { get; } = new(1, 1);
        public object TetherSync { get; } = new();
        public Task TetherWork { get; set; } = Task.CompletedTask;

        private int _ended;

        /// <summary>Stops the timer that ends the protection when it runs out (set when the timer is; the pilot ends it by hand first when a command breaks the protection).</summary>
        public Action? CancelExpiry { get; set; }

        /// <summary>
        /// The modules of the ship that the simulation has accepted an activation for, and not yet a stop: their state is Active in dogma, so the
        /// numbers the ship is told (its resonances, the hardeners) are those of the effects that run. A new pilot (undock, eject, boarding) starts with none.
        /// </summary>
        public ConcurrentDictionary<long, ItemState> ActiveModules { get; } = new();

        /// <summary>The effects the pilot's modules run, as asked and as the simulation holds them (<see cref="RunningModule"/>): a refresh rebuilds them.</summary>
        public ConcurrentDictionary<long, RunningModule> RunningModules { get; } = new();

        /// <summary>The module repairs under way (nanite paste): what each began with. Ended by a stop, or by the pilot leaving the ship (<see cref="CancelRepairsAsync"/>).</summary>
        public ConcurrentDictionary<long, ModuleRepairRecord> Repairs { get; } = new();

        /// <summary>The modules whose overheat the pilot asked for (accepted by the simulation as the request, applied at its next cycle): dogma sees them Overloaded.</summary>
        public ConcurrentDictionary<long, byte> Overheated { get; } = new();

        /// <summary>Owner IDs for fitted modules whose charge counts the simulation may report after activation.</summary>
        public ConcurrentDictionary<long, long> ModuleChargeOwners { get; } = new();

        /// <summary>Module IDs activated on a controlled structure; cleared when structure control is released.</summary>
        public ConcurrentDictionary<long, byte> StructureModuleIds { get; } = new();

        public bool End()
        {
            CancelExpiry?.Invoke();
            return Interlocked.Exchange(ref _ended, 1) == 0;
        }
    }


    private readonly ISimulation _sim;
    private readonly CharacterShips _ships;
    private readonly PgSpaceItemStore? _spaceItems;
    public Func<long, CancellationToken, Task<bool>>? StructureExistsAsync { get; set; }

    /// <summary>The fleet a character is in (its id), or null when it is in none: a command burst reaches the ships of its source's fleet (track C6).</summary>
    public Func<long, CancellationToken, Task<long?>>? FleetIdOfAsync { get; set; }

    /// <summary>
    /// The accounts whose guns the host reloads from their holds when the rounds run out (FP-C8): a bot with no client UI, named by the operator (EVE_AUTO_RELOAD_ACCOUNTS).
    /// Any other pilot's gun stops when its rounds are spent, and the client reloads it (<c>shipmodulebutton.py</c>), so the host does not load it twice.
    /// </summary>
    public IReadOnlySet<long> AutoReloadUsers { get; set; } = new HashSet<long>();

    private bool AutoReloadsOf(Pilot pilot) => pilot.Session.UserId is { } user && AutoReloadUsers.Contains(user);

    /// <summary>The timed buffs of the ships in space (the command bursts', track C6). In memory: a restart drops them.</summary>
    public ShipBuffs Buffs { get; } = new();
    private readonly DogmaStatic _statics;
    private readonly StationCatalog _stations;
    private readonly SolarSystemCatalog _systems;
    private readonly Func<long> _nowUnixMs;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<ulong, Pilot> _pilots = new();
    private readonly ConcurrentDictionary<ulong, byte> _warping = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _targeting = new();
    private readonly ConcurrentDictionary<ulong, IReadOnlySet<long>> _lockedTargets = new();
    /// <summary>The ship ids whose destruction is being processed (each ship is handled once).</summary>
    private readonly ConcurrentDictionary<long, byte> _dying = new();
    private readonly ConcurrentDictionary<long, byte> _concordDestroyed = new();
    /// <summary>Who waits to hear what became of a ship they had destroyed (<c>/suicide</c>), by ship.</summary>
    private readonly ConcurrentDictionary<long, TaskCompletionSource<SelfDestruct?>> _deaths = new();
    private readonly TimeSpan _protection;
    private readonly TimeSpan _jumpCloak;
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<long, (long FileTime, long ExpiresAtMs)>> _queues = new();
    /// <summary>The rounds the guns of the pilots in space have spent, written in the order the simulation tells them, a lane for each session.</summary>
    private readonly ChargeSpentQueue _charges;

    /// <param name="protection">How long a ship that has just undocked is protected if its pilot does not fly it first (the client is told when it ends); <see cref="Timeout.InfiniteTimeSpan"/> for never.</param>
    /// <param name="jumpCloak">How long a ship that has come through a stargate stays cloaked if its pilot does not fly it first.</param>
    private readonly ConcurrentDictionary<ulong, byte> _safeLogoffs = new();

    /// <summary>Blockers owned by adjacent systems (crime timers, targeting, fleet, drone and movement state).</summary>
    public Func<ShipLifecycleAction, ClientSession, long?, CancellationToken, ValueTask<IReadOnlyList<string>>>? LifecycleBlockers { get; set; }

    /// <summary>Invalidates the former owner's insurance after an outsider successfully boards their ejected ship.</summary>
    public Func<ShipBoarded, CancellationToken, ValueTask>? InvalidateInsuranceForBoarding { get; set; }

    private sealed record EjectedShip(
        long OwnerCharacterId,
        long OwnerCorporationId,
        int ShipTypeId,
        int SolarSystemId,
        ulong? StructureControlSessionId = null,
        long OwnerAllianceId = 0,
        long OwnerWarFactionId = 0);
    private readonly ConcurrentDictionary<long, EjectedShip> _ejectedShips = new();

private readonly TransitionRuntime _transitions;

    public SpaceSessions(ISimulation sim, CharacterShips ships, DogmaStatic statics, StationCatalog stations, SolarSystemCatalog systems, Func<long> nowUnixMs, Action<string>? log = null, TimeSpan? protection = null, TimeSpan? jumpCloak = null, Eve.Scheduler.LocalTimers? localTimers = null, PgSpaceItemStore? spaceItems = null)
    {
        _timers = localTimers ?? new LocalTimers(log);
        _transitions = new TransitionRuntime(_timers);
        _ownsTimers = localTimers is null;
        _protection = protection ?? TimeSpan.FromTicks(UndockInvulnerabilityTicks);
        _jumpCloak = jumpCloak ?? TimeSpan.FromTicks(JumpCloakTicks);
        _sim = sim;
        _ships = ships;
        _spaceItems = spaceItems;
        _statics = statics;
        _stations = stations;
        _systems = systems;
        _nowUnixMs = nowUnixMs;
        _log = log;
        _charges = new ChargeSpentQueue((character, module, rounds) => (ChargeWriter ?? ((c, m, r) => _ships.SpendChargeAsync(c, m, r)))(character, module, rounds), log);
        _sim.Received += OnReceived;
        _sim.Lost += OnLost;
    }

    private long _adjustRequests;

    /// <summary>
    /// Changes the rounds of a gun of the pilot's ship (a load that ended, an unload) in the simulation and runs what follows on the pilot's lane of counts, where the answer came in:
    /// every count of rounds the simulation told before it is written first. A pilot who is not in space has no gun that runs.
    /// </summary>
    public async Task<T> AdjustAsync<T>(ClientSession session, long moduleId, int chargeType, RoundsChange change, Func<GunRounds, Task<T>> afterwards, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.ContainsKey(id))
        {
            await _charges.FlushAsync(id).ConfigureAwait(false);
            return await afterwards(GunRounds.Idle).ConfigureAwait(false);
        }
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new SimAdjustCharge(
            (ulong)Interlocked.Increment(ref _adjustRequests), id, moduleId, chargeType,
            change switch
            {
                RoundsChange.Add add => new SimChargeOp.Add(add.Capacity, add.Available),
                RoundsChange.Remove remove => new SimChargeOp.Remove(remove.Quantity),
                _ => throw new ArgumentException($"unknown change {change}"),
            });
        // the answer is handled where the simulation's link delivers it, in the order of the counts it told: what follows is queued on the lane from there
        await _sim.AdjustChargeAsync(request, answer => _charges.EnqueueWork(id, async () =>
        {
            try
            {
                done.SetResult(await afterwards(new GunRounds(answer.Running, answer.Before, answer.After)).ConfigureAwait(false));
            }
            catch (Exception error)
            {
                done.SetException(error);
            }
        }), cancellationToken).ConfigureAwait(false);
        return await done.Task.ConfigureAwait(false);
    }

    /// <summary>The inventory could not give a gun that runs all the rounds the simulation added to it: the simulation takes them back (nothing waits for it).</summary>
    public void GiveBack(ClientSession session, long moduleId, long rounds)
    {
        var request = new SimAdjustCharge((ulong)Interlocked.Increment(ref _adjustRequests), (ulong)session.SessionId, moduleId, 0, new SimChargeOp.Remove(rounds));
        Background(async () =>
        {
            try
            {
                await _sim.AdjustChargeAsync(request).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _log?.Invoke($"the {rounds} rounds that module {moduleId} of session {session.SessionId} was short of could not be taken back: {error.Message}");
            }
        });
    }

    /// <summary>Where the rounds a gun has spent are written (the ships' store; a test puts its own).</summary>
    internal Func<long, long, long, Task>? ChargeWriter { get; set; }

    internal void EnqueueForTest(ulong session, long characterId, long moduleId, long roundsLeft) => _charges.Enqueue(session, characterId, moduleId, roundsLeft);

    /// <summary>Marks a module of a pilot as running, as an activation the simulation accepted does (the work-cycle host tests need a running module).</summary>
    internal void StartModuleForTest(ulong session, long moduleId) => _pilots[session].ActiveModules[moduleId] = ItemState.Active;

internal void StartStructureDamageModuleForTest(ulong session, long moduleId, string effectName, SimEffect effect)
    {
        var pilot = _pilots[session];
        pilot.ActiveModules[moduleId] = ItemState.Active;
        pilot.RunningModules[moduleId] = RunningModule.Of(effectName, 0, Math.Max(1, effect.Repeat), null, null, effect);
    }

    /// <summary>A ship has come into the scene of a solar system (an undock, a jump): what lies there that the simulation does not have yet can be put in it.</summary>
    public event Action<int>? SystemEntered;

    public Action<ClientSession, long, long>? DockedLocationChanged { get; set; }

    public Func<long, long, int, CancellationToken, Task<IReadOnlyList<int>>>? InspectUndockContrabandAsync { get; set; }

    /// <summary>The contraband penalty of an undock or a stargate jump (ST-16 C): the fine charged and the contraband removed; null when there is none.</summary>
    public Func<long, long, int, CancellationToken, Task<ContrabandPenalty?>>? ApplyContrabandPenaltyAsync { get; set; }

    /// <summary>The host put the wreck of a destroyed ship (or the corpse of a pilot) in space.</summary>
    public event Action<SimPlaceWreck>? WreckPlaced;

    // NPC death finishers run on separate tasks; keep synchronous event subscribers serialized.
    private readonly object _npcDestroyedGate = new();

    /// <summary>
    /// An NPC the host spawned was destroyed. Raised on a thread of the host after its wreck and bounty were done, so a handler should be quick (or hand its work on) and must not
    /// throw: what it throws is logged and nothing else.
    /// </summary>
    public event Action<NpcKill>? NpcDestroyed;

    /// <summary>
    /// A pilot's ship (not its capsule) was destroyed in space, raised once the database has the loss (what the ship held is in its wreck or gone) and before the client is told, on a
    /// thread of the host: a handler should be quick (or hand its work on) and must not throw: what it throws is logged and nothing else.
    /// </summary>
    public event Action<ShipLoss>? ShipLost;

    /// <summary>
    /// A pilot's capsule was destroyed in space (a loss of its own, not the ship's), raised once the database has the pod's replacement and before the client is
    /// told, on a thread of the host, with the same rules as <see cref="ShipLost"/>.
    /// </summary>
    public event Action<PodLoss>? PodLost;

    /// <summary>
    /// The domain events bus (set by the host that has one). What happens in space is announced on it as well (<c>npc.killed</c>, <c>ship.lost</c>, <c>bounty.paid</c>) so that
    /// missions, crimewatch and the rest subscribe instead of hooking this class; the C# events above stay for the code that needs a synchronous call.
    /// </summary>
    public EventBus? Events
    {
        get => _transitions.Events;
        set => _transitions.Events = value;
    }


    public TransitionRuntime Transitions => _transitions;

    /// <summary>Publishes an event on the bus without waiting for it; a failure is logged (the thing it announces has happened already).</summary>
    private void Announce(string topic, object payload, string key)
    {
        if (Events is not { } bus)
        {
            return;
        }
        Background(async () =>
        {
            try
            {
                await bus.PublishAsync(topic, payload, key);
            }
            catch (Exception error)
            {
                _log?.Invoke($"the event {topic} could not be published: {error.Message}");
            }
        });
    }

    private void Raise<T>(Action<T>? handlers, T value, string what)
    {
        foreach (var handler in handlers?.GetInvocationList().Cast<Action<T>>() ?? [])
        {
            try
            {
                handler(value);
            }
            catch (Exception error)
            {
                _log?.Invoke($"a handler of {what} failed: {error.Message}");
            }
        }
    }

    private void PlaceWreck(SimPlaceWreck wreck)
    {
        _sim.PlaceWreck(wreck);
        WreckPlaced?.Invoke(wreck);
    }

    /// <summary>The service registry the bound objects of a session are released from when it leaves a place (set once the registry exists).</summary>
    public ServiceRegistry? Registry { get; set; }

    /// <summary>
    /// The work the space sessions run off the simulation's receive loop (a death, a dock, a jump, a command's answer): it is started as <see cref="Background"/> and kept
    /// until it is done, so <see cref="QuiesceAsync"/> can wait for it and a shutdown does not leave it running after the teardown.
    /// </summary>
    private readonly ConcurrentDictionary<Task, string> _background = new();

    /// <summary>Starts <paramref name="work"/> off the caller; the call site (its line) names it when a wait for it fails.</summary>
    internal void Background(Func<Task> work, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _log?.Invoke($"background work of the space sessions (line {line}) failed: {error.Message}");
            }
        });
        _background[task] = $"line {line}";
        _ = task.ContinueWith(finished => _background.TryRemove(finished, out _), TaskScheduler.Default);
    }

    /// <summary>
    /// Waits until the background work started so far, and what it starts in turn, is done. Bounded: work still running after <paramref name="timeout"/> fails the
    /// wait with the call sites of that work (the production path stays asynchronous; this is for the tests and the shutdown).
    /// </summary>
    public async Task QuiesceAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var pending = _background.Where(entry => !entry.Key.IsCompleted).ToArray();
            if (pending.Length == 0)
            {
                return;
            }
            var remaining = deadline - DateTime.UtcNow;
            var all = Task.WhenAll(pending.Select(entry => entry.Key));
            if (remaining <= TimeSpan.Zero || await Task.WhenAny(all, Task.Delay(remaining)) != all)
            {
                throw new TimeoutException($"the background work of the space sessions did not finish: {string.Join(", ", pending.Select(entry => entry.Value))}");
            }
        }
    }

    public void Dispose()
{
    StopTetherRefresh();
    if (_ownsTimers) _timers.Dispose();
    _sim.Received -= OnReceived;
    _sim.Lost -= OnLost;
    ForgetMutadaptiveSpools();
    // the host stops: the work the sessions started is done first (nothing runs after the teardown), then the rounds that were spent are written
    try
    {
        QuiesceAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }
    catch (TimeoutException error)
    {
        _log?.Invoke(error.Message);
    }
    _charges.CloseAllAsync().GetAwaiter().GetResult();
}

    private static PyValue None => PyNone.Instance;

    private long FileTimeNow() => Py.FileTimeFromUnixMs(_nowUnixMs());

    /// <summary>Whether the session's ship is in space.</summary>
    public bool IsInSpace(ClientSession session) => _pilots.ContainsKey((ulong)session.SessionId);

    /// <summary>The solar system the session's ship is in, or null when it is not in space.</summary>
    public int? SolarSystemOf(ClientSession session) => _pilots.TryGetValue((ulong)session.SessionId, out var pilot) ? pilot.SolarSystemId : null;


    public long? ShipIdOf(ClientSession session) =>
        _pilots.TryGetValue((ulong)session.SessionId, out var pilot) ? pilot.ShipId : null;

    /// <summary>The type of the ship the session flies in space, or null when it is not in space.</summary>
    public int? ShipTypeIdOf(ClientSession session) =>
        _pilots.TryGetValue((ulong)session.SessionId, out var pilot) ? pilot.TypeId : null;

    public bool IsWarping(ClientSession session) => _warping.ContainsKey((ulong)session.SessionId);




    public ClientSession? ClientSessionOf(ulong sessionId) =>
        _pilots.TryGetValue(sessionId, out var pilot) ? pilot.Session : null;

    /// <summary>The scanner probes of the ships in space (set by the composition root). A launch or a recall is refused as not wired while there are none.</summary>
    public SpaceProbes? Probes { get; set; }

    /// <summary>The unanchoring of a player control tower by its corporation (posMgr.UnanchorStructure); the host's space composition sets it.</summary>
    public Func<ClientSession, long, CancellationToken, Task>? ControlTowerUnanchor { get; set; }

    /// <summary>The probe scan of the probes in space (set by the composition root): the scanMgr requests answer through it.</summary>
    public SpaceProbeScans? ProbeScans { get; set; }

    /// <summary>The places a pilot may warp to on a scan result (the 100 percent results of the last completed scan).</summary>
    public Scanning.ProbeScanWarpAuthority WarpAuthority { get; } = new();

    /// <summary>The directional scan (set by the composition root): the scanMgr's ConeScan answers through it.</summary>
    public Scanning.SpaceDirectionalScans? DirectionalScans { get; set; }

    /// <summary>The stargate rules of the client data and the SDE (set by the composition root): the ship lists of the gates, the tolls and the destinations.</summary>
    public Gates.GateRules? GateRules { get; set; }

    /// <summary>Charges a gate toll to a character's wallet (set by the composition root): false when the wallet cannot pay.</summary>
    public Func<long, decimal, string, CancellationToken, Task<bool>>? ChargeGateToll { get; set; }

    /// <summary>Refunds a gate toll whose jump the simulation did not take (set by the composition root).</summary>
    public Func<long, decimal, string, CancellationToken, Task>? RefundGateToll { get; set; }

    /// <summary>The emanation locks of the restricted systems (Zarzakh), in memory.</summary>
    private readonly Gates.EmanationLocks _emanationLocks = new();

    /// <summary>Whether the session's system allows scanning (the client's disallowScanning flag is not set; no system is false for nothing).</summary>
    public bool ScanningAllowedFor(ClientSession session) =>
        SolarSystemOf(session) is not int system || _systems.Get(system) is not { DisallowScanning: true };

    public Task LaunchAsync(ClientSession session, long moduleId, int count, CancellationToken cancellationToken)
    {
        // the client's disallowScanning flag: its probe window closes in such a system and it names no refusal, so the wording is ours
        if (!ScanningAllowedFor(session))
        {
            throw Notify("Scanner probes cannot be launched in this system.");
        }
        return (Probes ?? throw Notify("Scanner probes are not wired on this server.")).LaunchAsync(session, moduleId, count, cancellationToken);
    }

    public Task RecallAsync(ClientSession session, long probeId, CancellationToken cancellationToken) =>
        (Probes ?? throw Notify("Scanner probes are not wired on this server.")).RecallAsync(session, probeId, cancellationToken);

    /// <summary>The session of the pilot whose ship is <paramref name="shipId"/> and is in space, or null (a ship a scanner reads the capacitor of).</summary>
    public ClientSession? SessionOfShip(long shipId) =>
        _pilots.Values.FirstOrDefault(pilot => pilot.ShipId == shipId)?.Session;

    public double? SecurityOfSystem(int solarSystemId) => _systems.Get(solarSystemId)?.Security;

    /// <summary>The region of a solar system (for the Pochven and wormhole rules), or null when the system is not known.</summary>
    public int? RegionOfSystem(int solarSystemId) => _systems.Get(solarSystemId)?.RegionId;

    private static MachoWrappedException Notify(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));


    private static MachoWrappedException MovementRefused(PolicyDecision decision, string action) =>
        Notify(decision.BlockReason switch
        {
            PolicyBlockReason.WeaponsTimer => $"You cannot {action} while your weapons timer is active.",
            PolicyBlockReason.CriminalTimer => $"You cannot {action} while your criminal timer is active in high-security space.",
            PolicyBlockReason.SessionChangeTimer => $"You cannot {action} while your session change timer is active.",
            _ => $"You cannot {action} at this time.",
        });

    // ── character entry ─────────────────────────────────────────────────────

    /// <summary>Builds the selected character's session location and ship state, including any deferred restore.</summary>
    public async ValueTask<IReadOnlyList<(string Name, PyValue Value)>> GetSessionAttributesAsync(
        ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        if (await _ships.LoadAsync(characterId, cancellationToken) is not { } snapshot)
            return [];

        var character = snapshot.Character;
        var ship = snapshot.Ship;
        var type = _statics.Type(ship.TypeId);
        List<(string Name, PyValue Value)> attributes =
        [
            ("shipTypeID", new PyInt(ship.TypeId)),
            ("shipName", new PyStr(ship.CustomName ?? type?.Name ?? "Ship")),
        ];

        if (character.ShipId == ship.Id &&
            ship.LocationId == character.SolarSystemId &&
            ship.Flag == CharacterShips.InSpaceFlag)
        {
            if (FindAbyssalReconnectAsync is { } find &&
                ship.OwnerId == characterId && ship.Singleton && ship.Quantity == 1 &&
                await find(session, characterId, ship.Id, cancellationToken).ConfigureAwait(false) is { } reconnect)
            {
                if (!reconnect.Scene.IsValid || reconnect.Scene.InstanceId <= 0)
                    throw new InvalidDataException("The Abyssal reconnect returned an invalid scene key.");
                var system = _systems.Get(reconnect.Scene.LocationId)
                    ?? throw new InvalidDataException($"Abyssal reconnect system {reconnect.Scene.LocationId} is absent from the location catalog.");
                attributes.AddRange([
                    ("stationid", None),
                    ("stationid2", None),
                    ("structureid", None),
                ]);
                attributes.AddRange(AbyssalSessionLocationAttributes(reconnect.Scene, system));
                return attributes;
            }

            var live = _pilots.TryGetValue((ulong)session.SessionId, out var pilot) &&
                pilot.CharacterId == characterId && pilot.ShipId == ship.Id &&
                pilot.SolarSystemId == character.SolarSystemId;
            var saved = live || _spaceItems is null
                ? null
                : await _spaceItems.GetAsync(ship.Id, cancellationToken);
            if (live || saved is not null && HasValidSpaceRestore(snapshot, saved))
            {
                if (!_sim.IsConnected)
                    throw Notify("Space is not available right now. Try again in a moment.");
                attributes.AddRange([
                    ("stationid", None),
                    ("stationid2", None),
                    ("structureid", None),
                    ("solarsystemid", new PyInt(character.SolarSystemId)),
                    ("solarsystemid2", new PyInt(character.SolarSystemId)),
                    ("constellationid", new PyInt(character.ConstellationId)),
                    ("regionid", new PyInt(character.RegionId)),
                    ("locationid", new PyInt(character.SolarSystemId)),
                    ("worldspaceid", None),
                ]);
                return attributes;
            }

            if (_spaceItems is not null && saved is not null)
                await _spaceItems.RemoveAsync(ship.Id, cancellationToken);
            if (!await _ships.RecoverFromSpaceAsync(characterId, PlaceOfStation, cancellationToken))
                throw Notify("Space is not available right now. Try again in a moment.");
            if (await _ships.LoadAsync(characterId, cancellationToken) is not { } recovered)
                return attributes;
            snapshot = recovered;
            character = snapshot.Character;
            ship = snapshot.Ship;
            type = _statics.Type(ship.TypeId);
            attributes[0] = ("shipTypeID", new PyInt(ship.TypeId));
            attributes[1] = ("shipName", new PyStr(ship.CustomName ?? type?.Name ?? "Ship"));
        }

        if (_stations.Get(character.StationId) is not null)
        {
            attributes.AddRange([
                ("stationid", new PyInt(character.StationId)),
                ("stationid2", new PyInt(character.StationId)),
                ("structureid", None),
                ("solarsystemid", None),
                ("solarsystemid2", new PyInt(character.SolarSystemId)),
                ("constellationid", new PyInt(character.ConstellationId)),
                ("regionid", new PyInt(character.RegionId)),
                ("locationid", new PyInt(character.StationId)),
                ("worldspaceid", None),
            ]);
        }
        else if (StructureExistsAsync is not null &&
                 await StructureExistsAsync(character.StationId, cancellationToken))
        {
            attributes.AddRange([
                ("stationid", None),
                ("stationid2", None),
                ("structureid", new PyLong(character.StationId)),
                ("solarsystemid", new PyInt(character.SolarSystemId)),
                ("solarsystemid2", new PyInt(character.SolarSystemId)),
                ("constellationid", new PyInt(character.ConstellationId)),
                ("regionid", new PyInt(character.RegionId)),
                ("locationid", new PyInt(character.SolarSystemId)),
                ("worldspaceid", None),
            ]);
        }
        return attributes;
    }

    public async ValueTask OnCharacterLeavingAsync(
        ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        session.Set(LoginInventoryBootstrapPending, None);
        await OnSessionClosedAsync(session);
    }

    /// <summary>What a pilot that enters space does once it is in the scene (the squadrons its character has out there are taken up again).</summary>
    public Func<ClientSession, long, Task>? PilotEntered { get; set; }

    public async ValueTask OnCharacterEnteredAsync(
        ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        var sessionId = (ulong)session.SessionId;
        if (_pilots.TryGetValue(sessionId, out var current) && current.CharacterId == characterId)
            return;
        var isSpaceSession = IsSpaceSession(session);
        // Character selection may carry only solarsystemid2 for a persisted private run.
        // Resolve its authoritative run binding before requiring the ordinary-space session shape.
        var canRestoreAbyssal = FindAbyssalReconnectAsync is not null &&
            session.GetInt("stationid") is null && session.GetInt("structureid") is null &&
            session.GetInt("worldspaceid") is null && session.GetInt("solarsystemid2") is > 0 &&
            session.GetInt("locationid") == session.GetInt("solarsystemid2");
        if (!isSpaceSession && !canRestoreAbyssal) return;

        if (await _ships.LoadAsync(characterId, cancellationToken) is not { } snapshot)
        {
            _log?.Invoke($"space login for character {characterId} has no persisted ship restore state");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        if (await ReconnectAbyssalBeforeNormalEntryAsync(session, characterId, snapshot, cancellationToken).ConfigureAwait(false))
            return;
        if (!isSpaceSession) return;

        // Preflight can be abandoned or invalidated by leaving the previous character; reload the durable restore at entry.
        var spaceItems = _spaceItems;
        if (spaceItems is null)
        {
            _log?.Invoke($"space login for character {characterId} has no persisted ship restore state");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        var state = await spaceItems.GetAsync(snapshot.Ship.Id, cancellationToken);
        if (state is null || !HasValidSpaceRestore(snapshot, state))
        {
            _log?.Invoke($"space login for character {characterId} has no valid persisted pose");
            throw Notify("Space is not available right now. Try again in a moment.");
        }

        var systemId = state.SolarSystemId;
        await SyncBuffsAsync(snapshot.World, snapshot.Ship.Id, cancellationToken);
        var ship = SimShips.Of(
            snapshot,
            _statics,
            await SecurityStatusAsync(characterId, cancellationToken));
        var refusal = await _sim.EnterAsync(
            new SimEnter(
                sessionId,
                  SimSceneKey.SolarSystem(systemId),
                EnterHow.InSpace,
                0,
                ship,
                state.Position,
                state.Direction),
            cancellationToken);
        if (refusal is not null)
        {
            _log?.Invoke($"space login for character {characterId} was refused: {refusal}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        TellEnteredBuffs(sessionId, snapshot.Ship.Id, _nowUnixMs());

        try
        {
            await _sim.SetHeatProfileAsync(
                sessionId,
                SimShips.HeatProfileOf(snapshot, _statics),
                cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"heat profile restore for character {characterId}: {error.Message}");
        }

        _pilots[sessionId] = new Pilot(
            session, characterId, snapshot.Ship.Id, snapshot.Ship.TypeId,
            systemId, FileTimeNow(), snapshot.Character.StationId);
        _log?.Invoke($"pilot {characterId} entered space in system {systemId} (session {sessionId})");
        await RefreshTetherPolicyAsync(session, cancellationToken);
        if (PilotEntered is { } entered)
        {
            try
            {
                await entered(session, characterId);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log?.Invoke($"space login for character {characterId}: the squadrons in space could not be taken up: {error.Message}");
            }
        }
        session.Set(LoginInventoryBootstrapPending, new PyInt(1));
        session.Closed += () => OnSessionClosed(session);
        await spaceItems.RemoveAsync(snapshot.Ship.Id, cancellationToken);
        SystemEntered?.Invoke(systemId);
    }

    private bool HasValidSpaceRestore(ShipSnapshot snapshot, SpaceItem state)
    {
        var character = snapshot.Character;
        var ship = snapshot.Ship;
        return character.ShipId == ship.Id &&
               ship.LocationId == character.SolarSystemId &&
               ship.Flag == CharacterShips.InSpaceFlag &&
               state.SolarSystemId == character.SolarSystemId &&
               state.ExpiresAtMs > _nowUnixMs() &&
               state.Position.Count >= 3 &&
               state.Direction.Count >= 3;
    }

    private static bool IsSpaceSession(ClientSession session) =>
        session.GetInt("stationid") is null &&
        session.GetInt("structureid") is null &&
        session.GetInt("worldspaceid") is null &&
        session.GetInt("solarsystemid") is { } systemId &&
        session.GetInt("locationid") == systemId;

    public async ValueTask OnInventoryRpcAsync(
        ClientSession session,
        string method,
        bool stationHangar,
        bool activeSpaceShipInventoryList,
        CancellationToken cancellationToken)
    {
        if (method != "List" || !activeSpaceShipInventoryList ||
            session.GetInt(LoginInventoryBootstrapPending) != 1)
            return;

        session.Set(LoginInventoryBootstrapPending, None);
        if (!_sim.IsConnected)
            return;
        try
        {
            var prime = await _sim.CommandAsync(
                (ulong)session.SessionId,
                SimCommands.PrimeLoginBallparkVisuals,
                [],
                cancellationToken);
            if (!prime.Accepted)
                _log?.Invoke($"ballpark visuals for space login session {session.SessionId} were not primed: {prime.Reason}");
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"ballpark visuals for space login session {session.SessionId} could not be primed: {error.Message}");
        }
    }

    // ── undocking ────────────────────────────────────────────────────────────

    /// <summary>
    /// Tells the client who is queued to jump into a system: the pilots that jumped there in the last three minutes, this one included. The
    /// queue is the host's, not the simulation's (it is made of characters).
    /// </summary>
    private void SendJumpQueueUpdate(ClientSession session, long characterId, int destination)
    {
        var nowMs = _nowUnixMs();
        var queue = _queues.GetOrAdd(destination, _ => new());
        foreach (var (queued, entry) in queue.ToArray())
        {
            if (entry.ExpiresAtMs <= nowMs)
            {
                queue.TryRemove(queued, out _);
            }
        }
        queue[characterId] = (FileTimeNow(), nowMs + JumpQueueTimeoutSeconds * 1000L);
        var entries = queue.ToArray().OrderBy(e => e.Value.FileTime).ThenBy(e => e.Key).ToArray();
        session.SendNotification(
            "OnJumpQueueUpdate", "clientID",
            new PyInt(destination),
            new PyInt(JumpQueueSpace),
            Py.Tuple([.. entries.Select(e => InventoryWire.Id(e.Key))]),
            new PyDict([.. entries.Select(e => new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(e.Key), new PyLong(e.Value.FileTime)))]),
            new PyInt(JumpQueueTimeoutSeconds));
    }

    /// <summary>The message the client shows when it gives an order to a ship that is in warp.</summary>
    private static void TellShipInWarp(ClientSession session) => session.SendNotification("OnRemoteMessage", "clientID", new PyStr("ShipInWarp"), None);

    // ── what the simulation says ─────────────────────────────────────────────

    private void ForgetLockedTarget(long shipId)
    {
        foreach (var pair in _lockedTargets.ToArray())
        {
            if (!pair.Value.Contains(shipId))
                continue;

            var remaining = pair.Value.Where(target => target != shipId).ToHashSet();
            if (remaining.Count == 0)
            {
                ((ICollection<KeyValuePair<ulong, IReadOnlySet<long>>>)_lockedTargets).Remove(pair);
            }
            else
            {
                _lockedTargets.TryUpdate(pair.Key, remaining, pair.Value);
            }
        }
    }

    private void OnReceived(SimMessage message)
    {
        if (message is SimDestroyed removedEntity)
            RemoveSimDamageableEntity(removedEntity.ShipId);

        try
        {
            switch (message)
            {
                case SimHelloAck:
                    Background(RestoreAbyssalTracesAsync);
                    break;
                case SimDamageableEntityUpserted upserted:
                    UpsertSimDamageableEntity(upserted);
                    break;
                case SimDamageableEntityRemoved removed:
                    RemoveSimDamageableEntity(removed.SolarSystemId, removed.EntityId);
                    break;
                case SimStructureDamageCandidatesRequest candidates:
                    Background(() => ReplyToStructureDamageCandidatesAsync(candidates));
                    break;
                case SimTetherChanged tether:
                    HandleTetherChanged(tether);
                    break;
                case SimRemoteRepairCycle cycle:
                    HandleMutadaptiveRepairCycle(cycle);
                    break;
                case SimNotify notify when notify.Name == "OnDbuffUpdated":
                    if (_structureObservers.TryGetValue(notify.SessionId, out var observer))
                    {
                        _log?.Invoke($"simulation sent OnDbuffUpdated to structure observer session {notify.SessionId}");
                        Forward(observer, notify);
                    }
                    else if (_pilots.TryGetValue(notify.SessionId, out var pilot))
                    {
                        _log?.Invoke($"simulation sent OnDbuffUpdated to session {notify.SessionId} for ship {pilot.ShipId}");
                        Forward(pilot.Session, notify);
                    }
                    else
                    {
                        _log?.Invoke($"simulation sent OnDbuffUpdated for untracked session {notify.SessionId}");
                    }
                    break;
                case SimNotify notify when _structureObservers.TryGetValue(notify.SessionId, out var structureObserver):
                    Forward(structureObserver, notify);
                    break;
                case SimNotify notify when _pilots.TryGetValue(notify.SessionId, out var pilot):
                    Forward(pilot.Session, notify);
                    break;
                case SimAccepted accepted when !_structureObservers.ContainsKey(accepted.SessionId) && accepted.Command != SimCommands.ControlStructure && _pilots.TryGetValue(accepted.SessionId, out var mover):
                    if (accepted.Command == SimCommands.Stop)
                        _warping.TryRemove(accepted.SessionId, out _);
                    if (accepted.Command != SimCommands.Dock)
                        EndInvulnerability(mover);
                    // a warp closes the hacking window of the pilot (EveJS hackingMgrService: the window closes when the ship enters warp)
                    if (accepted.Command == SimCommands.WarpTo && HackingService is { } hackingWindow)
                        Background(() => hackingWindow.CloseOnWarpAsync(mover.Session, mover.CharacterId, CancellationToken.None).AsTask());
                    break;
                case SimArrived arrived when _pilots.ContainsKey(arrived.SessionId):
                    _warping.TryRemove(arrived.SessionId, out _);
                    Background(async () =>
                    {
                        try
                        {
                            await BeltArrivalAsync(arrived.SessionId, arrived.BeltId, arrived.Position);
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            _log?.Invoke($"the arrival of session {arrived.SessionId} at belt {arrived.BeltId} could not be dealt with: {error.Message}");
                        }
                    });
                    break;
                case SimDocked docked when _pilots.TryGetValue(docked.SessionId, out var pilot):
                    _warping.TryRemove(docked.SessionId, out _);
                    EndTetherPilot(pilot);
                    Background(() => FinishDockingAsync(pilot, docked.StationId, docked.Condition));
                    break;
                case SimJumped jumped when _pilots.TryGetValue(jumped.SessionId, out var jumper):
                    _warping.TryRemove(jumped.SessionId, out _);
                    EndTetherPilot(jumper);
                    Background(() => FinishJumpAsync(jumper, jumped));
                    break;
                case SimAbyssalFieldMembership membership:
                    Background(() => ApplyAbyssalFieldMembershipAsync(membership));
                    break;
                case SimDestroyed destroyed when _abyssalRewardCaches.TryGetValue(destroyed.ShipId, out var cache):
                    Background(() => FinishAbyssalRewardCacheDestroyedAsync(cache, destroyed));
                    break;
                case SimDestroyed destroyed when _pilots.TryGetValue(destroyed.SessionId, out var victim) && victim.ShipId == destroyed.ShipId:
                    _warping.TryRemove(destroyed.SessionId, out _);
                    EndTetherPilot(victim);
                    ForgetLockedTarget(destroyed.ShipId);
                    _lockedTargets.TryRemove(destroyed.SessionId, out _);
                    if (victim.SimulationScene.InstanceId > 0 && victim.AbyssalContentId is not null)
                        Background(() => FinishAbyssalParticipantDeathAsync(victim, null, destroyed));
                    else
                        Background(() => FinishDeathAsync(victim, destroyed));
                    break;
                case SimDestroyed destroyed
                    when _abyssalOfflineResidents.TryGetValue(destroyed.ShipId, out var offline)
                        && offline.SessionId == destroyed.SessionId
                        && offline.ContentId is not null:
                    _abyssalOfflineResidents.TryRemove(destroyed.ShipId, out _);
                    ForgetLockedTarget(destroyed.ShipId);
                    Background(() => FinishAbyssalParticipantDeathAsync(null, offline, destroyed));
                    break;
                case SimDestroyed destroyed when _ejectedShips.TryRemove(destroyed.ShipId, out var abandoned):
                {
                    ForgetLockedTarget(destroyed.ShipId);
                    TaskCompletionSource? completion = null;
                    if (abandoned.StructureControlSessionId is { } controllerSessionId
                        && _pilots.TryGetValue(controllerSessionId, out var controller))
                    {
                        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        controller.StructureControlDeath = completion.Task;
                        controller.RetryStructureControlDeath = () => FinishAbandonedShipDeathAsync(abandoned, destroyed);
                        controller.StructureControlCapsulePosition = destroyed.Position;
                        controller.StructureControlCapsuleDirection = destroyed.Direction;
                    }
                    Background(async () =>
                    {
                        try
                        {
                            await FinishAbandonedShipDeathAsync(abandoned, destroyed);
                        }
                        finally
                        {
                            completion?.TrySetResult();
                        }
                    });
                    break;
                }
                case SimDestroyed destroyed when _npcShips.TryRemove(destroyed.ShipId, out var npc):
                    ForgetLockedTarget(destroyed.ShipId);
                    Background(() => FinishNpcDeathAsync(npc, destroyed));
                    break;
                // an NPC the simulation keeps (a Pochven sentry or fighter): no profile here, so its type's bounty in the static data pays the last blow
                case SimDestroyed destroyed when destroyed.SessionId >= SimNpcSessionBase:
                    ForgetLockedTarget(destroyed.ShipId);
                    Background(() => PaySimNpcBountyAsync(destroyed, _pilots.Values.FirstOrDefault(pilot => pilot.ShipId == destroyed.KillerShipId)));
                    break;
                case SimChargeSpent spent when _pilots.TryGetValue(spent.SessionId, out var shooter):
                    var chargeOwnerId = shooter.ModuleChargeOwners.TryGetValue(spent.ModuleId, out var moduleOwner) ? moduleOwner : shooter.CharacterId;
                    _charges.Enqueue(spent.SessionId, chargeOwnerId, spent.ModuleId, spent.RoundsLeft);
                    // Structure charges are persisted by owner, but the character's reload UI is for personal ships only.
                    if (!IsStructureObserver(shooter.Session)
                        && shooter.RunningModules.TryGetValue(spent.ModuleId, out var gun) && gun.Reloads && spent.RoundsLeft < gun.PerCycle)
                    {
                        GunRanDry?.Invoke(new SpentGun(shooter.Session, shooter.CharacterId, shooter.ShipId, spent.ModuleId, gun.ChargeTypeId));
                    }
                    break;
                // an ECM burst reached ships: the attack is recorded on each pilot it reached, as for a module started at their ship (FP-C7)
                case SimEcmBurst burst when _pilots.TryGetValue(burst.SessionId, out var burster) && burster.ShipId == burst.ShipId:
                    Background(() => RecordEcmBurstAsync(burster, burst.Reached));
                    break;
                // an effect the simulation ended on its own account (the cycles done, the capacitor short, the target gone): the module is taken out of the
                // numbers the ship is sent with, and the ship is sent again so the simulation drops the module's modifiers (FP-C1)
                case SimModuleEnded ended when _pilots.TryGetValue(ended.SessionId, out var endedPilot) && endedPilot.ActiveModules.TryRemove(ended.ModuleId, out _):
                    Eve.Services.Space.Effects.ProjectedEffect.MutadaptiveSpool.Reset(ended.ModuleId);
                    endedPilot.RunningModules.TryRemove(ended.ModuleId, out _);
                    Background(() => SendShipAsync(ended.SessionId, endedPilot, null, CancellationToken.None));
                    Background(() => RefreshRunningAsync(endedPilot, ended.SessionId, CancellationToken.None));
                    break;
                case SimRefused { Command: SimCommands.Stop, Reason: "IN_WARP" } inWarp when _pilots.TryGetValue(inWarp.SessionId, out var warping):
                    _warping[inWarp.SessionId] = 0;
                    TellShipInWarp(warping.Session);
                    break;
                case SimRefused refused:
                    _log?.Invoke($"the simulation refused command {refused.Command} of session {refused.SessionId}: {refused.Reason}");
                    break;
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"a message of the simulation could not be handled: {error}");
        }
    }


    private void HandleMutadaptiveRepairCycle(SimRemoteRepairCycle cycle)
    {
        var amount = 0d;
        var sessionExists = _pilots.ContainsKey(cycle.SessionId);
        var accepted = sessionExists
            && Eve.Services.Space.Effects.ProjectedEffect.MutadaptiveSpool.TryNextCycle(
                cycle.ModuleId,
                cycle.TargetId,
                cycle.CycleId,
                cycle.CycleNumber,
                cycle.BaseAmount,
                cycle.RepairMultiplierBonusPerCycle,
                cycle.RepairMultiplierBonusMax,
                cycle.CycleAtMs,
                cycle.CycleDurationMs,
                out amount);
        var reason = accepted ? string.Empty : sessionExists ? "STALE_CYCLE" : "STALE_SESSION";
        _sim.RemoteRepairCycleResult(new SimRemoteRepairCycleResult(
            cycle.SessionId, cycle.ModuleId, cycle.TargetId, cycle.CycleId, accepted, reason, amount));
        if (cycle.EndsEffectAfterCycle)
        {
            Eve.Services.Space.Effects.ProjectedEffect.MutadaptiveSpool.CompleteCycle(cycle.ModuleId, cycle.CycleId);
        }
    }




    private void Forward(ClientSession session, SimNotify notify)
    {
        var args = MarshalCodec.Decode(notify.Args.ToArray()) is PyTuple tuple ? tuple.Items : [];
        session.SendNotification(notify.Name, notify.IdType, [.. args]);
        if (!IsStructureObserver(session) && (notify.Name == "SetState" || notify.Name == "DoDestinyUpdate" && args.Any(HasBallparkState))) ReplayTetherFx(session);
    }

    // ── the end of a jump ────────────────────────────────────────────────────

    /// <summary>
    /// A pilot's corporation, alliance, war faction or security status changed while it flies: the simulation is given its ship again with what the character has now, so the ship is what
    /// everybody who looks at it from then on sees and what the rules of aggression read. A change of the security status is shown to those who see the ship (an `OnSlimItemChange`, as the
    /// reference does when a crime costs some); the others of an affiliation are not (the reference's way of telling them is not recorded).
    /// Nothing is done for a character that is not flying a ship in space.
    /// </summary>
    public async Task AffiliationChangedAsync(long characterId, CancellationToken cancellationToken = default)
    {
        var pilot = _pilots.Values.FirstOrDefault(p => p.CharacterId == characterId);
        if (pilot is null || await _ships.LoadAsync(characterId, cancellationToken) is not { } flown || flown.Ship.Id != pilot.ShipId)
        {
            return;
        }
        _sim.ShipChanged((ulong)pilot.Session.SessionId, SimShips.Of(flown, _statics, await SecurityStatusAsync(characterId, cancellationToken)));
    }

    /// <summary>
    /// The spools of the modules this host runs are forgotten (the spool is process-wide, keyed by module, so another host's spools are not touched).
    /// </summary>
    private void ForgetMutadaptiveSpools()
    {
        foreach (var pilot in _pilots.Values)
        {
            foreach (var moduleId in pilot.RunningModules.Keys)
            {
                Eve.Services.Space.Effects.ProjectedEffect.MutadaptiveSpool.Reset(moduleId);
            }
        }
    }

    /// <summary>The simulation was lost: every ship it held is gone, so each is brought home to the station its character last was at.</summary>
    private void OnLost()
    {
        foreach (var observer in _structureObservers.Values.ToArray())
            Background(async () =>
            {
                try { await EndStructureControlAsync(observer, true, CancellationToken.None); }
                finally { ForgetStructureObserver(observer); observer.Close(); }
            });
        ClearSimDamageableEntities();
        _warping.Clear();
        ForgetMutadaptiveSpools();

        var abyssal = new List<(AbyssalSimulationParticipantLost Participant, Pilot? Pilot, AbyssalOfflineResident? Offline)>();
        foreach (var pilot in _pilots.Values.Where(pilot => pilot.SimulationScene.InstanceId > 0 && pilot.AbyssalContentId is not null).ToArray())
        {
            var sessionId = (ulong)pilot.Session.SessionId;
            if (pilot.AbyssalContentId is not { } contentId) continue;
            _pilots.TryRemove(sessionId, out _);
            _warping.TryRemove(sessionId, out _);
            _lockedTargets.TryRemove(sessionId, out _);
            EndTetherPilot(pilot);
            pilot.End();
            abyssal.Add((new AbyssalSimulationParticipantLost(pilot.CharacterId, pilot.ShipId,
                pilot.SimulationScene.LocationId, pilot.SimulationScene.RoomIndex, pilot.Session), pilot, null));
        }
        foreach (var offline in _abyssalOfflineResidents.Values.ToArray())
        {
            if (offline.ContentId is null) continue;
            abyssal.Add((new AbyssalSimulationParticipantLost(offline.CharacterId, offline.ShipId,
                offline.Scene.LocationId, offline.Scene.RoomIndex, null), null, offline));
        }
        foreach (var run in abyssal.GroupBy(entry => (entry.Offline?.ContentId ?? entry.Pilot?.AbyssalContentId, entry.Offline?.Scene.InstanceId ?? entry.Pilot?.SimulationScene.InstanceId)))
        {
            if (run.Key.Item1 is not { } contentId || run.Key.Item2 is not { } instanceId) continue;
            var entries = run.ToArray();
            var notice = new AbyssalSimulationRunLost(instanceId, contentId, entries.Select(entry => entry.Participant).ToArray());
            Background(async () =>
            {
                try
                {
                    foreach (var entry in entries)
                    {
                        if (entry.Pilot is not { } pilot) continue;
                        await _charges.CloseAsync((ulong)pilot.Session.SessionId).ConfigureAwait(false);
                        await CancelRepairsAsync(pilot, CancellationToken.None).ConfigureAwait(false);
                    }
                    if (AbyssalSimulationLostAsync is not { } reconcile)
                        throw new InvalidOperationException("Abyssal simulation loss requires Host restart reconciliation.");
                    await reconcile(notice, CancellationToken.None).ConfigureAwait(false);
                    foreach (var entry in entries)
                    {
                        if (entry.Offline is { } offline) _abyssalOfflineResidents.TryRemove(offline.ShipId, out _);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _log?.Invoke($"Abyssal restart reconciliation for run {instanceId} failed: {error.Message}");
                }
            });
        }

        foreach (var pilot in _pilots.Values.ToArray())
        {
            Background(async () =>
            {
                var character = pilot.Session.CharacterId is { } id ? id : pilot.CharacterId;
                var station = pilot.Session.GetInt("stationid2") ?? _stations.All.FirstOrDefault(s => s.SolarSystemId == pilot.SolarSystemId)?.Id;
                if (station is { } home)
                {
                    _log?.Invoke($"the simulation was lost: docking character {character} at station {home}");
                    await FinishDockingAsync(pilot, home, null);
                }
            });
        }
    }

    /// <summary>Where a station is (its solar system, constellation and region), for putting a character back in the system of its station.</summary>
    private (int SolarSystemId, int ConstellationId, int RegionId)? PlaceOfStation(long stationId) =>
        _stations.Get(stationId) is { } station ? (station.SolarSystemId, station.ConstellationId, station.RegionId) : null;

    private void StartShipSessionChange(ulong sessionId) => _transitions.StartSessionChange(sessionId);

    private void OnSessionClosed(ClientSession session) => Background(() => OnSessionClosedAsync(session));

    private async Task<bool> SaveSpaceStateAsync(Pilot pilot)
    {
        if (_spaceItems is null)
            return false;

        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync(
                (ulong)pilot.Session.SessionId,
                SimCommands.PositionOf,
                [pilot.ShipId],
                CancellationToken.None);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"position of character {pilot.CharacterId} could not be saved: {error.Message}");
            return false;
        }

        if (!reply.Accepted || reply.Position.Count < 3 || reply.Position.Take(3).Any(value => !double.IsFinite(value)) ||
            await _ships.LoadAsync(pilot.CharacterId, CancellationToken.None) is not { } snapshot ||
            snapshot.Ship.Id != pilot.ShipId)
            return false;

        var ship = SimShips.Of(snapshot, _statics);
        var direction = await _spaceItems.GetAsync(pilot.ShipId, CancellationToken.None) is { SolarSystemId: var oldSystem } oldState &&
                        oldSystem == pilot.SolarSystemId && oldState.Direction.Count >= 3
            ? oldState.Direction
            : [0d, 0d, 1d];
        var state = new SpaceItem(
            pilot.ShipId,
            pilot.SolarSystemId,
            reply.Position.Take(3).ToArray(),
            direction,
            Math.Max(1d, ship.Radius),
            ship.GroupId,
            0,
            pilot.CharacterId,
            ship.CorporationId,
            ship.AllianceId,
            ship.WarFactionId,
            // The active ship's pose must survive restarts until the next character entry consumes it.
            long.MaxValue);
        try
        {
            await _spaceItems.SaveAsync(state, CancellationToken.None);
            return true;
        }
        catch (Exception error)
        {
            _log?.Invoke($"space position of character {pilot.CharacterId} could not be saved: {error.Message}");
            return false;
        }
    }

    private async Task OnSessionClosedAsync(ClientSession session)
    {
        try { await EndStructureControlAsync(session, true, CancellationToken.None); }
        catch (Exception error) { _log?.Invoke($"structure control cleanup on disconnect failed: {error.Message}"); }
        finally { ForgetStructureObserver(session); }
        var id = (ulong)session.SessionId;
        var abyssalResident = _pilots.TryGetValue(id, out var resident)
            && resident.SimulationScene.InstanceId > 0;
        CancelSafeLogoff(session);
        _transitions.EndSessionChange(id);
        _lockedTargets.TryRemove(id, out _);
        _targeting.TryRemove(id, out _); // not disposed: an order may still be waiting on it
        if (!_pilots.TryRemove(id, out var pilot))
            return;

        if (abyssalResident && pilot.AbyssalContentId is { } disconnectedContent)
        {
            _abyssalOfflineResidents[pilot.ShipId] = new AbyssalOfflineResident(
                id,
                pilot.CharacterId,
                pilot.ShipId,
                disconnectedContent,
                pilot.SimulationScene,
                pilot.LastAbyssalMove);
        }

        if (pilot.CynoArrival) TetherProtectionEnded(pilot);
        EndTetherPilot(pilot);
        pilot.End();
        try
        {
            try { await pilot.TetherWork.ConfigureAwait(false); }
            catch (Exception error) { _log?.Invoke($"tether work on disconnect failed: {error.Message}"); }
            await CancelRepairsAsync(pilot, CancellationToken.None).ConfigureAwait(false);
            var chargesSaved = true;
            try
            {
                await _charges.CloseAsync(id).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                chargesSaved = false;
                _log?.Invoke($"the rounds of character {pilot.CharacterId} could not be written on disconnect: {error.Message}");
            }

            var spaceStateSaved = chargesSaved && !abyssalResident && await SaveSpaceStateAsync(pilot).ConfigureAwait(false);
            if (abyssalResident)
            {
                if (pilot.AbyssalContentId is not { } contentId || AbyssalParticipantDisconnectedAsync is not { } disconnected)
                    throw new InvalidOperationException("Abyssal disconnect persistence is not configured for this resident participant.");
                await disconnected(new AbyssalParticipantDisconnectedNotice(
                    pilot.SimulationScene.InstanceId,
                    contentId,
                    pilot.CharacterId,
                    pilot.ShipId,
                    pilot.SimulationScene.LocationId,
                    pilot.SimulationScene.RoomIndex), CancellationToken.None).ConfigureAwait(false);
            }
            _log?.Invoke(abyssalResident
                ? $"Abyssal pilot {pilot.CharacterId} disconnected in run {pilot.AbyssalContentId}; the resident ship remains in its room scene"
                : $"pilot {pilot.CharacterId} left space in system {pilot.SolarSystemId} (session {id}): position saved {spaceStateSaved}");
            if (!abyssalResident && !spaceStateSaved && chargesSaved)
            {
                _log?.Invoke($"pilot {pilot.CharacterId} (session {id}): the ship is recovered to its station, its position was not saved");
                await _ships.RecoverFromSpaceAsync(pilot.CharacterId, PlaceOfStation, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"the ship of character {pilot.CharacterId} could not be preserved after the session ended: {error.Message}");
        }
        finally
        {
            if (!abyssalResident)
            {
                _sim.Leave(id, LeaveWhy.LoggedOff);
            }
        }
    }
}
