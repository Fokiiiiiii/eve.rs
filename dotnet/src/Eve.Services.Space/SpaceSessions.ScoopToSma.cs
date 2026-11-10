using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>Recovers an abandoned ship into the active ship's SDE-backed maintenance bay.</summary>
    public async Task<ItemChanged> ScoopToSMAAsync(ClientSession session, long targetShipId, CancellationToken cancellationToken)
    {
        if (targetShipId <= 0 || session.CharacterId is not { } characterId ||
            !_pilots.TryGetValue((ulong)session.SessionId, out var pilot) || !ReferenceEquals(pilot.Session, session) ||
            !IsInSpace(session) || pilot.CharacterId != characterId || session.GetInt("shipid") != pilot.ShipId ||
            SolarSystemOf(session) is not { } systemId)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You must be in space to recover a ship.")));

        if (await _ships.LoadAsync(characterId, cancellationToken) is not { } active || active.Ship.Id != pilot.ShipId)
            throw MachoErrors.ThrowUserError("DeniedShipChanged");
        var capacityAttribute = _statics.AttributeId("shipMaintenanceBayCapacity");
        if (capacityAttribute is null || active.World.Get(active.Ship.Id, capacityAttribute.Value) <= 0)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Your ship does not have a ship maintenance bay.")));

        if (targetShipId == pilot.ShipId)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You cannot recover a ship that is being piloted.")));
        if (!_ejectedShips.TryGetValue(targetShipId, out var abandoned) || abandoned.SolarSystemId != systemId)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That ship is not in space near you.")));

        await _statics.EnsureTypesAsync([abandoned.ShipTypeId], cancellationToken);
        if (_statics.Type(abandoned.ShipTypeId) is not { CategoryId: 6 } || abandoned.ShipTypeId == CharacterShips.CapsuleTypeId)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That ship could not be found.")));

        var ownPosition = await PositionOfAsync(session, pilot.ShipId, cancellationToken);
        var targetPosition = await PositionOfAsync(session, targetShipId, cancellationToken);
        if (ownPosition is not { } own || targetPosition is not { } target || own.SolarSystemId != systemId || target.SolarSystemId != systemId)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That ship is not in space near you.")));

        CommandReply measure;
        try
        {
            measure = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.Measure, [targetShipId], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"measuring abandoned ship {targetShipId} for scoop: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }
        if (!measure.Accepted || measure.Distance is not { } distance)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That ship is not in space near you.")));
        if (distance > SpaceContainers.OpenRange)
            throw MachoErrors.ThrowUserError("CantUseTooDistantWithType",
                ("desired", new PyInt((long)SpaceContainers.OpenRange)),
                ("actual", new PyInt((long)Math.Floor(distance + 0.5))),
                ("typeID", new PyInt(abandoned.ShipTypeId)));

        ShipOutcome outcome;
        try
        {
            outcome = await _ships.ScoopShipToMaintenanceBayAsync(
                characterId, pilot.ShipId, systemId, targetShipId,
                async token =>
                {
                    var reply = await _sim.RemoveUnpilotedShipAsync(
                        new SimRemoveUnpilotedShip(SimClient.NextRequestId(), SimSceneKey.SolarSystem(systemId), targetShipId), token);
                    return reply.Removed && reply.Error is null;
                }, cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"removing abandoned ship {targetShipId} during scoop: {error.Message}");
            throw Notify("Space is not available right now. Try again in a moment.");
        }

        switch (outcome)
        {
            case ShipChanged { Stacks: { Count: 1 } stacks }:
                _ejectedShips.TryRemove(targetShipId, out _);
                return stacks[0];
            case ShipRefused refused:
                throw ShipErrors.Of(refused, _statics);
            default:
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That ship is not in space near you.")));
        }
    }
}
