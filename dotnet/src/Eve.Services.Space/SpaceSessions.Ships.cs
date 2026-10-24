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

public sealed partial class SpaceSessions
{
    /// <summary>The SDE category of subsystems (a Strategic Cruiser's fit).</summary>
    private const int SubsystemCategory = 32;

    /// <summary>
    /// Sends the session's ship out of its station. The client is told in the order the reference tells it (the ship moved, the objects
    /// bound to the station let go, the session changed, the undocking protection started) and the answer is the object it called and the time
    /// from which it may go on. Returns None when there is nothing to undock.
    /// </summary>
    public Task<PyValue> UndockAsync(ClientSession session, string? boundObjectId, CancellationToken cancellationToken, bool ignoreContraband = false) =>
        UndockFromAsync(session, "stationid", boundObjectId, cancellationToken, ignoreContraband);

    /// <summary>
    /// The undock from the station or the structure the session is in (<paramref name="placeKey"/> names which): the same ship, simulation and session
    /// changes; a structure is put in the simulation first (its ball is placed when a pilot comes into its system, which the undock may precede).
    /// </summary>
    /// <summary>
    /// The contraband penalty at an undock or a stargate jump (EVE.js inspectContrabandForTransition): the fine goes to the wallet, the contraband leaves the hold, and the
    /// client is told each removed item (OnItemChange with the junk location, as a merge or a removal is told).
    /// </summary>
    private async Task ApplyContrabandAsync(ClientSession session, long characterId, long shipId, int solarSystemId, CancellationToken cancellationToken)
    {
        if (ApplyContrabandPenaltyAsync is not { } apply || await apply(characterId, shipId, solarSystemId, cancellationToken) is not { } penalty)
        {
            return;
        }
        foreach (var item in penalty.Confiscated)
        {
            ShipNotifications.SendItemChange(session,
                new ItemChanged(item with { LocationId = CharacterShips.JunkLocation }, item.LocationId, item.Flag, item.Quantity),
                row => Eve.Services.Dogma.DogmaInfo.RowOf(row, _statics.Type(row.TypeId)));
        }
    }

    private async Task<PyValue> UndockFromAsync(ClientSession session, string placeKey, string? boundObjectId, CancellationToken cancellationToken, bool ignoreContraband = false)
    {
        if (session.CharacterId is not { } characterId || session.GetInt(placeKey) is not { } placeId || IsInSpace(session))
        {
            return None;
        }
        var inStructure = placeKey == "structureid";
        var previousStationId = session.GetInt("stationid") ?? 0;
        var previousStructureId = session.GetInt("structureid") ?? 0;
        if (!_sim.IsConnected)
        {
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        if (await _ships.LoadAsync(characterId, cancellationToken) is not { } snapshot)
        {
            return None;
        }
        if (!ignoreContraband && InspectUndockContrabandAsync is { } inspect &&
            (await inspect(characterId, snapshot.Ship.Id, snapshot.Character.SolarSystemId, cancellationToken)).Count > 0)
            throw MachoErrors.ThrowUserError("ShipContrabandWarningUndock");
        await ApplyContrabandAsync(session, characterId, snapshot.Ship.Id, snapshot.Character.SolarSystemId, cancellationToken);
        // a Strategic Cruiser leaves its station only with one subsystem of each class (the reference's STRATEGIC_CRUISER_FIT_INCOMPLETE)
        if (_statics.Type(snapshot.Ship.TypeId)?.GroupId == Eve.Fitting.StrategicCruisers.GroupId
            && Eve.Fitting.StrategicCruisers.IncompleteFit(snapshot.Fitted
                .Where(item => _statics.Type(item.TypeId)?.CategoryId == SubsystemCategory).Select(item => item.Flag)) is { } incomplete)
        {
            throw Notify(incomplete);
        }
        await SyncBuffsAsync(snapshot.World, snapshot.Ship.Id, cancellationToken);
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
                    if (inStructure && PlaceStructuresAsync is { } placeStructures)
                        await placeStructures(systemId, token);
                      refusal = await _sim.EnterAsync(new SimEnter(sessionId, SimSceneKey.SolarSystem(systemId), EnterHow.Undock, placeId, spec), token);
                    if (refusal is null)
                        await PlaceWormholeEndsAsync(systemId, token);
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
                TellEnteredBuffs(sessionId, snapshot.Ship.Id, _nowUnixMs());
                if (!_pilots.Values.Any(other => other.SolarSystemId == systemId))
                    ForgetNpcsOf(systemId);
            },
            (result, _) =>
            {
                var moved = result.Moved!;
                var now = FileTimeNow();
                pilot = new Pilot(session, characterId, moved.Ship.Id, moved.Ship.TypeId,
                    systemId, now + UndockInvulnerabilityTicks, placeId);
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
                    new PyList([InventoryWire.PackedRow(ShipRow(moved.Ship, type), new PyStr(inStructure ? "UndockingStructure:" : $"Undocking:{placeId}"))]),
                    ChangedFrom(moved), None);
                ReleaseBoundObjects(session);

                // server value is cleared (characterState: session.stationid2 = null); a later docking announces it from nothing (a pod's death does)
                session.Set("stationid2", None);
                session.Set("shipid", InventoryWire.Id(moved.Ship.Id));
                session.Change((placeKey, None), ("locationid", new PyInt(systemId)), ("solarsystemid", new PyInt(systemId)));
                DockedLocationChanged?.Invoke(session, previousStationId, previousStructureId);
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
        await RefreshTetherPolicyAsync(session, cancellationToken);
        return Py.Tuple(boundObjectId is null ? None : new PyStr(boundObjectId),
            new PyLong(Py.FileTimeFromUnixMs(_nowUnixMs() + SessionChangeCooldownMs)));
    }

