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
public sealed class SpaceSessions : IDisposable
{
    /// <summary>How long a ship that has undocked cannot be hurt (the reference's: 30 seconds).</summary>
    public const long UndockInvulnerabilityTicks = 300_000_000;

    /// <summary>How long after an undock the client is told the session change is over (the reference's: 10 seconds).</summary>
    private const long SessionChangeCooldownMs = 10_000;

    private sealed class Pilot(ClientSession session, long characterId, long shipId, int solarSystemId, long invulnerableUntil)
    {
        public ClientSession Session { get; } = session;

        public long CharacterId { get; } = characterId;

        public long ShipId { get; } = shipId;

        public int SolarSystemId { get; } = solarSystemId;

        /// <summary>A FILETIME: until when the ship cannot be hurt, unless a command of its pilot breaks it first.</summary>
        public long InvulnerableUntil { get; } = invulnerableUntil;

        public bool InvulnerabilityEnded;

        /// <summary>The pilot gave a command that ends the protection: the client is told once the simulation has told it what the command did.</summary>
        public bool EndAfterNextUpdate;
    }

    private readonly ISimulation _sim;
    private readonly CharacterShips _ships;
    private readonly DogmaStatic _statics;
    private readonly StationCatalog _stations;
    private readonly Func<long> _nowUnixMs;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<ulong, Pilot> _pilots = new();

    public SpaceSessions(ISimulation sim, CharacterShips ships, DogmaStatic statics, StationCatalog stations, Func<long> nowUnixMs, Action<string>? log = null)
    {
        _sim = sim;
        _ships = ships;
        _statics = statics;
        _stations = stations;
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
        var pilot = new Pilot(session, characterId, moved.Ship.Id, systemId, now + UndockInvulnerabilityTicks);
        _pilots[(ulong)session.SessionId] = pilot;
        session.Closed += () => OnSessionClosed(pilot);

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
    /// A movement command. The first speed command a pilot gives while the undocking protection lasts ends it: the client is told, after the
    /// update the simulation sends for the command (the reference's order).
    /// </summary>
    public void Command(ClientSession session, byte code, params double[] args)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return;
        }
        _sim.Command((ulong)session.SessionId, code, args);
        if (code == SimCommands.SetSpeedFraction && !pilot.InvulnerabilityEnded && FileTimeNow() < pilot.InvulnerableUntil)
        {
            pilot.EndAfterNextUpdate = true;
        }
    }

    private void EndInvulnerability(Pilot pilot)
    {
        if (!pilot.InvulnerabilityEnded && FileTimeNow() < pilot.InvulnerableUntil)
        {
            pilot.InvulnerabilityEnded = true;
            pilot.Session.SendNotification("OnInvulnCancelled", "shipid", InventoryWire.Id(pilot.ShipId));
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
            var reply = await _sim.DockAsync((ulong)session.SessionId, stationId, cancellationToken);
            if (reply.Accepted)
            {
                return new PyLong(reply.At);
            }
            _log?.Invoke($"docking of session {session.SessionId} at {stationId} refused: {reply.Reason}");
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"docking of session {session.SessionId}: {error.Message}");
        }
        return None;
    }

    // ── what the simulation says ─────────────────────────────────────────────

    private void OnReceived(SimMessage message)
    {
        try
        {
            switch (message)
            {
                case SimNotify notify when _pilots.TryGetValue(notify.SessionId, out var pilot):
                    Forward(pilot.Session, notify);
                    if (pilot.EndAfterNextUpdate && notify.Name == "DoDestinyUpdate")
                    {
                        pilot.EndAfterNextUpdate = false;
                        EndInvulnerability(pilot);
                    }
                    break;
                case SimDocked docked when _pilots.TryGetValue(docked.SessionId, out var pilot):
                    _ = Task.Run(() => FinishDockingAsync(pilot, docked.StationId));
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

    private void OnSessionClosed(Pilot pilot)
    {
        if (_pilots.TryRemove((ulong)pilot.Session.SessionId, out _))
        {
            _sim.Leave((ulong)pilot.Session.SessionId, LeaveWhy.LoggedOff);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _ships.RecoverFromSpaceAsync(pilot.CharacterId);
                }
                catch (Exception error)
                {
                    _log?.Invoke($"the ship of character {pilot.CharacterId} could not be put back after the session ended: {error.Message}");
                }
            });
        }
    }
}
