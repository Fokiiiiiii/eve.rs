using System.Text.Json;
using Eve.Content;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Space;
using Eve.Ships;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private async ValueTask<PyValue> EnterAbyssalRunAsync(
        ServiceCall call,
        CancellationToken cancellationToken)
    {
        var characterId = call.Session.GetInt("charid") ?? call.Session.GetInt("characterID") ?? 0;
        var shipId = spaceSessions?.ShipIdOf(call.Session);
        var traceItemId = PositiveAbyssalRpcId(call.Arg(0));
        if (characterId <= 0 || shipId is not > 0 || traceItemId <= 0)
            throw AbyssalErrorAdapter.ClientError(7);

        await using (var retryConnection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var retryTransaction = await retryConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            var existing = await abyssalRuntime.FindByTraceItemIdAsync(retryConnection, retryTransaction, traceItemId, cancellationToken).ConfigureAwait(false);
            if (existing?.ContentState == "active" && existing.Data.Participants.Any(participant =>
                    participant.CharacterId == characterId && participant.ShipId == shipId.Value &&
                    participant.Status is AbyssalParticipantStatus.Active or AbyssalParticipantStatus.Disconnected))
            {
                foundations.Events.Wake();
                return PyNone.Instance;
            }
        }
        var character = await characters.GetAsync(characterId, cancellationToken).ConfigureAwait(false)
            ?? throw AbyssalErrorAdapter.ClientError(7);
        if (character.ShipId != shipId.Value || character.SolarSystemId <= 0 || character.StationId > 0)
            throw AbyssalErrorAdapter.ClientError(22);

        var space = spaceSessions
            ?? throw new InvalidOperationException("Abyssal entrance requires initialized SpaceSessions.");
        var pose = await space.GetAbyssalDeploymentPoseAsync(
            shipId.Value,
            SimSceneKey.SolarSystem(character.SolarSystemId),
            cancellationToken).ConfigureAwait(false);
        if (!pose.Found || pose.ShipId != shipId.Value ||
            pose.Scene != SimSceneKey.SolarSystem(character.SolarSystemId))
        {
            throw AbyssalErrorAdapter.ClientError(22);
        }

        var ship = await items.GetAsync(shipId.Value, cancellationToken).ConfigureAwait(false)
            ?? throw AbyssalErrorAdapter.ClientError(7);
        var shipType = await itemTypes.GetAsync(ship.TypeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Abyssal participant ship type {ship.TypeId} is absent from the pinned SDE.");
        var shipRows = await sde.ReadAsync("types", [ship.TypeId], cancellationToken).ConfigureAwait(false);
        var shipRow = shipRows.SingleOrDefault()
            ?? throw new InvalidDataException($"Abyssal participant ship type {ship.TypeId} is absent from the pinned SDE.");
        if (!shipRow.Doc.TryGetProperty("radius", out var radiusValue) ||
            !radiusValue.TryGetDouble(out var radius) || !double.IsFinite(radius) || radius < 0)
        {
            throw new InvalidDataException($"Abyssal participant ship type {ship.TypeId} has no valid SDE radius.");
        }

        var fleetMember = await fleetStore.MemberForCharacterAsync(characterId, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var run = await abyssalRuntime.FindByTraceItemIdAsync(
                connection, transaction, traceItemId, cancellationToken).ConfigureAwait(false)
                ?? throw AbyssalErrorAdapter.ClientError(7);
            var traceItem = await items.LockAndGetAsync(
                connection, transaction, traceItemId, cancellationToken).ConfigureAwait(false)
                ?? throw AbyssalErrorAdapter.ClientError(7);
            ValidatePersistedTraceItem(traceItem, run.Data);
            if (run.ContentState != "active" ||
                run.Data.SourceSolarSystemId != character.SolarSystemId ||
                run.Data.TraceExpiresAtMs <= foundations.Scheduler.Now)
            {
                throw new AbyssalRuleException(AbyssalRuleCode.TraceExpired, "the deployed entrance is unavailable in this system");
            }
            var tracePose = JsonSerializer.Deserialize<AbyssalTracePoseSnapshot>(
                traceItem.CustomInfo ?? string.Empty, TracePoseJson)
                ?? throw new InvalidDataException($"Abyssal trace {traceItemId} has no persisted pose.");
            if (!AbyssalTraceRange.IsWithin(
                    pose.Position, radius, tracePose.Position, abyssalTraceRadius))
            {
                throw AbyssalErrorAdapter.ClientError(
                    29, new PyInt((long)AbyssalTraceRange.MaximumSurfaceDistanceMeters));
            }
            if (ship.OwnerId != characterId || ship.LocationId != character.SolarSystemId ||
                ship.Flag != CharacterShips.InSpaceFlag || !ship.Singleton || ship.Quantity != 1 ||
                !abyssalCatalog.IsShipAllowed(run.Data.GameModeId, ship.TypeId))
            {
                throw AbyssalErrorAdapter.FromRule(
                    new AbyssalRuleException(AbyssalRuleCode.InvalidShipClass,
                        "the active ship is not an eligible in-space hull for this run"),
                    isGateActivation: true);
            }

            var shipClass = run.Data.GameModeId switch
            {
                AbyssalGameMode.SoloCruiser => "cruiser",
                AbyssalGameMode.ThreeFrigates => "frigate",
                AbyssalGameMode.TwoDestroyers => "destroyer",
                _ => throw new AbyssalRuleException(AbyssalRuleCode.InvalidGameMode, "the run has an unsupported PvE Abyss mode"),
            };
            var admission = new AbyssalParticipantAdmission(
                characterId,
                shipId.Value,
                ship.TypeId,
                shipClass,
                fleetMember?.FleetId,
                new AbyssalParticipantOrigin(
                    character.SolarSystemId,
                    character.SolarSystemId,
                    new AbyssalPosition(pose.Position[0], pose.Position[1], pose.Position[2]),
                    new AbyssalPosition(pose.Direction[0], pose.Direction[1], pose.Direction[2]),
                    radius));
            var entered = await abyssalRuntime.EnterAsync(
                connection,
                transaction,
                new AbyssalEnterRequest(run.InstanceId, admission),
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (entered.Status is AbyssalOperationStatus.Applied or AbyssalOperationStatus.AlreadyApplied)
                foundations.Events.Wake();
            return PyNone.Instance;
        }
        catch (AbyssalRuleException error)
        {
            throw AbyssalErrorAdapter.FromRule(error, isGateActivation: true);
        }
    }
}