    /// <summary>Boards another ship from the current station hangar and updates the active session ship.</summary>
    /// <summary>Leaves a docked ship, activates the character's capsule, and keeps the ship in the hangar.</summary>
    /// <summary>Boards a ship already in the character's station hangar.</summary>
    public ValueTask<PyValue> EjectAsync(ClientSession session, CancellationToken cancellationToken) =>
        EjectPhysicalShipAsync(session, ShipIdOf(session) ?? 0, false, cancellationToken);

    private async ValueTask<PyValue> EjectPhysicalShipAsync(ClientSession session, long expectedSessionShipId, bool exitStructure, CancellationToken cancellationToken)
    {
        AssertAbyssalShipSwapAllowed(session);
        var id = (ulong)session.SessionId;
        CancelSafeLogoff(session);
        if (LifecycleBlockers is not { } readBlockers
            || !_pilots.TryGetValue(id, out var pilot)
            || session.CharacterId != pilot.CharacterId || session.GetInt("shipid") != expectedSessionShipId
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
        // a running module that prevents ejection (disallowEarlyDeactivation 906, the SDE's "prevent ejection from the ship it is fitted to"): the
        // refusal is the reference's shape (a CustomNotify, as the entosis link's), in words of ours: no client or EVE.js text names this module
        if (await FlownShipAsync(pilot, cancellationToken) is { } flown
            && flown.Fitted.Any(module => pilot.ActiveModules.ContainsKey(module.Id)
                && flown.World.Get(module.Id, Eve.Services.Space.Effects.ModuleAttributes.DisallowEarlyDeactivation) == 1))
        {
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You cannot eject while a module that prevents ejection is active.")));
        }

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
                await CancelRepairsAsync(pilot, token);
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
                    pilot.CharacterId, fitting.Character.CorporationId, ejected.Ship.TypeId, pilot.SolarSystemId,
                    OwnerAllianceId: fitting.Character.AllianceId ?? 0,
                    OwnerWarFactionId: fitting.Character.FactionId ?? 0);
                EndTetherPilot(pilot);
                pilot.End();
                var capsulePilot = new Pilot(
                    session, pilot.CharacterId, ejected.PilotShip.Id, ejected.PilotShip.TypeId, pilot.SolarSystemId, 0);
                if (!_pilots.TryUpdate(id, capsulePilot, pilot))
                {
                    session.Close();
                    return Task.FromResult(false);
                }
                if (exitStructure)
                    session.Change(("structureid", None), ("shipid", new PyLong(ejected.PilotShip.Id)), ("locationid", new PyInt(pilot.SolarSystemId)));
                else session.Change(("shipid", new PyLong(ejected.PilotShip.Id)));
                Background(() => RefreshTetherPolicyAsync(session));
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
        AssertAbyssalShipSwapAllowed(session);
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
                await CancelRepairsAsync(currentPilot, token);
                _ejectedShips[currentShipId] = new EjectedShip(
                    characterId, boarding.BoarderCorporationId, currentPilot.TypeId, currentPilot.SolarSystemId,
                    OwnerAllianceId: currentPilot.Session.GetInt("allianceid") ?? currentPilot.Session.GetInt("allianceID") ?? 0,
                    OwnerWarFactionId: currentPilot.Session.GetInt("warfactionid") ?? currentPilot.Session.GetInt("warFactionID") ?? 0);
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
                EndTetherPilot(currentPilot);
                currentPilot.End();
                if (!_pilots.TryUpdate(id, nextPilot, currentPilot))
                {
                    session.Close();
                    return Task.FromResult(false);
                }
                session.Change(("shipid", new PyLong(shipId)));
                Background(() => RefreshTetherPolicyAsync(session));
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
        if (IsInSpace(session) || IsStructureObserver(session))
        {
            _sim.Bind((ulong)session.SessionId);
        }
    }

