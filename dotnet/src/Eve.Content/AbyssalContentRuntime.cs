using System.Globalization;
using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Content;

public sealed class AbyssalContentRuntime
{
    public const string Kind = "abyssal";
    public const string DefinitionId = "abyssal-run-v1";
    public const long RunDurationMs = 20 * 60 * 1000;

    public const string LocationEnteredTopic = "abyssal.location.entered";
    public const string DeadlineAddedTopic = "abyssal.deadline.added";
    public const string RoomSpawnRequestedTopic = "abyssal.room.spawn_requested";
    public const string RoomTransitionRequestedTopic = "abyssal.room.transition.requested";
    public const string LocationExitedTopic = "abyssal.location.exited";
    public const string ParticipantReturnRequestedTopic = "abyssal.participant.return_requested";
    public const string ParticipantDeathSettlementRequestedTopic = "abyssal.participant.death_settlement_requested";
    public const string ParticipantContentFinishedTopic = "abyssal.participant.content_finished";

    private static readonly ObjectiveDef[] RunObjectives =
    [
        new("room-clear-1", "manual"),
        new("room-clear-2", "manual"),
        new("room-clear-3", "manual"),
        new("origin-return", "manual"),
    ];

    private readonly ContentRuntime _content;
    private readonly EventBus _bus;
    private readonly DurableScheduler _scheduler;

    public AbyssalContentRuntime(ContentRuntime content, EventBus bus, DurableScheduler scheduler)
    {
        _content = content;
        _bus = bus;
        _scheduler = scheduler;
        _content.RegisterAction("abyssal.queue-death-settlements", async (context, _, token) =>
            await QueueTerminalDeathSettlementRequestsAsync(context, token));
        _content.Define(new ContentDefinition(Kind, DefinitionId, RunObjectives)
        {
            OnFail = [new ActionRef("abyssal.queue-death-settlements")],
        });
    }

    /// <summary>
    /// Locks the creator and fleet admission keys in a stable order. The Host must call this
    /// before filament consumption, then consume, start, and commit in the same transaction.
    /// </summary>
    public async Task AcquireAdmissionLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long creatorCharacterId,
        long? fleetId,
        CancellationToken cancellationToken = default)
    {
        if (creatorCharacterId <= 0 || fleetId is <= 0)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "creator and fleet identifiers must be positive");
        }

        var keys = new List<string> { "abyssal:creator:" + creatorCharacterId.ToString(CultureInfo.InvariantCulture) };
        if (fleetId is { } id)
        {
            keys.Add("abyssal:fleet:" + id.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var key in keys.Order(StringComparer.Ordinal))
        {
            await Sql.ScalarAsync(connection, transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))",
                cancellationToken, key);
        }
    }

    public async Task<AbyssalOperationResult> StartAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await AcquireAdmissionLockAsync(connection, transaction, request.CreatorCharacterId, request.FleetId, cancellationToken);

        var uniqueKey = request.TraceItemId.ToString(CultureInfo.InvariantCulture);
        var existingId = await _content.FindAsync(connection, transaction, Kind, uniqueKey, cancellationToken);
        if (existingId is { } id)
        {
            var existing = await RequireInstanceAsync(connection, transaction, id, cancellationToken);
            EnsureSameTraceRequest(DataOf(existing), request);
            return Result(AbyssalOperationStatus.AlreadyApplied, existing);
        }

        ValidateStart(request, _scheduler.Now);
        var conflict = await FindAdmissionConflictUnderLockAsync(
            connection, transaction, request.CreatorCharacterId, request.FleetId, cancellationToken);
        if (conflict is not null)
        {
            throw Rule(AbyssalRuleCode.AdmissionConflict,
                "the creator or cooperative fleet already has an active Abyssal deployment");
        }

        var contentType = ContentTypeForGameMode(request.GameModeId);
        var contentId = Guid.NewGuid().ToString("D");
        var rooms = request.Plan.Rooms
            .Select(room => new AbyssalRoomState(
                room.LocationId,
                room.NebulaGraphicId!.Value,
                false,
                false,
                Array.Empty<AbyssalSpaceEntity>(),
                Array.Empty<long>()))
            .ToArray();
        var data = new AbyssalRunData(
            1,
            contentId,
            request.TraceItemId,
            request.CreatorCharacterId,
            request.CreatorShipId,
            request.FleetId,
            request.GameModeId,
            contentType,
            request.FilamentTypeId,
            request.Plan.Tier,
            request.Plan.WeatherType,
            request.SourceSolarSystemId,
            request.SourceLocationId,
            request.Plan.SpaceLocationId,
            request.SourcePosition,
            request.TraceExpiresAtMs,
            request.Plan,
            null,
            null,
            0,
            Array.Empty<AbyssalParticipantData>(),
            rooms,
            Array.Empty<AbyssalGateTransition>(),
            AbyssalExitMode.None,
            null)
        {
            // Rust visibility::GRID_BOX is 7,864,320 m; use its midpoint for this free private-scene frame.
            PrivateSceneOrigin = new AbyssalPosition(3_932_160, 3_932_160, 3_932_160),
        };

        var remainingTraceMs = request.TraceExpiresAtMs - _scheduler.Now;
        var created = await _content.StartAsync(
            connection,
            transaction,
            DefinitionId,
            request.CreatorCharacterId,
            data,
            uniqueKey,
            remainingTraceMs,
            cancellationToken);
        var instance = await RequireInstanceAsync(connection, transaction, created.Id, cancellationToken);
        EnsureSameTraceRequest(DataOf(instance), request);
        return Result(created.Created ? AbyssalOperationStatus.Applied : AbyssalOperationStatus.AlreadyApplied, instance);
    }

    /// <summary>Finds the latest ContentRuntime state for a trace item in the caller's transaction.</summary>
    public async Task<AbyssalRunState?> FindByTraceItemIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long traceItemId,
        CancellationToken cancellationToken = default)
    {
        if (traceItemId <= 0)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "the trace item identifier must be positive");
        }
        var instanceId = await _content.FindAsync(connection, transaction, Kind,
            traceItemId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        if (instanceId is null)
        {
            return null;
        }
        var instance = await _content.GetInTransactionAsync(connection, transaction, instanceId.Value, cancellationToken);
        return instance is null ? null : ToState(instance);
    }

