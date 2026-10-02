using System.Collections.Concurrent;
using Eve.Inventory;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Macho;
using Eve.Marshal;
using Eve.Npc;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;
using Eve.Universe;

namespace Eve.Services.Space;

/// <summary>
/// The pilots that are in space, and everything the service host does around the simulation: sending a ship out (the item, the session,
/// the notifications), forwarding what the simulation says to the client that plays the ship, and bringing the ship home when it docks, or
/// when the simulation is lost. What happens in space itself (motion, the balls, the stamps) is the simulation's (ADR 0007).
/// </summary>
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

    private sealed class Pilot(ClientSession session, long characterId, long shipId, int solarSystemId, long invulnerableUntil, long undockedFrom = 0, bool jumpCloak = false)
    {
        public ClientSession Session { get; } = session;

        public long CharacterId { get; } = characterId;

        public long ShipId { get; } = shipId;

        public int SolarSystemId { get; } = solarSystemId;

        /// <summary>A FILETIME: until when the ship cannot be hurt, unless a command of its pilot breaks it first.</summary>
        public long InvulnerableUntil { get; } = invulnerableUntil;

        /// <summary>The station the ship undocked from (the reference keeps saying so in the custom info of the ship's item).</summary>
        public long UndockedFrom { get; } = undockedFrom;

        /// <summary>Whether the protection is the cloak of a ship that has come through a stargate (its end is told with one more notification).</summary>
        public bool JumpCloak { get; } = jumpCloak;

        private int _ended;

        /// <summary>The timer that ends the protection when it runs out, if the pilot has not ended it by then.</summary>
        public Timer? Expiry { get; set; }

        /// <summary>Marks the protection ended; false when it had ended already (each ending is told once).</summary>
        public bool End()
        {
            Expiry?.Dispose();
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
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _targeting = new();
    /// <summary>The sessions whose ship's destruction is being told (each is told once).</summary>
    private readonly ConcurrentDictionary<ulong, byte> _dying = new();
    /// <summary>Who waits to hear what became of a ship they had destroyed (<c>/suicide</c>), by ship.</summary>
    private readonly ConcurrentDictionary<long, TaskCompletionSource<SelfDestruct?>> _deaths = new();
    private readonly TimeSpan _protection;
    private readonly TimeSpan _jumpCloak;
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<long, (long FileTime, long ExpiresAtMs)>> _queues = new();
    /// <summary>The rounds the guns of the pilots in space have spent, written in the order the simulation tells them, a lane for each session.</summary>
    private readonly ChargeSpentQueue _charges;

    /// <param name="protection">How long a ship that has just undocked is protected if its pilot does not fly it first (the client is told when it ends); <see cref="Timeout.InfiniteTimeSpan"/> for never.</param>
    /// <param name="jumpCloak">How long a ship that has come through a stargate stays cloaked if its pilot does not fly it first.</param>
    public SpaceSessions(ISimulation sim, CharacterShips ships, DogmaStatic statics, StationCatalog stations, SolarSystemCatalog systems, Func<long> nowUnixMs, Action<string>? log = null, TimeSpan? protection = null, TimeSpan? jumpCloak = null)
    {
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
        _bountyTimer?.Dispose();
        _sim.Received -= OnReceived;
        _sim.Lost -= OnLost;
        // the host stops: the rounds that were spent are written first
        _charges.CloseAllAsync().GetAwaiter().GetResult();
    }

    private static PyValue None => PyNone.Instance;

    private long FileTimeNow() => Py.FileTimeFromUnixMs(_nowUnixMs());

    /// <summary>Whether the session's ship is in space.</summary>
    public bool IsInSpace(ClientSession session) => _pilots.ContainsKey((ulong)session.SessionId);

    /// <summary>The solar system the session's ship is in, or null when it is not in space.</summary>
    public int? SolarSystemOf(ClientSession session) => _pilots.TryGetValue((ulong)session.SessionId, out var pilot) ? pilot.SolarSystemId : null;

    private static MachoWrappedException Notify(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));

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
        var spec = SimShips.Of(snapshot, _statics);
        spec = spec with { Condition = (spec.Condition ?? SimCondition.Full) with { ShieldCharge = 1, Charge = 1 } };
        var systemId = snapshot.Character.SolarSystemId;

        var transition = await _ships.UndockAsync(characterId, cancellationToken);
        if (transition.Moved is not { } moved)
        {
            _log?.Invoke($"undock of character {characterId} refused: {transition.Refusal}");
            return None;
        }

        string? refusal;
        try
        {
            refusal = await _sim.EnterAsync(new SimEnter((ulong)session.SessionId, systemId, EnterHow.Undock, stationId, spec), cancellationToken);
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

        var now = FileTimeNow();
        var pilot = new Pilot(session, characterId, moved.Ship.Id, systemId, now + UndockInvulnerabilityTicks, stationId);
        if (!_pilots.Values.Any(other => other.SolarSystemId == systemId))
        {
            ForgetNpcsOf(systemId);
        }
        _pilots[(ulong)session.SessionId] = pilot;
        SystemEntered?.Invoke(systemId);
        session.Closed += () => OnSessionClosed(session);
        pilot.Expiry = new Timer(_ => ProtectionRanOut(pilot), null, _protection, Timeout.InfiniteTimeSpan);

        var type = _statics.Type(moved.Ship.TypeId);
        session.SendNotification(
            "OnItemsChanged", "charid",
            new PyList([InventoryWire.PackedRow(ShipRow(moved.Ship, type), new PyStr($"Undocking:{stationId}"))]),
            ChangedFrom(moved), None);
        ReleaseBoundObjects(session);
        session.Change(("stationid", None), ("locationid", new PyInt(systemId)), ("solarsystemid", new PyInt(systemId)));
        session.SendNotification("OnInvulunOnUndockingUpdated", "shipid", InventoryWire.Id(pilot.ShipId), new PyLong(pilot.InvulnerableUntil), new PyLong(UndockInvulnerabilityTicks));

        return Py.Tuple(boundObjectId is null ? None : new PyStr(boundObjectId), new PyLong(Py.FileTimeFromUnixMs(_nowUnixMs() + SessionChangeCooldownMs)));
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
        if (_pilots.ContainsKey((ulong)session.SessionId))
        {
            _sim.Command((ulong)session.SessionId, code, args);
        }
    }

    private void EndInvulnerability(Pilot pilot)
    {
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
        if (order == TargetingOrder.Lock)
        {
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
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot) || session.CharacterId is not { } characterId)
        {
            return null;
        }
        // a gun is started with the rounds it has left: what was spent a moment ago is written first
        await _charges.FlushAsync(id).ConfigureAwait(false);
        if (await _ships.LoadAsync(characterId, cancellationToken) is not { } snapshot)
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
        // the safety of the pilot: a weapon is not started at the ship of another pilot where that would be a crime
        var attack = package.Turret is not null || package.Action is SimAction.Remote { What: SimRemote.Neutralize or SimRemote.Drain or SimRemote.Web or SimRemote.Paint or SimRemote.Damp or SimRemote.Scramble or SimRemote.Jam };
        if (attack
            && _pilots.Values.Any(other => other.ShipId == package.TargetId && other.CharacterId != characterId)
            && _systems.Get(pilot.SolarSystemId) is { } where
            && WeaponSafety.StopsFire(Security.ClassFromLevel(where.Security)))
        {
            return ModuleResult.Refusal(WeaponSafety.Refusal, made.ModuleType, made.ModuleName);
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
        EndInvulnerability(pilot);
        return ModuleResult.Done;
    }

    /// <summary>Asks for an effect to stop at the end of the cycle it is in.</summary>
    public async Task<ModuleResult?> DeactivateAsync(ClientSession session, long moduleId, string effectName, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.ContainsKey(id) || session.CharacterId is not { } characterId)
        {
            return null;
        }
        var snapshot = await _ships.LoadAsync(characterId, cancellationToken);
        var module = snapshot?.Fitted.FirstOrDefault(m => m.Id == moduleId);
        var type = module is null ? null : _statics.Type(module.TypeId);
        var reply = await AskSimAsync(id, () => _sim.CommandAsync(id, SimCommands.Deactivate, [moduleId], cancellationToken), session);
        return reply.Accepted ? ModuleResult.Done : ModuleResult.Refusal(reply.Reason == "NOT_ACTIVE" ? "MODULE_NOT_ACTIVE" : reply.Reason ?? "REFUSED", module?.TypeId ?? 0, type?.Name ?? "");
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
    private Timer? _bountyTimer;
    private int _paying;

    /// <summary>Raised for each bounty that was paid into a wallet (the wallet's owner is told of its new balance by whoever announces wallets).</summary>
    public event Action<BountyPaid>? BountyPaidOut;

    /// <summary>
    /// Keeps the bounties NPCs are worth: a kill whose last blow was a pilot's goes to <paramref name="store"/>, and what has come due is paid every
    /// <paramref name="payEvery"/> (not at all when null: a test pays by <see cref="PayBountiesAsync"/>).
    /// </summary>
    public void UseBounties(PgBountyStore store, TimeSpan? payEvery = null)
    {
        _bounties = store;
        _bountyTimer?.Dispose();
        _bountyTimer = payEvery is { } every ? new Timer(_ => _ = PayBountiesAsync(), null, TimeSpan.Zero, every) : null;
    }

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
            Raise(NpcDestroyed, new NpcKill(killer?.CharacterId ?? 0, npc.TypeId, npc.Profile, npc.SolarSystemId, destroyed.ShipId), nameof(NpcDestroyed));
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

    /// <summary>
    /// Spawns the NPCs of <paramref name="profiles"/> around <paramref name="anchor"/>, each armed with what its hull carries, and remembers what the host must know of
    /// them. What came into space is told: a batch that was cut short by the simulation is as long as what it spawned.
    /// </summary>
    private async Task<(List<(string Name, int Count)> Composition, int Unarmed)> SpawnGroupAsync(IReadOnlyList<NpcProfile> profiles, SpawnAnchor anchor, CancellationToken cancellationToken)
    {
        var composition = new List<(string Name, int Count)>();
        var unarmed = 0;
        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            var type = _statics.Type(profile.ShipTypeId)!;
            var shipId = NpcIdBase + Interlocked.Increment(ref _npcs);
            var weapon = NpcArmament.HullWeapon(type, shipId, _statics);
            var aggression = NpcArmament.AggressionRange(type, _statics);
            var identity = new SimNpcIdentity(NpcHostileThreshold, NpcFriendlyThreshold, aggression, weapon is null ? [] : [weapon], null, HostWreck: NpcWrecks is not null);
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
            await (_beltHulls ??= _statics.EnsureTypesAsync(rats.PoolShipTypes(), CancellationToken.None)).ConfigureAwait(false);
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
            await _statics.EnsureTypesAsync(plan.Profiles.Select(profile => profile.ShipTypeId), cancellationToken).ConfigureAwait(false);
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
        await _statics.EnsureTypesAsync(selection.ShipTypes, cancellationToken);
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
        if (!_pilots.ContainsKey((ulong)session.SessionId))
        {
            return None;
        }
        try
        {
            var reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.Dock, [stationId], cancellationToken);
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
    public async Task<PyValue> WarpAsync(ClientSession session, long targetId, double range, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return None;
        }
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.WarpTo, [targetId, range], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"warp of session {session.SessionId}: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        if (reply.Accepted)
        {
            EndInvulnerability(pilot);
            return None;
        }
        _log?.Invoke($"warp of session {session.SessionId} to {targetId} refused: {reply.Reason}");
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

    private void OnReceived(SimMessage message)
    {
        try
        {
            switch (message)
            {
                case SimNotify notify when _pilots.TryGetValue(notify.SessionId, out var pilot):
                    Forward(pilot.Session, notify);
                    break;
                case SimAccepted { Command: not SimCommands.Dock } accepted when _pilots.TryGetValue(accepted.SessionId, out var mover):
                    EndInvulnerability(mover);
                    break;
                case SimArrived arrived when _pilots.ContainsKey(arrived.SessionId):
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
                    _ = Task.Run(() => FinishDockingAsync(pilot, docked.StationId, docked.Condition));
                    break;
                case SimJumped jumped when _pilots.TryGetValue(jumped.SessionId, out var jumper):
                    _ = Task.Run(() => FinishJumpAsync(jumper, jumped));
                    break;
                case SimDestroyed destroyed when _pilots.TryGetValue(destroyed.SessionId, out var victim) && victim.ShipId == destroyed.ShipId:
                    _ = Task.Run(() => FinishDeathAsync(victim, destroyed));
                    break;
                case SimDestroyed destroyed when _npcShips.TryRemove(destroyed.ShipId, out var npc):
                    _ = Task.Run(() => FinishNpcDeathAsync(npc, destroyed));
                    break;
                case SimChargeSpent spent when _pilots.TryGetValue(spent.SessionId, out var shooter):
                    _charges.Enqueue(spent.SessionId, shooter.CharacterId, spent.ModuleId, spent.RoundsLeft);
                    break;
                case SimRefused { Command: SimCommands.Stop, Reason: "IN_WARP" } inWarp when _pilots.TryGetValue(inWarp.SessionId, out var warping):
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
        try
        {
            var session = pilot.Session;
            var place = _systems.Get(jumped.ToSystem);
            var transition = await _ships.JumpAsync(pilot.CharacterId, jumped.ToSystem, place?.ConstellationId ?? 0, place?.RegionId ?? 0);
            if (transition.Moved is not { } moved)
            {
                _log?.Invoke($"jump of character {pilot.CharacterId} to system {jumped.ToSystem} failed: {transition.Refusal}");
                return;
            }
            var now = FileTimeNow();
            var arrived = new Pilot(session, pilot.CharacterId, pilot.ShipId, jumped.ToSystem, now + JumpCloakTicks, pilot.UndockedFrom, jumpCloak: true);
            if (!_pilots.Values.Any(other => other.SolarSystemId == jumped.ToSystem))
            {
                ForgetNpcsOf(jumped.ToSystem);
            }
            if (!_pilots.TryUpdate((ulong)session.SessionId, arrived, pilot))
            {
                return; // the pilot went away meanwhile
            }
            pilot.End();
            arrived.Expiry = new Timer(_ => ProtectionRanOut(arrived), null, _jumpCloak, Timeout.InfiniteTimeSpan);
            SystemEntered?.Invoke(jumped.ToSystem);

            ReleaseBoundObjects(session);
            var type = _statics.Type(moved.Ship.TypeId);
            session.SendNotification(
                "OnItemChange", "clientID",
                InventoryWire.PackedRow(ShipRow(moved.Ship, type), new PyStr($"Undocking:{pilot.UndockedFrom}")),
                new PyDict([new(new PyInt(3), InventoryWire.Id(moved.PreviousLocationId))]),
                None);
            session.SendNotification("OnJumpCloakUpdated", "shipid", InventoryWire.Id(pilot.ShipId), new PyLong(now + JumpCloakTicks), new PyLong(JumpCloakTicks));
            var system = new PyInt(jumped.ToSystem);
            session.Change(
                ("solarsystemid", system),
                ("solarsystemid2", system),
                ("constellationid", new PyInt(moved.Character.ConstellationId)),
                ("regionid", new PyInt(moved.Character.RegionId)),
                ("locationid", system));
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
        if (!_pilots.TryRemove((ulong)pilot.Session.SessionId, out _))
        {
            return; // finished already
        }
        try
        {
            var session = pilot.Session;
            pilot.End();
            // the hangar shows what the guns hold: the rounds spent in space are written before the ship is moved
            await _charges.CloseAsync((ulong)session.SessionId).ConfigureAwait(false);
            pilot.Session.SendNotification("OnInvulnCancelled", "shipid", InventoryWire.Id(pilot.ShipId));
            ReleaseBoundObjects(session);
            var transition = await _ships.DockAsync(pilot.CharacterId, stationId);
            if (transition.Moved is not { } moved)
            {
                _log?.Invoke($"docking of character {pilot.CharacterId} at {stationId} failed: {transition.Refusal}");
                return;
            }
            if (condition is not null)
            {
                // what the ship has taken is kept with it in the hangar
                await _ships.SetShipConditionAsync(pilot.ShipId, new ShipCondition(condition.Damage, condition.Charge, condition.ShieldCharge, condition.ArmorDamage, false));
            }
            var type = _statics.Type(moved.Ship.TypeId);
            session.SendNotification(
                "OnItemsChanged", "charid",
                new PyList([InventoryWire.PackedRow(ShipRow(moved.Ship, type))]),
                ChangedFrom(moved),
                Py.Tuple(new PyStr("Station"), InventoryWire.Id(stationId), new PyStr("StationHangar")));
            session.Change(("stationid", new PyInt(stationId)), ("locationid", new PyInt(stationId)), ("solarsystemid", None));
            session.SendNotification("OnDockingFinished", "charid", InventoryWire.Id(stationId));
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
    private async Task FinishDeathAsync(Pilot pilot, SimDestroyed destroyed)
    {
        var id = (ulong)pilot.Session.SessionId;
        if (!_dying.TryAdd(id, 0))
        {
            return; // being told already
        }
        pilot.End();
        ShipDeath? death = null;
        SelfDestruct? done = null;
        var handedOver = false;
        try
        {
            var session = pilot.Session;
            // what the ship dropped is what it held when it died: the rounds spent in space are written first
            await _charges.CloseAsync(id).ConfigureAwait(false);
            if (await _ships.LoadAsync(pilot.CharacterId) is { } held && held.Ship.Id == pilot.ShipId && held.Ship.TypeId == CharacterShips.CapsuleTypeId)
            {
                done = await FinishPodDeathAsync(pilot, destroyed);
                handedOver = done is not null;
                return;
            }
            var seed = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ship:{pilot.ShipId}:{_nowUnixMs()}");
            death = await _ships.DestroyShipAsync(pilot.CharacterId, pilot.ShipId, destroyed.WreckKind?.TypeId, seed);
            if (death is null)
            {
                _log?.Invoke($"the ship {pilot.ShipId} of character {pilot.CharacterId} was destroyed in space but is not the character's ship in space any more");
                return;
            }
            await _statics.EnsureTypesAsync([CharacterShips.CapsuleTypeId, .. death.Changes.Select(c => c.Item.TypeId), .. death.Wreck is { } w ? new[] { w.TypeId } : []]);
            RaiseShipLost(pilot, death, destroyed);
            var capsule = death.Capsule;
            var character = death.Character;

            ReleaseBoundObjects(session);
            session.SendNotification("OnPodOrCorvetteAssembly", "charid", InventoryWire.Id(capsule.Id), new PyInt(capsule.TypeId));
            var now = FileTimeNow();
            if (death.Events.Count > 0)
            {
                session.SendNotification("OnMultiEvent", "clientID", Eve.Services.Ships.ShipNotifications.MultiEvent(pilot.CharacterId, pilot.ShipId, death.Events, now));
            }
            foreach (var change in death.Changes)
            {
                var type = _statics.Type(change.Item.TypeId);
                var row = change.Destroyed ? change.Item with { LocationId = Junkyard } : change.Item;
                var from = change.Destroyed
                    ? new PyDict([new(new PyInt(3), InventoryWire.Id(change.PreviousLocationId))])
                    : new PyDict([new(new PyInt(3), InventoryWire.Id(change.PreviousLocationId)), new(new PyInt(4), new PyInt(change.PreviousFlag))]);
                session.SendNotification("OnItemsChanged", "charid", new PyList([InventoryWire.PackedRow(RowOf(row, type))]), from, None);
            }
            var shipType = _statics.Type(death.Ship.TypeId);
            session.SendNotification(
                "OnItemsChanged", "charid",
                new PyList([InventoryWire.PackedRow(RowOf(death.Ship with { LocationId = Junkyard }, shipType), new PyStr($"Undocking:{pilot.UndockedFrom}"))]),
                new PyDict([new(new PyInt(3), InventoryWire.Id(death.Ship.LocationId))]), None);
            session.Change(("shipid", new PyLong(capsule.Id)));
            session.SendNotification("OnShipCosmeticsChanged", "clientID", InventoryWire.Id(pilot.ShipId), new PyDict([]));
            session.SendNotification("OnShipCosmeticsChanged", "clientID", InventoryWire.Id(capsule.Id), new PyDict([]));
            session.SendNotification("OnCurrentShipSkinChange", "clientID", InventoryWire.Id(capsule.Id), new PyInt(0));

            // the pilot flies the capsule where the ship was: the simulation adds its ball (it answers after the client has been sent it)
            if (await _ships.LoadAsync(pilot.CharacterId) is { } flown && flown.Ship.Id == capsule.Id)
            {
                var flying = new Pilot(session, pilot.CharacterId, capsule.Id, pilot.SolarSystemId, 0);
                flying.End();
                if (!_pilots.TryUpdate(id, flying, pilot))
                {
                    // the client went away while its ship was being destroyed: the capsule is brought home like any ship left in space
                    await _ships.RecoverFromSpaceAsync(pilot.CharacterId, PlaceOfStation);
                    return;
                }
                var refusal = await _sim.RespawnAsync(new SimRespawn(id, SimShips.Of(flown, _statics), destroyed.Position, destroyed.Direction));
                if (refusal is not null)
                {
                    _log?.Invoke($"the simulation did not take the capsule of character {pilot.CharacterId}: {refusal}");
                    return;
                }
                handedOver = true;
            }
            done = new SelfDestruct(
                death.Ship.Id,
                new SelfDestructPart("Wreck", death.Wreck is { } left ? _statics.Type(left.TypeId)?.Name : null, death.Wreck?.Id),
                new SelfDestructPart("Capsule", _statics.Type(capsule.TypeId)?.Name ?? "Capsule", capsule.Id));

            if (death.Wreck is { } wreck && destroyed.WreckKind is { } kind)
            {
                var wreckType = _statics.Type(wreck.TypeId);
                session.SendNotification(
                    "OnItemChange", "clientID",
                    InventoryWire.PackedRow(RowOf(wreck, wreckType), new PyStr("")),
                    new PyDict([new(new PyInt(3), new PyInt(0))]), None);
                PlaceWreck(new SimPlaceWreck(
                    pilot.SolarSystemId, wreck.Id, WreckGroup, kind, destroyed.Position, destroyed.Direction, destroyed.Radius, pilot.ShipId,
                    new SimCredit(character.Id, character.CorporationId, character.AllianceId ?? 0, character.FactionId ?? 0), death.Changes.All(c => c.Destroyed)));
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"the death of the ship {pilot.ShipId} of character {pilot.CharacterId} could not be told: {error}");
        }
        finally
        {
            if (!handedOver && _pilots.TryRemove(id, out _))
            {
                // no ship in space for this session: it is out of the simulation (which keeps its ballpark until it leaves)
                _sim.Leave(id, LeaveWhy.LoggedOff);
            }
            _dying.TryRemove(id, out _);
            if (_deaths.TryRemove(pilot.ShipId, out var waiting))
            {
                waiting.TrySetResult(done);
            }
        }
    }

    private void RaiseShipLost(Pilot pilot, ShipDeath death, SimDestroyed destroyed)
    {
        var killer = destroyed.KillerShipId == 0 ? null : _pilots.Values.FirstOrDefault(other => other.ShipId == destroyed.KillerShipId);
        var cause = _deaths.ContainsKey(pilot.ShipId) ? ShipLossCause.SelfDestruct
            : killer is not null ? ShipLossCause.Player
            : destroyed.KillerShipId != 0 && _npcShips.ContainsKey(destroyed.KillerShipId) ? ShipLossCause.Npc
            : ShipLossCause.Unknown;
        Raise(ShipLost, new ShipLoss(pilot.CharacterId, death.Ship.Id, death.Ship.TypeId, cause, pilot.SolarSystemId, cause == ShipLossCause.Player ? killer!.CharacterId : 0), nameof(ShipLost));
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
        if (await _ships.CharacterAsync(pilot.CharacterId) is not { } character || RookieShipOfRace?.Invoke(character.RaceId) is not { } rookie)
        {
            _log?.Invoke($"the capsule of character {pilot.CharacterId} was destroyed but there is no rookie ship to wake the pilot in");
            return null;
        }
        var stationId = character.HomeStationId > 0 && _stations.Get(character.HomeStationId) is not null ? character.HomeStationId : character.StationId;
        if (_stations.Get(stationId) is not { } station)
        {
            _log?.Invoke($"the capsule of character {pilot.CharacterId} was destroyed but its clone's station {stationId} is not known");
            return null;
        }
        var corpseType = character.Gender == 0 ? CorpseTypeFemale : CorpseTypeMale;
        await _statics.EnsureTypesAsync([CharacterShips.CapsuleTypeId, corpseType, rookie]);
        var pod = await _ships.DestroyCapsuleAsync(
            pilot.CharacterId, pilot.ShipId, corpseType, character.Name, rookie, new HomePlace(station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId));
        if (pod is null)
        {
            _log?.Invoke($"the capsule {pilot.ShipId} of character {pilot.CharacterId} was destroyed in space but is not the character's capsule in space any more");
            return null;
        }

        // the pilot is out of space: the ballpark of the simulation is let go
        _pilots.TryRemove(id, out _);
        _sim.Leave(id, LeaveWhy.Docked);
        ReleaseBoundObjects(session);
        var system = new PyInt(station.SolarSystemId);
        session.Change(
            ("stationid", new PyInt(station.Id)), ("stationid2", new PyInt(station.Id)), ("solarsystemid", None), ("solarsystemid2", system),
            ("constellationid", new PyInt(station.ConstellationId)), ("regionid", new PyInt(station.RegionId)), ("shipid", new PyLong(pod.Ship.Id)), ("locationid", new PyInt(station.Id)));
        if (pod.Corpse is { } corpse)
        {
            session.SendNotification(
                "OnItemChange", "clientID", InventoryWire.PackedRow(RowOf(corpse, _statics.Type(corpse.TypeId)), new PyStr("")), new PyDict([new(new PyInt(3), new PyInt(0))]), None);
        }
        session.SendNotification(
            "OnItemChange", "clientID", InventoryWire.PackedRow(RowOf(pod.Capsule with { LocationId = Junkyard }, _statics.Type(pod.Capsule.TypeId)), new PyStr("")),
            new PyDict([new(new PyInt(3), InventoryWire.Id(pod.Capsule.LocationId))]), None);
        session.SendNotification(
            "OnCfgDataChanged", "charid", new PyStr("evelocations"),
            new PyList([InventoryWire.Id(pod.Capsule.Id), new PyStr(_statics.Type(pod.Capsule.TypeId)?.Name ?? "Capsule"), new PyInt(pod.Capsule.LocationId), new PyInt(0), new PyInt(0), new PyInt(0), None]));

        if (pod.Corpse is { } left)
        {
            var type = _statics.Type(left.TypeId);
            var kind = new SimWreckKind(left.TypeId, left.CustomName ?? character.Name, type?.GraphicId ?? 0);
            var radius = type?.Attributes.GetValueOrDefault(162) ?? 0; // the type's radius; a corpse has none in the static data and is one metre
            PlaceWreck(new SimPlaceWreck(
                pilot.SolarSystemId, left.Id, CorpseGroup, kind, destroyed.Position, destroyed.Direction, radius > 0 ? radius : 1.0, 0,
                new SimCredit(character.Id, 0, 0, 0), true));
        }
        return new SelfDestruct(
            pod.Capsule.Id,
            new SelfDestructPart("Corpse", pod.Corpse?.CustomName, pod.Corpse?.Id),
            new SelfDestructPart("Ship", _statics.Type(pod.Ship.TypeId)?.Name, pod.Ship.Id));
    }

    /// <summary>
    /// A pilot's corporation, alliance or war faction changed while it flies: the simulation is given its ship again with the affiliation the character has now, so the ship is what
    /// everybody who looks at it from then on sees and what the rules of aggression read. (Those who already see the ship are not told: the reference's way of telling them is not recorded.)
    /// Nothing is done for a character that is not flying a ship in space.
    /// </summary>
    public async Task AffiliationChangedAsync(long characterId, CancellationToken cancellationToken = default)
    {
        var pilot = _pilots.Values.FirstOrDefault(p => p.CharacterId == characterId);
        if (pilot is null || await _ships.LoadAsync(characterId, cancellationToken) is not { } flown || flown.Ship.Id != pilot.ShipId)
        {
            return;
        }
        _sim.ShipChanged((ulong)pilot.Session.SessionId, SimShips.Of(flown, _statics));
    }

    /// <summary>The simulation was lost: every ship it held is gone, so each is brought home to the station its character last was at.</summary>
    private void OnLost()
    {
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

    private void OnSessionClosed(ClientSession session)
    {
        _targeting.TryRemove((ulong)session.SessionId, out _); // not disposed: an order may still be waiting on it
        if (_pilots.TryRemove((ulong)session.SessionId, out var pilot))
        {
            pilot.End();
            _sim.Leave((ulong)session.SessionId, LeaveWhy.LoggedOff);
            _ = Task.Run(async () =>
            {
                try
                {
                    // the ship is put back with what its guns hold: the rounds spent in space are written first
                    await _charges.CloseAsync((ulong)session.SessionId).ConfigureAwait(false);
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
