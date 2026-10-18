using Eve.Content;
using System.Globalization;
using System.Text.Json;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services.Space;
using Eve.Space;
using Eve.Ships;
using Npgsql;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private async Task ValidateAbyssalFilamentSecurityAsync(
        long filamentItemId,
        int solarSystemId,
        CancellationToken cancellationToken)
    {
        var filamentItem = await items.GetAsync(filamentItemId, cancellationToken).ConfigureAwait(false)
            ?? throw AbyssalErrorAdapter.ClientError(7);
        if (!abyssalCatalog.FilamentsByTypeId.TryGetValue(filamentItem.TypeId, out var filament))
            return;

        var rows = await sde.ReadAsync("mapSolarSystems", [solarSystemId], cancellationToken).ConfigureAwait(false);
        var system = rows.SingleOrDefault()
            ?? throw new InvalidDataException($"Abyssal activation system {solarSystemId} is absent from the pinned SDE.");
        if (!system.Doc.TryGetProperty("securityStatus", out var securityNode) ||
            !securityNode.TryGetDouble(out var securityStatus) || !double.IsFinite(securityStatus))
        {
            throw new InvalidDataException($"Abyssal activation system {solarSystemId} has no finite SDE security status.");
        }
        if (AbyssalDeploymentEligibility.NormalizeSecurityBand(securityStatus) >= 0.9 && filament.Tier > 0)
            throw AbyssalErrorAdapter.SecurityRestricted();
    }

    private async Task PrepareAndMaterializeAbyssalRoomAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalRunState run,
        int locationId,
        CancellationToken cancellationToken)
    {
        var current = await foundations.Content.GetForUpdateAsync(connection, transaction, run.InstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The materialization run is missing.");
        var saved = current.Data<AbyssalRunData>() ?? throw new InvalidDataException("The materialization plan is missing.");
        if (current.Kind != AbyssalContentRuntime.Kind || saved.ContentId != run.Data.ContentId)
            throw new InvalidDataException("The materialization identity changed.");
        if (current.State != "active" || saved.ExitMode != AbyssalExitMode.None || saved.CurrentRoomIndex != locationId)
            throw new InvalidOperationException("The materialization intent has been superseded.");
        run = new AbyssalRunState(current.Id, current.State, current.ExpiresMs, saved);
        var room = await abyssalRoomSpawnReservation.PrepareAsync(
            connection, transaction, run, locationId, cancellationToken).ConfigureAwait(false);
        await MaterializeAbyssalRoomAsync(connection, transaction, run, room, cancellationToken).ConfigureAwait(false);
    }

    private async Task MaterializeAbyssalRoomRequestedAsync(
        Eve.Events.EventContext context,
        CancellationToken cancellationToken)
    {
        var request = context.Event.Payload<AbyssalRoomSpawnRequestedEvent>()
            ?? throw new InvalidDataException("The Abyssal room-spawn event payload is missing.");
        var instance = await foundations.Content.GetForUpdateAsync(context.Connection, context.Transaction,
            request.InstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The room-spawn run is missing.");
        var data = instance.Data<AbyssalRunData>() ?? throw new InvalidDataException("The room-spawn data is missing.");
        var savedRoom = data.Rooms.SingleOrDefault(value => value.LocationId == request.LocationId)
            ?? throw new InvalidDataException("The room-spawn ordinal is not in the saved plan.");
        var savedPlan = data.Plan.Rooms.Single(value => value.LocationId == request.LocationId);
        if (instance.Kind != AbyssalContentRuntime.Kind || !savedRoom.SpawnPrepared ||
            data.ContentId != request.ContentId || data.SpaceLocationId != request.SpaceLocationId ||
            data.Tier != request.Tier || data.WeatherType != request.WeatherType ||
            savedPlan.EnvironmentProfileId != request.EnvironmentProfileId ||
            !savedRoom.Entities.SequenceEqual(request.Entities) ||
            JsonSerializer.Serialize(savedPlan.Loot, TracePoseJson) != JsonSerializer.Serialize(request.Loot, TracePoseJson) ||
            JsonSerializer.Serialize(savedPlan.EnvironmentObjects, TracePoseJson) != JsonSerializer.Serialize(request.EnvironmentObjects, TracePoseJson) ||
            JsonSerializer.Serialize(savedPlan.Artifacts, TracePoseJson) != JsonSerializer.Serialize(request.Artifacts, TracePoseJson))
            throw new InvalidDataException("The room-spawn intent does not match its persisted snapshot.");
        if (instance.State != "active")
        {
            if (data.Participants.All(AbyssalParticipantSettled)) return;
            throw new InvalidOperationException("A superseded room spawn is waiting for terminal settlement receipts.");
        }
        if (data.CurrentRoomIndex > request.LocationId || data.ExitMode != AbyssalExitMode.None) return;
        if (data.CurrentRoomIndex != request.LocationId)
            throw new InvalidDataException("The room-spawn intent is for an unentered future room.");
        var run = new AbyssalRunState(instance.Id, instance.State, instance.ExpiresMs, data);
        if (run.Data.ContentId != request.ContentId ||
            run.Data.SpaceLocationId != request.SpaceLocationId ||
            run.Data.CurrentRoomIndex != request.LocationId)
        {
            throw new InvalidDataException("The Abyssal room-spawn event does not match its active run.");
        }
        await MaterializeAbyssalRoomAsync(context.Connection, context.Transaction, run, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task MaterializeAbyssalRoomAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalRunState run,
        AbyssalRoomSpawnRequestedEvent request,
        CancellationToken cancellationToken)
    {
        if (run.InstanceId != request.InstanceId || run.Data.ContentId != request.ContentId ||
            run.Data.SpaceLocationId != request.SpaceLocationId ||
            run.Data.RunExpiresAtMs is not { } expiresAtMs || expiresAtMs <= foundations.Scheduler.Now)
        {
            throw new InvalidDataException("The Abyssal room cannot be materialized for a mismatched or expired run.");
        }
        var plan = run.Data.Plan.Rooms.SingleOrDefault(room => room.LocationId == request.LocationId)
            ?? throw new InvalidDataException($"Abyssal run {run.InstanceId} has no room plan for {request.LocationId}.");
        if (plan.EnvironmentProfileId != request.EnvironmentProfileId)
            throw new InvalidDataException("The Abyssal room environment does not match the saved run plan.");

        var space = spaceSessions
            ?? throw new InvalidOperationException("Abyssal room materialization requires initialized SpaceSessions.");
        var scene = new SimSceneKey(request.SpaceLocationId, request.InstanceId, request.LocationId);
        var center = await abyssalRoomSpawnReservation.ResolvePrivateSceneOriginAsync(
            run.Data, cancellationToken).ConfigureAwait(false);

        RememberAbyssalWeather(run.Data);
        var boundary = new SimAbyssalBoundaryProfile(
            [center.X, center.Y, center.Z],
            75_000,
            1_000,
            [
                new SimAbyssalBoundaryDamagePoint(0, 10.55),
                new SimAbyssalBoundaryDamagePoint(75_000, 778.55),
            ],
            15_000);
        var sceneSpec = new SimAbyssalSceneSpec(
            scene,
            new SimAbyssalEnvironmentProfile([], []),
            boundary,
            DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs).ToFileTime());
        var sceneError = await space.EnsureAbyssalSceneAsync(
            sceneSpec,
            ResolveAbyssalDogmaEffects(request.ContentId, request.Tier),
            cancellationToken).ConfigureAwait(false);
        if (sceneError is not null)
            throw new InvalidOperationException($"Abyssal room scene {scene} could not be ensured: {sceneError}");

        var gateOpen = run.Data.Rooms.Single(room => room.LocationId == request.LocationId).Cleared;
        foreach (var entity in request.Entities)
        {
            var position = new[] { entity.Position.X, entity.Position.Y, entity.Position.Z };
            // EveJS buildEnvironmentalObjectSpawnState publishes this direction, including scene-center objects.
            var direction = entity.Role == AbyssalEntityRole.Cloud
                ? new[] { 0d, 1d, 0d }
                : AbyssalRoomSpawnPlanner.DirectionToward(entity.Position, center);
            switch (entity.Role)
            {
                case AbyssalEntityRole.Gate:
                {
                    var item = await items.LockAndGetAsync(connection, transaction, entity.EntityId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Abyssal gate item {entity.EntityId} is missing.");
                    var error = await space.EnsureAbyssalSceneObjectAsync(
                        request.ContentId, scene, item.Id, item.TypeId, item.OwnerId, position, direction,
                        gateOpen, cancellationToken).ConfigureAwait(false);
                    if (error is not null)
                        throw new InvalidOperationException($"Abyssal gate {item.Id} could not be materialized: {error}");
                    break;
                }
                case AbyssalEntityRole.Npc:
                {
                    if (run.Data.Rooms.Single(room => room.LocationId == request.LocationId).DestroyedNpcIds.Contains(entity.EntityId))
                        break;
                    var reply = await space.SpawnAbyssalNpcAsync(
                        request.ContentId, scene, entity.EntityId, entity.TypeId, position, direction,
                        cancellationToken).ConfigureAwait(false);
                    if (reply.Error is not null)
                        throw new InvalidOperationException($"Abyssal NPC {entity.EntityId} could not be spawned: {reply.Error}");
                    break;
                }
                case AbyssalEntityRole.Cloud:
                {
                    var definition = plan.EnvironmentObjects.Single(value => value.Key == entity.PlanKey && value.TypeId == entity.TypeId);
                    var item = await items.LockAndGetAsync(connection, transaction, entity.EntityId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Abyssal environment item {entity.EntityId} is missing.");
                    if (item.TypeId != definition.TypeId || item.LocationId != request.SpaceLocationId ||
                        item.OwnerId != AbyssalTraceFactory.TraceOwnerId || !item.Singleton || item.Quantity != 1)
                        throw new InvalidDataException("The environmental object's persisted authority does not match its plan.");
                    Eve.Content.AbyssalEnvironmentalTypeDefinition? mechanics = null;
                    if (definition.MechanicsEnabled)
                        abyssalCatalog.EnvironmentalTypesById.TryGetValue(entity.TypeId, out mechanics);
                    var error = await space.EnsureAbyssalEnvironmentObjectAsync(request.ContentId, scene,
                        item.Id, item.TypeId, item.OwnerId, position, direction, mechanics, cancellationToken,
                        presentationRadiusMeters: definition.PresentationRadiusMeters).ConfigureAwait(false);
                    if (error is not null)
                        throw new InvalidOperationException($"Abyssal environment {entity.EntityId} could not be materialized: {error}");
                    break;
                }
                case AbyssalEntityRole.Container:
                {
                    var cacheItem = await items.LockAndGetAsync(connection, transaction, entity.EntityId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Abyssal cache {entity.EntityId} is missing.");
                    var receipt = System.Text.Json.JsonSerializer.Deserialize<AbyssalCacheReceipt>(
                        cacheItem.CustomInfo ?? string.Empty, TracePoseJson)
                        ?? throw new InvalidDataException($"Abyssal cache {entity.EntityId} has no source receipt.");
                    if (cacheItem.TypeId != entity.TypeId || cacheItem.LocationId != request.SpaceLocationId ||
                        cacheItem.OwnerId != AbyssalTraceFactory.TraceOwnerId || cacheItem.Flag != CharacterShips.InSpaceFlag ||
                        !cacheItem.Singleton || cacheItem.Quantity != 1 || receipt.SchemaVersion != 2 ||
                        receipt.ContentId != request.ContentId || receipt.RoomOrdinal != request.LocationId)
                        throw new InvalidDataException("The cache source item or receipt belongs to another identity.");
                    if (receipt.DestroyedAtMs is not null)
                        break;
                    var error = await space.EnsureAbyssalRewardCacheAsync(
                        request.ContentId, scene, cacheItem.Id, cacheItem.TypeId, cacheItem.OwnerId,
                        position, direction, cancellationToken).ConfigureAwait(false);
                    if (error is not null)
                        throw new InvalidOperationException($"Abyssal cache {entity.EntityId} could not be materialized: {error}");
                    break;
                }
                default:
                    throw new InvalidDataException($"Abyssal entity {entity.EntityId} has unsupported role {entity.Role}.");
            }
        }
    }

    private async Task<AbyssalRunState> FindActiveAbyssalRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await foundations.Content.GetInTransactionAsync(connection, transaction, instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Abyssal ContentRuntime instance {instanceId} is missing.");
        if (instance.Kind != AbyssalContentRuntime.Kind || instance.State != "active")
            throw new InvalidDataException($"Abyssal ContentRuntime instance {instanceId} is not active.");
        var data = instance.Data<AbyssalRunData>()
            ?? throw new InvalidDataException($"Abyssal ContentRuntime instance {instanceId} has no saved run data.");
        return new AbyssalRunState(instance.Id, instance.State, instance.ExpiresMs, data);
    }

    private async Task<bool> IsCommittedAbyssalEndGateAsync(Eve.Services.ClientSession session, long markerId, CancellationToken ct)
    {
        var characterId = session.CharacterId;
        var shipId = spaceSessions?.ShipIdOf(session);
        if (characterId is not > 0 || shipId is not > 0 || markerId <= 0) return false;
        await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT data FROM {foundations.ContentSchema}.instances WHERE kind = $1 AND data->>'exitGateMarkerId' = $2", connection);
        command.Parameters.AddWithValue(AbyssalContentRuntime.Kind);
        command.Parameters.AddWithValue(markerId.ToString(CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var data = JsonSerializer.Deserialize<AbyssalRunData>(reader.GetString(0), TracePoseJson)
                ?? throw new InvalidDataException("The committed origin-gate data is missing.");
            if (data.ExitMode == AbyssalExitMode.FinalGate && data.ExitGateMarkerId == markerId &&
                data.Participants.Any(value => value.CharacterId == characterId && value.ShipId == shipId &&
                    value.Status is AbyssalParticipantStatus.Returning or AbyssalParticipantStatus.Returned))
                return true;
        }
        return false;
    }

    private async Task ValidateAbyssalGateRangeAsync(
        AbyssalParticipantRunBinding binding, long markerId, CancellationToken ct)
    {
        if (binding.Run.Data.GateTransitions.Any(transition => transition.GateMarkerId == markerId &&
                transition.Recipients.Any(recipient => recipient.CharacterId == binding.Participant.CharacterId &&
                    recipient.ShipId == binding.Participant.ShipId)) ||
            binding.Run.Data.ExitMode == AbyssalExitMode.FinalGate && binding.Run.Data.ExitGateMarkerId == markerId)
            return;
        var room = binding.Run.Data.Rooms.Single(value => value.LocationId == binding.Participant.CurrentRoomIndex);
        var gate = room.Entities.SingleOrDefault(entity => entity.Role == AbyssalEntityRole.Gate && entity.EntityId == markerId)
            ?? throw AbyssalErrorAdapter.FromRule(
                new AbyssalRuleException(AbyssalRuleCode.InvalidGate, "the marker is not this room's conduit"), true);
        var pose = await (spaceSessions ?? throw new InvalidOperationException("Conduit validation requires SpaceSessions."))
            .GetAbyssalParticipantPoseAsync(binding.Run.Data.ContentId, binding.Participant.ShipId,
                new SimSceneKey(binding.Run.Data.SpaceLocationId, binding.Run.InstanceId, room.LocationId), ct).ConfigureAwait(false);
        if (!pose.Found)
            throw AbyssalErrorAdapter.FromRule(
                new AbyssalRuleException(AbyssalRuleCode.InvalidGate, "the authoritative participant pose is unavailable"), true);
        var rows = await sde.ReadAsync("types", [gate.TypeId, binding.Participant.ShipTypeId], ct).ConfigureAwait(false);
        double Radius(int typeId)
        {
            var row = rows.Single(value => value.Key == typeId);
            if (!row.Doc.TryGetProperty("radius", out var value) || !value.TryGetDouble(out var radius) ||
                !double.IsFinite(radius) || radius < 0)
                throw new InvalidDataException($"Conduit range type {typeId} has no finite SDE radius.");
            return radius;
        }
        if (!AbyssalTraceRange.IsWithin(pose.Position, Radius(binding.Participant.ShipTypeId),
                [gate.Position.X, gate.Position.Y, gate.Position.Z], Radius(gate.TypeId)))
            throw AbyssalErrorAdapter.FromRule(
                new AbyssalRuleException(AbyssalRuleCode.InvalidGate, "the conduit is beyond its surface activation range"), true);
    }

    private async Task SetAbyssalGateOpenAsync(
        AbyssalRunState run,
        int locationId,
        bool isOpen,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await foundations.Content.GetForUpdateAsync(connection, transaction, run.InstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The gate-update run is missing.");
        var data = current.Data<AbyssalRunData>() ?? throw new InvalidDataException("The gate-update plan is missing.");
        if (data.ContentId != run.Data.ContentId || current.Kind != AbyssalContentRuntime.Kind)
            throw new InvalidDataException("The gate-update identity changed.");
        if (current.State != "active" || data.ExitMode != AbyssalExitMode.None || data.CurrentRoomIndex != locationId) return;
        run = new AbyssalRunState(current.Id, current.State, current.ExpiresMs, data);
        var gate = run.Data.Rooms.Single(room => room.LocationId == locationId).Entities
            .Single(entity => entity.Role == AbyssalEntityRole.Gate);
        var item = await items.LockAndGetAsync(connection, transaction, gate.EntityId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Abyssal gate item {gate.EntityId} is missing.");
        var center = await abyssalRoomSpawnReservation.ResolvePrivateSceneOriginAsync(run.Data, cancellationToken)
            .ConfigureAwait(false);
        var scene = new SimSceneKey(run.Data.SpaceLocationId, run.InstanceId, locationId);
        var error = await (spaceSessions
            ?? throw new InvalidOperationException("Abyssal gate updates require initialized SpaceSessions."))
            .EnsureAbyssalSceneObjectAsync(
                run.Data.ContentId,
                scene,
                item.Id,
                item.TypeId,
                item.OwnerId,
                [gate.Position.X, gate.Position.Y, gate.Position.Z],
                AbyssalRoomSpawnPlanner.DirectionToward(gate.Position, center),
                isOpen,
                cancellationToken).ConfigureAwait(false);
        if (error is not null)
            throw new InvalidOperationException($"Abyssal gate {item.Id} could not be updated: {error}");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ReconcileAbyssalSimulationLostAsync(
        AbyssalSimulationRunLost notice,
        CancellationToken cancellationToken)
    {
        if (notice.InstanceId <= 0 || !Guid.TryParse(notice.ContentId, out _))
            throw new InvalidDataException("The Abyssal simulation-loss identity is invalid.");

        var instance = await foundations.Content.GetAsync(notice.InstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Abyssal ContentRuntime instance {notice.InstanceId} is missing after simulation loss.");
        var data = instance.Data<AbyssalRunData>()
            ?? throw new InvalidDataException($"Abyssal ContentRuntime instance {notice.InstanceId} has no run data.");
        if (instance.Kind != AbyssalContentRuntime.Kind ||
            !string.Equals(data.ContentId, notice.ContentId, StringComparison.Ordinal) ||
            notice.Participants.Any(lost => !data.Participants.Any(participant =>
                participant.CharacterId == lost.CharacterId &&
                participant.ShipId == lost.ShipId &&
                participant.CurrentRoomIndex == lost.RoomIndex)) )
        {
            throw new InvalidDataException($"Abyssal simulation loss for instance {notice.InstanceId} does not match its persisted participant roster.");
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await abyssalRuntime.RecoverAfterServerRestartAsync(
            connection, transaction, notice.InstanceId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        foundations.Events.Wake();
    }

    private async Task ProcessAbyssalReturnRequestedAsync(
        Eve.Events.EventContext context,
        CancellationToken cancellationToken)
    {
        var request = context.Event.Payload<AbyssalParticipantReturnRequestedEvent>()
            ?? throw new InvalidDataException("The Abyssal participant-return event payload is missing.");
        if (request.DeathApplied) throw new InvalidDataException("A lethal return requires the death settlement path.");
        var instance = await foundations.Content.GetInTransactionAsync(
            context.Connection, context.Transaction, request.InstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Abyssal return run is missing.");
        var data = instance.Data<AbyssalRunData>() ?? throw new InvalidDataException("The Abyssal return data is missing.");
        var participant = data.Participants.SingleOrDefault(value => value.CharacterId == request.RecipientCharacterId)
            ?? throw new InvalidDataException("The returning participant is missing.");
        if (instance.Kind != AbyssalContentRuntime.Kind || data.ContentId != request.ContentId ||
            participant.ShipId != request.ShipId || participant.Origin != request.Origin)
            throw new InvalidDataException("The return identity does not match its persisted run.");
        if (participant.Status == AbyssalParticipantStatus.Returned) return;
        if (instance.State != "active" || participant.Status != AbyssalParticipantStatus.Returning) return;

        var room = data.Plan.Rooms.Single(value => value.LocationId == participant.CurrentRoomIndex);
        await NotifyAbyssalExitAsync(new AbyssalLocationExitedEvent(request.InstanceId, request.ContentId,
            request.RecipientCharacterId, participant.CurrentRoomIndex, room.EnvironmentProfileId)).ConfigureAwait(false);

        var space = spaceSessions ?? throw new InvalidOperationException("Abyssal return requires SpaceSessions.");
        var sourceScene = new SimSceneKey(data.SpaceLocationId, instance.Id, participant.CurrentRoomIndex);
        if (request.Administrative)
        {
            var character = await characters.LockAndGetAsync(context.Connection, context.Transaction,
                request.RecipientCharacterId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The returning character is missing.");
            var ship = await items.LockAndGetAsync(context.Connection, context.Transaction,
                request.ShipId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The returning ship is missing.");
            if (character.ShipId != ship.Id || ship.OwnerId != character.Id ||
                ship.LocationId != data.SpaceLocationId && ship.LocationId != request.Origin.SolarSystemId)
                throw new InvalidDataException("The administrative return ship authority is invalid.");
            var detached = await space.DetachAbyssalParticipantAsync(
                request.ContentId, sourceScene, character, ship, cancellationToken).ConfigureAwait(false);
            if (!detached.Succeeded) throw new InvalidOperationException(detached.Error);
            await PersistAbyssalOriginAsync(context.Connection, context.Transaction, request, cancellationToken).ConfigureAwait(false);
            if (characterSessions.Of(request.RecipientCharacterId) is not null)
            {
                await foundations.Events.PublishAsync(context.Connection, context.Transaction, AbyssalAdministrativeArrivalTopic,
                    request, $"abyssal:{request.ContentId}:return:{request.RecipientCharacterId}:arrival", cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            var error = await space.MoveAbyssalParticipantAsync(request.ContentId, request.ShipId, sourceScene,
                SimSceneKey.SolarSystem(checked((int)request.Origin.SolarSystemId)),
                [request.Origin.ShipPosition.X, request.Origin.ShipPosition.Y, request.Origin.ShipPosition.Z],
                [request.Origin.ShipDirection.X, request.Origin.ShipDirection.Y, request.Origin.ShipDirection.Z],
                cancellationToken).ConfigureAwait(false);
            if (error is not null) throw new InvalidOperationException($"Abyssal origin transfer failed: {error}");
            await PersistAbyssalOriginAsync(context.Connection, context.Transaction, request, cancellationToken).ConfigureAwait(false);
        }
        await abyssalRuntime.RecordOriginReturnAsync(context.Connection, context.Transaction,
            new AbyssalReturnReceiptRequest(request.InstanceId, request.RecipientCharacterId), cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistAbyssalCharacterLocationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        int solarSystemId,
        CancellationToken cancellationToken)
    {
        var rows = await sde.ReadAsync("mapSolarSystems", [solarSystemId], cancellationToken).ConfigureAwait(false);
        var system = rows.SingleOrDefault()
            ?? throw new InvalidDataException($"Abyssal arrival system {solarSystemId} is absent from the pinned SDE.");
        if (!system.Doc.TryGetProperty("constellationID", out var constellationNode) ||
            !constellationNode.TryGetInt32(out var constellationId) ||
            !system.Doc.TryGetProperty("regionID", out var regionNode) ||
            !regionNode.TryGetInt32(out var regionId))
        {
            throw new InvalidDataException($"Abyssal arrival system {solarSystemId} has no SDE constellation/region.");
        }
        var character = await characters.LockAndGetAsync(connection, transaction, characterId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Abyssal participant {characterId} is missing.");
        var ship = character.ShipId is { } shipId
            ? await items.LockAndGetAsync(connection, transaction, shipId, cancellationToken).ConfigureAwait(false)
            : null;
        if (ship is null || ship.OwnerId != characterId || !ship.Singleton || ship.Quantity != 1)
            throw new InvalidDataException($"Abyssal participant {characterId} has no authoritative owned ship.");
        await items.MoveAsync(connection, transaction, ship.Id, solarSystemId, CharacterShips.InSpaceFlag, cancellationToken).ConfigureAwait(false);
        await characters.SetLocationAsync(
            connection, transaction, characterId, solarSystemId, constellationId, regionId, cancellationToken)
            .ConfigureAwait(false);
    }
}