/// <summary>
    /// Finds an active deployment for the creator or fleet while holding the caller transaction's admission lock.
    /// Keep this transaction open through filament consumption and <see cref="StartAsync"/> so competing deployments serialize.
    /// A retry of the same active trace by the same creator and fleet is not a conflict.
    /// </summary>
    public async Task<AbyssalRunState?> FindAdmissionConflictAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long creatorCharacterId,
        long? fleetId,
        long traceItemId,
        CancellationToken cancellationToken = default)
    {
        if (traceItemId <= 0)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "the trace item identifier must be positive");
        }
        await AcquireAdmissionLockAsync(connection, transaction, creatorCharacterId, fleetId, cancellationToken);

        var sameTraceId = await _content.FindAsync(connection, transaction, Kind,
            traceItemId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        if (sameTraceId is { } instanceId)
        {
            var sameTrace = await _content.GetInTransactionAsync(connection, transaction, instanceId, cancellationToken);
            if (sameTrace is null)
            {
                return null;
            }
            var sameTraceState = ToState(sameTrace);
            return sameTraceState.Data.CreatorCharacterId == creatorCharacterId &&
                sameTraceState.Data.FleetId == fleetId
                ? null
                : sameTraceState;
        }

        return await FindAdmissionConflictUnderLockAsync(
            connection, transaction, creatorCharacterId, fleetId, cancellationToken);
    }

    /// <summary>
    /// Looks up a live or disconnected participant without locking, mutating data, or publishing events.
    /// The caller owns the transaction used to read the ContentRuntime instances.
    /// </summary>
    public async Task<AbyssalParticipantRunBinding?> FindActiveForParticipantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        long shipId,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || shipId <= 0)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "the participant character and ship identifiers must be positive");
        }

        var matches = new List<AbyssalParticipantRunBinding>();
        foreach (var instance in await _content.ActiveByKindReadOnlyAsync(connection, transaction, Kind, cancellationToken))
        {
            var data = DataOf(instance);
            if (data.RunExpiresAtMs is { } deadline && deadline <= _scheduler.Now)
            {
                continue;
            }
            var participant = data.Participants.SingleOrDefault(item =>
                item.CharacterId == characterId &&
                item.ShipId == shipId &&
                item.Status is AbyssalParticipantStatus.Active or AbyssalParticipantStatus.Disconnected);
            if (participant is not null)
            {
                matches.Add(new AbyssalParticipantRunBinding(ToState(instance), participant));
            }
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException("a participant is present in more than one active Abyssal run");
        }
        return matches.SingleOrDefault();
    }

    public async Task<AbyssalOperationResult> EnterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalEnterRequest request,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireInstanceAsync(connection, transaction, request.InstanceId, cancellationToken);
        var data = DataOf(instance);
        EnsureActive(instance, data);
        ValidateAdmission(data, request.Participant);

        var existingIndex = IndexOfParticipant(data, request.Participant.CharacterId);
        if (existingIndex >= 0)
        {
            var existing = data.Participants[existingIndex];
            if (existing.ShipId != request.Participant.ShipId)
            {
                throw Rule(AbyssalRuleCode.InvalidRequest, "the character is already bound to another ship in this run");
            }
            if (existing.Status == AbyssalParticipantStatus.Disconnected)
            {
                return await ReplayParticipantAsync(connection, transaction, instance, data, existingIndex, cancellationToken);
            }
            if (existing.Status == AbyssalParticipantStatus.Active)
            {
                return Result(AbyssalOperationStatus.AlreadyApplied, instance);
            }
            throw Rule(AbyssalRuleCode.InvalidRequest, "a terminal or returning participant cannot enter again");
        }

        if (data.Participants.Count >= data.Plan.MaximumParticipants)
        {
            throw Rule(AbyssalRuleCode.ParticipantLimitExceeded, "the run has reached the player limit for its game mode");
        }

        var now = _scheduler.Now;
        if (data.RunStartedAtMs is null && now >= data.TraceExpiresAtMs)
        {
            throw Rule(AbyssalRuleCode.TraceExpired, "the deployed entrance has expired");
        }

        var roomIndex = data.CurrentRoomIndex == 0 ? 1 : data.CurrentRoomIndex;
        var participant = new AbyssalParticipantData(
            request.Participant.CharacterId,
            request.Participant.ShipId,
            request.Participant.ShipTypeId,
            request.Participant.ShipClass,
            request.Participant.FleetId,
            AbyssalParticipantStatus.Active,
            roomIndex,
            request.Participant.Origin,
            now,
            now,
            null,
            null,
            null,
            null,
            null,
            null,
            0);

        var deadline = data.RunExpiresAtMs ?? checked(now + RunDurationMs);
        var updated = data with
        {
            RunStartedAtMs = data.RunStartedAtMs ?? now,
            RunExpiresAtMs = deadline,
            CurrentRoomIndex = roomIndex,
            Participants = data.Participants.Append(participant).ToArray(),
        };
        instance = await SaveAsync(connection, transaction, instance.Id, updated, deadline, cancellationToken);
        await PublishLifecycleAsync(connection, transaction, instance.Id, updated, participant, cancellationToken);

        return Result(AbyssalOperationStatus.Applied, instance);
    }

    public async Task<AbyssalOperationResult> DisconnectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        long characterId,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireInstanceAsync(connection, transaction, instanceId, cancellationToken);
        var data = DataOf(instance);
        var index = IndexOfParticipant(data, characterId);
        if (index < 0)
        {
            throw Rule(AbyssalRuleCode.ParticipantNotFound, "the character is not a participant in this run");
        }

        var participant = data.Participants[index];
        if (participant.Status == AbyssalParticipantStatus.Disconnected)
        {
            return Result(AbyssalOperationStatus.AlreadyApplied, instance);
        }
        EnsureActive(instance, data);
        if (participant.Status != AbyssalParticipantStatus.Active)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "only an active participant can disconnect");
        }

        var now = _scheduler.Now;
        var updatedParticipant = participant with
        {
            Status = AbyssalParticipantStatus.Disconnected,
            LastSeenAtMs = now,
            DisconnectedAtMs = now,
        };
        var updated = ReplaceParticipant(data, index, updatedParticipant);
        instance = await SaveAsync(connection, transaction, instanceId, updated, instance.ExpiresMs, cancellationToken);
        return Result(AbyssalOperationStatus.Applied, instance);
    }

    public async Task<AbyssalOperationResult> ReconnectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long characterId,
        long shipId,
        CancellationToken cancellationToken = default)
    {
        var instances = await _content.ActiveByKindAsync(connection, transaction, Kind, cancellationToken);
        var matches = instances
            .Select(instance => (Instance: instance, Data: DataOf(instance)))
            .Where(pair => pair.Data.Participants.Any(participant => participant.CharacterId == characterId))
            .ToArray();
        if (matches.Length == 0)
        {
            throw Rule(AbyssalRuleCode.ParticipantNotFound, "the character has no active Abyssal run");
        }
        if (matches.Length > 1)
        {
            throw new InvalidOperationException("a character is present in more than one active Abyssal run");
        }

        var (instance, data) = matches[0];
        EnsureActive(instance, data);
        var participantIndex = IndexOfParticipant(data, characterId);
        var participant = data.Participants[participantIndex];
        if (participant.ShipId != shipId)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "the reconnecting character is not in the recorded run ship");
        }
        if (participant.Status == AbyssalParticipantStatus.Active)
        {
            return Result(AbyssalOperationStatus.AlreadyApplied, instance);
        }
        if (participant.Status != AbyssalParticipantStatus.Disconnected)
        {
            throw Rule(AbyssalRuleCode.ParticipantNotFound, "the participant has already left or been destroyed");
        }

        return await ReplayParticipantAsync(connection, transaction, instance, data, participantIndex, cancellationToken);
    }

    public async Task<AbyssalOperationResult> PrepareRoomSpawnAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalRoomSpawnRequest request,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireInstanceAsync(connection, transaction, request.InstanceId, cancellationToken);
        var data = DataOf(instance);
        var roomIndex = RoomIndex(data, request.LocationId);
        var room = data.Rooms[roomIndex];
        if (room.SpawnPrepared)
        {
            return Result(AbyssalOperationStatus.AlreadyApplied, instance);
        }
        EnsureActive(instance, data);
        if (request.LocationId != data.CurrentRoomIndex)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "spawn reservations are accepted only for the current room");
        }

        ValidateSpawnReservation(data, roomIndex, request.Entities);
        var npcIds = request.Entities.Where(entity => entity.Role == AbyssalEntityRole.Npc).Select(entity => entity.EntityId).ToArray();
        var updatedRoom = room with
        {
            SpawnPrepared = true,
            Entities = request.Entities.ToArray(),
            Cleared = npcIds.Length == 0,
        };
        var updated = ReplaceRoom(data, roomIndex, updatedRoom);
        instance = await SaveAsync(connection, transaction, instance.Id, updated, instance.ExpiresMs, cancellationToken);

        var plan = data.Plan.Rooms[roomIndex];
        var key = Prefix(instance.Id) + "spawn:" + request.LocationId.ToString(CultureInfo.InvariantCulture);
        await PublishAsync(connection, transaction, RoomSpawnRequestedTopic,
            new AbyssalRoomSpawnRequestedEvent(
                instance.Id,
                data.ContentId,
                data.SpaceLocationId,
                request.LocationId,
                data.Tier,
                data.WeatherType,
                plan.EnvironmentProfileId,
                updatedRoom.Entities,
                plan.Loot)
            {
                EnvironmentObjects = plan.EnvironmentObjects ?? Array.Empty<AbyssalEnvironmentObjectPlan>(),
                Artifacts = plan.Artifacts ?? Array.Empty<AbyssalArtifactPlan>(),
            },
            key,
            cancellationToken);

        if (updatedRoom.Cleared)
        {
            await ProgressRoomClearAsync(connection, transaction, instance.Id, request.LocationId, cancellationToken);
            instance = await RequireInstanceAsync(connection, transaction, instance.Id, cancellationToken);
        }
        return Result(AbyssalOperationStatus.Applied, instance);
    }

    public async Task<AbyssalOperationResult> NpcDestroyedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalNpcDestroyedRequest request,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireInstanceAsync(connection, transaction, request.InstanceId, cancellationToken);
        var data = DataOf(instance);
        var roomIndex = RoomIndex(data, request.LocationId);
        var room = data.Rooms[roomIndex];
        if (!room.SpawnPrepared)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "NPC deaths cannot be recorded before room spawn IDs are committed");
        }

        var entity = room.Entities.SingleOrDefault(item => item.EntityId == request.EntityId && item.Role == AbyssalEntityRole.Npc);
        if (entity is null)
        {
            throw Rule(AbyssalRuleCode.EntityNotInRoom, "the destroyed entity is not an NPC reserved for this room");
        }
        if (room.DestroyedNpcIds.Contains(request.EntityId))
        {
            return Result(AbyssalOperationStatus.AlreadyApplied, instance);
        }
        EnsureActive(instance, data);

        var destroyed = room.DestroyedNpcIds.Append(request.EntityId).ToArray();
        var npcIds = room.Entities.Where(item => item.Role == AbyssalEntityRole.Npc).Select(item => item.EntityId).ToArray();
        var clear = npcIds.All(destroyed.Contains);
        var updatedRoom = room with { DestroyedNpcIds = destroyed, Cleared = clear };
        var updated = ReplaceRoom(data, roomIndex, updatedRoom);
        instance = await SaveAsync(connection, transaction, instance.Id, updated, instance.ExpiresMs, cancellationToken);

        if (clear && !room.Cleared)
        {
            await ProgressRoomClearAsync(connection, transaction, instance.Id, request.LocationId, cancellationToken);
            instance = await RequireInstanceAsync(connection, transaction, instance.Id, cancellationToken);
        }
        return Result(AbyssalOperationStatus.Applied, instance);
    }

    public async Task<AbyssalOperationResult> ActivateRoomGateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalRoomGateActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireInstanceAsync(connection, transaction, request.InstanceId, cancellationToken);
        var data = DataOf(instance);
        var existingTransition = data.GateTransitions.SingleOrDefault(item => item.GateMarkerId == request.GateMarkerId);
        if (existingTransition is not null)
        {
            return Result(AbyssalOperationStatus.AlreadyApplied, instance);
        }

        EnsureActive(instance, data);
        if (request.GateMarkerId <= 0 || request.LocationId is < 1 or > 2 || request.LocationId != data.CurrentRoomIndex)
        {
            throw Rule(AbyssalRuleCode.InvalidGate, "the encounter gate does not match the current room");
        }
        var roomIndex = RoomIndex(data, request.LocationId);
        var fromRoom = data.Rooms[roomIndex];
        var fromRoomPlan = data.Plan.Rooms[roomIndex];
        if (!fromRoom.Cleared)
        {
            throw Rule(AbyssalRuleCode.GateLocked, "the gate remains locked until every NPC in this room is destroyed");
        }

        var activatorIndex = IndexOfParticipant(data, request.ActivatingCharacterId);
        if (activatorIndex < 0 || data.Participants[activatorIndex].Status != AbyssalParticipantStatus.Active)
        {
            throw Rule(AbyssalRuleCode.ParticipantNotFound, "the gate activator is not active in this run");
        }
        var destination = request.LocationId + 1;
        var recipients = data.Participants
            .Where(participant => participant.Status is AbyssalParticipantStatus.Active or AbyssalParticipantStatus.Disconnected)
            .Select(ToRecipient)
            .ToArray();
        var participants = data.Participants.Select(participant =>
            participant.Status is AbyssalParticipantStatus.Active or AbyssalParticipantStatus.Disconnected
                ? participant with { CurrentRoomIndex = destination }
                : participant).ToArray();
        var transition = new AbyssalGateTransition(request.GateMarkerId, request.LocationId, destination, recipients);
        var updated = data with
        {
            CurrentRoomIndex = destination,
            Participants = participants,
            GateTransitions = data.GateTransitions.Append(transition).ToArray(),
        };
        instance = await SaveAsync(connection, transaction, instance.Id, updated, instance.ExpiresMs, cancellationToken);

        var nextRoom = updated.Rooms[destination - 1];
        var nextRoomPlan = updated.Plan.Rooms[destination - 1];
        await PublishAsync(connection, transaction, RoomTransitionRequestedTopic,
            new AbyssalRoomTransitionRequestedEvent(
                instance.Id, data.ContentId, data.SpaceLocationId, request.GateMarkerId, request.LocationId, destination,
                fromRoomPlan.EnvironmentProfileId, nextRoomPlan.EnvironmentProfileId,
                request.ActivatingCharacterId, recipients),
            Prefix(instance.Id) + "gate:" + request.GateMarkerId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

        foreach (var participant in participants.Where(item => item.Status == AbyssalParticipantStatus.Active))
        {
            await PublishLocationEnteredAsync(connection, transaction, instance.Id, data.ContentId, data.Tier,
                data.SpaceLocationId, participant, destination, nextRoom.NebulaGraphicId,
                nextRoomPlan.EnvironmentProfileId,
                "gate:" + request.GateMarkerId.ToString(CultureInfo.InvariantCulture), cancellationToken);
            await PublishLocationExitedAsync(connection, transaction, instance.Id, data.ContentId, participant.CharacterId,
                request.LocationId, fromRoomPlan.EnvironmentProfileId,
                "gate:" + request.GateMarkerId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }
        return Result(AbyssalOperationStatus.Applied, instance);
    }

    public async Task<AbyssalOperationResult> EndGateActivationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalEndGateActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireInstanceAsync(connection, transaction, request.InstanceId, cancellationToken);
        var data = DataOf(instance);
        if (data.ExitGateMarkerId == request.GateMarkerId && data.ExitMode == AbyssalExitMode.FinalGate)
        {
            return Result(AbyssalOperationStatus.AlreadyApplied, instance);
        }

        EnsureActive(instance, data);
        if (request.GateMarkerId <= 0 || request.LocationId != 3 || request.LocationId != data.CurrentRoomIndex)
        {
            throw Rule(AbyssalRuleCode.InvalidGate, "the origin gate does not match room three");
        }
        if (data.ExitMode != AbyssalExitMode.None)
        {
            throw Rule(AbyssalRuleCode.InvalidGate, "an exit transition is already committed");
        }
        var roomIndex = RoomIndex(data, request.LocationId);
        if (!data.Rooms[roomIndex].Cleared)
        {
            throw Rule(AbyssalRuleCode.GateLocked, "the origin gate remains locked until every NPC in room three is destroyed");
        }

        var activatorIndex = IndexOfParticipant(data, request.ActivatingCharacterId);
        if (activatorIndex < 0 || data.Participants[activatorIndex].Status != AbyssalParticipantStatus.Active)
        {
            throw Rule(AbyssalRuleCode.ParticipantNotFound, "the origin-gate activator is not active in this run");
        }

        var now = _scheduler.Now;
        var recipients = data.Participants
            .Where(participant => participant.Status is AbyssalParticipantStatus.Active or AbyssalParticipantStatus.Disconnected)
            .ToArray();
        if (recipients.Length == 0)
        {
            throw Rule(AbyssalRuleCode.ParticipantNotFound, "there are no participants left to return");
        }
        var updatedParticipants = data.Participants.Select(participant =>
            participant.Status is AbyssalParticipantStatus.Active or AbyssalParticipantStatus.Disconnected
                ? participant with
                {
                    Status = AbyssalParticipantStatus.Returning,
                    ReturnRequestedAtMs = now,
                    ReturnReason = "final-gate",
                }
                : participant).ToArray();
        var updated = data with
        {
            ExitMode = AbyssalExitMode.FinalGate,
            ExitGateMarkerId = request.GateMarkerId,
            Participants = updatedParticipants,
        };
        instance = await SaveAsync(connection, transaction, instance.Id, updated, null, cancellationToken);

        foreach (var participant in recipients.Where(item => item.Status == AbyssalParticipantStatus.Active))
        {
            await PublishLocationExitedAsync(connection, transaction, instance.Id, data.ContentId, participant.CharacterId,
                request.LocationId, data.Plan.Rooms[roomIndex].EnvironmentProfileId,
                "exit:" + request.GateMarkerId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }
        foreach (var participant in recipients)
        {
            await PublishReturnRequestedAsync(connection, transaction, instance.Id, data.ContentId, participant,
                "final-gate", administrative: false, rewardEligible: true, cancellationToken);
        }
        return Result(AbyssalOperationStatus.Applied, instance);
    }

    public async Task<AbyssalOperationResult> ReturnParticipantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalParticipantReturnRequest request,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireInstanceAsync(connection, transaction, request.InstanceId, cancellationToken);
        var data = DataOf(instance);
        var participantIndex = IndexOfParticipant(data, request.CharacterId);
        if (participantIndex < 0)
        {
            throw Rule(AbyssalRuleCode.ParticipantNotFound, "the character is not a participant in this run");
        }
        var participant = data.Participants[participantIndex];
        if (participant.Status is AbyssalParticipantStatus.Returning or AbyssalParticipantStatus.Returned or AbyssalParticipantStatus.Destroyed)
        {
            return Result(AbyssalOperationStatus.AlreadyApplied, instance);
        }
        EnsureActive(instance, data);
        var returning = participant with
        {
            Status = AbyssalParticipantStatus.Returning,
            ReturnRequestedAtMs = _scheduler.Now,
            ReturnReason = request.Reason,
        };
        var updated = ReplaceParticipant(data, participantIndex, returning);
        instance = await SaveAsync(connection, transaction, instance.Id, updated, instance.ExpiresMs, cancellationToken);

        if (participant.Status == AbyssalParticipantStatus.Active)
        {
            await PublishLocationExitedAsync(connection, transaction, instance.Id, data.ContentId, participant.CharacterId,
                participant.CurrentRoomIndex, data.Plan.Rooms[RoomIndex(data, participant.CurrentRoomIndex)].EnvironmentProfileId,
                "participant-return:" + participant.CharacterId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }
        await PublishReturnRequestedAsync(connection, transaction, instance.Id, data.ContentId, returning,
            request.Reason, administrative: false, rewardEligible: false, cancellationToken);
        return Result(AbyssalOperationStatus.Applied, instance);
    }

    public async Task<AbyssalOperationResult> RecordOriginReturnAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalReturnReceiptRequest request,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireInstanceAsync(connection, transaction, request.InstanceId, cancellationToken);
        var data = DataOf(instance);
        var participantIndex = IndexOfParticipant(data, request.CharacterId);
        if (participantIndex < 0)
        {
            throw Rule(AbyssalRuleCode.ParticipantNotFound, "the character is not a participant in this run");
        }
        var participant = data.Participants[participantIndex];
        if (participant.Status == AbyssalParticipantStatus.Returned)
        {
            return Result(AbyssalOperationStatus.AlreadyApplied, instance);
        }
        if (instance.State != "active" || participant.Status != AbyssalParticipantStatus.Returning)
        {
            throw Rule(AbyssalRuleCode.ReturnNotRequested, "an origin-return receipt requires a committed return request");
        }

        var returned = participant with
        {
            Status = AbyssalParticipantStatus.Returned,
            CurrentRoomIndex = 0,
            LastSeenAtMs = _scheduler.Now,
            ReturnedAtMs = _scheduler.Now,
            ContentFinishedAtMs = _scheduler.Now,
        };
        var updated = ReplaceParticipant(data, participantIndex, returned);
        instance = await SaveAsync(connection, transaction, instance.Id, updated, instance.ExpiresMs, cancellationToken);
        await PublishAsync(connection, transaction, ParticipantContentFinishedTopic,
            new AbyssalParticipantContentFinishedEvent(instance.Id, data.ContentId, participant.CharacterId),
            Prefix(instance.Id) + "finished:" + participant.CharacterId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

        await FinishFromRosterAsync(connection, transaction, instance.Id, updated, cancellationToken);
        instance = await RequireInstanceAsync(connection, transaction, instance.Id, cancellationToken);
        return Result(AbyssalOperationStatus.Applied, instance);
    }

    public async Task<AbyssalOperationResult> ParticipantDestroyedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalParticipantDestroyedRequest request,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireInstanceAsync(connection, transaction, request.InstanceId, cancellationToken);
        var data = DataOf(instance);
        var participantIndex = IndexOfParticipant(data, request.CharacterId);
        if (participantIndex < 0)
        {
            throw Rule(AbyssalRuleCode.ParticipantNotFound, "the character is not a participant in this run");
        }
        var participant = data.Participants[participantIndex];
        if (participant.ShipId != request.ShipId)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "the destroyed ship is not the ship recorded for this participant");
        }
        if (request.WreckTypeId is <= 0)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "the destroyed wreck type identifier must be positive when present");
        }
        if (participant.DeathSettlementRequestedAtMs is not null &&
            participant.DeathSettlementWreckTypeId != request.WreckTypeId)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "a duplicate death settlement cannot change its wreck type snapshot");
        }
        if (participant.DeathSettlementReceiptAtMs is not null)
        {
            return Result(AbyssalOperationStatus.AlreadyApplied, instance);
        }
        if (participant.Status == AbyssalParticipantStatus.Destroyed && IsDeathSettlementPending(participant))
        {
            return Result(AbyssalOperationStatus.AlreadyApplied, instance);
        }
        if (participant.Status == AbyssalParticipantStatus.Returned)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "a returned participant cannot be destroyed in this run");
        }

        var now = _scheduler.Now;
        var deadlineDue = instance.State == "active" &&
            data.ExitMode != AbyssalExitMode.FinalGate &&
            data.RunExpiresAtMs is { } runDeadline && runDeadline <= now;
        var timeoutAlreadyCommitted = instance.State == "expired" &&
            data.ExitMode != AbyssalExitMode.FinalGate;
        if (instance.State == "active")
        {
            if (!deadlineDue && data.ExitMode != AbyssalExitMode.FinalGate)
            {
                EnsureActive(instance, data);
            }
        }
        else if (!timeoutAlreadyCommitted && participant.Status != AbyssalParticipantStatus.Destroyed)
        {
            throw Rule(AbyssalRuleCode.RunNotActive,
                "ship destruction can settle only in an active run or after its timeout has committed");
        }

        var cause = participant.DeathSettlementCause ??
            (timeoutAlreadyCommitted || deadlineDue
                ? AbyssalDeathSettlementCause.RunExpired
                : AbyssalDeathSettlementCause.ShipDestroyed);
        var wasPending = IsDeathSettlementPending(participant);
        var destroyed = participant with
        {
            Status = AbyssalParticipantStatus.Destroyed,
            LastSeenAtMs = now,
            DestroyedAtMs = participant.DestroyedAtMs ?? now,
            DeathSettlementKey = participant.DeathSettlementKey ?? BuildDeathSettlementKey(data.ContentId, participant.CharacterId),
            DeathSettlementCause = cause,
            DeathSettlementRequestedAtMs = participant.DeathSettlementRequestedAtMs ?? now,
            DeathSettlementWreckTypeId = participant.DeathSettlementRequestedAtMs is null
                ? request.WreckTypeId
                : participant.DeathSettlementWreckTypeId,
        };
        var updated = ReplaceParticipant(data, participantIndex, destroyed);
        if (instance.State == "active")
        {
            instance = await SaveAsync(connection, transaction, instance.Id, updated, instance.ExpiresMs, cancellationToken);
        }
        else if (!await _content.UpdateDataAsync(connection, transaction, instance.Id, updated, cancellationToken))
        {
            throw Rule(AbyssalRuleCode.RunNotActive, "the participant death request could not be persisted");
        }

        if (!wasPending)
        {
            await PublishDeathSettlementRequestedAsync(
                connection, transaction, instance.Id, updated, destroyed, cancellationToken);
        }

        if (deadlineDue)
        {
            await _content.ExpireIfDueAsync(connection, transaction, instance.Id, cancellationToken);
        }
        else if (instance.State == "active")
        {
            await FinishFromRosterAsync(connection, transaction, instance.Id, updated, cancellationToken);
        }

        instance = await RequireInstanceAsync(connection, transaction, instance.Id, cancellationToken);
        return Result(AbyssalOperationStatus.Applied, instance);
    }

    /// <summary>Records the durable pod/clone settlement receipt before terminal client teardown.</summary>
    public async Task<AbyssalOperationResult> RecordParticipantDestructionSettlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalParticipantDestructionSettlementRequest request,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireInstanceAsync(connection, transaction, request.InstanceId, cancellationToken);
        var data = DataOf(instance);
        var participantIndex = IndexOfParticipant(data, request.CharacterId);
        if (participantIndex < 0)
        {
            throw Rule(AbyssalRuleCode.ParticipantNotFound, "the character is not a participant in this run");
        }

        var participant = data.Participants[participantIndex];
        if (participant.ShipId != request.ShipId ||
            string.IsNullOrWhiteSpace(request.SettlementKey) ||
            !string.Equals(participant.DeathSettlementKey, request.SettlementKey, StringComparison.Ordinal) ||
            participant.DeathSettlementRequestedAtMs is null ||
            participant.DeathSettlementCause is null)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest,
                "a death settlement receipt must match the persisted participant request and settlement key");
        }
        if (participant.DeathSettlementReceiptAtMs is not null)
        {
            return Result(AbyssalOperationStatus.AlreadyApplied, instance);
        }
        if (participant.Status == AbyssalParticipantStatus.Returned)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "a returned participant cannot receive a death settlement");
        }

        var now = _scheduler.Now;
        var settled = participant with
        {
            Status = AbyssalParticipantStatus.Destroyed,
            LastSeenAtMs = now,
            DestroyedAtMs = participant.DestroyedAtMs ?? now,
            DeathSettlementReceiptAtMs = now,
            ContentFinishedAtMs = now,
        };
        var updated = ReplaceParticipant(data, participantIndex, settled);
        if (!await _content.UpdateDataAsync(connection, transaction, instance.Id, updated, cancellationToken))
        {
            throw Rule(AbyssalRuleCode.RunNotActive, "the participant's death settlement receipt could not be persisted");
        }
        instance = await RequireInstanceAsync(connection, transaction, instance.Id, cancellationToken);

        var operation = "death-settlement:" + request.SettlementKey;
        await PublishLocationExitedAsync(connection, transaction, instance.Id, data.ContentId, participant.CharacterId,
            participant.CurrentRoomIndex, data.Plan.Rooms[RoomIndex(data, participant.CurrentRoomIndex)].EnvironmentProfileId,
            operation, cancellationToken);
        await PublishAsync(connection, transaction, ParticipantContentFinishedTopic,
            new AbyssalParticipantContentFinishedEvent(instance.Id, data.ContentId, participant.CharacterId),
            Prefix(instance.Id) + "finished:" + participant.CharacterId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        return Result(AbyssalOperationStatus.Applied, instance);
    }

    public async Task<AbyssalOperationResult> ExpireIfDueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        CancellationToken cancellationToken = default)
    {
        var expired = await _content.ExpireIfDueAsync(connection, transaction, instanceId, cancellationToken);
        var instance = await RequireInstanceAsync(connection, transaction, instanceId, cancellationToken);
        return Result(expired ? AbyssalOperationStatus.Applied : AbyssalOperationStatus.AlreadyApplied, instance);
    }

    public async Task<AbyssalOperationResult> RecoverAfterServerRestartAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireInstanceAsync(connection, transaction, instanceId, cancellationToken);
        var data = DataOf(instance);
        var now = _scheduler.Now;

        if (instance.State != "active")
        {
            var (reconciled, newlyRequested) = EnsureDeathSettlementRequests(
                data, includeLivingForTimeout: instance.State == "expired", now);
            if (newlyRequested.Count > 0)
            {
                if (!await _content.UpdateDataAsync(connection, transaction, instanceId, reconciled, cancellationToken))
                {
                    throw Rule(AbyssalRuleCode.RunNotActive, "terminal death settlement requests could not be persisted");
                }
                instance = await RequireInstanceAsync(connection, transaction, instanceId, cancellationToken);
                data = reconciled;
            }

            var replayed = await ReplayPendingDeathSettlementRequestsAsync(
                connection, transaction, instance.Id, data, data.Participants, cancellationToken);
            return Result(newlyRequested.Count > 0 || replayed > 0
                ? AbyssalOperationStatus.Applied
                : AbyssalOperationStatus.AlreadyApplied, instance);
        }

        if (data.RunStartedAtMs is null)
        {
            throw Rule(AbyssalRuleCode.RunNotActive, "restart reconciliation applies to an entered Abyssal run");
        }

        if (data.ExitMode == AbyssalExitMode.FinalGate)
        {
            var replayed = await ReplayPendingDeathSettlementRequestsAsync(
                connection, transaction, instance.Id, data, data.Participants, cancellationToken);
            return Result(replayed > 0 ? AbyssalOperationStatus.Applied : AbyssalOperationStatus.AlreadyApplied, instance);
        }

        var pendingBeforeTransition = data.Participants.Where(IsDeathSettlementPending).ToArray();
        if (data.RunExpiresAtMs is { } deadline && deadline <= now)
        {
            var expired = await _content.ExpireIfDueAsync(connection, transaction, instanceId, cancellationToken);
            instance = await RequireInstanceAsync(connection, transaction, instanceId, cancellationToken);
            data = DataOf(instance);
            var replayed = await ReplayPendingDeathSettlementRequestsAsync(
                connection, transaction, instance.Id, data, pendingBeforeTransition, cancellationToken);
            return Result(expired || replayed > 0
                ? AbyssalOperationStatus.Applied
                : AbyssalOperationStatus.AlreadyApplied, instance);
        }

        if (data.Participants.Count == 0)
        {
            throw Rule(AbyssalRuleCode.RunNotActive, "restart reconciliation requires at least one admitted participant");
        }

        if (data.Participants.All(participant => participant.Status == AbyssalParticipantStatus.Destroyed))
        {
            await _content.FailAsync(connection, transaction, instanceId, "all Abyssal participants were destroyed", cancellationToken);
            instance = await RequireInstanceAsync(connection, transaction, instanceId, cancellationToken);
            data = DataOf(instance);
            var replayed = await ReplayPendingDeathSettlementRequestsAsync(
                connection, transaction, instance.Id, data, pendingBeforeTransition, cancellationToken);
            return Result(AbyssalOperationStatus.Applied, instance);
        }

        var (withDeathRequests, newlyRequestedParticipants) =
            EnsureDeathSettlementRequests(data, includeLivingForTimeout: false, now);
        var livingParticipants = withDeathRequests.Participants
            .Where(participant => participant.Status is AbyssalParticipantStatus.Active
                or AbyssalParticipantStatus.Disconnected
                or AbyssalParticipantStatus.Returning)
            .ToArray();
        var updatedParticipants = withDeathRequests.Participants.Select(participant =>
            participant.Status is AbyssalParticipantStatus.Active
                or AbyssalParticipantStatus.Disconnected
                or AbyssalParticipantStatus.Returning
                ? participant with
                {
                    Status = AbyssalParticipantStatus.Returning,
                    ReturnRequestedAtMs = participant.ReturnRequestedAtMs ?? now,
                    ReturnReason = "server-restart",
                }
                : participant).ToArray();
        var updated = withDeathRequests with
        {
            ExitMode = AbyssalExitMode.ServerRestart,
            Participants = updatedParticipants,
        };
        instance = await SaveAsync(connection, transaction, instance.Id, updated, instance.ExpiresMs, cancellationToken);

        foreach (var requested in newlyRequestedParticipants)
        {
            await PublishDeathSettlementRequestedAsync(connection, transaction, instance.Id, updated, requested, cancellationToken);
        }
        var replayedPending = await ReplayPendingDeathSettlementRequestsAsync(
            connection, transaction, instance.Id, updated, pendingBeforeTransition, cancellationToken);

        foreach (var participant in livingParticipants)
        {
            var returning = updatedParticipants.Single(item => item.CharacterId == participant.CharacterId);
            await PublishReturnRequestedAsync(connection, transaction, instance.Id, data.ContentId, returning,
                "server-restart", administrative: true, rewardEligible: false, cancellationToken);
        }

        await FinishFromRosterAsync(connection, transaction, instance.Id, updated, cancellationToken);
        instance = await RequireInstanceAsync(connection, transaction, instance.Id, cancellationToken);
        return Result(livingParticipants.Length > 0 || newlyRequestedParticipants.Count > 0 || replayedPending > 0
            ? AbyssalOperationStatus.Applied
            : AbyssalOperationStatus.AlreadyApplied, instance);
    }

    private async Task<AbyssalOperationResult> ReplayParticipantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ContentInstance instance,
        AbyssalRunData data,
        int participantIndex,
        CancellationToken cancellationToken)
    {
        EnsureActive(instance, data);
        var participant = data.Participants[participantIndex];
        if (participant.Status is not (AbyssalParticipantStatus.Active or AbyssalParticipantStatus.Disconnected))
        {
            throw Rule(AbyssalRuleCode.ParticipantNotFound, "only a living participant in this run can reconnect");
        }

        var replayed = participant with
        {
            Status = AbyssalParticipantStatus.Active,
            LastSeenAtMs = _scheduler.Now,
            DisconnectedAtMs = null,
            LifecycleReplayOrdinal = checked(participant.LifecycleReplayOrdinal + 1),
        };
        var updated = ReplaceParticipant(data, participantIndex, replayed);
        instance = await SaveAsync(connection, transaction, instance.Id, updated, instance.ExpiresMs, cancellationToken);
        await PublishLifecycleAsync(connection, transaction, instance.Id, updated, replayed, cancellationToken);
        return Result(AbyssalOperationStatus.Applied, instance);
    }

    private async Task PublishLifecycleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        AbyssalRunData data,
        AbyssalParticipantData participant,
        CancellationToken cancellationToken)
    {
        var roomIndex = RoomIndex(data, participant.CurrentRoomIndex);
        var room = data.Rooms[roomIndex];
        var roomPlan = data.Plan.Rooms[roomIndex];
        var operation = "lifecycle:" + participant.CharacterId.ToString(CultureInfo.InvariantCulture)
            + ":" + participant.LifecycleReplayOrdinal.ToString(CultureInfo.InvariantCulture);
        await PublishLocationEnteredAsync(connection, transaction, instanceId, data.ContentId, data.Tier,
            data.SpaceLocationId, participant, participant.CurrentRoomIndex, room.NebulaGraphicId,
            roomPlan.EnvironmentProfileId, operation, cancellationToken);
        await PublishAsync(connection, transaction, DeadlineAddedTopic,
            new AbyssalDeadlineAddedEvent(instanceId, data.ContentId, participant.CharacterId, data.RunExpiresAtMs!.Value),
            Prefix(instanceId) + operation + ":deadline",
            cancellationToken);
    }

    private Task PublishLocationEnteredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        string contentId,
        int difficultyTier,
        int spaceLocationId,
        AbyssalParticipantData participant,
        int locationId,
        int nebulaGraphicId,
        string environmentProfileId,
        string operation,
        CancellationToken cancellationToken) =>
        PublishAsync(connection, transaction, LocationEnteredTopic,
            new AbyssalLocationEnteredEvent(instanceId, contentId, difficultyTier, spaceLocationId,
                participant.CharacterId, locationId, nebulaGraphicId, environmentProfileId),
            Prefix(instanceId) + operation + ":location-entered",
            cancellationToken);

    private Task PublishLocationExitedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        string contentId,
        long characterId,
        int locationId,
        string environmentProfileId,
        string operation,
        CancellationToken cancellationToken) =>
        PublishAsync(connection, transaction, LocationExitedTopic,
            new AbyssalLocationExitedEvent(instanceId, contentId, characterId, locationId, environmentProfileId),
            Prefix(instanceId) + operation + ":location-exited",
            cancellationToken);

    private Task PublishReturnRequestedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        string contentId,
        AbyssalParticipantData participant,
        string reason,
        bool administrative,
        bool rewardEligible,
        CancellationToken cancellationToken) =>
        PublishAsync(connection, transaction, ParticipantReturnRequestedTopic,
            new AbyssalParticipantReturnRequestedEvent(
                instanceId,
                contentId,
                participant.CharacterId,
                participant.ShipId,
                participant.Origin,
                reason,
                administrative,
                rewardEligible,
                false),
            Prefix(instanceId) + "return:" + participant.CharacterId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

private async Task QueueTerminalDeathSettlementRequestsAsync(
        ActionContext context,
        CancellationToken cancellationToken)
    {
        var data = DataOf(context.Instance);
        var (updated, newlyRequested) = EnsureDeathSettlementRequests(
            data, includeLivingForTimeout: context.Outcome == "expired", _scheduler.Now);
        if (newlyRequested.Count == 0)
        {
            return;
        }
        if (!await _content.UpdateDataAsync(context.Connection, context.Transaction, context.Instance.Id, updated, cancellationToken))
        {
            throw new InvalidOperationException("terminal Abyssal death settlement requests could not be persisted");
        }

        foreach (var participant in newlyRequested)
        {
            await PublishDeathSettlementRequestedAsync(
                context.Connection, context.Transaction, context.Instance.Id, updated, participant, cancellationToken);
        }
    }

    private static (AbyssalRunData Data, IReadOnlyList<AbyssalParticipantData> NewlyRequested)
        EnsureDeathSettlementRequests(AbyssalRunData data, bool includeLivingForTimeout, long now)
    {
        var participants = data.Participants.ToArray();
        var newlyRequested = new List<AbyssalParticipantData>();
        for (var index = 0; index < participants.Length; index++)
        {
            var participant = participants[index];
            if (participant.DeathSettlementReceiptAtMs is not null || IsDeathSettlementPending(participant))
            {
                continue;
            }

            var destroyed = participant.Status == AbyssalParticipantStatus.Destroyed;
            var timeoutParticipant = includeLivingForTimeout &&
                participant.Status is AbyssalParticipantStatus.Active
                    or AbyssalParticipantStatus.Disconnected
                    or AbyssalParticipantStatus.Returning;
            if (!destroyed && !timeoutParticipant)
            {
                continue;
            }

            var settlement = participant with
            {
                DeathSettlementKey = participant.DeathSettlementKey ??
                    BuildDeathSettlementKey(data.ContentId, participant.CharacterId),
                DeathSettlementCause = participant.DeathSettlementCause ??
                    (timeoutParticipant ? AbyssalDeathSettlementCause.RunExpired : AbyssalDeathSettlementCause.ShipDestroyed),
                DeathSettlementRequestedAtMs = participant.DeathSettlementRequestedAtMs ?? now,
            };
            participants[index] = settlement;
            newlyRequested.Add(settlement);
        }
        return (data with { Participants = participants }, newlyRequested);
    }

    private async Task<int> ReplayPendingDeathSettlementRequestsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        AbyssalRunData data,
        IEnumerable<AbyssalParticipantData> participants,
        CancellationToken cancellationToken)
    {
        var replayed = 0;
        foreach (var participant in participants)
        {
            if (!IsDeathSettlementPending(participant))
            {
                continue;
            }
            await PublishDeathSettlementRequestedAsync(connection, transaction, instanceId, data, participant, cancellationToken);
            replayed++;
        }
        return replayed;
    }

    private Task PublishDeathSettlementRequestedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        AbyssalRunData data,
        AbyssalParticipantData participant,
        CancellationToken cancellationToken)
    {
        if (!IsDeathSettlementPending(participant))
        {
            throw new InvalidOperationException("a death settlement outbox event requires a persisted pending participant request");
        }

        var settlementKey = participant.DeathSettlementKey!;
        var requestedAtMs = participant.DeathSettlementRequestedAtMs!.Value;
        return PublishAsync(connection, transaction, ParticipantDeathSettlementRequestedTopic,
            new AbyssalParticipantDeathSettlementRequestedEvent(
                instanceId,
                data.ContentId,
                settlementKey,
                participant.CharacterId,
                participant.ShipId,
                participant.DeathSettlementCause!.Value,
                participant.Origin,
                data.SpaceLocationId,
                participant.CurrentRoomIndex,
                requestedAtMs,
                participant.DeathSettlementWreckTypeId),
            Prefix(instanceId) + "death-settlement-request:" + settlementKey,
            cancellationToken);
    }

    private static bool IsDeathSettlementPending(AbyssalParticipantData participant) =>
        participant.DeathSettlementRequestedAtMs is not null &&
        !string.IsNullOrWhiteSpace(participant.DeathSettlementKey) &&
        participant.DeathSettlementCause is not null &&
        participant.DeathSettlementReceiptAtMs is null;

    private static string BuildDeathSettlementKey(string contentId, long characterId) =>
        "abyssal:" + contentId + ":death:" + characterId.ToString(CultureInfo.InvariantCulture);

    private async Task FinishFromRosterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        AbyssalRunData data,
        CancellationToken cancellationToken)
    {
        if (data.Participants.Any(participant =>
                participant.Status is AbyssalParticipantStatus.Active
                    or AbyssalParticipantStatus.Disconnected
                    or AbyssalParticipantStatus.Returning))
        {
            return;
        }
        if (data.Participants.Any(participant => participant.Status == AbyssalParticipantStatus.Returned))
        {
            if (data.ExitMode == AbyssalExitMode.FinalGate)
            {
                var advanced = await _content.ProgressAsync(connection, transaction, instanceId, "origin-return", 1, cancellationToken);
                if (!advanced)
                {
                    throw new InvalidOperationException("the committed final-gate return could not complete the active origin-return objective");
                }
            }
            else
            {
                await _content.AbandonAsync(connection, transaction, instanceId,
                    data.ExitMode == AbyssalExitMode.ServerRestart ? "server restart origin return" : "last participant returned",
                    cancellationToken);
            }
        }
        else
        {
            await _content.FailAsync(connection, transaction, instanceId, "all Abyssal participants were destroyed", cancellationToken);
        }
    }

    private async Task ProgressRoomClearAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        int locationId,
        CancellationToken cancellationToken)
    {
        var progressed = await _content.ProgressAsync(connection, transaction, instanceId, RoomClearObjectiveId(locationId), 1, cancellationToken);
        if (!progressed)
        {
            throw new InvalidOperationException("room clear state and ContentRuntime objective state diverged");
        }
    }

    private async Task<ContentInstance> SaveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        AbyssalRunData data,
        long? expiresAtMs,
        CancellationToken cancellationToken)
    {
        if (!await _content.UpdateDataAndDeadlineAsync(connection, transaction, instanceId, data, expiresAtMs, cancellationToken))
        {
            throw Rule(AbyssalRuleCode.RunNotActive, "the ContentRuntime instance is no longer active");
        }
        return await RequireInstanceAsync(connection, transaction, instanceId, cancellationToken);
    }

    private async Task<ContentInstance> RequireInstanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await _content.GetForUpdateAsync(connection, transaction, instanceId, cancellationToken);
        return instance ?? throw Rule(AbyssalRuleCode.RunNotActive, "the ContentRuntime instance does not exist");
    }

    private async Task PublishAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string topic,
        object payload,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        await _bus.PublishAsync(connection, transaction, topic, payload, idempotencyKey, cancellationToken);

    private static AbyssalOperationResult Result(AbyssalOperationStatus status, ContentInstance instance) =>
        new(status, ToState(instance));

    private static AbyssalRunState ToState(ContentInstance instance) =>
        new(instance.Id, instance.State, instance.ExpiresMs, DataOf(instance));

    private static AbyssalRunData DataOf(ContentInstance instance) =>
        instance.Data<AbyssalRunData>() ?? throw new InvalidOperationException("the persisted Abyssal ContentRuntime data is missing");

    private void EnsureActive(ContentInstance instance, AbyssalRunData data)
    {
        if (instance.State != "active")
        {
            throw Rule(AbyssalRuleCode.RunNotActive, "the Abyssal ContentRuntime instance has ended");
        }
        if (data.RunExpiresAtMs is { } runDeadline && runDeadline <= _scheduler.Now)
        {
            throw Rule(AbyssalRuleCode.RunExpired, "the Abyssal run deadline has elapsed");
        }
    }

    private static void EnsureSameTraceRequest(AbyssalRunData data, AbyssalStartRequest request)
    {
        if (data.TraceItemId != request.TraceItemId ||
            data.CreatorCharacterId != request.CreatorCharacterId ||
            data.CreatorShipId != request.CreatorShipId ||
            data.FleetId != request.FleetId ||
            data.GameModeId != request.GameModeId ||
            data.FilamentTypeId != request.FilamentTypeId ||
            data.SourceSolarSystemId != request.SourceSolarSystemId ||
            data.SourceLocationId != request.SourceLocationId ||
            data.SpaceLocationId != request.Plan.SpaceLocationId ||
            data.SourcePosition != request.SourcePosition ||
            data.TraceExpiresAtMs != request.TraceExpiresAtMs)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "the trace is already bound to a different creator, ship, or fleet");
        }
    }

