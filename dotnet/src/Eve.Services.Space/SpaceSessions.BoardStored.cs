using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>Applies the simulation, session, notifications and insurance effects after the stored-ship transaction commits.</summary>
    public async Task<PyValue> ApplyStoredShipBoardingAsync(
        ClientSession session,
        StoredShipBoarding boarding,
        IReadOnlyList<double> position,
        IReadOnlyList<double> direction,
        CancellationToken cancellationToken)
    {
        AssertAbyssalShipSwapAllowed(session);
        CancelSafeLogoff(session);
        var id = (ulong)session.SessionId;
        if (session.CharacterId is not { } characterId ||
            !_pilots.TryGetValue(id, out var currentPilot) ||
            currentPilot.CharacterId != characterId ||
            session.GetInt("shipid") != currentPilot.ShipId ||
            _transitions.SessionChangeActive(id) || position.Count < 3 || direction.Count < 3)
            return PyNone.Instance;

        var simulationFailed = false;
        var nextShipTypeId = 0;
        PyValue activationState = PyNone.Instance;
        var transition = await _transitions.ExecuteAsync(
            _ => Task.FromResult(boarding),
            _ => true,
            async (result, token) =>
            {
                await CancelRepairsAsync(currentPilot, token);
                if (currentPilot.TypeId == CharacterShips.CapsuleTypeId)
                {
                    _ejectedShips[currentPilot.ShipId] = new EjectedShip(
                        characterId,
                        result.Boarding.BoarderCorporationId,
                        currentPilot.TypeId,
                        currentPilot.SolarSystemId,
                        OwnerAllianceId: session.GetInt("allianceid") ?? session.GetInt("allianceID") ?? 0,
                        OwnerWarFactionId: session.GetInt("warfactionid") ?? session.GetInt("warFactionID") ?? 0);
                }
                _sim.Leave(id, LeaveWhy.Boarded);
                if (await _ships.LoadAsync(characterId, token) is not { } fitting || fitting.Ship.Id != result.Boarding.ShipId)
                {
                    _log?.Invoke($"the simulation could not load stored ship {result.Boarding.ShipId} for character {characterId}");
                    simulationFailed = true;
                    session.Close();
                    return;
                }

                nextShipTypeId = fitting.Ship.TypeId;
                activationState = new Eve.Services.Dogma.DogmaInfo(_statics).ActivationState(fitting, FileTimeNow());
                var refusal = await _sim.RespawnAsync(new SimRespawn(
                    id,
                    SimShips.Of(fitting, _statics, await SecurityStatusAsync(characterId, token)),
                    position.Take(3).ToArray(),
                    direction.Take(3).ToArray()), token);
                if (refusal is not null)
                {
                    _log?.Invoke($"the simulation refused stored ship {result.Boarding.ShipId} for character {characterId}: {refusal}");
                    simulationFailed = true;
                    session.Close();
                }
            },
            (result, token) =>
            {
                if (simulationFailed)
                    return Task.FromResult(false);
                var nextPilot = new Pilot(session, characterId, result.Boarding.ShipId, nextShipTypeId, currentPilot.SolarSystemId, 0);
                EndTetherPilot(currentPilot);
                currentPilot.End();
                if (!_pilots.TryUpdate(id, nextPilot, currentPilot))
                {
                    session.Close();
                    return Task.FromResult(false);
                }
                session.Change(("shipid", new PyLong(result.Boarding.ShipId)));
                _ejectedShips.TryRemove(result.Boarding.ShipId, out var _removedBoarding);
                Background(() => RefreshTetherPolicyAsync(session));
                return Task.FromResult(true);
            },
            async (result, _) =>
            {
                foreach (var change in result.Change.Stacks ?? [])
                {
                    ShipNotifications.SendItemChange(session, change,
                        item => Eve.Services.Dogma.DogmaInfo.RowOf(item, _statics.Type(item.TypeId)));
                }
                if (result.Boarding.PreviousOwnerId != characterId && InvalidateInsuranceForBoarding is { } invalidateInsurance)
                {
                    try
                    {
                        await invalidateInsurance(new ShipBoarded(
                            result.Boarding.ShipId,
                            result.Boarding.PreviousOwnerId,
                            result.Boarding.BoarderCharacterId,
                            result.Boarding.BoarderCorporationId,
                            FileTimeNow()), CancellationToken.None);
                    }
                    catch (Exception error)
                    {
                        _log?.Invoke($"insurance for stored ship {result.Boarding.ShipId} could not be invalidated: {error.Message}");
                    }
                }
            },
            cancellationToken,
            id);

        return transition is not null && !simulationFailed ? activationState : PyNone.Instance;
    }
}
