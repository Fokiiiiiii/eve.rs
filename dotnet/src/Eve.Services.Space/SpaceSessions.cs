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

/// <summary>
/// The pilots that are in space, and everything the service host does around the simulation: sending a ship out (the item, the session,
/// the notifications), forwarding what the simulation says to the client that plays the ship, and bringing the ship home when it docks, or
/// when the simulation is lost. What happens in space itself (motion, the balls, the stamps) is the simulation's (ADR 0007).
/// </summary>
public enum ShipLifecycleAction
{
    SafeLogoff,
    Eject,
    Board
}

public sealed record ShipBoarded(
    long ShipId,
    long PreviousOwnerCharacterId,
    long ActorCharacterId,
    long ActorCorporationId,
    long BoardedAtFileTime);

public sealed class SpaceSessions : IDisposable, ISpaceTargeting, ISpaceModules, ISpaceHealth, ISpaceHulls, ISpaceNpcs, ISpaceDeath, ISpaceRounds
{
    /// <summary>How long a ship that has undocked cannot be hurt (the reference's: 30 seconds).</summary>
    public const long UndockInvulnerabilityTicks = 300_000_000;

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

        public int SolarSystemId { get; } = solarSystemId;

        /// <summary>A FILETIME: until when the ship cannot be hurt, unless a command of its pilot breaks it first.</summary>
        public long InvulnerableUntil { get; } = invulnerableUntil;

        /// <summary>The station the ship undocked from (the reference keeps saying so in the custom info of the ship's item).</summary>
        public long UndockedFrom { get; } = undockedFrom;

        /// <summary>Whether the protection is the cloak of a ship that has come through a stargate (its end is told with one more notification).</summary>
        public bool JumpCloak { get; } = jumpCloak;

        private int _ended;

        /// <summary>Stops the timer that ends the protection when it runs out (set when the timer is; the pilot ends it by hand first when a command breaks the protection).</summary>
        public Action? CancelExpiry { get; set; }

        /// <summary>
        /// The modules of the ship that the simulation has accepted an activation for, and not yet a stop: their state is Active in dogma, so the
        /// numbers the ship is told (its resonances, the hardeners) are those of the effects that run. A new pilot (undock, eject, boarding) starts with none.
        /// </summary>
        public ConcurrentDictionary<long, ItemState> ActiveModules { get; } = new();

        /// <summary>Marks the protection ended; false when it had ended already (each ending is told once).</summary>
        public bool End()
        {
            CancelExpiry?.Invoke();
            return Interlocked.Exchange(ref _ended, 1) == 0;
        }
    }

    private readonly ISimulation _sim;
    private readonly CharacterShips _ships;
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

    private sealed record EjectedShip(long OwnerCharacterId, long OwnerCorporationId, int ShipTypeId, int SolarSystemId);
    private readonly ConcurrentDictionary<long, EjectedShip> _ejectedShips = new();