private async Task<AbyssalRunState?> FindAdmissionConflictUnderLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long creatorCharacterId,
        long? fleetId,
        CancellationToken cancellationToken)
    {
        var active = await _content.ActiveByKindReadOnlyAsync(connection, transaction, Kind, cancellationToken);
        foreach (var instance in active.OrderBy(item => item.Id))
        {
            var data = DataOf(instance);
            if (data.CreatorCharacterId == creatorCharacterId ||
                fleetId is { } requestedFleet && data.FleetId == requestedFleet)
            {
                return ToState(instance);
            }
        }
        return null;
    }

private static bool HasValidBlueprintRuns(AbyssalLootPlan loot)
    {
        var runs = loot.BlueprintRunsPerCopy;
        return loot.IsBlueprintCopy
            ? runs is not null && runs.Count == loot.Count && runs.All(run => run is >= 1 and <= 5)
            : runs is null or { Count: 0 };
    }

    private static void ValidateStart(AbyssalStartRequest request, long now)
    {
        if (request.TraceItemId <= 0 || request.CreatorCharacterId <= 0 || request.CreatorShipId <= 0 ||
            request.FilamentTypeId <= 0 || request.SourceSolarSystemId <= 0 || request.SourceLocationId <= 0 ||
            request.TraceExpiresAtMs <= now)
        {
            throw Rule(request.TraceExpiresAtMs <= now ? AbyssalRuleCode.TraceExpired : AbyssalRuleCode.InvalidRequest,
                "the trace identity, origin, filament, or expiry is invalid");
        }
        ValidatePosition(request.SourcePosition);
        if (request.Plan is null || request.Plan.Rooms is null || request.Plan.Rooms.Count != 3 ||
            request.Plan.Tier is < 0 or > 6 || string.IsNullOrWhiteSpace(request.Plan.WeatherType) ||
            request.Plan.NebulaGraphicId <= 0 ||
            request.Plan.SpaceLocationId <= 0)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "the validated run plan must contain tier, weather, and three rooms");
        }

        var expectedMaximum = request.GameModeId switch
        {
            AbyssalGameMode.SoloCruiser => 1,
            AbyssalGameMode.ThreeFrigates => 3,
            AbyssalGameMode.TwoDestroyers => 2,
            _ => throw Rule(AbyssalRuleCode.InvalidGameMode, "the game_mode_id is not a supported PvE Abyss mode"),
        };
        if (request.Plan.MaximumParticipants != expectedMaximum)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "the plan participant limit does not match game_mode_id");
        }
        if (request.GameModeId != AbyssalGameMode.SoloCruiser && request.FleetId is null)
        {
            throw Rule(AbyssalRuleCode.FleetMismatch, "a cooperative game mode requires its deploying fleet");
        }

        var locations = request.Plan.Rooms.Select(room => room.LocationId).ToArray();
        if (!locations.SequenceEqual(new[] { 1, 2, 3 }))
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "room location identifiers must be the client ordinals 1, 2, and 3");
        }
        if (request.Plan.Rooms[0].NebulaGraphicId != request.Plan.NebulaGraphicId ||
            request.Plan.Rooms.Any(room => room.NebulaGraphicId is null or <= 0 ||
                                           string.IsNullOrWhiteSpace(room.EnvironmentProfileId) ||
                                           room.NpcSpawns is null || room.Loot is null || room.Objectives is null ||
                                           room.EnvironmentObjects is null || room.Artifacts is null))
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "every room requires validated environment, spawn, and artifact data");
        }
        foreach (var room in request.Plan.Rooms)
        {
            if (room.NpcSpawns.Any(spawn => spawn.TypeId <= 0 || spawn.Count < 0) ||
                room.Loot.Any(loot => loot is null || loot.TypeId <= 0 || loot.Count < 0) ||
                room.Objectives.Any(objective => string.IsNullOrWhiteSpace(objective.Id) || objective.NpcTypeIds is null))
            {
                throw Rule(AbyssalRuleCode.InvalidRequest, "the room plan contains invalid spawn, loot, or objective data");
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var environmentalObject in room.EnvironmentObjects)
            {
                if (environmentalObject is null || string.IsNullOrWhiteSpace(environmentalObject.Key) ||
                    !keys.Add(environmentalObject.Key) || environmentalObject.TypeId <= 0 ||
                    environmentalObject.LocalPosition is null || !IsFinite(environmentalObject.LocalPosition) ||
                    environmentalObject.Rotation is null || !IsFinite(environmentalObject.Rotation) ||
                    string.IsNullOrWhiteSpace(environmentalObject.Role) ||
                    string.IsNullOrWhiteSpace(environmentalObject.SourceAuthority) ||
                    (environmentalObject.PresentationRadiusMeters is { } radius &&
                     (!double.IsFinite(radius) || radius < 0)))
                {
                    throw Rule(AbyssalRuleCode.InvalidRequest, "the room plan contains an invalid or duplicate environmental-object key or transform");
                }
            }

            foreach (var artifact in room.Artifacts)
            {
                if (artifact is null || string.IsNullOrWhiteSpace(artifact.Key) || !keys.Add(artifact.Key) ||
                    artifact.TypeId <= 0 || !Enum.IsDefined(artifact.Kind) || artifact.LocalPosition is null ||
                    !IsFinite(artifact.LocalPosition) || artifact.Loot is null ||
                    string.IsNullOrWhiteSpace(artifact.QuantityPolicy) || string.IsNullOrWhiteSpace(artifact.SourceAuthority))
                {
                    throw Rule(AbyssalRuleCode.InvalidRequest, "the room plan contains an invalid or duplicate artifact key or allocation");
                }
                if (artifact.Loot.Any(loot => loot is null || loot.TypeId <= 0 || loot.Count < 0 || !HasValidBlueprintRuns(loot)))
                {
                    throw Rule(AbyssalRuleCode.InvalidRequest, "artifact loot contains invalid quantities or blueprint-copy data");
                }
            }

            if (room.Artifacts.Any(artifact => artifact.Kind == AbyssalArtifactKind.MainCache) && room.Loot.Count != 0)
            {
                throw Rule(AbyssalRuleCode.InvalidRequest,
                    "main-cache rewards must have one owner: room loot or main-cache artifact loot, not both");
            }
            if (room.Loot.Any(loot => !HasValidBlueprintRuns(loot)))
            {
                throw Rule(AbyssalRuleCode.InvalidRequest,
                    "blueprint-copy loot requires one 1..5 run count per copy, and non-blueprint loot cannot include blueprint runs");
            }
        }
    }

    private static void ValidateAdmission(AbyssalRunData data, AbyssalParticipantAdmission admission)
    {
        if (admission is null || admission.CharacterId <= 0 || admission.ShipId <= 0 || admission.ShipTypeId <= 0 ||
            string.IsNullOrWhiteSpace(admission.ShipClass) || admission.Origin is null ||
            admission.Origin.SolarSystemId <= 0 || admission.Origin.LocationId <= 0 ||
            !double.IsFinite(admission.Origin.Radius) || admission.Origin.Radius < 0)
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "the participant admission or saved origin is invalid");
        }
        ValidatePosition(admission.Origin.ShipPosition);
        ValidatePosition(admission.Origin.ShipDirection);
        var expectedClass = data.GameModeId switch
        {
            AbyssalGameMode.SoloCruiser => "cruiser",
            AbyssalGameMode.ThreeFrigates => "frigate",
            AbyssalGameMode.TwoDestroyers => "destroyer",
            _ => throw Rule(AbyssalRuleCode.InvalidGameMode, "the run contains an unsupported game_mode_id"),
        };
        if (!string.Equals(expectedClass, admission.ShipClass, StringComparison.OrdinalIgnoreCase))
        {
            throw Rule(AbyssalRuleCode.InvalidShipClass, "the participant ship class does not match game_mode_id");
        }
        if (data.GameModeId != AbyssalGameMode.SoloCruiser && admission.FleetId != data.FleetId)
        {
            throw Rule(AbyssalRuleCode.FleetMismatch, "the participant is not in the fleet that deployed this trace");
        }
    }

    private static void ValidateSpawnReservation(AbyssalRunData data, int roomIndex, IReadOnlyList<AbyssalSpaceEntity> entities)
    {
        if (entities is null)
        {
            throw Rule(AbyssalRuleCode.RoomSpawnMismatch, "a room spawn reservation must include the entity IDs to persist");
        }
        if (entities.Any(entity => entity is null || entity.EntityId <= 0 || entity.TypeId <= 0 ||
                                   entity.Position is null || !IsFinite(entity.Position) ||
                                   (entity.PlanKey is not null && string.IsNullOrWhiteSpace(entity.PlanKey))))
        {
            throw Rule(AbyssalRuleCode.RoomSpawnMismatch, "the room spawn reservation contains invalid entity IDs, transforms, or plan keys");
        }

        var ids = entities.Select(entity => entity.EntityId).ToArray();
        if (ids.Distinct().Count() != ids.Length)
        {
            throw Rule(AbyssalRuleCode.RoomSpawnMismatch, "the room spawn reservation contains invalid or duplicate entity IDs");
        }
        var alreadyReserved = data.Rooms.SelectMany(room => room.Entities).Select(entity => entity.EntityId).ToHashSet();
        if (ids.Any(alreadyReserved.Contains))
        {
            throw Rule(AbyssalRuleCode.RoomSpawnMismatch, "a space entity ID is already reserved by another room");
        }

        var roomPlan = data.Plan.Rooms[roomIndex];
        var plannedKeys = (roomPlan.EnvironmentObjects ?? Array.Empty<AbyssalEnvironmentObjectPlan>())
            .Select(environmentalObject => (environmentalObject.Key, environmentalObject.TypeId))
            .Concat((roomPlan.Artifacts ?? Array.Empty<AbyssalArtifactPlan>())
                .Select(artifact => (artifact.Key, artifact.TypeId)))
            .ToDictionary(entry => entry.Key, entry => entry.TypeId, StringComparer.Ordinal);
        var keyedEntities = entities.Where(entity => entity.PlanKey is not null).ToArray();
        if (keyedEntities.Length != plannedKeys.Count ||
            keyedEntities.Select(entity => entity.PlanKey).Distinct(StringComparer.Ordinal).Count() != keyedEntities.Length ||
            keyedEntities.Any(entity => !plannedKeys.TryGetValue(entity.PlanKey!, out var typeId) || typeId != entity.TypeId))
        {
            throw Rule(AbyssalRuleCode.RoomSpawnMismatch,
                "reserved plan keys and type IDs must cover the saved environmental objects and artifacts exactly once");
        }

        var planned = roomPlan.NpcSpawns
            .Where(spawn => spawn.Count > 0)
            .GroupBy(spawn => spawn.TypeId)
            .ToDictionary(group => group.Key, group => group.Sum(spawn => spawn.Count));
        var actual = entities.Where(entity => entity.Role == AbyssalEntityRole.Npc)
            .GroupBy(entity => entity.TypeId)
            .ToDictionary(group => group.Key, group => group.Count());
        if (planned.Count != actual.Count || planned.Any(pair => !actual.TryGetValue(pair.Key, out var count) || count != pair.Value))
        {
            throw Rule(AbyssalRuleCode.RoomSpawnMismatch, "reserved NPC type IDs and counts do not match the saved room plan");
        }
    }

    public static AbyssalContentType ContentTypeForGameMode(AbyssalGameMode mode) => mode switch
    {
        AbyssalGameMode.SoloCruiser => AbyssalContentType.SoloCruiser,
        AbyssalGameMode.ThreeFrigates => AbyssalContentType.FrigateFleet,
        AbyssalGameMode.TwoDestroyers => AbyssalContentType.TwoPlayerDestroyers,
        _ => throw Rule(AbyssalRuleCode.InvalidGameMode, "the game_mode_id is not a supported PvE Abyss mode"),
    };

    private static AbyssalParticipantRecipient ToRecipient(AbyssalParticipantData participant) =>
        new(participant.CharacterId, participant.ShipId, participant.Status);

    private static int IndexOfParticipant(AbyssalRunData data, long characterId) =>
        data.Participants.ToList().FindIndex(participant => participant.CharacterId == characterId);

    private static AbyssalRunData ReplaceParticipant(AbyssalRunData data, int index, AbyssalParticipantData participant)
    {
        var participants = data.Participants.ToArray();
        participants[index] = participant;
        return data with { Participants = participants };
    }

    private static AbyssalRunData ReplaceRoom(AbyssalRunData data, int index, AbyssalRoomState room)
    {
        var rooms = data.Rooms.ToArray();
        rooms[index] = room;
        return data with { Rooms = rooms };
    }

    private static int RoomIndex(AbyssalRunData data, int locationId)
    {
        var index = data.Rooms.ToList().FindIndex(room => room.LocationId == locationId);
        return index >= 0 ? index : throw Rule(AbyssalRuleCode.InvalidRequest, "the location_id is outside the run's three room ordinals");
    }

    private static string RoomClearObjectiveId(int locationId) =>
        "room-clear-" + locationId.ToString(CultureInfo.InvariantCulture);

    private static string Prefix(long instanceId) =>
        "abyssal:" + instanceId.ToString(CultureInfo.InvariantCulture) + ":";

    private static AbyssalRuleException Rule(AbyssalRuleCode code, string message) => new(code, message);

    private static void ValidatePosition(AbyssalPosition? position)
    {
        if (position is null || !IsFinite(position))
        {
            throw Rule(AbyssalRuleCode.InvalidRequest, "the saved position must contain finite coordinates");
        }
    }

    private static bool IsFinite(AbyssalPosition position) =>
        double.IsFinite(position.X) && double.IsFinite(position.Y) && double.IsFinite(position.Z);

    private static bool IsFinite(AbyssalRotation rotation) =>
        double.IsFinite(rotation.X) && double.IsFinite(rotation.Y) && double.IsFinite(rotation.Z);
}
