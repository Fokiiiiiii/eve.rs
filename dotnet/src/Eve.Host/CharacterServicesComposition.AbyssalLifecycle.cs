using Eve.Content;
using Eve.Events;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private readonly SemaphoreSlim abyssalPublicationLock = new(1, 1);

    private async Task<Eve.Services.Space.SimAbyssalTracePublishReply> PublishAbyssalDeploymentAsync(
        AbyssalTracePublishRequestedEvent request, ulong? actorSessionId, CancellationToken ct)
    {
        await abyssalPublicationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (request.Consumption is { } consumed) abyssalNotifications.SendFilamentConsumed(consumed);
            return await (spaceSessions ?? throw new InvalidOperationException("Trace publication requires SpaceSessions."))
                .EnsureAbyssalTracePublishedAsync(request.Trace, actorSessionId, ct).ConfigureAwait(false);
        }
        finally { abyssalPublicationLock.Release(); }
    }

    private const string AbyssalAdministrativeArrivalTopic = "abyssal.host.administrative_arrival_requested";
    private AbyssalCacheSettlementProcessor abyssalCacheSettlement = default!;

    internal void RegisterAbyssalReturnAndCacheHandlers()
    {
        RegisterAbyssalCleanup();
        foundations.Events.Subscribe("abyssal.space.room-transition", [AbyssalContentRuntime.RoomTransitionRequestedTopic],
            MoveAbyssalRoomGroupAsync, StartAt.Beginning);
        foundations.Events.Subscribe("abyssal.host.room-spawn", [AbyssalContentRuntime.RoomSpawnRequestedTopic],
            MaterializeAbyssalRoomRequestedAsync, StartAt.Beginning);
        foundations.Events.Subscribe("abyssal.client.location-entered", [AbyssalContentRuntime.LocationEnteredTopic],
            NotifyAbyssalLocationEnteredAsync, StartAt.Beginning);
        foundations.Events.Subscribe("abyssal.host.trace-publish",
            [AbyssalRunAdmission.TracePublishRequestedTopic], PublishAbyssalTraceRequestedAsync, StartAt.Beginning);
        foundations.Events.Subscribe("abyssal.host.death-settlement",
            [AbyssalContentRuntime.ParticipantDeathSettlementRequestedTopic], ProcessAbyssalDeathSettlementAsync, StartAt.Beginning);
        // One subscriber cursor serializes exit notices, return movement and the resulting finished notice.
        foundations.Events.Subscribe("abyssal.host.participant-return",
            [AbyssalContentRuntime.LocationExitedTopic, AbyssalContentRuntime.ParticipantReturnRequestedTopic,
             AbyssalContentRuntime.ParticipantContentFinishedTopic, AbyssalAdministrativeArrivalTopic],
            DispatchAbyssalLifecycleAsync, StartAt.Beginning);
        foundations.Events.Subscribe("abyssal.host.cache-destroyed",
            [AbyssalCacheSettlementProcessor.DestroyedTopic], abyssalCacheSettlement.RecordDestructionAsync, StartAt.Beginning);
        foundations.Events.Subscribe("abyssal.host.cache-materialize",
            [AbyssalCacheSettlementProcessor.MaterializeTopic], abyssalCacheSettlement.MaterializeAsync, StartAt.Beginning);
        foundations.Events.Subscribe("abyssal.host.cache-wreck",
            [AbyssalCacheSettlementProcessor.WreckTopic], PublishAbyssalCacheWreckAsync, StartAt.Beginning);
    }

    private Task DispatchAbyssalLifecycleAsync(EventContext context, CancellationToken ct) => context.Event.Topic switch
    {
        AbyssalContentRuntime.LocationExitedTopic => NotifyAbyssalExitFromOutboxAsync(context, ct),
        AbyssalContentRuntime.ParticipantReturnRequestedTopic => ProcessAbyssalReturnRequestedAsync(context, ct),
        AbyssalAdministrativeArrivalTopic => CompleteAbyssalAdministrativeArrivalAsync(context, ct),
        AbyssalContentRuntime.ParticipantContentFinishedTopic => NotifyAbyssalFinishedAsync(context),
        _ => throw new InvalidDataException("Unknown Abyssal lifecycle event."),
    };

    private async Task NotifyAbyssalExitFromOutboxAsync(EventContext context, CancellationToken ct)
    {
        var request = context.Event.Payload<AbyssalLocationExitedEvent>()
            ?? throw new InvalidDataException("Missing Abyssal exit event.");
        if (!await MayDeliverAbyssalLifecycleAsync(context, request.InstanceId, request.RecipientCharacterId, ct).ConfigureAwait(false)) return;
        var instance = await foundations.Content.GetInTransactionAsync(context.Connection, context.Transaction, request.InstanceId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The exit notification run is missing.");
        var data = instance.Data<AbyssalRunData>() ?? throw new InvalidDataException("The exit notification data is missing.");
        var participant = data.Participants.Single(value => value.CharacterId == request.RecipientCharacterId);
        if (data.ContentId != request.ContentId) throw new InvalidDataException("The exit notification identity changed.");
        if (participant.CurrentRoomIndex > request.LocationId + 1 ||
            (participant.Status is AbyssalParticipantStatus.Returning or AbyssalParticipantStatus.Returned) &&
            participant.CurrentRoomIndex != request.LocationId) return;
        await NotifyAbyssalExitAsync(request).ConfigureAwait(false);
    }

    private async Task<bool> MayDeliverAbyssalLifecycleAsync(EventContext context, long instanceId, long characterId, CancellationToken ct)
    {
        if (characterSessions.Of(characterId) is not { } session) return false;
        if (spaceSessions?.ShipIdOf(session) is not { } shipId) return true;
        var binding = await abyssalRuntime.FindActiveForParticipantAsync(context.Connection, context.Transaction, characterId, shipId, ct).ConfigureAwait(false);
        return binding is null || binding.Run.InstanceId == instanceId;
    }

    private Task NotifyAbyssalExitAsync(AbyssalLocationExitedEvent request)
    {
        if (characterSessions.Of(request.RecipientCharacterId) is null) return Task.CompletedTask;
        var environment = abyssalCatalog.GetEnvironmentRoom(request.EnvironmentProfileId);
        var (dungeonId, _) = AbyssalDungeonProjection.RoomIds(request.LocationId, environment);
        if (!abyssalNotifications.SendExitingDungeon(request.RecipientCharacterId, dungeonId, request.ContentId, request.LocationId) ||
            !abyssalNotifications.SendLocationExited(request))
            throw new InvalidOperationException($"Abyssal exit delivery failed for character {request.RecipientCharacterId}.");
        return Task.CompletedTask;
    }

    private async Task NotifyAbyssalFinishedAsync(EventContext context)
    {
        var request = context.Event.Payload<AbyssalParticipantContentFinishedEvent>()
            ?? throw new InvalidDataException("Missing Abyssal finished event.");
        if (await MayDeliverAbyssalLifecycleAsync(context, request.InstanceId, request.RecipientCharacterId, CancellationToken.None).ConfigureAwait(false) &&
            !abyssalNotifications.SendContentFinished(request))
            throw new InvalidOperationException($"Abyssal finished delivery failed for character {request.RecipientCharacterId}.");
    }

    private async ValueTask PersistAbyssalCacheDestructionAsync(Eve.Services.Space.AbyssalRewardCacheDestroyedNotice notice, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await foundations.Events.PublishAsync(connection, transaction, AbyssalCacheSettlementProcessor.DestroyedTopic,
            new AbyssalCacheDestroyedEvent(notice.InstanceId, notice.ContentId, notice.RoomOrdinal,
                notice.CacheItemId, notice.TypeId, notice.Position, notice.Direction),
            $"abyssal:{notice.ContentId}:cache:{notice.CacheItemId}:destroyed", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        foundations.Events.Wake();
    }

    private async Task PublishAbyssalCacheWreckAsync(EventContext context, CancellationToken ct)
    {
        var container = await abyssalCacheSettlement.PreparePublicationAsync(context, ct).ConfigureAwait(false);
        if (container is null) return;
        var request = context.Event.Payload<AbyssalCacheWreckRequestedEvent>()!.Destruction;
        var error = await (spaceSessions ?? throw new InvalidOperationException("Abyssal wreck publication requires SpaceSessions."))
            .EnsureAbyssalLootContainerAsync(request.ContentId, container, ct).ConfigureAwait(false);
        if (error is not null) throw new InvalidOperationException($"Abyssal cache wreck publication failed: {error}");
    }

    private async Task PersistAbyssalOriginAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        AbyssalParticipantReturnRequestedEvent request, CancellationToken ct)
    {
        var character = await characters.LockAndGetAsync(connection, transaction, request.RecipientCharacterId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The returning character is missing.");
        var ship = await items.LockAndGetAsync(connection, transaction, request.ShipId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The returning ship is missing.");
        if (character.ShipId != ship.Id || ship.OwnerId != character.Id || !ship.Singleton || ship.Quantity != 1 ||
            ship.Flag != CharacterShips.InSpaceFlag)
            throw new InvalidDataException("The returning ship no longer matches the persisted character authority.");
        await PersistAbyssalCharacterLocationAsync(connection, transaction, character.Id,
            checked((int)request.Origin.SolarSystemId), ct).ConfigureAwait(false);
        var type = await itemTypes.GetAsync(ship.TypeId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The returning ship type is absent from SDE.");
        await spaceItemStore.SaveAsync(connection, transaction, new SpaceItem(ship.Id,
            checked((int)request.Origin.SolarSystemId),
            [request.Origin.ShipPosition.X, request.Origin.ShipPosition.Y, request.Origin.ShipPosition.Z],
            [request.Origin.ShipDirection.X, request.Origin.ShipDirection.Y, request.Origin.ShipDirection.Z],
            request.Origin.Radius, type.GroupId, 0, character.Id, character.CorporationId,
            character.AllianceId ?? 0, character.FactionId ?? 0, long.MaxValue), ct).ConfigureAwait(false);
    }

    private async Task CompleteAbyssalAdministrativeArrivalAsync(EventContext context, CancellationToken ct)
    {
        var request = context.Event.Payload<AbyssalParticipantReturnRequestedEvent>()
            ?? throw new InvalidDataException("The administrative arrival request is missing.");
        var instance = await foundations.Content.GetInTransactionAsync(context.Connection, context.Transaction, request.InstanceId, ct)
            .ConfigureAwait(false) ?? throw new InvalidDataException("The administrative arrival run is missing.");
        var data = instance.Data<AbyssalRunData>() ?? throw new InvalidDataException("The administrative arrival run data is missing.");
        var participant = data.Participants.Single(value => value.CharacterId == request.RecipientCharacterId);
        if (data.ContentId != request.ContentId || participant.ShipId != request.ShipId)
            throw new InvalidDataException("The administrative arrival identity changed.");
        if (participant.Status == AbyssalParticipantStatus.Returned) return;
        if (instance.State != "active" || participant.Status != AbyssalParticipantStatus.Returning) return;
        if (characterSessions.Of(request.RecipientCharacterId) is { } session)
        {
            var character = await characters.GetAsync(request.RecipientCharacterId, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("The administrative arrival character is missing.");
            if (character.ShipId != request.ShipId || character.SolarSystemId != request.Origin.SolarSystemId)
                throw new InvalidDataException("The administrative arrival authority has not been persisted.");
            session.Change(("stationid", PyNone.Instance), ("stationid2", PyNone.Instance),
                ("solarsystemid", new PyInt(character.SolarSystemId)), ("solarsystemid2", new PyInt(character.SolarSystemId)),
                ("locationid", new PyInt(character.SolarSystemId)), ("constellationid", new PyInt(character.ConstellationId)),
                ("regionid", new PyInt(character.RegionId)), ("shipid", new PyLong(request.ShipId)));
            await (spaceSessions ?? throw new InvalidOperationException("Administrative arrival requires SpaceSessions."))
                .OnCharacterEnteredAsync(session, character.Id, ct).ConfigureAwait(false);
        }
        await abyssalRuntime.RecordOriginReturnAsync(context.Connection, context.Transaction,
            new AbyssalReturnReceiptRequest(request.InstanceId, request.RecipientCharacterId), ct).ConfigureAwait(false);
    }
}