    public void UpdateState(ClientSession session)
    {
        if (IsInSpace(session) || IsStructureObserver(session))
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
        if (IsStructureObserver(session))
        {
            if (IsMovementOrder(code, args)) throw Notify("A controlled structure cannot move.");
            _sim.Command((ulong)session.SessionId, code, args);
            return;
        }
        CancelSafeLogoff(session);
        if (_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {

            if (IsMovementOrder(code, args) && Buffs.IsImmobile(pilot.ShipId, _nowUnixMs()))
            {
                _log?.Invoke($"movement command {code} of session {session.SessionId} refused: the ship is held still by a fleet grant");
                return;
            }
            _sim.Command((ulong)session.SessionId, code, args);
        }
    }

    /// <summary>A movement order that takes the ship somewhere: a speed above zero, a direction, a target to follow or orbit, or an alignment.</summary>
    private static bool IsMovementOrder(byte code, double[] args) => code switch
    {
        SimCommands.SetSpeedFraction => args.Length > 0 && args[0] > 0,
        SimCommands.GotoDirection or SimCommands.FollowBall or SimCommands.Orbit or SimCommands.AlignTo => true,
        _ => false,
    };

    /// <summary>The effect of a module is offensive: it damages, jams, webs, paints or scrambles another ship (the attacks the crimewatch and the safety judge).</summary>
    private static bool IsOffensive(SimEffect package) =>
        package.Turret is not null || package.Action is SimAction.Strike or SimAction.Doomsday || package.Action is SimAction.Remote { What: SimRemote.Neutralize or SimRemote.Drain or SimRemote.Web or SimRemote.Paint or SimRemote.Damp { RangePercent: < 0 } or SimRemote.Damp { ResolutionPercent: < 0 } or SimRemote.Damp { GravimetricStrengthPercent: < 0 } or SimRemote.Damp { LadarStrengthPercent: < 0 } or SimRemote.Damp { MagnetometricStrengthPercent: < 0 } or SimRemote.Damp { RadarStrengthPercent: < 0 } or SimRemote.Scramble or SimRemote.Jam or SimRemote.Disrupt { RangePercent: < 0 } or SimRemote.Disrupt { FalloffPercent: < 0 } or SimRemote.Disrupt { TrackingPercent: < 0 } or SimRemote.GuidanceDisrupt { VelocityPercent: < 0 } or SimRemote.GuidanceDisrupt { DelayPercent: > 0 } or SimRemote.GuidanceDisrupt { AoeVelocityPercent: < 0 } or SimRemote.GuidanceDisrupt { AoeCloudPercent: > 0 } };

    private void EndInvulnerability(Pilot pilot, bool keepsJumpCloak = false)
    {
        if (keepsJumpCloak && pilot.JumpCloak)
        {
            return;
        }
        var hasProtection = pilot.InvulnerableUntil > FileTimeNow() || pilot.JumpCloak || pilot.CancelExpiry is not null;
        if (pilot.End() && hasProtection)
        {
            if (pilot.JumpCloak)
            {
                pilot.Session.SendNotification("OnJumpCloakCancelled", "shipid", InventoryWire.Id(pilot.ShipId));
            }
            pilot.Session.SendNotification("OnInvulnCancelled", "shipid", InventoryWire.Id(pilot.ShipId));
            TetherProtectionEnded(pilot);
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
}
