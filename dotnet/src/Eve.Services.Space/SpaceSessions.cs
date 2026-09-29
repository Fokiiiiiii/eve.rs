using System.Collections.Concurrent;
using Eve.Inventory;
using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;
using Eve.Universe;

namespace Eve.Services.Space;

/// <summary>
/// The pilots that are in space, and everything the service host does around the simulation: sending a ship out (the item, the session,
/// the notifications), forwarding what the simulation says to the client that plays the ship, and bringing the ship home when it docks, or
/// when the simulation is lost. What happens in space itself (motion, the balls, the stamps) is the simulation's (ADR 0007).
/// </summary>
public sealed class SpaceSessions : IDisposable, ISpaceTargeting
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
    private readonly TimeSpan _protection;
    private readonly TimeSpan _jumpCloak;
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<long, (long FileTime, long ExpiresAtMs)>> _queues = new();

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
        _sim.Received += OnReceived;
        _sim.Lost += OnLost;
    }

    /// <summary>The service registry the bound objects of a session are released from when it leaves a place (set once the registry exists).</summary>
    public ServiceRegistry? Registry { get; set; }

    public void Dispose()
    {
        _sim.Received -= OnReceived;
        _sim.Lost -= OnLost;
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
        var spec = SimShips.Of(snapshot, _statics);
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
        _pilots[(ulong)session.SessionId] = pilot;
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
                case SimDocked docked when _pilots.TryGetValue(docked.SessionId, out var pilot):
                    _ = Task.Run(() => FinishDockingAsync(pilot, docked.StationId));
                    break;
                case SimJumped jumped when _pilots.TryGetValue(jumped.SessionId, out var jumper):
                    _ = Task.Run(() => FinishJumpAsync(jumper, jumped));
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
            if (!_pilots.TryUpdate((ulong)session.SessionId, arrived, pilot))
            {
                return; // the pilot went away meanwhile
            }
            pilot.End();
            arrived.Expiry = new Timer(_ => ProtectionRanOut(arrived), null, _jumpCloak, Timeout.InfiniteTimeSpan);

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
    private async Task FinishDockingAsync(Pilot pilot, long stationId)
    {
        if (!_pilots.TryRemove((ulong)pilot.Session.SessionId, out _))
        {
            return; // finished already
        }
        try
        {
            var session = pilot.Session;
            pilot.End();
            pilot.Session.SendNotification("OnInvulnCancelled", "shipid", InventoryWire.Id(pilot.ShipId));
            ReleaseBoundObjects(session);
            var transition = await _ships.DockAsync(pilot.CharacterId, stationId);
            if (transition.Moved is not { } moved)
            {
                _log?.Invoke($"docking of character {pilot.CharacterId} at {stationId} failed: {transition.Refusal}");
                return;
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
                    await FinishDockingAsync(pilot, home);
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
