using Eve.Crimewatch;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>The jump drive's cynosural jumps (set by the composition after the fleets; null when the host has no space).</summary>
    public SpaceJumpDrive? JumpDrive { get; set; }

    /// <summary>
    /// The jump drive's jump to a cynosural field (<c>CmdJumpToFleetModuleBeacon</c> and <c>CmdJumpToFleetDeployableBeacon</c>): the drive is set spooling to the
    /// system of the field, and the ship appears by the field after <paramref name="spoolMs"/>. The checks are the stargate jump's (the ship, the restrictions of
    /// the movement policy, the drive's disallowed state); the answer is the bound object and the time the ship can be used from again, as a stargate jump's.
    /// The ship is in the other system when the simulation says it has jumped (<see cref="FinishJumpAsync"/>). The host has checked the field, the fleet, the range,
    /// the fuel and the cooldown first (<see cref="SpaceJumpDrive"/>).
    /// </summary>
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
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.CynoJump, [fieldId, destinationSystem, spoolMs, capacitorShare], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"cynosural jump of session {session.SessionId}: {error.Message}");
            throw JumpRefused("SCENE_NOT_FOUND");
        }
        if (!reply.Accepted || reply.DestinationSystem is not { } destination)
        {
            _log?.Invoke($"cynosural jump of session {session.SessionId} to field {fieldId} refused: {reply.Reason}");
            // the wording is ours: the wiki gives no refusal text (DEVIATIONS, Track A8)
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
