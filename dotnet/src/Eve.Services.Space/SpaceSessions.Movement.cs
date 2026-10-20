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
using Eve.Services.Space.Pochven;
using Eve.Universe;
using ModuleAttributes = Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>Asks the simulation to dock the ship; the answer to the client's call is the time the docking was accepted, or nothing when it was not.</summary>
    /// <summary>Told after a pilot has docked (the mission runtime's courier delivery); null when nothing listens.</summary>
    public Func<long, long, CancellationToken, Task>? PilotDocked { get; set; }

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

    /// <summary>Called after the simulation accepts a character's warp command.</summary>
    public Func<long, CancellationToken, Task>? WarpAcceptedAsync { get; set; }

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

    /// <summary>The point <paramref name="distance"/> metres ahead of the pilot's ship along its heading, as the simulation has it (protocol 49).</summary>
    public Task<WarpCoordinates?> DropPointAsync(ClientSession session, double distance, CancellationToken cancellationToken) =>
        QueryPositionAsync(session, SimCommands.DropPoint, [distance], cancellationToken);

    /// <summary>Whether a candidate deployment point is within the checkpoint's 10,000 km asteroid exclusion radius.</summary>
    public async Task<bool> HasAsteroidWithinDistanceAsync(int solarSystemId, IReadOnlyList<double> position, double distanceMeters, CancellationToken cancellationToken = default)
    {
        if (_spaceItems is null)
        {
            throw new InvalidOperationException("space item storage is required for asteroid placement checks");
        }
        if (solarSystemId <= 0 || position.Count != 3 || position.Any(value => !double.IsFinite(value)) || !double.IsFinite(distanceMeters) || distanceMeters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "a solar system, finite 3D point and nonnegative finite radius are required");
        }

        var asteroidGroups = (await _statics.TypesInCategoryAsync("Asteroid", cancellationToken))
            .Select(type => type.GroupId)
            .ToHashSet();
        var radiusSquared = distanceMeters * distanceMeters;
        foreach (var item in await _spaceItems.InSystemAsync(solarSystemId, cancellationToken))
        {
            if (!asteroidGroups.Contains(item.GroupId) || item.Position.Count != 3)
            {
                continue;
            }
            var x = position[0] - item.Position[0];
            var y = position[1] - item.Position[1];
            var z = position[2] - item.Position[2];
            if (x * x + y * y + z * z <= radiusSquared)
            {
                return true;
            }
        }
        return false;
    }

    public Task<WarpCoordinates?> PositionOfAsync(
        ClientSession session,
        long targetId,
        CancellationToken cancellationToken) =>
        QueryPositionAsync(session, SimCommands.PositionOf, [targetId], cancellationToken);

    /// <summary>
    /// answers the pilot of a ship only and the return to the ship asks for the place later (<see cref="ScenePositionOfAsync"/>). Called while the session still pilots it.
    /// </summary>
    public async Task RecordBoardingPlaceAsync(ClientSession session, long shipId, CancellationToken cancellationToken)
    {
        if (_spaceItems is null || SolarSystemOf(session) is not { } system ||
            await PositionOfAsync(session, shipId, cancellationToken) is not { } at || at.SolarSystemId != system)
        {
            return;
        }
        var existing = await _spaceItems.GetAsync(shipId, cancellationToken);
        var expiresAtMs = _nowUnixMs() + 86_400_000L;
        await _spaceItems.SaveAsync(new SpaceItem(
            shipId, system, [at.X, at.Y, at.Z], [1d, 0d, 0d], existing?.Radius ?? 0, existing?.GroupId ?? 0, existing?.LauncherId ?? 0,
            session.CharacterId ?? 0, session.GetInt("corpid") ?? 0, session.GetInt("allianceid") ?? 0, 0, expiresAtMs), cancellationToken);
    }

    /// <summary>
    /// Where a ship of the scene is, asked by a session that may no longer pilot it: a structure observer's own ship, boarded from space, stays in the scene
    /// </summary>
    public async Task<WarpCoordinates?> ScenePositionOfAsync(ClientSession session, long shipId, CancellationToken cancellationToken)
    {
        if (SolarSystemOf(session) is not { } system)
        {
            return null;
        }
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.PositionOf, [shipId], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"position query of session {session.SessionId}: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        if (reply.Accepted && reply.Position.Count >= 3)
        {
            return new WarpCoordinates(system, reply.Position[0], reply.Position[1], reply.Position[2]);
        }
        // the simulation answers the pilot of a ship only: a boarded ship is put where its saved place is (the place its pilot left it at)
        return _spaceItems is null || await _spaceItems.GetAsync(shipId, cancellationToken) is not { } saved || saved.SolarSystemId != system || saved.Position.Count < 3
            ? null
            : new WarpCoordinates(system, saved.Position[0], saved.Position[1], saved.Position[2]);
    }

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
        if (pilot.SimulationScene.InstanceId > 0) throw Notify(AbyssalWarpBlockedMessage);
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
            var warpHistoryNeedsPersistence = pilot.Session.GetInt("hasEverWarpedPersisted") != 1;
            pilot.Session.Set("hasEverWarped", new PyInt(1));
            if (warpHistoryNeedsPersistence && WarpAcceptedAsync is { } acceptedWarp)
            {
                try
                {
                    await acceptedWarp(pilot.CharacterId, CancellationToken.None).ConfigureAwait(false);
                    pilot.Session.Set("hasEverWarpedPersisted", new PyInt(1));
                }
                catch (Exception error)
                {
                    _log?.Invoke($"accepted warp history for character {pilot.CharacterId} could not be saved: {error.Message}");
                }
            }
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
        await CheckGateRulesAsync(pilot, gateId, cancellationToken);
        // the toll is paid before the simulation hears the jump; a jump that does not happen gets it back
        var toll = await ChargeTollAsync(session, pilot, gateId, cancellationToken);
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.Jump, [gateId, toGateId], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            await RefundTollAsync(toll, cancellationToken);
            _log?.Invoke($"jump of session {session.SessionId}: {error.Message}");
            throw JumpRefused("SCENE_NOT_FOUND");
        }
        if (!reply.Accepted || reply.DestinationSystem is not { } destination)
        {
            await RefundTollAsync(toll, cancellationToken);
            _log?.Invoke($"jump of session {session.SessionId} through {gateId} refused: {reply.Reason}");
            throw JumpRefused(reply.Reason);
        }

        // EVE.js jumpSessionViaStargate: the contraband of the ship is inspected in the system it arrives in
        await ApplyContrabandAsync(session, pilot.CharacterId, pilot.ShipId, destination, cancellationToken);
        _emanationLocks.Left(pilot.CharacterId, pilot.SolarSystemId);
        if (GateRules?.DestinationOf(gateId) is { } arrivalGate)
        {
            _emanationLocks.Entered(pilot.CharacterId, destination, arrivalGate, _nowUnixMs());
        }
        SendJumpQueueUpdate(session, pilot.CharacterId, destination);
        return Py.Tuple(boundObjectId is null ? None : new PyStr(boundObjectId), new PyLong(Py.FileTimeFromUnixMs(_nowUnixMs() + SessionChangeCooldownMs)));
    }

    /// <summary>
    /// The gate toll of the gate (the client's alignment-based toll, computed as the client computes it), charged to the pilot's wallet. Null when the
    /// </summary>
    private async Task<(long CharacterId, string Key, decimal Amount)?> ChargeTollAsync(ClientSession session, Pilot pilot, long gateId, CancellationToken cancellationToken)
    {
        if (GateRules?.TollOf(gateId) is not { } toll || ChargeGateToll is null)
        {
            return null;
        }
        var aligned = Gates.GateRules.IsAligned(toll, session.GetInt("warfactionid"));
        var amount = decimal.Round((decimal)Gates.GateRules.TollAmount(toll, await ShipMassAsync(pilot, cancellationToken), aligned), 2, MidpointRounding.AwayFromZero);
        if (amount <= 0)
        {
            return null;
        }
        var key = $"gate-toll:{pilot.CharacterId}:{gateId}:{_nowUnixMs()}";
        if (!await ChargeGateToll(pilot.CharacterId, amount, key, cancellationToken))
        {
            throw Notify("You cannot pay the toll of this stargate.");
        }
        return (pilot.CharacterId, key, amount);
    }

    private async Task RefundTollAsync((long CharacterId, string Key, decimal Amount)? toll, CancellationToken cancellationToken)
    {
        if (toll is { } charged && RefundGateToll is not null)
        {
            await RefundGateToll(charged.CharacterId, charged.Amount, charged.Key + ":refund", cancellationToken);
        }
    }

    /// <summary>The ship's mass as it flies (dogma attribute 4, its fittings included): the mass the client's toll is computed from.</summary>
    private async Task<double> ShipMassAsync(Pilot pilot, CancellationToken cancellationToken) =>
        await FlownShipAsync(pilot, cancellationToken) is { } flown ? flown.World.Get(flown.Ship.Id, Eve.Services.Space.Effects.ModuleAttributes.Mass) : 0;

    /// against the gate's list, and the emanation lock of the system it is in.</summary>
    private async Task CheckGateRulesAsync(Pilot pilot, long gateId, CancellationToken cancellationToken)
    {
        await _statics.EnsureTypesAsync([pilot.TypeId], cancellationToken);
        var group = _statics.Type(pilot.TypeId)?.GroupId ?? 0;
        if (RegionOfSystem(pilot.SolarSystemId) is { } region && PochvenRules.StargateRefusal(region, group) is { } refusal)
        {
            throw Notify(refusal);
        }
        if (GateRules is null)
        {
            return;
        }
        if (!GateRules.ShipAllowed(gateId, group))
        {
            throw Notify("Capital ships cannot use this stargate.");
        }
        if (!_emanationLocks.MayLeave(pilot.CharacterId, pilot.SolarSystemId, gateId, _nowUnixMs()))
        {
            throw Notify("An emanation lock holds you to another stargate of this system.");
        }
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
                    if (JumpDrive is { } drive)
                    {
                        await drive.ArrivedAsync(pilot.CharacterId, token);
                    }
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
                        jumped.ToSystem, arrivalFileTime + JumpCloakTicks, pilot.UndockedFrom, jumpCloak: true)
                    { CynoArrival = pilot.CynoJumpPending };
                    if (!_pilots.TryUpdate((ulong)session.SessionId, arrived, pilot))
                        return Task.FromResult(false);
                    pilot.End();
                    ArmProtection(arrived, _jumpCloak);
                    BeginCynoTetherProtection(arrived);
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
                    Background(() => RefreshTetherPolicyAsync(session));
                    // a system with an environment (a wormhole's effects) changes what the ship flies with: the simulation is given it again
                    if (SystemEnvironment is { } environmentOf && (environmentOf(jumped.ToSystem).Count > 0 || environmentOf(pilot.SolarSystemId).Count > 0))
                        Background(() => RefreshEnvironmentAsync(session, pilot.CharacterId, pilot.ShipId));
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
            var previousStationId = session.GetInt("stationid") ?? 0;
            var previousStructureId = session.GetInt("structureid") ?? 0;
            // an Upwell structure takes the ship the same way a station does (its id is the place the ship and the character are in)
            var inStructure = await IsStructureAsync(stationId, CancellationToken.None);
            var transition = await _transitions.ExecuteAsync(
                async cancellationToken =>
                {
                    await CancelRepairsAsync(pilot, cancellationToken);
                    await _charges.CloseAsync((ulong)session.SessionId).ConfigureAwait(false);
                    var shipCondition = condition is null
                        ? null
                        : new ShipCondition(condition.Damage, condition.Charge, condition.ShieldCharge, condition.ArmorDamage, false);
                    var docked = await _ships.DockAsync(pilot.CharacterId, stationId, shipCondition, cancellationToken);
                    if (docked.Moved is not null && PilotDocked is { } onDocked) await onDocked(pilot.CharacterId, stationId, cancellationToken);
                    return docked;
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
                        inStructure
                            ? Py.Tuple(new PyStr("Structure"), InventoryWire.Id(stationId), new PyStr("StructureShipHangar"))
                            : Py.Tuple(new PyStr("Station"), InventoryWire.Id(stationId), new PyStr("StationHangar")));

                    if (inStructure)
                        session.Change(("stationid", None), ("structureid", new PyInt(stationId)), ("locationid", new PyInt(pilot.SolarSystemId)), ("solarsystemid", new PyInt(pilot.SolarSystemId)));
                    else
                        session.Change(("stationid", new PyInt(stationId)), ("locationid", new PyInt(stationId)), ("solarsystemid", None));
                    return Task.FromResult(true);
                },
                notify: (_, _) =>
                {
                    session.SendNotification("OnDockingFinished", "charid", InventoryWire.Id(stationId));
                    DockedLocationChanged?.Invoke(session, previousStationId, previousStructureId);
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