private readonly TransitionRuntime _transitions;

    public SpaceSessions(ISimulation sim, CharacterShips ships, DogmaStatic statics, StationCatalog stations, SolarSystemCatalog systems, Func<long> nowUnixMs, Action<string>? log = null, TimeSpan? protection = null, TimeSpan? jumpCloak = null, Eve.Scheduler.LocalTimers? localTimers = null)
    {
        _timers = localTimers ?? new LocalTimers(log);
        _transitions = new TransitionRuntime(_timers);
        _ownsTimers = localTimers is null;
        _protection = protection ?? TimeSpan.FromTicks(UndockInvulnerabilityTicks);
        _jumpCloak = jumpCloak ?? TimeSpan.FromTicks(JumpCloakTicks);
        _sim = sim;
        _ships = ships;
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
        _ = Task.Run(async () =>
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

    /// <summary>A ship has come into the scene of a solar system (an undock, a jump): what lies there that the simulation does not have yet can be put in it.</summary>
    public event Action<int>? SystemEntered;

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
        _ = Task.Run(async () =>
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

    public void Dispose()
{
    if (_ownsTimers) _timers.Dispose();
    _sim.Received -= OnReceived;
    _sim.Lost -= OnLost;
    Eve.Services.Space.Effects.ProjectedEffect.MutadaptiveSpool.ResetAll();
    // the host stops: the rounds that were spent are written first
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

    public bool IsWarping(ClientSession session) => _warping.ContainsKey((ulong)session.SessionId);




    public ClientSession? ClientSessionOf(ulong sessionId) =>
        _pilots.TryGetValue(sessionId, out var pilot) ? pilot.Session : null;

    public double? SecurityOfSystem(int solarSystemId) => _systems.Get(solarSystemId)?.Security;

    private static MachoWrappedException Notify(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));


    private static MachoWrappedException MovementRefused(PolicyDecision decision, string action) =>
        Notify(decision.BlockReason switch
        {
            PolicyBlockReason.WeaponsTimer => $"You cannot {action} while your weapons timer is active.",
            PolicyBlockReason.CriminalTimer => $"You cannot {action} while your criminal timer is active in high-security space.",
            PolicyBlockReason.SessionChangeTimer => $"You cannot {action} while your session change timer is active.",
            _ => $"You cannot {action} at this time.",
        });

    // ── undocking ────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends the session's ship out of its station. The client is told in the order the reference tells it (the ship moved, the objects
    /// bound to the station let go, the session changed, the undocking protection started) and the answer is the object it called and the time
    /// from which it may go on. Returns None when there is nothing to undock.
    /// </summary>
    public async Task<PyValue> UndockAsync(ClientSession session, string? boundObjectId, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId || session.GetInt("stationid") is not { } stationId || IsInSpace(session))
        {
            return None;
        }
        if (!_sim.IsConnected)
        {
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        if (await _ships.LoadAsync(characterId, cancellationToken) is not { } snapshot)
        {
            return None;
        }
        // a ship comes out of its station with its shield and capacitor full; what it has lost of its armor and hull it keeps (the reference's)
        var spec = SimShips.Of(snapshot, _statics, await SecurityStatusAsync(characterId, cancellationToken));
        spec = spec with { Condition = (spec.Condition ?? SimCondition.Full) with { ShieldCharge = 1, Charge = 1 } };
        var systemId = snapshot.Character.SolarSystemId;
        var sessionId = (ulong)session.SessionId;
        Pilot? pilot = null;

        var transition = await _transitions.ExecuteAsync(
            token => _ships.UndockAsync(characterId, token),
            result => result.Moved is not null,
            async (result, token) =>
            {
                string? refusal;
                try
                {
                    refusal = await _sim.EnterAsync(new SimEnter(sessionId, systemId, EnterHow.Undock, stationId, spec), token);
                    if (refusal is null)
                        await _sim.SetHeatProfileAsync(sessionId, SimShips.HeatProfileOf(snapshot, _statics), token);
                }
                catch (SimUnavailableException error)
                {
                    refusal = error.Message;
                }
                if (refusal is not null)
                {
                    _log?.Invoke($"the simulation did not take the ship of character {characterId}: {refusal}");
                    await _ships.RecoverFromSpaceAsync(characterId, CancellationToken.None);
                    throw Notify("Space is not available right now. Try again in a moment.");
                }
                if (!_pilots.Values.Any(other => other.SolarSystemId == systemId))
                    ForgetNpcsOf(systemId);
            },
            (result, _) =>
            {
                var moved = result.Moved!;
                var now = FileTimeNow();
                pilot = new Pilot(session, characterId, moved.Ship.Id, moved.Ship.TypeId,
                    systemId, now + UndockInvulnerabilityTicks, stationId);
                _pilots[sessionId] = pilot;
                SystemEntered?.Invoke(systemId);
                session.Closed += () => OnSessionClosed(session);
                ArmProtection(pilot, _protection);
                return Task.FromResult(true);
            },
            (result, _) =>
            {
                var moved = result.Moved!;
                var type = _statics.Type(moved.Ship.TypeId);
                session.SendNotification(
                    "OnItemsChanged", "charid",
                    new PyList([InventoryWire.PackedRow(ShipRow(moved.Ship, type), new PyStr($"Undocking:{stationId}"))]),
                    ChangedFrom(moved), None);
                ReleaseBoundObjects(session);
                session.Change(("stationid", None), ("locationid", new PyInt(systemId)), ("solarsystemid", new PyInt(systemId)));
                session.SendNotification("OnInvulunOnUndockingUpdated", "shipid", InventoryWire.Id(pilot!.ShipId),
                    new PyLong(pilot.InvulnerableUntil), new PyLong(UndockInvulnerabilityTicks));
                return Task.CompletedTask;
            },
            cancellationToken,
            sessionId);
        if (transition.Moved is null)
        {
            _log?.Invoke($"undock of character {characterId} refused: {transition.Refusal}");
            return None;
        }
        return Py.Tuple(boundObjectId is null ? None : new PyStr(boundObjectId),
            new PyLong(Py.FileTimeFromUnixMs(_nowUnixMs() + SessionChangeCooldownMs)));
    }


    /// <summary>Boards another ship from the current station hangar and updates the active session ship.</summary>
    /// <summary>Leaves a docked ship, activates the character's capsule, and keeps the ship in the hangar.</summary>

    /// <summary>Boards a ship already in the character's station hangar.</summary>
    public async ValueTask<PyValue> EjectAsync(ClientSession session, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        CancelSafeLogoff(session);
        if (LifecycleBlockers is not { } readBlockers
            || !_pilots.TryGetValue(id, out var pilot)
            || session.CharacterId != pilot.CharacterId || session.GetInt("shipid") != pilot.ShipId
            || !IsInSpace(session)
            || await _ships.LoadAsync(pilot.CharacterId, cancellationToken) is not { } fitting
            || fitting.Ship.Id != pilot.ShipId
            || fitting.Ship.TypeId == CharacterShips.CapsuleTypeId)
        {
            return None;
        }

        IReadOnlyList<string> blockers;
        try
        {
            blockers = await readBlockers(ShipLifecycleAction.Eject, session, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _log?.Invoke($"eject conditions for session {session.SessionId} could not be checked: {error.Message}");
            return SafeLogoffRefusal();
        }
        if (blockers.Count != 0)
            return new PyList([.. blockers.Select(label => new PyStr(label))]);

        var position = await PositionOfAsync(session, pilot.ShipId, cancellationToken);
        if (position is not { } place)
            return None;

        var simulationFailed = false;
        var transition = await _transitions.ExecuteAsync(
            token => _ships.EjectAsync(pilot.CharacterId, pilot.ShipId, token),
            result => result is not null,
            async (result, token) =>
            {
                var ejected = result!;
                await _charges.CloseAsync(id);
                foreach (var moduleId in ejected.ModulesToStop)
                    await _sim.OfflineModuleAsync(id, moduleId, token);
                _sim.ShipChanged(
                    id,
                    SimShips.Of(ejected.EjectedSnapshot, _statics, await SecurityStatusAsync(pilot.CharacterId, token)));
                _sim.Leave(id, LeaveWhy.Ejected);
                if (ejected.HullCollapsed)
                {
                    _sim.Destroy(ejected.Ship.Id,
                        new SimCredit(pilot.CharacterId, fitting.Character.CorporationId,
                            fitting.Character.AllianceId ?? 0, fitting.Character.FactionId ?? 0));
                }

                if (await _ships.LoadAsync(pilot.CharacterId, token) is not { } capsule
                    || capsule.Ship.Id != ejected.PilotShip.Id)
                {
                    _log?.Invoke($"the capsule of character {pilot.CharacterId} could not be loaded after ejecting");
                    simulationFailed = true;
                    session.Close();
                    return;
                }
                var refusal = await _sim.RespawnAsync(new SimRespawn(
                    id,
                    SimShips.Of(capsule, _statics, await SecurityStatusAsync(pilot.CharacterId, token)),
                    [place.X, place.Y, place.Z],
                    [1.0, 0.0, 0.0]), token);
                if (refusal is not null)
                {
                    _log?.Invoke($"the simulation did not take the capsule of character {pilot.CharacterId} after ejecting: {refusal}");
                    simulationFailed = true;
                    session.Close();
                }
            },
            (result, _) =>
            {
                if (simulationFailed)
                    return Task.FromResult(false);

                var ejected = result!;
                _ejectedShips[ejected.Ship.Id] = new EjectedShip(
                    pilot.CharacterId, fitting.Character.CorporationId, ejected.Ship.TypeId, pilot.SolarSystemId);
                pilot.End();
                var capsulePilot = new Pilot(
                    session, pilot.CharacterId, ejected.PilotShip.Id, ejected.PilotShip.TypeId, pilot.SolarSystemId, 0);
                if (!_pilots.TryUpdate(id, capsulePilot, pilot))
                {
                    session.Close();
                    return Task.FromResult(false);
                }
                session.Change(("shipid", new PyLong(ejected.PilotShip.Id)));
                return Task.FromResult(true);
            },
            (result, _) =>
            {
                var ejected = result!;
                if (ejected.Events.Count != 0)
                {
                    session.SendNotification(
                        "OnMultiEvent", "clientID",
                        Eve.Services.Ships.ShipNotifications.MultiEvent(
                            pilot.CharacterId, pilot.ShipId, ejected.Events, FileTimeNow()));
                }
                return Task.CompletedTask;
            },
            cancellationToken,
            id);

        return transition is not null && !simulationFailed
            ? Py.Tuple(new PyDict([]), new PyDict([]), new PyDict([]), new PyDict([]))
            : None;
    }

    public async ValueTask<PyValue> BoardAsync(ClientSession session, long shipId, long currentShipId, CancellationToken cancellationToken)
    {
        CancelSafeLogoff(session);
        var id = (ulong)session.SessionId;
        if (session.CharacterId is not { } characterId
            || session.GetInt("shipid") != currentShipId)
            return None;
        var inSpace = IsInSpace(session);
        if (inSpace && _transitions.SessionChangeActive(id))
            return None;

        if (LifecycleBlockers is not { } readBlockers)
            return SafeLogoffRefusal();
        IReadOnlyList<string> blockers;
        try
        {
            blockers = await readBlockers(ShipLifecycleAction.Board, session, shipId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _log?.Invoke($"board conditions for session {session.SessionId} could not be checked: {error.Message}");
            return SafeLogoffRefusal();
        }
        if (blockers.Count != 0)
            return new PyList([.. blockers.Select(label => new PyStr(label))]);

        var locationId = session.GetInt("stationid") ?? session.GetInt("structureid");
        if (!inSpace)
        {
            if (locationId is not { } dockLocationId)
                return None;
            var changed = await _transitions.ExecuteAsync(
                token => _ships.BoardAsync(characterId, dockLocationId, currentShipId, shipId, token),
                result => result,
                applySimulation: null,
                applySession: (_, _) =>
                {
                    session.Change(("shipid", new PyLong(shipId)));
                    return Task.FromResult(true);
                },
                notify: null,
                cancellationToken,
                id);
            if (!changed)
                return None;
            var dockedEmpty = new PyDict([]);
            return Py.Tuple(dockedEmpty, dockedEmpty, dockedEmpty, dockedEmpty);
        }

        if (!_pilots.TryGetValue(id, out var currentPilot)
            || currentPilot.CharacterId != characterId
            || currentPilot.ShipId != currentShipId
            || !_ejectedShips.TryGetValue(shipId, out var abandoned)
            || abandoned.ShipTypeId == CharacterShips.CapsuleTypeId
            || abandoned.SolarSystemId != currentPilot.SolarSystemId)
            return None;

        var currentPosition = await PositionOfAsync(session, currentShipId, cancellationToken);
        var shipPosition = await PositionOfAsync(session, shipId, cancellationToken);
        if (currentPosition is not { } from || shipPosition is not { } to || from.SolarSystemId != to.SolarSystemId)
            return None;
        var dx = from.X - to.X;
        var dy = from.Y - to.Y;
        var dz = from.Z - to.Z;
        if (dx * dx + dy * dy + dz * dz > 6_500d * 6_500d)
            return None;

        if (abandoned.OwnerCharacterId != characterId && IsTargetedByAnotherPlayer(id, shipId))
            return None;

        var simulationFailed = false;
        var nextShipTypeId = 0;
        var transition = await _transitions.ExecuteAsync(
            token => _ships.BoardInSpaceAsync(
                characterId, currentPilot.SolarSystemId, currentShipId, shipId, token),
            result => result is not null,
            async (result, token) =>
            {
                var boarding = result!;
                _ejectedShips[currentShipId] = new EjectedShip(
                    characterId, boarding.BoarderCorporationId, currentPilot.TypeId, currentPilot.SolarSystemId);
                _ejectedShips.TryRemove(shipId, out _);
                _sim.Leave(id, LeaveWhy.Boarded);

                if (await _ships.LoadAsync(characterId, token) is not { } fitting || fitting.Ship.Id != shipId)
                {
                    _log?.Invoke($"the simulation could not load boarded ship {shipId} for character {characterId}");
                    simulationFailed = true;
                    session.Close();
                    return;
                }

                nextShipTypeId = fitting.Ship.TypeId;
                var refusal = await _sim.RespawnAsync(new SimRespawn(
                    id,
                    SimShips.Of(fitting, _statics, await SecurityStatusAsync(characterId, token)),
                    [to.X, to.Y, to.Z],
                    [1.0, 0.0, 0.0]), token);
                if (refusal is not null)
                {
                    _log?.Invoke($"the simulation did not take ship {shipId} after boarding: {refusal}");
                    simulationFailed = true;
                    session.Close();
                }
            },
            (result, _) =>
            {
                if (simulationFailed)
                    return Task.FromResult(false);

                var nextPilot = new Pilot(
                    session, characterId, shipId, nextShipTypeId, currentPilot.SolarSystemId, 0);
                currentPilot.End();
                if (!_pilots.TryUpdate(id, nextPilot, currentPilot))
                {
                    session.Close();
                    return Task.FromResult(false);
                }
                session.Change(("shipid", new PyLong(shipId)));
                return Task.FromResult(true);
            },
            async (result, _) =>
            {
                var boarding = result!;
                if (boarding.PreviousOwnerId == characterId || InvalidateInsuranceForBoarding is not { } invalidateInsurance)
                    return;
                var boarded = new ShipBoarded(
                    boarding.ShipId,
                    boarding.PreviousOwnerId,
                    boarding.BoarderCharacterId,
                    boarding.BoarderCorporationId,
                    FileTimeNow());
                try
                {
                    await invalidateInsurance(boarded, CancellationToken.None);
                }
                catch (Exception error)
                {
                    _log?.Invoke($"insurance invalidation after ship {shipId} was boarded failed: {error.Message}");
                }
            },
            cancellationToken,
            id);

        if (transition is null || simulationFailed)
            return None;
        var empty = new PyDict([]);
        return Py.Tuple(empty, empty, empty, empty);
    }

    /// <summary>Prepares a capsule; the client's following Board call performs the active ship switch.</summary>
    public async ValueTask<PyValue> LeaveShipAsync(ClientSession session, long shipId, CancellationToken cancellationToken)
    {
        var locationId = session.GetInt("stationid") ?? session.GetInt("structureid");
        if (session.CharacterId is not { } characterId || locationId is not { } dockLocationId
            || session.GetInt("shipid") != shipId || IsInSpace(session))
        {
            return None;
        }

        var sessionId = (ulong)session.SessionId;
        var capsule = await _transitions.ExecuteAsync(
            token => _ships.LeaveShipAsync(characterId, dockLocationId, shipId, token),
            result => result is not null,
            applySimulation: null,
            applySession: (result, _) =>
            {
                session.Change(("shipid", new PyLong(result!.Id)));
                return Task.FromResult(true);
            },
            notify: null,
            cancellationToken,
            sessionId);
        return capsule is null ? None : InventoryWire.Id(capsule.Id);
    }

    private static ItemRow ShipRow(Item ship, TypeDef? type) =>
        new(ship.Id, ship.TypeId, ship.OwnerId, ship.LocationId, ship.Flag, -1, type?.GroupId ?? 0, type?.CategoryId ?? 0);

    private static PyDict ChangedFrom(SpaceMove moved) =>
        new([new(new PyInt(3), InventoryWire.Id(moved.PreviousLocationId)), new(new PyInt(4), new PyInt(moved.PreviousFlag))]);

    /// <summary>Tells the client to let go of every object bound to the place it is leaving, and lets them go on this side too.</summary>
    private void ReleaseBoundObjects(ClientSession session)
    {
        foreach (var oid in session.BoundOids)
        {
            session.SendNotification("OnMachoObjectDisconnect", "+clientID", new PyStr(oid), InventoryWire.Id(session.ClientId), None);
            if (Registry is not null)
            {
                Registry.UnregisterBoundObject(oid, session);
            }
            else
            {
                session.ForgetBoundObject(oid);
            }
        }
    }

    // ── what the pilot does ──────────────────────────────────────────────────

    /// <summary>The client bound the system's ballpark: the simulation sends it the scene.</summary>
    public void Bind(ClientSession session)
    {
        if (IsInSpace(session))
        {
            _sim.Bind((ulong)session.SessionId);
        }
    }

    public void UpdateState(ClientSession session)
    {
        if (IsInSpace(session))
        {
            _sim.UpdateState((ulong)session.SessionId);
        }
    }

    /// <summary>
    /// A movement command. The first one the simulation accepts while the undocking protection lasts ends it (whatever the command is, stopping
    /// included): the client is told after the update the simulation sent for the command, which is the order the reference keeps
    /// (<see cref="OnReceived"/>). A command the simulation refuses costs the pilot nothing.
    /// </summary>
    public void Command(ClientSession session, byte code, params double[] args)
    {
        CancelSafeLogoff(session);
        if (_pilots.ContainsKey((ulong)session.SessionId))
        {
            _sim.Command((ulong)session.SessionId, code, args);
        }
    }

    /// <summary>The effect of a module is offensive: it damages, jams, webs, paints or scrambles another ship (the attacks the crimewatch and the safety judge).</summary>
    private static bool IsOffensive(SimEffect package) =>
        package.Turret is not null || package.Action is SimAction.Remote { What: SimRemote.Neutralize or SimRemote.Drain or SimRemote.Web or SimRemote.Paint or SimRemote.Damp { RangePercent: < 0 } or SimRemote.Damp { ResolutionPercent: < 0 } or SimRemote.Damp { GravimetricStrengthPercent: < 0 } or SimRemote.Damp { LadarStrengthPercent: < 0 } or SimRemote.Damp { MagnetometricStrengthPercent: < 0 } or SimRemote.Damp { RadarStrengthPercent: < 0 } or SimRemote.Scramble or SimRemote.Jam or SimRemote.Disrupt { RangePercent: < 0 } or SimRemote.Disrupt { FalloffPercent: < 0 } or SimRemote.Disrupt { TrackingPercent: < 0 } };

    private void EndInvulnerability(Pilot pilot, bool keepsJumpCloak = false)
    {
        if (keepsJumpCloak && pilot.JumpCloak)
        {
            return;
        }
        if (pilot.End())
        {
            if (pilot.JumpCloak)
            {
                pilot.Session.SendNotification("OnJumpCloakCancelled", "shipid", InventoryWire.Id(pilot.ShipId));
            }
            pilot.Session.SendNotification("OnInvulnCancelled", "shipid", InventoryWire.Id(pilot.ShipId));
        }
    }

    /// <summary>The time is up and the pilot has not flown the ship: the client is told, as when a movement ends it.</summary>
    private readonly LocalTimers _timers;
    private readonly bool _ownsTimers;
    private long _protectionTimers;

    /// <summary>Ends the pilot's protection after <paramref name="span"/> unless it has ended before (every pilot's timer has a key of its own, so a ship that jumped does not cancel the timer of the one that arrived).</summary>
    private void ArmProtection(Pilot pilot, TimeSpan span)
    {
        var key = $"space.protection:{Interlocked.Increment(ref _protectionTimers)}";
        pilot.CancelExpiry = () => _timers.Cancel(key);
        _timers.Set(key, span, () => ProtectionRanOut(pilot));
    }

    private void ProtectionRanOut(Pilot pilot)
    {
        try
        {
            if (_pilots.TryGetValue((ulong)pilot.Session.SessionId, out var current) && ReferenceEquals(current, pilot))
            {
                EndInvulnerability(pilot);
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"the end of the protection of session {pilot.Session.SessionId} could not be told: {error.Message}");
        }
    }

    /// <summary>
    /// An order about the ship's targets. The simulation keeps the locks (a lock takes time, and ends when either ship warps or goes); this asks and
    /// hears back. A lock the simulation accepts ends the undocking protection, and the client is told once more that it is over, as the reference
    /// does after every lock it accepts. One order of a pilot is asked at a time (the answers are matched by what was asked).
    /// </summary>
    public async Task<TargetingResult?> OrderAsync(ClientSession session, TargetingOrder order, IReadOnlyList<long> targets, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        CancelSafeLogoff(session);
        if (!_pilots.TryGetValue(id, out var pilot))
        {
            return null;
        }
        var gate = _targeting.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync(id, (byte)order, [.. targets.Select(target => (double)target)], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"targeting of session {session.SessionId}: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        finally
        {
            gate.Release();
        }
        if (!reply.Accepted)
        {
            return TargetingResult.Refusal(reply.Reason ?? "REFUSED", reply.TargetType, reply.DesiredRange, reply.ActualDistance);
        }

        if (order != TargetingOrder.Targeters)
        {
            if (reply.Targets.Count == 0)
                _lockedTargets.TryRemove(id, out _);
            else
                _lockedTargets[id] = new HashSet<long>(reply.Targets);
        }

        if (order == TargetingOrder.Lock)
        {
            foreach (var targetId in reply.Targets)
            {
                var target = _pilots.Values.FirstOrDefault(other => other.ShipId == targetId);
                if (target is not null)
                    CancelSafeLogoff(target.Session);
            }
            EndInvulnerability(pilot);
            session.SendNotification("OnInvulnCancelled", "shipid", InventoryWire.Id(pilot.ShipId));
        }
        return new TargetingResult(true, reply.Pending, reply.Targets);
    }

    /// <summary>
    /// Starts an effect of a module. The dogma engine works out the package (the cycle, its cost, the ship's numbers with and without the effect) and the
    /// simulation runs it; an effect it accepts ends the undocking protection, as a movement does. A module order is asked one at a time for a pilot.
    /// </summary>
    public async Task<ModuleResult?> ActivateAsync(ClientSession session, long moduleId, string effectName, long targetId, int repeat, CancellationToken cancellationToken)
    {
        CancelSafeLogoff(session);
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot) || session.CharacterId is not { } characterId)
        {
            return null;
        }
        // a gun is started with the rounds it has left: what was spent a moment ago is written first
        await _charges.FlushAsync(id).ConfigureAwait(false);
        if (await FlownShipAsync(pilot, cancellationToken) is not { } snapshot)
        {
            return null;
        }
        // where the module may be used (the client's dogmaLM refuses before it works out anything)
        if (snapshot.Fitted.Any(m => m.Id == moduleId)
            && _systems.Get(pilot.SolarSystemId) is { } place
            && ModuleRestrictions.Check(Security.ClassFromLevel(place.Security), attribute => snapshot.World.Get(moduleId, attribute)) is { } forbidden)
        {
            throw MachoErrors.ThrowUserError(forbidden);
        }
        var made = ModulePackages.Of(snapshot, _statics, moduleId, effectName, targetId, repeat);
        if (made.Effect is not { } package)
        {
            return ModuleResult.Refusal(made.Reason ?? "REFUSED", made.ModuleType, made.ModuleName);
        }

        var targetPilot = _pilots.Values.FirstOrDefault(other => other.ShipId == package.TargetId);
        var securityClass = _systems.Get(pilot.SolarSystemId) is { } system
            ? Security.ClassFromLevel(system.Security)
            : (SecurityClass?)null;
        AggressionRequest? assistanceRequest = null;
        Func<AggressionRequest, CancellationToken, Task>? recordAssistance = null;
        var assistance = package.Action is SimAction.Remote
        {
            What: SimRemote.Repair or SimRemote.MutadaptiveRepair or SimRemote.Transfer
                or SimRemote.Damp { RangePercent: >= 0, ResolutionPercent: >= 0, GravimetricStrengthPercent: >= 0, LadarStrengthPercent: >= 0, MagnetometricStrengthPercent: >= 0, RadarStrengthPercent: >= 0 }
                or SimRemote.Disrupt { RangePercent: >= 0, FalloffPercent: >= 0, TrackingPercent: >= 0 }
        };
        // the target's type (a pilot's ship, or an NPC's) carries what it refuses; the attributes are the static data's, as the reference's targets' are
        var targetTypeId = targetPilot?.TypeId;
        if (targetTypeId is null && _npcShips.TryGetValue(package.TargetId, out var npc))
        {
            targetTypeId = npc.TypeId;
        }
        if (package.TargetId > 0
            && targetTypeId is { } immuneTypeId
            && _statics.Type(immuneTypeId) is { } immuneType
            && immuneType.Attributes.TryGetValue(ModuleAttributes.DisallowOffensiveModifiers, out var immune) && immune > 0
            && (IsOffensive(package) || snapshot.World.Get(moduleId, ModuleAttributes.DisallowAgainstEwImmuneTarget) > 0))
        {
            // the client's own refusal for a target that cannot be hit by offensive modules (dogma/effects/util.py PreCheckIfImmune)
            throw MachoErrors.ThrowUserError("DeniedActivateTargetModuleDisallowed");
        }
        if (assistance)
        {
            if (targetTypeId is not { } targetTypeIdValue)
            {
                return ModuleResult.Refusal("REFUSED", made.ModuleType, made.ModuleName);
            }
            var targetType = _statics.Type(targetTypeIdValue);
            if (targetType is null
                || (targetType.Attributes.TryGetValue(ModuleAttributes.DisallowAssistance, out var disallowAssistance) && disallowAssistance > 0))
            {
                return ModuleResult.Refusal("REFUSED", made.ModuleType, made.ModuleName);
            }

            if (targetPilot is { CharacterId: > 0 } recipient
                && recipient.CharacterId != characterId
                && securityClass is { } assistanceWhere)
            {
                var request = new AggressionRequest(
                    characterId,
                    recipient.CharacterId,
                    recipient.ShipId,
                    pilot.SolarSystemId,
                    _systems.Get(pilot.SolarSystemId)?.Security ?? 0,
                    assistanceWhere,
                    await IsCapsuleAsync(recipient, assistanceWhere, cancellationToken));
                assistanceRequest = request;
                var policy = RemoteAssistancePolicyOf;
                recordAssistance = RecordRemoteAssistance;
                if (policy is null || recordAssistance is null)
                {
                    return ModuleResult.Refusal(WeaponSafety.Refusal, made.ModuleType, made.ModuleName);
                }
                var decision = await policy(request, SafetyLevel.Full, cancellationToken);
                if (!decision.Allowed && decision.BlockReason == Eve.Crimewatch.PolicyBlockReason.Safety)
                {
                    var level = SafetyLevelOf is { } ask
                        ? await ask(characterId, cancellationToken)
                        : SafetyLevel.Full;
                    decision = await policy(request, level, cancellationToken);
                }
                if (!decision.Allowed)
                {
                    return ModuleResult.Refusal(WeaponSafety.Refusal, made.ModuleType, made.ModuleName);
                }
            }
        }

        // the safety of the pilot: a weapon is not started at the ship of another pilot where that would be a crime the safety the pilot has set does not allow
        var attack = IsOffensive(package);
        var victim = attack && targetPilot is { CharacterId: > 0 } && targetPilot.CharacterId != characterId
            ? targetPilot
            : null;
        if (securityClass is { } empire && (attack || package.Action is SimAction.Smartbomb))
        {
            var level = SafetyLevelOf is { } ask ? await ask(characterId, cancellationToken) : SafetyLevel.Full;
            if (victim is not null)
            {
                var targetIsCapsule = await IsCapsuleAsync(victim, empire, cancellationToken);
                if (WeaponSafety.RequiredLevel(empire, targetIsCapsule) is not null)
                {
                    var context = await WeaponSafetyContextAsync(characterId, victim.CharacterId, targetIsCapsule, cancellationToken);
                    if (WeaponSafety.StopsFire(empire, level, context))
                    {
                        return ModuleResult.Refusal(WeaponSafety.Refusal, made.ModuleType, made.ModuleName);
                    }
                }
            }
            // An untargeted smartbomb can hit neutral pilots as well as legal opponents, so its pilot-damage safety stays target-independent.
            if (package.Action is SimAction.Smartbomb bomb && WeaponSafety.StopsFire(empire, level))
            {
                package = package with { Action = bomb with { HurtsPilots = false } };
            }
        }
        var reply = await AskSimAsync(id, () => _sim.ActivateAsync(id, package, cancellationToken), session);
        if (!reply.Accepted)
        {
            var refusal = ModuleResult.Refusal(reply.Reason == "ALREADY_ACTIVE" ? "MODULE_ALREADY_ACTIVE" : reply.Reason ?? "REFUSED", made.ModuleType, made.ModuleName);
            // a target out of reach is told with how far the module reaches and how far the target is
            return reply.Reason == "TARGET_OUT_OF_RANGE"
                ? refusal with { TargetType = reply.TargetType, ShipType = snapshot.Ship.TypeId, DesiredRange = reply.DesiredRange, ActualDistance = reply.ActualDistance }
                : refusal;
        }
        pilot.ActiveModules[moduleId] = ItemState.Active;
        await SendShipAsync(id, pilot, SimShips.VitalsOf(snapshot), cancellationToken);
        made.Accepted?.Invoke();
        // a module that may run in a gate cloak (canActivateInGateCloak: the warp core stabilizers and interdiction nullifiers) keeps the cloak of a jump
        EndInvulnerability(pilot, keepsJumpCloak: snapshot.World.Get(moduleId, ModuleAttributes.CanActivateInGateCloak) > 0);
        if (assistanceRequest is { } remoteRequest && recordAssistance is { } recordRemoteAssistance)
        {
            try
            {
                await recordRemoteAssistance(remoteRequest, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log?.Invoke($"crimewatch could not record remote assistance by {characterId} on {remoteRequest.Target}: {error.Message}");
            }
        }
        if (victim is not null && securityClass is { } crimeWhere && Aggression is { } record)
        {
            // what the attack makes of the attacker is crimewatch's; the module runs whatever it keeps
            try
            {
                var security = _systems.Get(pilot.SolarSystemId)?.Security ?? 0;
                await record(new AggressionRequest(characterId, victim.CharacterId, victim.ShipId, pilot.SolarSystemId, security, crimeWhere, await IsCapsuleAsync(victim, crimeWhere, cancellationToken)), cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log?.Invoke($"crimewatch could not record the attack of {characterId} on {victim.CharacterId}: {error.Message}");
            }
        }
        return ModuleResult.Done;
    }

    /// <summary>
    /// Told after a pilot's module was started at the ship of another pilot: who attacked whom, where, and whether the ship attacked is a capsule (<see cref="AggressionRequest"/>).
    /// Crimewatch (timers, flags, what a crime brings) hangs here.
    /// </summary>
    public Func<AggressionRequest, CancellationToken, Task>? Aggression { get; set; }


    public Func<AggressionRequest, int, CancellationToken, Task<PolicyDecision>>? RemoteAssistancePolicyOf { get; set; }

    public Func<AggressionRequest, CancellationToken, Task>? RecordRemoteAssistance { get; set; }


    /// <summary>Host supplies the persisted Crimewatch state used to enforce criminal movement restrictions.</summary>
    public Func<long, CancellationToken, Task<CombatState>>? CrimewatchStateOf { get; set; }

    /// <summary>Host calls this after a criminal with an active timer enters high-security space.</summary>
    public Func<long, long, double, CancellationToken, Task>? CriminalEnteredSystem { get; set; }

    /// <summary>The security status of a character (its relation of that kind; 0 when it is not set). The ship in space shows it in its slim item.</summary>
    public Func<long, CancellationToken, Task<double>>? SecurityStatusOf { get; set; }

    private async Task<double> SecurityStatusAsync(long characterId, CancellationToken cancellationToken) =>
        SecurityStatusOf is { } ask ? await ask(characterId, cancellationToken) : 0;

    /// <summary>The ship safety level of a character (set by the host that keeps them; full when it is not set). See <see cref="WeaponSafety"/>.</summary>
    public Func<long, CancellationToken, Task<int>>? SafetyLevelOf { get; set; }


    /// <summary>Resolves current Crimewatch flags and pair-specific legal permissions for a player ship target.</summary>
    public Func<long, long, CancellationToken, Task<WeaponTargetContext>>? WeaponTargetContextOf { get; set; }

    /// <summary>Whether the pilot's ship is a capsule. Only a low-security attack depends on it (a capsule there is a crime of a criminal), so it is only looked up then.</summary>
    private async Task<WeaponTargetContext> WeaponSafetyContextAsync(
        long attackerCharacterId,
        long targetCharacterId,
        bool targetIsCapsule,
        CancellationToken cancellationToken)
    {
        var ordinaryPlayerAttack = new WeaponTargetContext(
            AttackerIsPlayer: true,
            TargetIsPlayerProperty: true,
            TargetIsCapsule: targetIsCapsule);
        if (WeaponTargetContextOf is not { } read)
        {
            return ordinaryPlayerAttack;
        }

        try
        {
            var resolved = await read(attackerCharacterId, targetCharacterId, cancellationToken);
            return resolved with
            {
                AttackerIsPlayer = true,
                TargetIsPlayerProperty = true,
                TargetIsCapsule = targetIsCapsule,
                SamePilot = false
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"weapon safety facts for {attackerCharacterId} -> {targetCharacterId} failed: {error.Message}");
            return ordinaryPlayerAttack;
        }
    }

    private async Task<bool> IsCapsuleAsync(Pilot pilot, SecurityClass securityClass, CancellationToken cancellationToken)
    {
        if (!Security.IsLowSec(securityClass))
        {
            return false;
        }
        var snapshot = await _ships.LoadAsync(pilot.CharacterId, cancellationToken);
        return snapshot is not null && _statics.Type(snapshot.Ship.TypeId)?.GroupId == CapsuleGroup;
    }

    private const int CapsuleGroup = 29;

    /// <summary>Asks for an effect to stop at the end of the cycle it is in.</summary>
    public async Task<ModuleResult?> DeactivateAsync(ClientSession session, long moduleId, string effectName, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot) || session.CharacterId is not { } characterId)
        {
            return null;
        }
        var snapshot = await FlownShipAsync(pilot, cancellationToken);
        var module = snapshot?.Fitted.FirstOrDefault(m => m.Id == moduleId);
        var type = module is null ? null : _statics.Type(module.TypeId);
        var reply = await AskSimAsync(id, () => _sim.CommandAsync(id, SimCommands.Deactivate, [moduleId], cancellationToken), session);
        if (reply.Accepted)
        {
            Eve.Services.Space.Effects.ProjectedEffect.MutadaptiveSpool.StopAfterCurrentCycle(moduleId);
            // the simulation ends the effect at the end of its cycle; the host takes the module out of its numbers now, and the ship is sent again
            pilot.ActiveModules.TryRemove(moduleId, out _);
            await SendShipAsync(id, pilot, snapshot is null ? null : SimShips.VitalsOf(snapshot), cancellationToken);
        }
        return reply.Accepted ? ModuleResult.Done : ModuleResult.Refusal(reply.Reason == "NOT_ACTIVE" ? "MODULE_NOT_ACTIVE" : reply.Reason ?? "REFUSED", module?.TypeId ?? 0, type?.Name ?? "");
    }

    /// <summary>
    /// The ship a pilot flies as dogma has it now: the stored module states, with the modules the pilot has started (<see cref="Pilot.ActiveModules"/>) active.
    /// Null when the ship is not the one the pilot flies.
    /// </summary>
    private async Task<ShipSnapshot?> FlownShipAsync(Pilot pilot, CancellationToken cancellationToken)
    {
        if (await _ships.LoadAsync(pilot.CharacterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != pilot.ShipId)
        {
            return null;
        }
        foreach (var (moduleId, state) in pilot.ActiveModules)
        {
            snapshot.World.SetState(moduleId, state);
        }
        return snapshot;
    }

    /// <summary>
    /// The one path that tells the simulation what a ship in space is now: its resonances and numbers as dogma has them, with the modules the pilot
    /// has started. Every change of a module's state in space (activation, stop, online, offline) goes through here, so the simulation never keeps
    /// a number it was told before the change. <paramref name="before"/> is the ship's vitals before the change: when the change moves no resonance
    /// and no hit points, nothing is sent (null: send).
    /// </summary>
    private async Task SendShipAsync(ulong sessionId, Pilot pilot, SimVitals? before, CancellationToken cancellationToken)
    {
        if (!_sim.IsConnected || await FlownShipAsync(pilot, cancellationToken) is not { } snapshot)
        {
            return;
        }
        if (before is not null && SameVitals(before, SimShips.VitalsOf(snapshot)))
        {
            return;
        }
        _sim.ShipChanged(sessionId, SimShips.Of(snapshot, _statics, await SecurityStatusAsync(pilot.CharacterId, cancellationToken)));
    }

    /// <summary>Whether two ships take and resist the same (the lists are compared by value; a record's equality would compare them by reference).</summary>
    private static bool SameVitals(SimVitals a, SimVitals b) =>
        a.ShieldCapacity == b.ShieldCapacity && a.ArmorHp == b.ArmorHp && a.StructureHp == b.StructureHp
        && a.Resonances.Shield.SequenceEqual(b.Resonances.Shield) && a.Resonances.Armor.SequenceEqual(b.Resonances.Armor)
        && a.Resonances.Structure.SequenceEqual(b.Resonances.Structure);

    /// <inheritdoc/>
    public Task RefreshShipAsync(ClientSession session, CancellationToken cancellationToken) =>
        _pilots.TryGetValue((ulong)session.SessionId, out var pilot)
            ? SendShipAsync((ulong)session.SessionId, pilot, null, cancellationToken)
            : Task.CompletedTask;

    public async Task<ModuleResult?> OfflineAsync(ClientSession session, long moduleId, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot)) return null;
        if (!_sim.IsConnected) return ModuleResult.Refusal("SIM_UNAVAILABLE");
        var snapshot = await FlownShipAsync(pilot, cancellationToken);
        if (snapshot is not null)
            await _sim.SetHeatProfileAsync(id, SimShips.HeatProfileOf(snapshot, _statics), cancellationToken);
        await _sim.OfflineModuleAsync(id, moduleId, cancellationToken);
        pilot.ActiveModules.TryRemove(moduleId, out _);
        Eve.Services.Space.Effects.ProjectedEffect.MutadaptiveSpool.Reset(moduleId);
        return ModuleResult.Done;
    }

    public async Task<ModuleResult?> OverloadAsync(ClientSession session, long moduleId, int effectId, bool active, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.ContainsKey(id) || session.CharacterId is not { } characterId) return null;
        if (!_sim.IsConnected) return ModuleResult.Refusal("SIM_UNAVAILABLE");

        var snapshot = await _ships.LoadAsync(characterId, cancellationToken);
        var module = snapshot?.Fitted.FirstOrDefault(item => item.Id == moduleId);
        if (module is null || snapshot is null) return ModuleResult.Refusal("MODULE_NOT_FOUND");
        var type = _statics.Type(module.TypeId);
        var overload = type?.EffectIds.Select(_statics.Effect)
            .FirstOrDefault(effect => effect is { Category: EffectDef.Overload } && effect.Id == effectId);
        if (overload is null) return ModuleResult.Refusal("MODULE_NOT_OVERLOADABLE", module.TypeId, type?.Name ?? "");
        if (snapshot.World.Get(moduleId, 3) >= snapshot.World.Get(moduleId, 9)) return ModuleResult.Refusal("MODULE_INCAPACITATED", module.TypeId, type?.Name ?? "");
        if (snapshot.World.Item(moduleId) is not { State: >= ItemState.Online })
            return ModuleResult.Refusal("MODULE_NOT_ONLINE", module.TypeId, type?.Name ?? "");
        if (active && snapshot.Skills.FirstOrDefault(skill => skill.TypeId == 28164)?.Level is not > 0)
            return ModuleResult.Refusal("THERMODYNAMICS_SKILL_REQUIRED", module.TypeId, type?.Name ?? "");

        await _sim.SetHeatProfileAsync(id, SimShips.HeatProfileOf(snapshot, _statics), cancellationToken);
        await _sim.SetModuleOverheatAsync(id, moduleId, active, cancellationToken);
        return ModuleResult.Done;
    }

    public async Task<ModuleResult?> OverloadRackAsync(ClientSession session, long moduleId, bool active, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot)) return null;
        if (!_sim.IsConnected) return ModuleResult.Refusal("SIM_UNAVAILABLE");
        if (session.CharacterId is not { } characterId) return ModuleResult.Refusal("NO_SHIP_IN_SPACE");

        var snapshot = await _ships.LoadAsync(characterId, cancellationToken);
        var anchor = snapshot?.Fitted.FirstOrDefault(item => item.Id == moduleId);
        if (snapshot is null || anchor is null) return ModuleResult.Refusal("MODULE_NOT_FOUND");
        static int RackOf(int flag) => flag switch { >= 27 and <= 34 => 0, >= 19 and <= 26 => 1, >= 11 and <= 18 => 2, _ => -1 };
        var rack = RackOf(anchor.Flag);
        if (rack < 0) return ModuleResult.Refusal("MODULE_NOT_OVERLOADABLE", anchor.TypeId, _statics.Type(anchor.TypeId)?.Name ?? "");
        if (active && snapshot.Skills.FirstOrDefault(skill => skill.TypeId == 28164)?.Level is not > 0)
            return ModuleResult.Refusal("THERMODYNAMICS_SKILL_REQUIRED", anchor.TypeId, _statics.Type(anchor.TypeId)?.Name ?? "");

        bool Overloadable(Eve.Inventory.Item item) =>
            _statics.Type(item.TypeId)?.EffectIds.Select(_statics.Effect).Any(effect => effect?.Category == EffectDef.Overload) == true;
        var modules = snapshot.Fitted
            .Where(item => RackOf(item.Flag) == rack && snapshot.World.Item(item.Id) is { State: >= ItemState.Online } && Overloadable(item))
            .Where(item => snapshot.World.Get(item.Id, 3) < snapshot.World.Get(item.Id, 9))
            .OrderBy(item => item.Flag)
            .Select(item => item.Id)
            .ToArray();
        if (modules.Length == 0) return ModuleResult.Refusal("MODULE_NOT_OVERLOADABLE", anchor.TypeId, _statics.Type(anchor.TypeId)?.Name ?? "");

        await _sim.SetHeatProfileAsync(id, SimShips.HeatProfileOf(snapshot, _statics), cancellationToken);
        await _sim.SetRackOverheatAsync(id, rack, active, cancellationToken);
        return ModuleResult.Done with { ModuleIds = modules };
    }

    /// <summary>What the ship in space has taken now, from the simulation.</summary>
    public async Task<SpaceCondition?> ConditionAsync(ClientSession session, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.ContainsKey(id))
        {
            return null;
        }
        var reply = await AskSimAsync(id, () => _sim.CommandAsync(id, SimCommands.GetCondition, [], cancellationToken), session);
        return reply.Condition is { } condition ? new SpaceCondition(condition.Damage, condition.Charge, condition.ShieldCharge, condition.ArmorDamage) : null;
    }

    /// <summary>Mends the ship in space: the simulation tells the pilot and those who see it before it answers.</summary>
    public async Task<bool> HealAsync(ClientSession session, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.ContainsKey(id))
        {
            return false;
        }
        var reply = await AskSimAsync(id, () => _sim.CommandAsync(id, SimCommands.Heal, [], cancellationToken), session);
        return reply.Accepted;
    }

    // ── hulls nobody flies ───────────────────────────────────────────────────

    /// <summary>The item id of the first hull of <c>/deathtest</c>: the reference numbers its transient ships from about here (the ids of the items in the database are far below).</summary>
    private const long HullIdBase = 4_200_000_000_000_000;

    /// <summary>How far from the pilot the hulls of <c>/deathtest</c> come, how soon they blow up, how many there are when the pilot does not say (the reference's).</summary>
    public const double DeathTestRadiusMeters = 20_000;
    public const double DeathTestDelaySeconds = 2.0;

    private long _hulls;
    private ulong _spawnRequests;

    /// <summary>
    /// <c>buildRandomDirection</c> of the reference's <c>shipDestruction.js</c> (a direction near the base direction, turned by a random angle and lifted a little),
    /// with the base direction along x: the simulation, not the host, knows which way the pilot's ship faces, and where the hulls come is random anyway.
    /// </summary>
    private static double[] RandomDirection()
    {
        var angle = Random.Shared.NextDouble() * Math.PI * 2;
        var vertical = (Random.Shared.NextDouble() - 0.5) * 0.3;
        double[] direction = [1 + Math.Cos(angle), vertical, Math.Sin(angle)];
        var length = Math.Sqrt(direction.Sum(x => x * x));
        return length > 0 && double.IsFinite(length) ? [.. direction.Select(x => x / length)] : [1, 0, 0];
    }

    /// <summary><c>buildShipDeathPositions</c>: places 3 km to <paramref name="radius"/> from the anchor, no two within 1.5 km (48 tries each).</summary>
    private static List<double[]> HullPlaces(int count, double radius)
    {
        var places = new List<double[]>();
        var reach = Math.Max(3_000, radius);
        for (var index = 0; index < count; index++)
        {
            double[]? accepted = null;
            for (var attempt = 0; attempt < 48 && accepted is null; attempt++)
            {
                var direction = RandomDirection();
                var offset = 3_000 + Random.Shared.NextDouble() * Math.Max(0, reach - 3_000);
                double[] candidate = [direction[0] * offset, direction[1] * offset, direction[2] * offset];
                var collides = places.Any(other => Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(other[i] - candidate[i], 2))) < 1_500);
                if (!collides)
                {
                    accepted = candidate;
                }
            }
            if (accepted is null)
            {
                break;
            }
            places.Add(accepted);
        }
        return places;
    }

    public async Task<HullField?> DeathTestAsync(ClientSession session, int? shipTypeId, int count, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return null;
        }
        var snapshot = await _ships.LoadAsync(pilot.CharacterId, cancellationToken);
        var typeId = shipTypeId ?? snapshot?.Ship.TypeId ?? 0;
        // the wreck is the caller's to loot, as the reference has it
        var credit = snapshot is { } flown
            ? new SimCredit(flown.Character.Id, flown.Character.CorporationId, flown.Character.AllianceId ?? 0, flown.Character.FactionId ?? 0)
            : new SimCredit(pilot.CharacterId, 0, 0, 0);
        await _statics.EnsureTypesAsync([typeId], cancellationToken);
        if (_statics.Type(typeId) is not { } type)
        {
            return null;
        }
        var spawned = new List<long>();
        foreach (var place in HullPlaces(count, DeathTestRadiusMeters))
        {
            var shipId = HullIdBase + Interlocked.Increment(ref _hulls);
            var spawn = new SimSpawn(
                NextSpawnRequest(), pilot.SolarSystemId, SimShips.OfType(shipId, type), pilot.ShipId, place, [0, 0, 0], RandomDirection(), "",
                SimNpcIdentity.Default with { Hull = true });
            if (await SpawnAsync(spawn, "deathtest", cancellationToken))
            {
                spawned.Add(shipId);
            }
        }
        if (spawned.Count == 0)
        {
            return null;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(DeathTestDelaySeconds));
            foreach (var shipId in spawned)
            {
                _sim.Destroy(shipId, credit);
            }
        }, CancellationToken.None);
        return new HullField(type.Name, spawned.Count, DeathTestRadiusMeters, DeathTestDelaySeconds);
    }

    private ulong NextSpawnRequest() => Interlocked.Increment(ref _spawnRequests);

    /// <summary>Asks the simulation to make a ship that nobody flies; false when it refused (the reason is logged), a message for the client when it cannot be reached.</summary>
    private async Task<bool> SpawnAsync(SimSpawn spawn, string what, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await _sim.SpawnAsync(spawn, cancellationToken);
            if (reply.Session is not null)
            {
                return true;
            }
            _log?.Invoke($"{what}: the ship was refused: {reply.Error}");
            return false;
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"{what}: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
    }

    // ── NPCs ─────────────────────────────────────────────────────────────────

    /// <summary>The item id of the first NPC ship of <c>/npc</c> (transient, like the hulls of <c>/deathtest</c>, below the ids the simulation gives wrecks).</summary>
    private const long NpcIdBase = 4_100_000_000_000_000;

    /// <summary>The tree an NPC of <c>/npc</c> runs (the simulation's <c>BELT_RAT</c>, deviation 46): it waits until a ship comes within its range and then fights it.</summary>
    public const string NpcBehavior = "belt rat";

    /// <summary>The standings at which an NPC treats others as enemies or as friends (the reference gives every NPC these limits).</summary>
    private const long NpcHostileThreshold = -11;
    private const long NpcFriendlyThreshold = 11;

    /// <summary>How many ships an NPC can hold locked at least (the reference's <c>MIN_NATIVE_NPC_LOCK_SLOTS</c>).</summary>
    private const int NpcMinimumLockSlots = 1;

    private long _npcs;

    /// <summary>The NPCs that can be spawned (null when there are none).</summary>
    public NpcSelector? Npcs { get; init; }

    /// <summary>The rats of the asteroid belts a pilot arrives at (null: no belt has any).</summary>
    public BeltRats? BeltRats { get; init; }

    /// <summary>
    /// What <c>spreadMeters</c> of a batch of belt rats is in the reference (the chance either way of how far from the belt each comes): not the default one, which grows with the size of the batch.
    /// </summary>
    private const double BeltRatSpreadMeters = 8_000;

    private readonly SemaphoreSlim _beltGate = new(1, 1);
    private Task? _beltHulls;
    private long _beltGroups;

    /// <summary>What an NPC leaves when it is destroyed: the host makes its wreck with what its loot table gave (null: the scene makes an empty wreck).</summary>
    public NpcWrecks? NpcWrecks { get; init; }

    /// <summary>What the host knows of an NPC it made: what it was worth, what it was and where (the simulation tells it only that the ship is gone).</summary>
    private sealed record NpcShip(NpcProfile Profile, int TypeId, int SolarSystemId, long BeltId = 0, long Group = 0);

    private readonly ConcurrentDictionary<long, NpcShip> _npcShips = new();

    // ── bounties ─────────────────────────────────────────────────────────────

    private PgBountyStore? _bounties;
    private int _paying;

    /// <summary>Raised for each bounty that was paid into a wallet (the wallet's owner is told of its new balance by whoever announces wallets).</summary>
    public event Action<BountyPaid>? BountyPaidOut;

    /// <summary>
    /// Keeps the bounties NPCs are worth: a kill whose last blow was a pilot's goes to <paramref name="store"/>. What has come due is paid by <see cref="PayBountiesAsync"/>, which the
    /// host runs as a recurring job of the durable scheduler (<c>space.bounty.pay</c>); a test calls it.
    /// </summary>
    public void UseBounties(PgBountyStore store) => _bounties = store;

    /// <summary>Pays the bounties that are due (one payout at a time); each is raised as <see cref="BountyPaidOut"/>.</summary>
    public async Task<IReadOnlyList<BountyPaid>> PayBountiesAsync(CancellationToken cancellationToken = default)
    {
        if (_bounties is not { } store || Interlocked.Exchange(ref _paying, 1) == 1)
        {
            return [];
        }
        try
        {
            var paid = await store.PayDueAsync(_nowUnixMs(), cancellationToken);
            foreach (var payment in paid)
            {
                _log?.Invoke($"bounty paid: {payment.Amount} ISK for {payment.Kills} kill(s) to character {payment.CharacterId}");
                BountyPaidOut?.Invoke(payment);
                Announce("bounty.paid", new { characterId = payment.CharacterId, amount = payment.Amount, kills = payment.Kills }, $"char:{payment.CharacterId}");
            }
            return paid;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"the bounties could not be paid: {error.Message}");
            return [];
        }
        finally
        {
            Volatile.Write(ref _paying, 0);
        }
    }

    /// <summary>
    /// An NPC of ours was destroyed: when the last blow was a pilot's ship, its bounty goes to that pilot's character and the client is told what was added and
    /// when it will be paid (<c>OnBountyAddedToPayout</c>).
    /// </summary>
    private async Task FinishNpcDeathAsync(NpcShip npc, SimDestroyed destroyed)
    {
        var killer = _pilots.Values.FirstOrDefault(pilot => pilot.ShipId == destroyed.KillerShipId);
        await LeaveNpcWreckAsync(npc, destroyed, killer);
        try
        {
            await PayNpcBountyAsync(npc, destroyed, killer);
        }
        finally
        {
            var kill = new NpcKill(killer?.CharacterId ?? 0, npc.TypeId, npc.Profile, npc.SolarSystemId, destroyed.ShipId);
            lock (_npcDestroyedGate)
            {
                Raise(NpcDestroyed, kill, nameof(NpcDestroyed));
                Announce("npc.killed", new { characterId = kill.FinalBlowCharacterId, typeId = kill.NpcTypeId, profile = kill.Profile.Id, solarSystemId = kill.SolarSystemId, shipId = kill.ShipId }, $"system:{kill.SolarSystemId}");
            }
        }
    }

    private async Task PayNpcBountyAsync(NpcShip npc, SimDestroyed destroyed, Pilot? killer)
    {
        if (_bounties is not { } store || killer is null || NpcBounty.AmountOf(npc.Profile.Bounty) is not { } amount)
        {
            return;
        }
        var added = await store.RecordAsync(new BountyKill(killer.CharacterId, npc.SolarSystemId, npc.TypeId, destroyed.ShipId, amount), _nowUnixMs());
        if (!added.AlreadyRecorded)
        {
            // `util.KeyVal`; the payout time is a FILETIME, past what a JavaScript number holds, so it goes as a long
            killer.Session.SendNotification(
                "OnBountyAddedToPayout", "charid",
                Py.KeyVal(
                    ("enemyTypeID", new PyInt(added.EnemyTypeId)), ("amount", new PyInt((int)added.Amount)), ("payoutTime", new PyLong(added.PayoutTime)),
                    ("isModified", added.IsModified ? PyBool.True : PyBool.False)));
        }
    }

    /// <summary>
    /// The wreck of an NPC that asked the host to make it (<see cref="SimNpcIdentity.HostWreck"/>): an item of the pilot who dealt the last blow (of the NPC's corporation when no pilot did) with what
    /// its loot table gave, put in space where the ship was for that pilot to loot. A wreck the scene made (its id is told) already has nothing in it, and is left as it is.
    /// </summary>
    private async Task LeaveNpcWreckAsync(NpcShip npc, SimDestroyed destroyed, Pilot? killer)
    {
        if (NpcWrecks is not { } wrecks || destroyed.WreckId != 0 || destroyed.WreckKind is not { } kind)
        {
            return;
        }
        try
        {
            var character = killer is null ? null : await _ships.CharacterAsync(killer.CharacterId);
            var made = await wrecks.LeaveAsync(kind.TypeId, npc.Profile.LootTableId, character?.Id ?? npc.Profile.CorporationId, npc.SolarSystemId);
            var credit = character is null ? new SimCredit(0, 0, 0, 0) : new SimCredit(character.Id, character.CorporationId, character.AllianceId ?? 0, character.FactionId ?? 0);
            PlaceWreck(new SimPlaceWreck(npc.SolarSystemId, made.Wreck.Id, WreckGroup, kind, destroyed.Position, destroyed.Direction, destroyed.Radius, destroyed.ShipId, credit, made.Contents.Count == 0));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"the wreck of NPC ship {destroyed.ShipId} could not be made: {error.Message}");
        }
    }

    /// <summary><c>buildRandomUnitVector</c> of the reference's <c>npcAnchors.js</c>: a direction chosen evenly over the sphere.</summary>
    private static double[] RandomUnitVector()
    {
        var theta = Random.Shared.NextDouble() * Math.PI * 2;
        var u = Random.Shared.NextDouble() * 2 - 1;
        var planar = Math.Sqrt(Math.Max(0, 1 - u * u));
        return [Math.Cos(theta) * planar, u, Math.Sin(theta) * planar];
    }

    /// <summary>
    /// <c>buildSpawnStateForDefinition</c>: how far from the anchor the NPC of a batch comes (what its profile says, a random amount either way that grows with the
    /// size of the batch, and a step for each one before it so they are not on top of each other), at least a kilometre.
    /// </summary>
    internal static double NpcSpawnDistance(NpcProfile profile, int index, int total, double random) =>
        NpcSpawnDistance(Math.Max(1_000, profile.SpawnDistanceMeters), Math.Min(6_000, 800 + total * 600), NpcFormationSpacing, index, random);

    /// <summary>As above, from the distance, the window of chance either way and the step between ones of a batch that are given.</summary>
    internal static double NpcSpawnDistance(double baseDistance, double window, double step, int index, double random)
    {
        var jitter = (random * 2 - 1) * window;
        var spacing = Math.Min(12_000, index * step);
        return Math.Max(1_000, baseDistance + jitter + spacing);
    }

    /// <summary>The step between the NPCs of a batch (<c>formationSpacingMeters</c> of the reference's default).</summary>
    private const double NpcFormationSpacing = 1_250;

    /// <summary>
    /// What a batch of NPCs comes into space around: a ship (the batch is placed from where it is), or a place of the system (a belt). A distance of 0 is the profile's own;
    /// a window of chance either way that is not given grows with the size of the batch.
    /// </summary>
    private sealed record SpawnAnchor(int SolarSystemId, long ShipId, double[] Origin, double BaseDistance = 0, double? Window = null, long BeltId = 0, long Group = 0);

    /// <summary>The hulls of NPCs are known before they are armed, and the missiles the hulls launch with them.</summary>
    private async Task EnsureNpcTypesAsync(IEnumerable<int> shipTypes, CancellationToken cancellationToken)
    {
        var hulls = shipTypes.Distinct().ToArray();
        await _statics.EnsureTypesAsync(hulls, cancellationToken).ConfigureAwait(false);
        var missiles = hulls.Select(hull => _statics.Type(hull)).OfType<TypeDef>().Select(NpcArmament.MissileTypeOf).Where(type => type > 0).Distinct().ToArray();
        if (missiles.Length > 0)
        {
            await _statics.EnsureTypesAsync(missiles, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Spawns the NPCs of <paramref name="profiles"/> around <paramref name="anchor"/>, each armed with what its hull carries, and remembers what the host must know of
    /// them. What came into space is told: a batch that was cut short by the simulation is as long as what it spawned.
    /// </summary>
    private async Task<(List<(string Name, int Count)> Composition, int Unarmed)> SpawnGroupAsync(
        IReadOnlyList<NpcProfile> profiles,
        SpawnAnchor anchor,
        CancellationToken cancellationToken,
        long hostileResponseThreshold = NpcHostileThreshold,
        double aggressionRangeOverride = 0)
    {
        var composition = new List<(string Name, int Count)>();
        var unarmed = 0;
        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            var type = _statics.Type(profile.ShipTypeId)!;
            var shipId = NpcIdBase + Interlocked.Increment(ref _npcs);
            var weapon = NpcArmament.HullWeapon(type, shipId, _statics);
            var aggression = Math.Max(NpcArmament.AggressionRange(type, _statics), aggressionRangeOverride);
            var identity = new SimNpcIdentity(hostileResponseThreshold, NpcFriendlyThreshold, aggression, weapon is null ? [] : [weapon], null, HostWreck: NpcWrecks is not null);
            var hull = SimShips.OfType(shipId, type);
            var ship = hull with
            {
                OwnerId = profile.CorporationId, CorporationId = (int)profile.CorporationId, AllianceId = (int)profile.AllianceId, WarFactionId = profile.FactionId,
                SecurityStatus = profile.SecurityStatus,
                // an entity type has no lock range (it has `entityAttackRange` instead) and sometimes no lock count: with 0 of either it could never lock what it
                // attacks, so it locks as far as it attacks and holds at least one (`applyNativeNpcHullCombatOverrides` of the reference)
                MaxTargetRange = Math.Max(hull.MaxTargetRange, aggression),
                MaxLockedTargets = Math.Max(NpcMinimumLockSlots, hull.MaxLockedTargets),
            };
            var distance = NpcSpawnDistance(anchor.BaseDistance > 0 ? anchor.BaseDistance : Math.Max(1_000, profile.SpawnDistanceMeters), anchor.Window ?? Math.Min(6_000, 800 + profiles.Count * 600), NpcFormationSpacing, index, Random.Shared.NextDouble());
            var direction = RandomUnitVector();
            double[] place = [anchor.Origin[0] + direction[0] * distance, anchor.Origin[1] + direction[1] * distance, anchor.Origin[2] + direction[2] * distance];
            var spawn = new SimSpawn(NextSpawnRequest(), anchor.SolarSystemId, ship, anchor.ShipId, place, [0, 0, 0], RandomUnitVector(), NpcBehavior, identity);
            if (!await SpawnAsync(spawn, "npc", cancellationToken))
            {
                break;
            }
            _npcShips[shipId] = new NpcShip(profile, type.TypeId, anchor.SolarSystemId, anchor.BeltId, anchor.Group);
            unarmed += weapon is null ? 1 : 0;
            var name = profile.Name.Length > 0 ? profile.Name : profile.Id;
            var at = composition.FindIndex(part => part.Name == name);
            if (at < 0)
            {
                composition.Add((name, 1));
            }
            else
            {
                composition[at] = (name, composition[at].Count + 1);
            }
        }
        return (composition, unarmed);
    }

    /// <summary>
    /// A pilot's warp ended at a belt (<c>maybeSpawnAsteroidBeltRatsForArrival</c> of the reference): the belt may have a group of rats come to it, by the rolls the director makes. What came of the
    /// arrival is returned (null when the rats do not apply: there are none, the pilot is not in space or its system is not known). Arrivals are taken one at a time, so that two pilots at the
    /// same belt cannot both bring a group.
    /// </summary>
    internal async Task<BeltDecision?> BeltArrivalAsync(ulong session, long beltId, IReadOnlyList<double> beltPosition, CancellationToken cancellationToken = default)
    {
        if (BeltRats is not { } rats || !_pilots.TryGetValue(session, out var pilot) || _systems.Get(pilot.SolarSystemId) is not { } place)
        {
            return null;
        }
        await _beltGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // the hulls the rats can have are known before one is chosen
            if (_beltHulls is { IsFaulted: true })
            {
                _beltHulls = null;
            }
            await (_beltHulls ??= EnsureNpcTypesAsync(rats.PoolShipTypes(), CancellationToken.None)).ConfigureAwait(false);
            var system = new BeltSystem(place.Id, place.Security, place.RegionId, place.FactionId);
            var active = _npcShips.Values.Where(npc => npc.SolarSystemId == system.SolarSystemId && npc.BeltId == beltId && npc.Group != 0).Select(npc => npc.Group).Distinct().Count();
            var now = _nowUnixMs();
            var decision = rats.Decide(system, beltId, now, active, Random.Shared.NextDouble);
            if (decision is not { Outcome: BeltOutcome.Spawn, Plan: { } plan })
            {
                if (decision.Outcome == BeltOutcome.PlanFailed)
                {
                    _log?.Invoke($"no belt rat could be made for {decision.Faction} in system {system.SolarSystemId}");
                }
                return decision;
            }
            await EnsureNpcTypesAsync(plan.Profiles.Select(profile => profile.ShipTypeId), cancellationToken).ConfigureAwait(false);
            var anchor = new SpawnAnchor(system.SolarSystemId, 0, [.. beltPosition], rats.Config.SpawnDistanceMeters, BeltRatSpreadMeters, beltId, Interlocked.Increment(ref _beltGroups));
            var (composition, _) = await SpawnGroupAsync(plan.Profiles, anchor, cancellationToken).ConfigureAwait(false);
            if (composition.Count > 0)
            {
                rats.Spawned(system.SolarSystemId, beltId, now);
                _log?.Invoke($"belt {beltId} of system {system.SolarSystemId}: {plan.Faction} rats ({plan.Band}): {string.Join(", ", composition.Select(part => $"{part.Count} {part.Name}"))}");
            }
            return decision;
        }
        finally
        {
            _beltGate.Release();
        }
    }

    /// <summary>A scene has been made for a system (a ship has come into a system nobody was in): the NPCs the host has kept of it were in the one before, which is gone.</summary>
    private void ForgetNpcsOf(int solarSystemId)
    {
        foreach (var (id, npc) in _npcShips)
        {
            if (npc.SolarSystemId == solarSystemId)
            {
                _npcShips.TryRemove(id, out _);
            }
        }
    }

    public async Task<NpcSpawnReport> SpawnNpcsAsync(ClientSession session, string query, int amount, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return NpcSpawnReport.Failed(NpcSpawnReport.NotInSpace);
        }
        if (Npcs is not { } selector)
        {
            return NpcSpawnReport.Failed(NpcSpawnReport.SpawnFailed);
        }
        var (selection, selectionError, suggestions) = selector.Select(query);
        if (selection is null)
        {
            return NpcSpawnReport.Failed(selectionError!, suggestions);
        }
        await EnsureNpcTypesAsync(selection.ShipTypes, cancellationToken);
        var plan = selector.Roll(selection, amount, type => NpcHull.HasHitPoints(_statics.Type(type)), Random.Shared.NextDouble);
        if (!plan.Succeeded)
        {
            return NpcSpawnReport.Failed(plan.Error!, plan.Suggestions);
        }

        var (composition, unarmed) = await SpawnGroupAsync(plan.Profiles, new SpawnAnchor(pilot.SolarSystemId, pilot.ShipId, [0, 0, 0]), cancellationToken);
        return composition.Count == 0
            ? NpcSpawnReport.Failed(NpcSpawnReport.SpawnFailed)
            : new NpcSpawnReport(null, [], selection.Name, composition, plan.Profiles.Count, unarmed);
    }


    /// <summary>Spawns the authored response pool around an active high-sec criminal; its response timer guarantees the criminal ship is lost.</summary>
    public async Task<bool> RespondToCriminalAsync(
        long attackerCharacterId,
        long solarSystemId,
        long attackerShipId,
        CancellationToken cancellationToken = default)
    {
        var pilot = _pilots.Values.FirstOrDefault(candidate =>
            candidate.CharacterId == attackerCharacterId
            && candidate.SolarSystemId == solarSystemId
            && (attackerShipId <= 0 || candidate.ShipId == attackerShipId));
        if (pilot is null || pilot.TypeId == CharacterShips.CapsuleTypeId
            || Npcs is not { } selector)
        {
            return false;
        }

        var (selection, error, suggestions) = selector.Select("concord_response_fleet");
        if (selection is null)
        {
            _log?.Invoke($"CONCORD response pool is unavailable: {error}; {string.Join(", ", suggestions)}");
            return false;
        }
        await EnsureNpcTypesAsync(selection.ShipTypes, cancellationToken);
        var plan = selector.Roll(
            selection,
            3,
            type => NpcHull.HasHitPoints(_statics.Type(type)),
            Random.Shared.NextDouble);
        if (!plan.Succeeded)
        {
            _log?.Invoke($"CONCORD response could not be rolled: {plan.Error}");
            return false;
        }

        var position = await PositionOfAsync(pilot.Session, pilot.ShipId, cancellationToken);
        if (position is not { } targetPoint)
        {
            return false;
        }
        var (composition, _) = await SpawnGroupAsync(
            plan.Profiles,
            new SpawnAnchor(
                pilot.SolarSystemId,
                pilot.ShipId,
                [targetPoint.X, targetPoint.Y, targetPoint.Z],
                BaseDistance: 8_000,
                Window: 4_000),
            cancellationToken,
            hostileResponseThreshold: 0,
            aggressionRangeOverride: 50_000);
        if (composition.Count == 0)
        {
            return false;
        }

        var markedShip = pilot.ShipId;
        _concordDestroyed[markedShip] = 0;
        _timers.Set($"space:concord:mark:{markedShip}", TimeSpan.FromMinutes(2), () =>
        {
            _concordDestroyed.TryRemove(markedShip, out _);
        });
        var creditProfile = plan.Profiles[0];
        _timers.Set(
            $"space:concord:punishment:{attackerCharacterId}:{solarSystemId}",
            TimeSpan.FromSeconds(15),
            () => FinishConcordPunishmentAsync(
                attackerCharacterId,
                solarSystemId,
                new SimCredit(0, creditProfile.CorporationId, creditProfile.AllianceId, creditProfile.FactionId)));
        return true;
    }

    private Task FinishConcordPunishmentAsync(long characterId, long solarSystemId, SimCredit credit)
    {
        var current = _pilots.Values.FirstOrDefault(pilot =>
            pilot.CharacterId == characterId
            && pilot.SolarSystemId == solarSystemId
            && pilot.TypeId != CharacterShips.CapsuleTypeId);
        if (current is null)
        {
            return Task.CompletedTask;
        }
        _concordDestroyed[current.ShipId] = 0;
        _sim.Destroy(current.ShipId, credit);
        return Task.CompletedTask;
    }


    /// <summary>Spawns the configured CONCORD response fleet around an active criminal and schedules the ship loss after the response ships engage.</summary>


    /// <summary>One order about a pilot's modules or targets is asked of the simulation at a time (the answers are matched by what was asked); an unreachable simulation is a message the client shows.</summary>
    private async Task<CommandReply> AskSimAsync(ulong id, Func<Task<CommandReply>> ask, ClientSession session)
    {
        var gate = _targeting.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await ask();
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"modules of session {session.SessionId}: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Asks the simulation to dock the ship; the answer to the client's call is the time the docking was accepted, or nothing when it was not.</summary>
    public async Task<PyValue> DockAsync(ClientSession session, long stationId, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot))
        {
            return None;
        }
        var policy = await MovementPolicyAsync(pilot, PolicyAction.Dock, cancellationToken);
        if (!policy.Allowed)
        {
            throw MovementRefused(policy, "dock");
        }
        try
        {
            var reply = await _sim.CommandAsync(id, SimCommands.Dock, [stationId], cancellationToken);
            if (reply.Accepted)
            {
                return new PyLong(reply.At);
            }
            if (reply.ApproachDistance is { } distance)
            {
                // the ship is on its way to the station; the client is told how far it was (the reference rounds half up)
                throw MachoErrors.ThrowUserError("DockingApproach", ("distance", new PyInt((long)Math.Max(0, Math.Floor(distance + 0.5)))));
            }
            _log?.Invoke($"docking of session {session.SessionId} at {stationId} refused: {reply.Reason}");
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"docking of session {session.SessionId}: {error.Message}");
        }
        return None;
    }

    /// <summary>
    /// <c>CmdWarpToStuff</c> to a body of the system: the answer is nothing whether the ship goes or not, and a warp that cannot be done is a message
    /// the client shows ("You cannot warp there right now."). A ship that is in warp already is told so and nothing else happens. The first movement
    /// the simulation accepts ends the undocking protection, and a warp is one.
    /// </summary>
    public Task<PyValue> WarpAsync(ClientSession session, long targetId, double range, CancellationToken cancellationToken) =>
        WarpCommandAsync(session, SimCommands.WarpTo, [targetId, range], $"to {targetId}", cancellationToken);


    public Task<PyValue> WarpToPointAsync(
        ClientSession session,
        double x,
        double y,
        double z,
        double range,
        CancellationToken cancellationToken) =>
        WarpCommandAsync(session, SimCommands.WarpToPoint, [x, y, z, range], "to coordinates", cancellationToken);


    public Task<WarpCoordinates?> WarpTargetPointAsync(
        ClientSession session,
        long targetId,
        double range,
        CancellationToken cancellationToken) =>
        QueryPositionAsync(session, SimCommands.ResolveWarpTarget, [targetId, range], cancellationToken);

    public Task<WarpCoordinates?> PositionOfAsync(
        ClientSession session,
        long targetId,
        CancellationToken cancellationToken) =>
        QueryPositionAsync(session, SimCommands.PositionOf, [targetId], cancellationToken);

    private async Task<WarpCoordinates?> QueryPositionAsync(
        ClientSession session,
        byte command,
        double[] args,
        CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return null;
        }
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync((ulong)session.SessionId, command, args, cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"position query of session {session.SessionId}: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        return reply.Accepted && reply.Position.Count >= 3
            ? new WarpCoordinates(pilot.SolarSystemId, reply.Position[0], reply.Position[1], reply.Position[2])
            : null;
    }

    private async Task<PyValue> WarpCommandAsync(
        ClientSession session,
        byte command,
        double[] args,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return None;
        }
        var policy = await MovementPolicyAsync(pilot, PolicyAction.Warp, cancellationToken);
        if (!policy.Allowed)
        {
            throw MovementRefused(policy, "warp");
        }
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync((ulong)session.SessionId, command, args, cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"warp of session {session.SessionId}: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        if (reply.Accepted)
        {
            _warping[(ulong)session.SessionId] = 0;
            EndInvulnerability(pilot);
            return None;
        }
        _log?.Invoke($"warp of session {session.SessionId} {destination} refused: {reply.Reason}");
        if (reply.Reason == "IN_WARP")
        {
            TellShipInWarp(session);
            return None;
        }
        if (reply.Reason == "WARP_SCRAMBLED")
        {
            throw Notify("You cannot warp because you are warp scrambled.");
        }
        throw Notify("You cannot warp there right now.");
    }


    private async Task<PolicyDecision> MovementPolicyAsync(Pilot pilot, PolicyAction action, CancellationToken cancellationToken)
    {
        var securityClass = _systems.Get(pilot.SolarSystemId) is { } system
            ? Security.ClassFromLevel(system.Security)
            : SecurityClass.ZeroSec;
        var now = _nowUnixMs();
        var state = CrimewatchStateOf is { } readState
            ? await readState(pilot.CharacterId, cancellationToken)
            : CombatState.Idle;
        return PolicyEngine.Evaluate(new PolicyRequest(
            action,
            securityClass,
            new PolicyFacts(
                HasWeaponsTimer: state.HasWeaponsTimer(now),
                HasCriminalTimer: state.IsCriminal(now),
                ActorIsCapsule: pilot.TypeId == CharacterShips.CapsuleTypeId,
                HasSessionChangeTimer: action is not (PolicyAction.Dock or PolicyAction.GateJump)
                    && _transitions.SessionChangeActive((ulong)pilot.Session.SessionId))));
    }

    // ── jumping ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>CmdStargateJump(fromGate, toGate, shipID)</c>: the ship goes into the gate. The client is told the jump-out effect (the simulation's update),
    /// the queue of the system it goes to, and answers with the bound object and the time it can be used from again; the ship is in the other
    /// system a few seconds on (<see cref="FinishJumpAsync"/>). What cannot be done is one of the messages the reference tells.
    /// </summary>
    public async Task<PyValue> JumpAsync(ClientSession session, long gateId, long toGateId, long shipId, string? boundObjectId, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            throw JumpRefused("NOT_IN_SPACE");
        }
        if (shipId > 0 && shipId != pilot.ShipId)
        {
            throw JumpRefused("SHIP_ID_MISMATCH");
        }
        var policy = await MovementPolicyAsync(pilot, PolicyAction.GateJump, cancellationToken);
        if (!policy.Allowed)
        {
            throw MovementRefused(policy, "jump");
        }
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.Jump, [gateId, toGateId], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"jump of session {session.SessionId}: {error.Message}");
            throw JumpRefused("SCENE_NOT_FOUND");
        }
        if (!reply.Accepted || reply.DestinationSystem is not { } destination)
        {
            _log?.Invoke($"jump of session {session.SessionId} through {gateId} refused: {reply.Reason}");
            throw JumpRefused(reply.Reason);
        }
        SendJumpQueueUpdate(session, pilot.CharacterId, destination);
        return Py.Tuple(boundObjectId is null ? None : new PyStr(boundObjectId), new PyLong(Py.FileTimeFromUnixMs(_nowUnixMs() + SessionChangeCooldownMs)));
    }

    /// <summary>The message the client shows for a jump that cannot be done (the reference's mapping of what went wrong).</summary>
    private static MachoWrappedException JumpRefused(string? reason) => reason switch
    {
        "NOT_IN_SPACE" or "NO_SHIP_IN_SPACE" or "SHIP_NOT_FOUND" or "SHIP_ID_MISMATCH" or "WRONG_SOLAR_SYSTEM" => MachoErrors.ThrowUserError("DeniedShipChanged"),
        "STARGATE_NOT_FOUND" or "STARGATE_DESTINATION_MISMATCH" => MachoErrors.ThrowUserError("TargetingAttemptCancelled"),
        "TOO_FAR_FROM_STARGATE" => MachoErrors.ThrowUserError("CustomInfo", ("info", Py.Tuple(new PyInt(101), new PyStr("UI/Menusvc/MenuHints/NotWithingMaxJumpDist")))),
        "STARGATE_NOT_ACTIVE" => MachoErrors.ThrowUserError("CustomInfo", ("info", Py.Tuple(new PyInt(101), new PyStr("UI/GateIcons/GateClosed")))),
        "STARGATE_JUMP_IN_PROGRESS" => MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("Stargate jump already in progress."))),
        _ => MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("The stargate jump could not be completed."))),
    };

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
        try
        {
            switch (message)
            {
                case SimRemoteRepairCycle cycle:
                    HandleMutadaptiveRepairCycle(cycle);
                    break;
                case SimNotify notify when _pilots.TryGetValue(notify.SessionId, out var pilot):
                    Forward(pilot.Session, notify);
                    break;
                case SimAccepted accepted when _pilots.TryGetValue(accepted.SessionId, out var mover):
                    if (accepted.Command == SimCommands.Stop)
                        _warping.TryRemove(accepted.SessionId, out _);
                    if (accepted.Command != SimCommands.Dock)
                        EndInvulnerability(mover);
                    break;
                case SimArrived arrived when _pilots.ContainsKey(arrived.SessionId):
                    _warping.TryRemove(arrived.SessionId, out _);
                    _ = Task.Run(async () =>
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
                    _ = Task.Run(() => FinishDockingAsync(pilot, docked.StationId, docked.Condition));
                    break;
                case SimJumped jumped when _pilots.TryGetValue(jumped.SessionId, out var jumper):
                    _warping.TryRemove(jumped.SessionId, out _);
                    _ = Task.Run(() => FinishJumpAsync(jumper, jumped));
                    break;
                case SimDestroyed destroyed when _pilots.TryGetValue(destroyed.SessionId, out var victim) && victim.ShipId == destroyed.ShipId:
                    _warping.TryRemove(destroyed.SessionId, out _);
                    ForgetLockedTarget(destroyed.ShipId);
                    _lockedTargets.TryRemove(destroyed.SessionId, out _);
                    _ = Task.Run(() => FinishDeathAsync(victim, destroyed));
                    break;
                case SimDestroyed destroyed when _ejectedShips.TryRemove(destroyed.ShipId, out var abandoned):
                    ForgetLockedTarget(destroyed.ShipId);
                    _ = Task.Run(() => FinishAbandonedShipDeathAsync(abandoned, destroyed));
                    break;
                case SimDestroyed destroyed when _npcShips.TryRemove(destroyed.ShipId, out var npc):
                    ForgetLockedTarget(destroyed.ShipId);
                    _ = Task.Run(() => FinishNpcDeathAsync(npc, destroyed));
                    break;
                case SimChargeSpent spent when _pilots.TryGetValue(spent.SessionId, out var shooter):
                    _charges.Enqueue(spent.SessionId, shooter.CharacterId, spent.ModuleId, spent.RoundsLeft);
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




    private static void Forward(ClientSession session, SimNotify notify)
    {
        var args = MarshalCodec.Decode(notify.Args.ToArray()) is PyTuple tuple ? tuple.Items : [];
        session.SendNotification(notify.Name, notify.IdType, [.. args]);
    }

    // ── the end of a jump ────────────────────────────────────────────────────

    /// <summary>
    /// The ship has come out of the gate in the other system: the ship and the character are moved there and the client is told in the reference's
    /// order (the objects bound to the old system let go, the ship's item, the jump cloak, the session change). The client then binds the ballpark
    /// of the new system, and the simulation sends it the scene.
    /// </summary>
    private async Task FinishJumpAsync(Pilot pilot, SimJumped jumped)
    {
        var session = pilot.Session;
        var arrivalFileTime = 0L;
        try
        {
            var transition = await _transitions.ExecuteAsync(
                token =>
                {
                    var place = _systems.Get(jumped.ToSystem);
                    return _ships.JumpAsync(
                        pilot.CharacterId,
                        jumped.ToSystem,
                        place?.ConstellationId ?? 0,
                        place?.RegionId ?? 0,
                        token);
                },
                result => result.Moved is not null,
                async (_, token) =>
                {
                    if (!_pilots.Values.Any(other => other.SolarSystemId == jumped.ToSystem))
                        ForgetNpcsOf(jumped.ToSystem);
                    if (_systems.Get(jumped.ToSystem) is { } enteredSystem
                        && Security.IsHighSecOrHigher(Security.ClassFromLevel(enteredSystem.Security))
                        && CriminalEnteredSystem is { } criminalEntered)
                    {
                        try
                        {
                            await criminalEntered(pilot.CharacterId, jumped.ToSystem, enteredSystem.Security, CancellationToken.None);
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            _log?.Invoke($"criminal timer entry response for character {pilot.CharacterId} could not be dispatched: {error.Message}");
                        }
                    }
                },
                (result, _) =>
                {
                    var moved = result.Moved!;
                    arrivalFileTime = FileTimeNow();
                    var arrived = new Pilot(session, pilot.CharacterId, pilot.ShipId, pilot.TypeId,
                        jumped.ToSystem, arrivalFileTime + JumpCloakTicks, pilot.UndockedFrom, jumpCloak: true);
                    if (!_pilots.TryUpdate((ulong)session.SessionId, arrived, pilot))
                        return Task.FromResult(false);
                    pilot.End();
                    ArmProtection(arrived, _jumpCloak);
                    SystemEntered?.Invoke(jumped.ToSystem);
                    ReleaseBoundObjects(session);
                    var type = _statics.Type(moved.Ship.TypeId);
                    session.SendNotification(
                        "OnItemChange", "clientID",
                        InventoryWire.PackedRow(ShipRow(moved.Ship, type), new PyStr($"Undocking:{pilot.UndockedFrom}")),
                        new PyDict([new(new PyInt(3), InventoryWire.Id(moved.PreviousLocationId))]),
                        None);
                    session.SendNotification(
                        "OnJumpCloakUpdated", "shipid",
                        InventoryWire.Id(pilot.ShipId),
                        new PyLong(arrivalFileTime + JumpCloakTicks),
                        new PyLong(JumpCloakTicks));
                    var system = new PyInt(jumped.ToSystem);
                    session.Change(
                        ("solarsystemid", system),
                        ("solarsystemid2", system),
                        ("constellationid", new PyInt(moved.Character.ConstellationId)),
                        ("regionid", new PyInt(moved.Character.RegionId)),
                        ("locationid", system));
                    return Task.FromResult(true);
                },
                null,
                CancellationToken.None,
                (ulong)session.SessionId);
            if (transition.Moved is null)
                _log?.Invoke($"jump of character {pilot.CharacterId} to system {jumped.ToSystem} failed: {transition.Refusal}");
        }
        catch (Exception error)
        {
            _log?.Invoke($"jump of character {pilot.CharacterId} to system {jumped.ToSystem} failed: {error}");
        }
    }

    // ── docking ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The ship has docked (or is being brought home): what the reference tells the client at the end of a docking, in its order, with the
    /// ship in the station's hangar and the session in the station.
    /// </summary>
    private async Task FinishDockingAsync(Pilot pilot, long stationId, SimCondition? condition)
    {
        try
        {
            var session = pilot.Session;
            var transition = await _transitions.ExecuteAsync(
                async cancellationToken =>
                {
                    await _charges.CloseAsync((ulong)session.SessionId).ConfigureAwait(false);
                    var shipCondition = condition is null
                        ? null
                        : new ShipCondition(condition.Damage, condition.Charge, condition.ShieldCharge, condition.ArmorDamage, false);
                    return await _ships.DockAsync(pilot.CharacterId, stationId, shipCondition, cancellationToken);
                },
                result => result.Moved is not null,
                applySimulation: null,
                applySession: (result, token) =>
                {
                    if (!_pilots.TryRemove((ulong)session.SessionId, out _))
                        return Task.FromResult(false);
                    session.SendNotification("OnInvulnCancelled", "shipid", InventoryWire.Id(pilot.ShipId));
                    pilot.End();
                    ReleaseBoundObjects(session);
                    var moved = result.Moved!;
                    var type = _statics.Type(moved.Ship.TypeId);
                    session.SendNotification(
                        "OnItemsChanged", "charid",
                        new PyList([InventoryWire.PackedRow(ShipRow(moved.Ship, type))]),
                        ChangedFrom(moved),
                        Py.Tuple(new PyStr("Station"), InventoryWire.Id(stationId), new PyStr("StationHangar")));
                    session.Change(("stationid", new PyInt(stationId)), ("locationid", new PyInt(stationId)), ("solarsystemid", None));
                    return Task.FromResult(true);
                },
                notify: (_, _) =>
                {
                    session.SendNotification("OnDockingFinished", "charid", InventoryWire.Id(stationId));
                    return Task.CompletedTask;
                },
                cancellationToken: CancellationToken.None,
                sessionId: (ulong)session.SessionId);
            if (transition.Moved is null)
            {
                _log?.Invoke($"docking of character {pilot.CharacterId} at {stationId} failed: {transition.Refusal}");
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"docking of character {pilot.CharacterId} at {stationId} failed: {error}");
        }
    }

    // ── death ────────────────────────────────────────────────────────────────

    /// <summary>How long <c>/suicide</c> waits for the simulation and the character's items before it says it failed.</summary>
    private static readonly TimeSpan SelfDestructWait = TimeSpan.FromSeconds(20);

    public async Task<SelfDestruct?> SelfDestructAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return null;
        }
        if (await _ships.LoadAsync(pilot.CharacterId, cancellationToken) is not { } flown || flown.Ship.Id != pilot.ShipId)
        {
            return null;
        }
        var waiting = new TaskCompletionSource<SelfDestruct?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_deaths.TryAdd(pilot.ShipId, waiting))
        {
            return null; // it is being destroyed already
        }
        try
        {
            // the wreck is the pilot's own to loot (the host makes it when the simulation says the ship is gone), so nobody else is credited
            _sim.Destroy(pilot.ShipId, SimCredit.Nobody);
            return await waiting.Task.WaitAsync(SelfDestructWait, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _deaths.TryRemove(pilot.ShipId, out _);
        }
    }

    /// <summary>The place an item that is destroyed is said to go to (the client's junkyard).</summary>
    private const long Junkyard = 6;

    /// <summary>The group of a ship's wreck, and of a corpse (<c>Biomass</c>).</summary>
    private const int WreckGroup = 186;
    private const int CorpseGroup = 14;

    private static ItemRow RowOf(Item item, TypeDef? type) =>
        new(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : (int)Math.Min(item.Quantity, int.MaxValue), type?.GroupId ?? 0, type?.CategoryId ?? 0);

    /// <summary>
    /// The ship of a pilot was destroyed (the simulation has told the clients that watched it): what it held goes into a wreck or is destroyed, the pilot is in a
    /// capsule where the ship was, and the client is told, in the reference's order, of the objects that let go, the capsule, the modules that left, the items,
    /// the session change and the capsule's ball; then the wreck is put in space. What the simulation needs to go on (the capsule) is given to it first.
    /// </summary>
    private async Task FinishAbandonedShipDeathAsync(EjectedShip abandoned, SimDestroyed destroyed)
    {
        try
        {
            var seed = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"ship:{destroyed.ShipId}:{_nowUnixMs()}");
            var wreckTypeId = abandoned.ShipTypeId == CharacterShips.CapsuleTypeId
                ? null
                : destroyed.WreckKind?.TypeId;
            var death = await _ships.DestroyAbandonedShipAsync(
                abandoned.OwnerCharacterId,
                abandoned.SolarSystemId,
                destroyed.ShipId,
                wreckTypeId,
                seed);
            if (death is null)
            {
                _log?.Invoke($"unpiloted ship {destroyed.ShipId} was destroyed in space but is no longer owned by character {abandoned.OwnerCharacterId} at system {abandoned.SolarSystemId}");
                return;
            }

            if (abandoned.ShipTypeId != CharacterShips.CapsuleTypeId)
            {
                await _statics.EnsureTypesAsync([
                    .. death.Changes.Select(change => change.Item.TypeId),
                    .. death.Wreck is { } wreck ? new[] { wreck.TypeId } : []
                ]);

                var killer = destroyed.KillerShipId == 0
                    ? null
                    : _pilots.Values.FirstOrDefault(other => other.ShipId == destroyed.KillerShipId);
                var npcKiller = destroyed.KillerShipId != 0
                    && _npcShips.TryGetValue(destroyed.KillerShipId, out var npc)
                    ? npc
                    : null;
                var cause = npcKiller?.Profile.Id.Contains("concord_response", StringComparison.OrdinalIgnoreCase) == true
                    ? ShipLossCause.Concord
                    : killer is not null
                        ? ShipLossCause.Player
                        : npcKiller is not null
                            ? ShipLossCause.Npc
                            : ShipLossCause.Unknown;
                var loss = new ShipLoss(
                    abandoned.OwnerCharacterId,
                    death.Ship.Id,
                    death.Ship.TypeId,
                    cause,
                    abandoned.SolarSystemId,
                    cause == ShipLossCause.Player ? killer!.CharacterId : 0);
                Raise(ShipLost, loss, nameof(ShipLost));
                Announce(
                    "ship.lost",
                    new
                    {
                        characterId = loss.CharacterId,
                        shipId = loss.ShipId,
                        shipTypeId = loss.ShipTypeId,
                        cause = loss.Cause.ToString(),
                        solarSystemId = loss.SolarSystemId,
                        killerCharacterId = loss.KillerCharacterId
                    },
                    $"char:{loss.CharacterId}");
            }

            if (death.Wreck is { } left && destroyed.WreckKind is { } kind)
            {
                PlaceWreck(new SimPlaceWreck(
                    abandoned.SolarSystemId,
                    left.Id,
                    WreckGroup,
                    kind,
                    destroyed.Position,
                    destroyed.Direction,
                    destroyed.Radius,
                    destroyed.ShipId,
                    new SimCredit(abandoned.OwnerCharacterId, abandoned.OwnerCorporationId, 0, 0),
                    death.Changes.All(change => change.Destroyed)));
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"the loss of unpiloted ship {destroyed.ShipId} of character {abandoned.OwnerCharacterId} could not be processed: {error}");
        }
    }

    private void NotifyDeathClient(Pilot pilot, ShipDeath lost, SimDestroyed destroyed, Item? recoveredCapsule = null)
    {
        var session = pilot.Session;
        if (lost.Capsule is { } capsule)
            session.SendNotification(
                "OnPodOrCorvetteAssembly", "charid",
                InventoryWire.Id(capsule.Id), new PyInt(capsule.TypeId));
        if (lost.EscapeShip is { } escapeShip)
        {
            var fromEscapeBay = new PyDict([
                new(new PyInt(3), InventoryWire.Id(lost.Ship.Id)),
                new(new PyInt(4), new PyInt(CharacterShips.FrigateEscapeBayFlag))
            ]);
            session.SendNotification(
                "OnItemsChanged", "charid",
                new PyList([InventoryWire.PackedRow(RowOf(escapeShip, _statics.Type(escapeShip.TypeId)))]),
                fromEscapeBay,
                None);
        }
        var now = FileTimeNow();
        if (lost.Events.Count > 0)
            session.SendNotification(
                "OnMultiEvent", "clientID",
                Eve.Services.Ships.ShipNotifications.MultiEvent(
                    pilot.CharacterId, pilot.ShipId, lost.Events, now));
        foreach (var change in lost.Changes)
        {
            var type = _statics.Type(change.Item.TypeId);
            var changedItem = recoveredCapsule is { } recoveredItem && recoveredItem.Id == change.Item.Id ? recoveredItem : change.Item;
            var row = change.Destroyed ? changedItem with { LocationId = Junkyard } : changedItem;
            var from = change.Destroyed
                ? new PyDict([new(new PyInt(3), InventoryWire.Id(change.PreviousLocationId))])
                : new PyDict([
                    new(new PyInt(3), InventoryWire.Id(change.PreviousLocationId)),
                    new(new PyInt(4), new PyInt(change.PreviousFlag))
                ]);
            session.SendNotification(
                "OnItemsChanged", "charid",
                new PyList([InventoryWire.PackedRow(RowOf(row, type))]), from, None);
        }
        var shipType = _statics.Type(lost.Ship.TypeId);
        session.SendNotification(
            "OnItemsChanged", "charid",
            new PyList([InventoryWire.PackedRow(
                RowOf(lost.Ship with { LocationId = Junkyard }, shipType),
                new PyStr($"Undocking:{pilot.UndockedFrom}"))]),
            new PyDict([new(new PyInt(3), InventoryWire.Id(lost.Ship.LocationId))]),
            None);

    }

    private void NotifyDeathAfterSessionChange(Pilot pilot, ShipDeath lost, SimDestroyed destroyed)
    {
        var session = pilot.Session;
        session.SendNotification(
            "OnShipCosmeticsChanged", "clientID", InventoryWire.Id(pilot.ShipId), new PyDict([]));
        session.SendNotification(
            "OnShipCosmeticsChanged", "clientID", InventoryWire.Id(lost.PilotShip.Id), new PyDict([]));
        session.SendNotification(
            "OnCurrentShipSkinChange", "clientID", InventoryWire.Id(lost.PilotShip.Id), new PyInt(0));
        if (lost.Wreck is { } wreckItem && destroyed.WreckKind is not null)
        {
            session.SendNotification(
                "OnItemChange", "clientID",
                InventoryWire.PackedRow(RowOf(wreckItem, _statics.Type(wreckItem.TypeId)), new PyStr("")),
                new PyDict([new(new PyInt(3), new PyInt(0))]),
                None);
        }
    }

    private async Task FinishDeathAsync(Pilot pilot, SimDestroyed destroyed)
    {
        var id = (ulong)pilot.Session.SessionId;
        if (!_dying.TryAdd(pilot.ShipId, 0))
            return;
        pilot.End();
        ShipDeath? death = null;
        SelfDestruct? done = null;
        var handedOver = false;
        var simulationFailed = false;
        try
        {
            var session = pilot.Session;
            await _charges.CloseAsync(id).ConfigureAwait(false);
            if (await _ships.LoadAsync(pilot.CharacterId) is { } held
                && held.Ship.Id == pilot.ShipId
                && held.Ship.TypeId == CharacterShips.CapsuleTypeId)
            {
                done = await FinishPodDeathAsync(pilot, destroyed);
                handedOver = done is not null;
                return;
            }

            var seed = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"ship:{pilot.ShipId}:{_nowUnixMs()}");
            death = await _transitions.ExecuteAsync(
                token => _ships.DestroyShipAsync(
                    pilot.CharacterId, pilot.ShipId, destroyed.WreckKind?.TypeId, seed, token),
                result => result is not null,
                async (result, token) =>
                {
                    var lost = result!;
                    var survivor = lost.PilotShip;
                    await _statics.EnsureTypesAsync([
                        CharacterShips.CapsuleTypeId,
                        survivor.TypeId,
                        .. lost.Changes.Select(change => change.Item.TypeId),
                        .. lost.Wreck is { } wreck ? new[] { wreck.TypeId } : []
                    ]);

                    if (await _ships.LoadAsync(pilot.CharacterId, token) is not { } flown
                        || flown.Ship.Id != survivor.Id)
                    {
                        _log?.Invoke($"surviving ship {survivor.Id} of character {pilot.CharacterId} could not be loaded after death");
                        simulationFailed = true;
                        await _ships.RecoverFromSpaceAsync(pilot.CharacterId, PlaceOfStation);
                        return;
                    }

                    try
                    {
                        var refusal = await _sim.RespawnAsync(new SimRespawn(
                            id,
                            SimShips.Of(flown, _statics, await SecurityStatusAsync(pilot.CharacterId, token)),
                            destroyed.Position,
                            destroyed.Direction), token);
                        if (refusal is not null)
                        {
                            var survivorDescription = survivor.TypeId == CharacterShips.CapsuleTypeId
                                ? $"the capsule {survivor.Id}"
                                : $"surviving ship {survivor.Id}";
                            _log?.Invoke($"the simulation did not take {survivorDescription} of character {pilot.CharacterId}: {refusal}");
                            simulationFailed = true;
                            _sim.Leave(id, LeaveWhy.Docked);
                            await _ships.RecoverFromSpaceAsync(pilot.CharacterId, PlaceOfStation);
                            return;
                        }
                    }
                    catch (Exception error)
                    {
                        _log?.Invoke($"the simulation could not respawn surviving ship {survivor.Id} of character {pilot.CharacterId}: {error.Message}");
                        simulationFailed = true;
                        await _ships.RecoverFromSpaceAsync(pilot.CharacterId, PlaceOfStation);
                        return;
                    }

                    if (lost.Wreck is { } wreckItem && destroyed.WreckKind is { } kind)
                    {
                        PlaceWreck(new SimPlaceWreck(
                            pilot.SolarSystemId,
                            wreckItem.Id,
                            WreckGroup,
                            kind,
                            destroyed.Position,
                            destroyed.Direction,
                            destroyed.Radius,
                            pilot.ShipId,
                            new SimCredit(
                                lost.Character.Id,
                                lost.Character.CorporationId,
                                lost.Character.AllianceId ?? 0,
                                lost.Character.FactionId ?? 0),
                            lost.Changes.All(change => change.Destroyed)));
                    }
                },
                async (result, transitionToken) =>
                {
                    var lost = result!;
                    ReleaseBoundObjects(session);
                    if (simulationFailed)
                    {
                        if (await _ships.LoadAsync(pilot.CharacterId, transitionToken) is not { } recovered
                            || recovered.Ship.LocationId != recovered.Character.StationId)
                        {
                            session.Close();
                            return true;
                        }
                        _pilots.TryRemove(id, out _);
                        NotifyDeathClient(pilot, lost, destroyed, recovered.Ship);
                        var character = recovered.Character;
                        var stationId = new PyInt(character.StationId);
                        session.Change(
                            ("stationid", stationId),
                            ("stationid2", stationId),
                            ("solarsystemid", None),
                            ("solarsystemid2", new PyInt(character.SolarSystemId)),
                            ("constellationid", new PyInt(character.ConstellationId)),
                            ("regionid", new PyInt(character.RegionId)),
                            ("shipid", new PyLong(recovered.Ship.Id)),
                            ("locationid", stationId));
                        handedOver = true;
                        return true;
                    }

                    var survivor = lost.PilotShip;
                    var flying = new Pilot(
                        session, pilot.CharacterId, survivor.Id, survivor.TypeId, pilot.SolarSystemId, 0);
                    flying.End();
                    if (!_pilots.TryUpdate(id, flying, pilot))
                    {
                        await _ships.RecoverFromSpaceAsync(pilot.CharacterId, PlaceOfStation);
                        session.Close();
                        return true;
                    }
                    NotifyDeathClient(pilot, lost, destroyed);
                    session.Change(("shipid", new PyLong(survivor.Id)));
                    handedOver = true;
                    return true;
                },
                (result, _) =>
                {
                    var lost = result!;
                    RaiseShipLost(pilot, lost, destroyed);
                    NotifyDeathAfterSessionChange(pilot, lost, destroyed);
                    return Task.CompletedTask;
                },
                CancellationToken.None,
                id);

            if (death is null)
            {
                _log?.Invoke($"the ship {pilot.ShipId} of character {pilot.CharacterId} was destroyed in space but is not the character's ship in space any more");
                return;
            }
            if (handedOver)
            {
                done = new SelfDestruct(
                    death.Ship.Id,
                    new SelfDestructPart(
                        "Wreck",
                        death.Wreck is { } wreck ? _statics.Type(wreck.TypeId)?.Name : null,
                        death.Wreck?.Id),
                    new SelfDestructPart(
                        death.Capsule is null ? "Frigate Escape Bay" : "Capsule",
                        _statics.Type(death.PilotShip.TypeId)?.Name ?? "Capsule",
                        death.PilotShip.Id));
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"the death of the ship {pilot.ShipId} of character {pilot.CharacterId} could not be told: {error}");
        }
        finally
        {
            if (!handedOver && _pilots.TryRemove(id, out _))
                _sim.Leave(id, LeaveWhy.LoggedOff);
            _dying.TryRemove(pilot.ShipId, out _);
            if (_deaths.TryRemove(pilot.ShipId, out var waiting))
                waiting.TrySetResult(done);
        }
    }

    private void RaiseShipLost(Pilot pilot, ShipDeath death, SimDestroyed destroyed)
    {
        var killer = destroyed.KillerShipId == 0 ? null : _pilots.Values.FirstOrDefault(other => other.ShipId == destroyed.KillerShipId);
        var concord = _concordDestroyed.TryRemove(pilot.ShipId, out _)
            || (destroyed.KillerShipId != 0
                && _npcShips.TryGetValue(destroyed.KillerShipId, out var npc)
                && npc.Profile.Id.Contains("concord_response", StringComparison.OrdinalIgnoreCase));
        var cause = _deaths.ContainsKey(pilot.ShipId) ? ShipLossCause.SelfDestruct
            : concord ? ShipLossCause.Concord
            : killer is not null ? ShipLossCause.Player
            : destroyed.KillerShipId != 0 && _npcShips.ContainsKey(destroyed.KillerShipId) ? ShipLossCause.Npc
            : ShipLossCause.Unknown;
        var loss = new ShipLoss(pilot.CharacterId, death.Ship.Id, death.Ship.TypeId, cause, pilot.SolarSystemId, cause == ShipLossCause.Player ? killer!.CharacterId : 0);
        Raise(ShipLost, loss, nameof(ShipLost));
        Announce("ship.lost", new { characterId = loss.CharacterId, shipId = loss.ShipId, shipTypeId = loss.ShipTypeId, cause = loss.Cause.ToString(), solarSystemId = loss.SolarSystemId, killerCharacterId = loss.KillerCharacterId }, $"char:{loss.CharacterId}");
    }

    /// <summary>The corpse of a pilot whose capsule was destroyed: <c>resolveCorpseTypeID</c> of EVE.js (the client's <c>genderFemale = 0</c>; anything else leaves the male one).</summary>
    private const int CorpseTypeMale = 25;
    private const int CorpseTypeFemale = 29148;

    /// <summary>The rookie ship type of a race (what a pilot wakes in after its capsule is destroyed), set by the host that knows the races.</summary>
    public Func<int, int?>? RookieShipOfRace { get; set; }

    /// <summary>
    /// The capsule of a pilot was destroyed (the simulation has told the clients that watched it): the pilot wakes in the clone at its home station in a rookie
    /// ship, and its corpse is put in space where the capsule was. The client is told in the reference's order: the session change into the station, the corpse
    /// and the capsule as items, the capsule's place.
    /// </summary>
    private async Task<SelfDestruct?> FinishPodDeathAsync(Pilot pilot, SimDestroyed destroyed)
    {
        var id = (ulong)pilot.Session.SessionId;
        var session = pilot.Session;
        if (await _ships.CharacterAsync(pilot.CharacterId) is not { } character
            || RookieShipOfRace?.Invoke(character.RaceId) is not { } rookie)
        {
            _log?.Invoke($"the capsule of character {pilot.CharacterId} was destroyed but there is no rookie ship to wake the pilot in");
            return null;
        }
        var stationId = character.HomeStationId > 0 && _stations.Get(character.HomeStationId) is not null
            ? character.HomeStationId
            : character.StationId;
        if (_stations.Get(stationId) is not { } station)
        {
            _log?.Invoke($"the capsule of character {pilot.CharacterId} was destroyed but its clone's station {stationId} is not known");
            return null;
        }
        var corpseType = character.Gender == 0 ? CorpseTypeFemale : CorpseTypeMale;
        await _statics.EnsureTypesAsync([CharacterShips.CapsuleTypeId, corpseType, rookie]);

        var pod = await _transitions.ExecuteAsync(
            token => _ships.DestroyCapsuleAsync(
                pilot.CharacterId,
                pilot.ShipId,
                corpseType,
                character.Name,
                rookie,
                new HomePlace(station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId),
                token),
            result => result is not null,
            (result, token) =>
            {
                var death = result!;
                _pilots.TryRemove(id, out _);
                _sim.Leave(id, LeaveWhy.Docked);
                if (death.Corpse is { } corpse)
                {
                    var type = _statics.Type(corpse.TypeId);
                    var kind = new SimWreckKind(corpse.TypeId, corpse.CustomName ?? character.Name, type?.GraphicId ?? 0);
                    var radius = type?.Attributes.GetValueOrDefault(162) ?? 0;
                    PlaceWreck(new SimPlaceWreck(
                        pilot.SolarSystemId,
                        corpse.Id,
                        CorpseGroup,
                        kind,
                        destroyed.Position,
                        destroyed.Direction,
                        radius > 0 ? radius : 1.0,
                        0,
                        new SimCredit(character.Id, 0, 0, 0),
                        true));
                }
                return Task.CompletedTask;
            },
            (result, _) =>
            {
                var death = result!;
                ReleaseBoundObjects(session);
                var system = new PyInt(station.SolarSystemId);
                session.Change(
                    ("stationid", new PyInt(station.Id)),
                    ("stationid2", new PyInt(station.Id)),
                    ("solarsystemid", None),
                    ("solarsystemid2", system),
                    ("constellationid", new PyInt(station.ConstellationId)),
                    ("regionid", new PyInt(station.RegionId)),
                    ("shipid", new PyLong(death.Ship.Id)),
                    ("locationid", new PyInt(station.Id)));
                return Task.FromResult(true);
            },
            (result, _) =>
            {
                var death = result!;
                if (death.Corpse is { } corpse)
                {
                    session.SendNotification(
                        "OnItemChange", "clientID",
                        InventoryWire.PackedRow(RowOf(corpse, _statics.Type(corpse.TypeId)), new PyStr("")),
                        new PyDict([new(new PyInt(3), new PyInt(0))]),
                        None);
                }
                session.SendNotification(
                    "OnItemChange", "clientID",
                    InventoryWire.PackedRow(
                        RowOf(death.Capsule with { LocationId = Junkyard }, _statics.Type(death.Capsule.TypeId)),
                        new PyStr("")),
                    new PyDict([new(new PyInt(3), InventoryWire.Id(death.Capsule.LocationId))]),
                    None);
                session.SendNotification(
                    "OnCfgDataChanged", "charid", new PyStr("evelocations"),
                    new PyList([
                        InventoryWire.Id(death.Capsule.Id),
                        new PyStr(_statics.Type(death.Capsule.TypeId)?.Name ?? "Capsule"),
                        new PyInt(death.Capsule.LocationId),
                        new PyInt(0),
                        new PyInt(0),
                        new PyInt(0),
                        None
                    ]));
                return Task.CompletedTask;
            },
            CancellationToken.None,
            id);

        return pod is null
            ? null
            : new SelfDestruct(
                pod.Capsule.Id,
                new SelfDestructPart("Corpse", pod.Corpse?.CustomName, pod.Corpse?.Id),
                new SelfDestructPart("Ship", _statics.Type(pod.Ship.TypeId)?.Name, pod.Ship.Id));
    }

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

    /// <summary>The simulation was lost: every ship it held is gone, so each is brought home to the station its character last was at.</summary>
    private void OnLost()
    {
        _warping.Clear();
        Eve.Services.Space.Effects.ProjectedEffect.MutadaptiveSpool.ResetAll();
        foreach (var pilot in _pilots.Values.ToArray())
        {
            _ = Task.Run(async () =>
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

    private const string SafeLogoffBlockedLabel = "UI/Inflight/SafeLogoff/ConditionsFailedHeader";
    private static readonly TimeSpan SafeLogoffDuration = TimeSpan.FromSeconds(30);

    private static PyValue SafeLogoffRefusal() => new PyList([new PyStr(SafeLogoffBlockedLabel)]);

    public async ValueTask<PyValue> SafeLogoffAsync(ClientSession session, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        var readBlockers = LifecycleBlockers;
        if (readBlockers is null
            || !_pilots.TryGetValue(id, out var pilot) || !IsInSpace(session)
            || session.GetInt("shipid") != pilot.ShipId
            || _dying.ContainsKey(pilot.ShipId) || _deaths.ContainsKey(pilot.ShipId)
            || pilot.JumpCloak || _transitions.SessionChangeActive(id) || HasTargetLocks(id, pilot.ShipId)
            || await _ships.LoadAsync(pilot.CharacterId, cancellationToken) is not { } snapshot
            || snapshot.Ship.Id != pilot.ShipId
            || snapshot.Fitted.Any(module => snapshot.StateOf(module.Id) is ItemState.Active or ItemState.Overloaded))
        {
            return SafeLogoffRefusal();
        }

        IReadOnlyList<string> blockers;
        try
        {
            blockers = await readBlockers(ShipLifecycleAction.SafeLogoff, session, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _log?.Invoke($"safe logoff conditions for session {session.SessionId} could not be checked: {error.Message}");
            return SafeLogoffRefusal();
        }
        if (blockers.Count != 0)
            return new PyList([.. blockers.Select(label => new PyStr(label))]);

        if (_safeLogoffs.TryAdd(id, 0))
        {
            _timers.Set($"space.safe-logoff:{id}", SafeLogoffDuration, () =>
            {
                if (_safeLogoffs.TryRemove(id, out _))
                    session.Close();
            });
        }
        return new PyList([]);
    }

    public PyValue AbortSafeLogoff(ClientSession session)
    {
        CancelSafeLogoff(session);
        return None;
    }

    public bool CancelSafeLogoff(ClientSession session)
    {
        var id = (ulong)session.SessionId;
        if (!_safeLogoffs.TryRemove(id, out _))
            return false;
        _timers.Cancel($"space.safe-logoff:{id}");
        return true;
    }

    private bool HasTargetLocks(ulong sessionId, long shipId) =>
        _lockedTargets.TryGetValue(sessionId, out var own) && own.Count > 0
        || _lockedTargets.Any(entry => entry.Key != sessionId
            && _pilots.ContainsKey(entry.Key)
            && entry.Value.Contains(shipId));

    private bool IsTargetedByAnotherPlayer(ulong sessionId, long shipId) =>
        _lockedTargets.Any(entry => entry.Key != sessionId
            && _pilots.ContainsKey(entry.Key)
            && entry.Value.Contains(shipId));

    private void StartShipSessionChange(ulong sessionId) => _transitions.StartSessionChange(sessionId);

    private void OnSessionClosed(ClientSession session)
    {
        var id = (ulong)session.SessionId;
        CancelSafeLogoff(session);
        _transitions.EndSessionChange(id);
        _lockedTargets.TryRemove(id, out _);
        _targeting.TryRemove(id, out _); // not disposed: an order may still be waiting on it
        if (_pilots.TryRemove(id, out var pilot))
        {
            pilot.End();
            _sim.Leave(id, LeaveWhy.LoggedOff);
            _ = Task.Run(async () =>
            {
                try
                {
                    // the ship is put back with what its guns hold: the rounds spent in space are written first
                    await _charges.CloseAsync(id).ConfigureAwait(false);
                    await _ships.RecoverFromSpaceAsync(pilot.CharacterId, PlaceOfStation);
                }
                catch (Exception error)
                {
                    _log?.Invoke($"the ship of character {pilot.CharacterId} could not be put back after the session ended: {error.Message}");
                }
            });
        }
    }
}


public sealed class TransitionRuntime
{
    private static readonly TimeSpan SessionChangeDuration = TimeSpan.FromSeconds(10);
    private readonly LocalTimers _timers;
    private readonly ConcurrentDictionary<ulong, byte> _sessionChanges = new();

    public TransitionRuntime(LocalTimers timers, EventBus? events = null)
    {
        _timers = timers;
        Events = events;
    }

    public EventBus? Events { get; set; }

    public bool SessionChangeActive(ulong sessionId) => _sessionChanges.ContainsKey(sessionId);

    public void StartSessionChange(ulong sessionId)
    {
        _sessionChanges[sessionId] = 0;
        _timers.Set($"space.transition.session-change:{sessionId}", SessionChangeDuration,
            () => _sessionChanges.TryRemove(sessionId, out _));
    }

    public void EndSessionChange(ulong sessionId)
    {
        _timers.Cancel($"space.transition.session-change:{sessionId}");
        _sessionChanges.TryRemove(sessionId, out _);
    }

    public async Task WriteEventAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        Eve.Ships.ShipTransitionEvent transition,
        CancellationToken cancellationToken)
    {
        if (Events is { } events)
        {
            await events.PublishAsync(
                connection,
                transaction,
                "space.transition",
                transition,
                $"{transition.Kind}:{transition.CharacterId}:{transition.ShipId}:{transition.RelatedShipId}",
                cancellationToken);
        }
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> persist,
        Func<T, bool> committed,
        Func<T, CancellationToken, Task>? applySimulation,
        Func<T, CancellationToken, Task<bool>>? applySession,
        Func<T, CancellationToken, Task>? notify,
        CancellationToken cancellationToken,
        ulong? sessionId = null)
    {
        var result = await persist(cancellationToken);
        if (!committed(result))
            return result;

        try
        {
            if (applySimulation is not null)
                await applySimulation(result, cancellationToken);
            if (applySession is not null && !await applySession(result, cancellationToken))
                return result;
            if (sessionId is { } id)
                StartSessionChange(id);
            if (notify is not null)
                await notify(result, cancellationToken);
            return result;
        }
        finally
        {
            Events?.Wake();
        }
    }
}
