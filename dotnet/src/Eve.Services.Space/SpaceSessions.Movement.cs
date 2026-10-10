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
    /// <summary>Asks the simulation to dock the ship; the answer to the client's call is the time the docking was accepted, or nothing when it was not.</summary>
    public async Task<PyValue> DockAsync(ClientSession session, long stationId, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot))
        {
            return None;
        }
        // a ship that its buffs keep from docking (disallowDocking 2354 on the ship, a fleet grant of the invulnerability core); the wording is the module's (see below)
        if (await ShipAttributeAsync(pilot, Eve.Services.Space.Effects.ModuleAttributes.DisallowDocking, cancellationToken))
        {
            throw Notify("You cannot dock while a module that prevents docking is active.");
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
            // a module that disallows docking is running (disallowDocking 2354): the wording is EveJS's (beyonceService: DOCKING_DISALLOWED_BY_MODULE)
            if (reply.Reason == "DOCKING_DISALLOWED_BY_MODULE")
            {
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You cannot dock while a module that prevents docking is active.")));
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
        // a ship that its buffs scramble (warpScrambleStatus 104, the warp penalty of a fleet grant) cannot warp: the same refusal as a scrambler's
        if (await ShipAttributeAsync(pilot, Eve.Services.Space.Effects.ModuleAttributes.WarpScrambleStatus, cancellationToken))
        {
            throw Notify("You cannot warp because you are warp scrambled.");
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
        // the reference's errors for a bubble (the user error WarpDisrupted) and for a cloak that holds the ship (the notice of its text)
        if (reply.Reason == "WARP_DISRUPTED_BY_BUBBLE")
        {
            throw MachoErrors.ThrowUserError("WarpDisrupted");
        }
        if (reply.Reason == "WARP_BLOCKED_BY_CLOAK")
        {
            throw Notify("You cannot warp while cloaked. Deactivate your cloaking device first.");
        }
        throw Notify("You cannot warp there right now.");
    }


    /// <summary>Whether the pilot's ship carries an attribute of its own above zero, as it flies (its buffs; a fleet grant sets the ship's attributes).</summary>
    private async Task<bool> ShipAttributeAsync(Pilot pilot, int attributeId, CancellationToken cancellationToken) =>
        await FlownShipAsync(pilot, cancellationToken) is { } flown && flown.World.Get(flown.Ship.Id, attributeId) > 0;

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
        // a ship that its buffs keep from jumping (disallowDriveJumping 2453, a fleet grant of the invulnerability core); the client's wording for it is not in the tables read
        if (await ShipAttributeAsync(pilot, Eve.Services.Space.Effects.ModuleAttributes.DisallowDriveJumping, cancellationToken))
        {
            throw Notify("You cannot jump at this time.");
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
                    await CancelRepairsAsync(pilot, token);
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
            else
                TellEnteredBuffs((ulong)session.SessionId, pilot.ShipId, _nowUnixMs());
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
                    await CancelRepairsAsync(pilot, cancellationToken);
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
}
