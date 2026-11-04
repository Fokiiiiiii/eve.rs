using Eve.Crimewatch;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>
    /// Whether the character may dock at the structure: its docking service is online and it is on the structure's docking access (the host decides
    /// from the structure's record and profile). Unset: the docking is not checked against access.
    /// </summary>
    public Func<long, long, CancellationToken, Task<bool>>? StructureDockingAllowedAsync { get; set; }

    /// <summary>
    /// </summary>
    public Func<int, CancellationToken, Task>? PlaceStructuresAsync { get; set; }

    /// <summary>
    /// unset, no structure is a hub and an entosis link is refused.
    /// </summary>
    public Func<int, long, CancellationToken, Task<bool>>? IsSovereigntyHubAsync { get; set; }

    /// <summary>
    /// on its own and the client is told the distance (DockingApproach); in range the simulation accepts the docking, and the ship is put in the structure's
    /// hangar when the simulation reports it docked (<see cref="FinishDockingAsync"/>). The answer is nothing, as the reference's.
    /// </summary>
    public async Task DockStructureAsync(ClientSession session, long structureId, long shipId, CancellationToken cancellationToken)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot) || (shipId > 0 && shipId != pilot.ShipId))
            throw MachoErrors.ThrowUserError("DeniedShipChanged");
        if (await ShipAttributeAsync(pilot, Eve.Services.Space.Effects.ModuleAttributes.DisallowDocking, cancellationToken))
            throw DockingRequestDenied("You cannot dock while a module that prevents docking is active.");
        if (StructureExistsAsync is null || !await StructureExistsAsync(structureId, cancellationToken))
            throw MachoErrors.ThrowUserError("TargetingAttemptCancelled");
        if (StructureDockingAllowedAsync is { } allowed && !await allowed(pilot.CharacterId, structureId, cancellationToken))
            throw DockingRequestDenied("Structure docking access denied.");
        var policy = await MovementPolicyAsync(pilot, PolicyAction.Dock, cancellationToken);
        if (!policy.Allowed)
        {
            throw policy.BlockReason == PolicyBlockReason.CriminalTimer
                ? MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("Docking is disabled while the criminal timer is active.")))
                : DockingRequestDenied("Docking request denied.");
        }

        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync(id, SimCommands.Dock, [structureId], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"docking of session {session.SessionId} at structure {structureId}: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        if (reply.Accepted)
            return;
        if (reply.ApproachDistance is { } distance)
            throw MachoErrors.ThrowUserError("DockingApproach", ("distance", new PyInt((long)Math.Max(0, Math.Floor(distance + 0.5)))));
        _log?.Invoke($"docking of session {session.SessionId} at structure {structureId} refused: {reply.Reason}");
        throw StructureDockingRefusal(reply.Reason);
    }

    /// <summary>Whether an id is an Upwell structure rather than a station of the static data (a structure is a record of the host).</summary>
    private async Task<bool> IsStructureAsync(long id, CancellationToken cancellationToken) =>
        _stations.Get(id) is null && StructureExistsAsync is not null && await StructureExistsAsync(id, cancellationToken);

    private static MachoWrappedException StructureDockingRefusal(string? reason) => reason switch
    {
        "NOT_IN_SPACE" or "SCENE_NOT_FOUND" => MachoErrors.ThrowUserError("DeniedShipChanged"),
        "STRUCTURE_NOT_FOUND" => MachoErrors.ThrowUserError("TargetingAttemptCancelled"),
        "STRUCTURE_DISABLED" => DockingRequestDenied("Structure docking is unavailable."),
        "SHIP_TOO_LARGE" => MachoErrors.ThrowUserError("DockingRequestDenied",
            ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Station/StationManagment/ShipTooLarge")))),
        "DOCKING_DISALLOWED_BY_MODULE" => DockingRequestDenied("You cannot dock while a module that prevents docking is active."),
        _ => DockingRequestDenied("Docking request denied."),
    };

    private static MachoWrappedException DockingRequestDenied(string reason) =>
        MachoErrors.ThrowUserError("DockingRequestDenied", ("reason", new PyStr(reason)));

    /// <summary>
    /// ship is refused as the reference refuses it (DeniedShipChanged). The ship is put in space near the structure
    /// </summary>
    public async Task UndockStructureAsync(ClientSession session, long structureId, long shipId, CancellationToken cancellationToken, bool ignoreContraband = false)
    {
        if (session.GetInt("structureid") != structureId || (shipId > 0 && session.GetInt("shipid") is { } active && active != shipId) ||
            IsStructureObserver(session) && IsInSpace(session))
            throw MachoErrors.ThrowUserError("DeniedShipChanged");
        if (IsStructureObserver(session)) await EndStructureControlAsync(session, false, cancellationToken);
        await UndockFromAsync(session, "structureid", null, cancellationToken, ignoreContraband);
    }
}
