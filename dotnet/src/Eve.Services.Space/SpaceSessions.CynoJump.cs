using Eve.Crimewatch;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>The jump bridges the fleets open and pass through (set by the composition; null when the host has no space).</summary>
    public SpaceBridges? Bridges { get; set; }

    /// <summary>The Black Ops conduit jumps (set by the composition; null when the host has no space).</summary>
    public SpaceConduits? Conduits { get; set; }

    /// <summary>
    /// The ship opens a jump bridge where it is (command 30): the simulation keeps the bridge for <paramref name="lifetimeMs"/>, holds the ship still for
    /// <paramref name="immobileMs"/> and takes <paramref name="capacitor"/> GJ. The isotopes are the bridge's own (<see cref="SpaceBridges"/>).
    /// </summary>
    public async Task OpenBridgeAsync(
        ClientSession session, long shipId, long fieldId, int destinationSystem, long lifetimeMs, long immobileMs, double rangeMeters, double capacitor, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            throw JumpRefused("NOT_IN_SPACE");
        }
        if (shipId > 0 && shipId != pilot.ShipId)
        {
            throw JumpRefused("SHIP_ID_MISMATCH");
        }
        if (await ShipAttributeAsync(pilot, Eve.Services.Space.Effects.ModuleAttributes.DisallowDriveJumping, cancellationToken))
        {
            throw Notify("You cannot jump at this time.");
        }
        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.OpenBridge, [fieldId, destinationSystem, lifetimeMs, immobileMs, rangeMeters, capacitor], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"bridge of session {session.SessionId}: {error.Message}");
            throw JumpRefused("SCENE_NOT_FOUND");
        }
        if (!reply.Accepted)
        {
            _log?.Invoke($"bridge of session {session.SessionId} to field {fieldId} refused: {reply.Reason}");
            throw BridgeRefusal(reply.Reason);
        }
    }

    /// <summary>
    /// The passenger's jump through the bridge of the ship <paramref name="bridgeShipId"/> (command 31): the ship leaves at once for the field in
    /// <paramref name="destinationSystem"/>, and the arrival is the simulation's, as a cynosural jump's.
    /// </summary>
    public async Task<PyValue> BridgeJumpAsync(
        ClientSession session, long shipId, long bridgeShipId, long fieldId, int destinationSystem, string? boundObjectId, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            throw JumpRefused("NOT_IN_SPACE");
        }
        if (shipId > 0 && shipId != pilot.ShipId)
        {
            throw JumpRefused("SHIP_ID_MISMATCH");
        }
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
            pilot.CynoJumpPending = true;
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.BridgeJump, [bridgeShipId, fieldId, destinationSystem], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            pilot.CynoJumpPending = false;
            _log?.Invoke($"bridge jump of session {session.SessionId}: {error.Message}");
            throw JumpRefused("SCENE_NOT_FOUND");
        }
        catch
        {
            pilot.CynoJumpPending = false;
            throw;
        }
        if (!reply.Accepted || reply.DestinationSystem is not { } destination)
        {
            pilot.CynoJumpPending = false;
            _log?.Invoke($"bridge jump of session {session.SessionId} through ship {bridgeShipId} refused: {reply.Reason}");
            throw BridgeRefusal(reply.Reason);
        }
        SendJumpQueueUpdate(session, pilot.CharacterId, destination);
        return Py.Tuple(boundObjectId is null ? None : new PyStr(boundObjectId), new PyLong(Py.FileTimeFromUnixMs(_nowUnixMs() + SessionChangeCooldownMs)));
    }

    /// <summary>
    /// The conduit jump of the session's ship (command 32): it and the ships in <paramref name="passengers"/> (ship ids) jump to the field at once, the
    /// passengers within the conduit's range. Refused as a whole when the simulation finds one that is not.
    /// </summary>
    public async Task<PyValue> ConduitJumpAsync(
        ClientSession session, long shipId, long fieldId, int destinationSystem, IReadOnlyList<long> passengers, string? boundObjectId, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            throw JumpRefused("NOT_IN_SPACE");
        }
        if (shipId > 0 && shipId != pilot.ShipId)
        {
            throw JumpRefused("SHIP_ID_MISMATCH");
        }
        if (await ShipAttributeAsync(pilot, Eve.Services.Space.Effects.ModuleAttributes.DisallowDriveJumping, cancellationToken))
        {
            throw Notify("You cannot jump at this time.");
        }
        var jumpingPilots = _pilots.Values.Where(p => ReferenceEquals(p, pilot) || passengers.Contains(p.ShipId)).ToArray();
        var args = new List<double> { fieldId, destinationSystem, JumpDriveRules.ConduitRangeMeters };
        args.AddRange(passengers.Select(ship => (double)ship));
        CommandReply reply;
        try
        {
            foreach (var jumper in jumpingPilots) jumper.CynoJumpPending = true;
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.ConduitJump, args.ToArray(), cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            foreach (var jumper in jumpingPilots) jumper.CynoJumpPending = false;
            _log?.Invoke($"conduit jump of session {session.SessionId}: {error.Message}");
            throw JumpRefused("SCENE_NOT_FOUND");
        }
        catch
        {
            foreach (var jumper in jumpingPilots) jumper.CynoJumpPending = false;
            throw;
        }
        if (!reply.Accepted || reply.DestinationSystem is not { } destination)
        {
            foreach (var jumper in jumpingPilots) jumper.CynoJumpPending = false;
            _log?.Invoke($"conduit jump of session {session.SessionId} to field {fieldId} refused: {reply.Reason}");
            throw BridgeRefusal(reply.Reason);
        }
        SendJumpQueueUpdate(session, pilot.CharacterId, destination);
        return Py.Tuple(boundObjectId is null ? None : new PyStr(boundObjectId), new PyLong(Py.FileTimeFromUnixMs(_nowUnixMs() + SessionChangeCooldownMs)));
    }

    private static MachoWrappedException BridgeRefusal(string? reason) => reason switch
    {
        "NOT_ENOUGH_CAPACITOR" => Notify("Your ship does not have enough capacitor to open the jump bridge."),
        "WARP_DISRUPTED_BY_BUBBLE" => Notify("Your ship cannot use a jump bridge inside a warp bubble."),
        "BRIDGE_NOT_FOUND" => Notify("That fleet bridge is no longer available."),
        "OUT_OF_BRIDGE_RANGE" => Notify("You are too far from the jump bridge."),
        "OUT_OF_CONDUIT_RANGE" => Notify("A ship in the group is too far from the conduit."),
        "PASSENGER_NOT_IN_SPACE" => Notify("A ship in the group is not in space."),
        _ => JumpRefused(reason ?? "UNKNOWN"),
    };

    /// <summary>The jump drive's cynosural jumps (set by the composition after the fleets; null when the host has no space).</summary>
    public SpaceJumpDrive? JumpDrive { get; set; }

    /// <summary>
    /// The jump drive's jump to a cynosural field or accessible structure beacon: the drive is set spooling to the
    /// system of the field, and the ship appears by the field after <paramref name="spoolMs"/>. The checks are the stargate jump's (the ship, the restrictions of
    /// the movement policy, the drive's disallowed state); the answer is the bound object and the time the ship can be used from again, as a stargate jump's.
    /// The ship is in the other system when the simulation says it has jumped (<see cref="FinishJumpAsync"/>). The host has checked the target, its applicable access rule, the range,
    /// the fuel and the cooldown first (<see cref="SpaceJumpDrive"/>).
    /// </summary>
    /// <summary>Places the structure's published Pharolux beacon in the matching loaded solar system.</summary>
    public void PlaceCynoBeacon(Eve.Space.SimPlaceCynoBeacon beacon) => _sim.PlaceCynoBeacon(beacon);

    /// <summary>Removes only this structure beacon from its known solar-system scene.</summary>
    public void RemoveCynoBeacon(int solarSystemId, long structureBeaconId) => _sim.RemoveCynoBeacon(solarSystemId, structureBeaconId);

    public async Task<PyValue> CynoJumpAsync(
        ClientSession session, long shipId, long fieldId, int destinationSystem, long spoolMs, double capacitorShare, string? boundObjectId, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            throw JumpRefused("NOT_IN_SPACE");
        }
        if (shipId > 0 && shipId != pilot.ShipId)
        {
            throw JumpRefused("SHIP_ID_MISMATCH");
        }
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
            pilot.CynoJumpPending = true;
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.CynoJump, [fieldId, destinationSystem, spoolMs, capacitorShare], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            pilot.CynoJumpPending = false;
            _log?.Invoke($"cynosural jump of session {session.SessionId}: {error.Message}");
            throw JumpRefused("SCENE_NOT_FOUND");
        }
        catch
        {
            pilot.CynoJumpPending = false;
            throw;
        }
        if (!reply.Accepted || reply.DestinationSystem is not { } destination)
        {
            pilot.CynoJumpPending = false;
            _log?.Invoke($"cynosural jump of session {session.SessionId} to field {fieldId} refused: {reply.Reason}");

            if (reply.Reason == "NOT_ENOUGH_CAPACITOR")
            {
                throw Notify("Your ship does not have enough capacitor to activate its jump drive.");
            }
            throw JumpRefused(reply.Reason);
        }
        SendJumpQueueUpdate(session, pilot.CharacterId, destination);
        return Py.Tuple(boundObjectId is null ? None : new PyStr(boundObjectId), new PyLong(Py.FileTimeFromUnixMs(_nowUnixMs() + SessionChangeCooldownMs)));
    }
}
