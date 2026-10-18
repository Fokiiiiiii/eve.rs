using System.Collections.Concurrent;
using System.Text.Json;
using Eve.Content;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;

namespace Eve.Host;


internal sealed partial class CharacterServicesComposition
{
    private AbyssalStaticCatalog abyssalCatalog = default!;
    private AbyssalContentRuntime abyssalRuntime = default!;
    private AbyssalDungeonReadModel abyssalDungeonReadModel = default!;
    private AbyssalNotificationAdapter abyssalNotifications = default!;
    private AbyssalDeathSettlementProcessor abyssalDeathSettlement = default!;
    private AbyssalRunAdmission abyssalRunAdmission = default!;
    private double abyssalTraceRadius;
    private AbyssalLootMaterializer abyssalLootMaterializer = default!;
    private AbyssalRoomSpawnReservation abyssalRoomSpawnReservation = default!;
    private readonly ConcurrentDictionary<string, IReadOnlyList<Eve.Dogma.EffectDef>> abyssalDogmaEffectsByContentId = new(StringComparer.Ordinal);

    private async Task InitializeAbyssalRuntime()
    {
        abyssalCatalog = await AbyssalStaticCatalog.LoadAsync(sde).ConfigureAwait(false);
        abyssalRuntime = new AbyssalContentRuntime(foundations.Content, foundations.Events, foundations.Scheduler);
        abyssalDeathSettlement = new AbyssalDeathSettlementProcessor(ships, characters, items, itemTypes, catalog, stations);
        if (spaceSessions is null)
            throw new InvalidOperationException("Abyssal Host integration requires the initialized SpaceSessions facade.");
        spaceSessions.RookieShipOfRace = raceId => catalog.Race(raceId)?.ShipTypeId;
        spaceSessions.AbyssalEnvironmentEffects = ResolveAbyssalDogmaEffects;

        var blueprintCategoryId = await AbyssalLootMaterializer.LoadBlueprintCategoryIdAsync(sde).ConfigureAwait(false);
        abyssalLootMaterializer = new AbyssalLootMaterializer(items, itemTypes, blueprintCategoryId);
        abyssalCacheSettlement = new AbyssalCacheSettlementProcessor(
            items, itemTypes, sde, foundations.Content, foundations.Events, abyssalLootMaterializer, () => foundations.Scheduler.Now);
        abyssalRoomSpawnReservation = new AbyssalRoomSpawnReservation(
            sde, items, itemTypes, abyssalCatalog, abyssalRuntime);
        var traceRows = await sde.ReadAsync("types", [AbyssalTraceFactory.TraceTypeId]).ConfigureAwait(false);
        var traceRow = traceRows.SingleOrDefault()
            ?? throw new InvalidDataException($"SDE trace type {AbyssalTraceFactory.TraceTypeId} is missing.");
        var traceType = await itemTypes.GetAsync(AbyssalTraceFactory.TraceTypeId).ConfigureAwait(false);
        if (traceType is null ||
            traceType.GroupId != AbyssalTraceFactory.TraceGroupId ||
            traceType.CategoryId != AbyssalTraceFactory.TraceCategoryId ||
            !traceRow.Doc.TryGetProperty("radius", out var radiusValue) ||
            !radiusValue.TryGetDouble(out abyssalTraceRadius) ||
            !double.IsFinite(abyssalTraceRadius) ||
            abyssalTraceRadius <= 0)
        {
            throw new InvalidDataException("The Abyssal trace's SDE group, category, or radius is invalid.");
        }

        abyssalDeploymentEligibility = await AbyssalDeploymentEligibility.LoadAsync(sde, itemTypes).ConfigureAwait(false);
        abyssalRunAdmission = new AbyssalRunAdmission(
            source,
            characters,
            items,
            itemTypes,
            abyssalCatalog,
            abyssalRuntime,
            foundations.Events,
            new AbyssalFilamentConsumer(items, itemTypes),
            abyssalDeploymentEligibility,
            ReadAbyssalDeploymentEligibilityAsync,
            blueprintCategoryId,
            abyssalTraceRadius,
            () => foundations.Scheduler.Now);

        abyssalDungeonReadModel = new AbyssalDungeonReadModel(
            source,
            abyssalRuntime,
            abyssalCatalog,
            spaceSessions);
        abyssalNotifications = new AbyssalNotificationAdapter(characterSessions);
        spaceSessions.FindAbyssalReconnectAsync = FindAbyssalReconnectAsync;
        spaceSessions.AbyssalParticipantDisconnectedAsync = PersistAbyssalDisconnectAsync;
        spaceSessions.AbyssalNpcDestroyedAsync = PersistAbyssalNpcDestructionAsync;
        spaceSessions.AbyssalParticipantDestroyedAsync = PersistAbyssalDeathRequestAsync;
        spaceSessions.AbyssalSimulationLostAsync = ReconcileAbyssalSimulationLostAsync;
        spaceSessions.AbyssalRewardCacheDestroyedAsync = PersistAbyssalCacheDestructionAsync;

        foundations.Events.Subscribe(
            "abyssal.host.trace-removal",
            [AbyssalRunAdmission.TraceRemovalRequestedTopic],
            RemoveAbyssalTraceRequestedAsync, Eve.Events.StartAt.Beginning);
        RegisterAbyssalReturnAndCacheHandlers();

        foundations.Events.Subscribe(
            "abyssal.client.deadline",
            [AbyssalContentRuntime.DeadlineAddedTopic],
            (context, _) =>
            {
                abyssalNotifications.SendDeadlineAdded(
                    context.Event.Payload<AbyssalDeadlineAddedEvent>()!);
                return Task.CompletedTask;
            });


        await ReconcileAbyssalStartupAsync(CancellationToken.None).ConfigureAwait(false);
    }


    private async Task ReconcileAbyssalStartupAsync(CancellationToken cancellationToken)
    {
        var space = spaceSessions
            ?? throw new InvalidOperationException("Abyssal startup reconciliation requires initialized SpaceSessions.");
        var now = foundations.Scheduler.Now;
        var restore = new List<SimAbyssalTrace>();

        await using (var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            var active = await new AbyssalStartupReadModel(foundations.Content, foundations.ContentSchema)
                .ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            foreach (var instance in active.OrderBy(value => value.Id))
            {
                var data = instance.Data<AbyssalRunData>()
                    ?? throw new InvalidDataException($"Abyssal ContentRuntime instance {instance.Id} has no saved run data.");

                if (instance.State != "active")
                {
                    await abyssalRuntime.RecoverAfterServerRestartAsync(
                        connection, transaction, instance.Id, cancellationToken).ConfigureAwait(false);
                    await foundations.Events.PublishAsync(connection, transaction, AbyssalCleanupTopic,
                        new AbyssalCleanupRequestedEvent(instance.Id, data.ContentId), $"abyssal:{data.ContentId}:cleanup", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (data.RunStartedAtMs is not null)
                {
                    RememberAbyssalWeather(data);
                    await abyssalRuntime.RecoverAfterServerRestartAsync(
                        connection, transaction, instance.Id, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var traceItem = await items.LockAndGetAsync(
                    connection, transaction, data.TraceItemId, cancellationToken).ConfigureAwait(false);
                if (data.TraceExpiresAtMs <= now || instance.ExpiresMs is { } expiresAtMs && expiresAtMs <= now)
                {
                    var expired = await abyssalRuntime.ExpireIfDueAsync(
                        connection, transaction, instance.Id, cancellationToken).ConfigureAwait(false);
                    if (expired.State.ContentState == "active")
                        throw new InvalidDataException($"Unentered Abyssal trace {data.TraceItemId} passed its deadline without expiring.");

                    if (traceItem is not null)
                    {
                        ValidatePersistedTraceItem(traceItem, data);
                        await items.DeleteAsync(connection, transaction, traceItem.Id, cancellationToken).ConfigureAwait(false);
                    }
                    await foundations.Events.PublishAsync(
                        connection,
                        transaction,
                        AbyssalRunAdmission.TraceRemovalRequestedTopic,
                        new AbyssalTraceRemovalRequestedEvent(
                            instance.Id,
                            data.ContentId,
                            checked((int)data.SourceSolarSystemId),
                            data.TraceItemId),
                        $"abyssal:{data.ContentId}:trace-remove",
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (instance.ExpiresMs is null || traceItem is null)
                    throw new InvalidDataException($"Unentered Abyssal trace {data.TraceItemId} is missing its item or durable deadline.");
                ValidatePersistedTraceItem(traceItem, data);
                var pose = JsonSerializer.Deserialize<AbyssalTracePoseSnapshot>(traceItem.CustomInfo ?? string.Empty, TracePoseJson)
                    ?? throw new InvalidDataException($"Abyssal trace {data.TraceItemId} has no saved pose snapshot.");
                restore.Add(AbyssalTraceFactory.Restore(
                    checked((int)data.SourceSolarSystemId),
                    data.TraceItemId,
                    (int)data.GameModeId,
                    data.FilamentTypeId,
                    data.Tier,
                    data.TraceExpiresAtMs,
                    abyssalTraceRadius,
                    pose));
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var trace in restore)
        {
            var published = await space.EnsureAbyssalTracePublishedAsync(
                trace, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!published.Succeeded)
                throw new InvalidOperationException(published.Error ?? $"Abyssal trace {trace.ItemId} could not be restored.");
        }

        foundations.Events.Wake();
    }


    private async Task PublishAbyssalTraceRequestedAsync(
        Eve.Events.EventContext context,
        CancellationToken cancellationToken)
    {
        var request = context.Event.Payload<AbyssalTracePublishRequestedEvent>()
            ?? throw new InvalidDataException("The Abyssal trace-publish event payload is missing.");
        var run = await abyssalRuntime.FindByTraceItemIdAsync(context.Connection, context.Transaction,
            request.Trace.ItemId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The trace publication run is missing.");
        if (run.ContentState != "active" || run.Data.TraceExpiresAtMs <= foundations.Scheduler.Now) return;
        var reply = await PublishAbyssalDeploymentAsync(request, null, cancellationToken).ConfigureAwait(false);
        if (!reply.Succeeded)
            throw new InvalidOperationException(reply.Error ?? $"Abyssal trace {request.Trace.ItemId} could not be published.");
    }


    private void RememberAbyssalWeather(AbyssalRunData data)
    {
        if (!Guid.TryParse(data.ContentId, out _))
            throw new InvalidDataException("Abyssal weather requires a persisted UUID ContentId.");

        var savedWeather = data.Plan.Rooms.SelectMany(room => room.EnvironmentObjects)
            .Where(value => value.Role == "weather_object").ToArray();
        if (savedWeather.Length != 0 && (savedWeather.Length != data.Plan.Rooms.Count ||
            data.Plan.Rooms.Any(room => room.EnvironmentObjects.Count(value => value.Role == "weather_object") != 1) ||
            savedWeather.Select(value => value.TypeId).Distinct().Count() != 1))
            throw new InvalidDataException("The saved room weather objects do not agree on one run weather type.");
        var selectedTypeId = savedWeather.Length > 0 ? savedWeather[0].TypeId
            : abyssalCatalog.ResolveWeather(data.Tier, data.WeatherType, data.ContentId).TypeId;
        var selected = abyssalCatalog.GetWeatherStages(data.Tier, data.WeatherType)
            .SingleOrDefault(value => value.TypeId == selectedTypeId)
            ?? throw new InvalidDataException("The saved weather type is not an allowed SDE weather stage for this run.");
        var effects = selected.Effects.Select(effect =>
            dogma.BuffCollections.TryGetValue(effect.CollectionId, out var collection)
                ? collection.EffectFor(effect.Value)
                : throw new InvalidDataException(
                    $"Abyssal weather buff collection {effect.CollectionId} is absent from the pinned Dogma catalog."))
            .ToArray();
        if (effects.Length != 2)
            throw new InvalidDataException($"Abyssal weather {data.WeatherType}/{selected.Stage} did not resolve both Dogma effects.");

        abyssalDogmaEffectsByContentId[data.ContentId] = Array.AsReadOnly(effects);
    }

    private IReadOnlyList<Eve.Dogma.EffectDef> ResolveAbyssalDogmaEffects(string contentId, int _)
    {
        if (abyssalDogmaEffectsByContentId.TryGetValue(contentId, out var effects))
            return effects;
        throw new InvalidDataException($"Abyssal weather effects for ContentId {contentId} were not loaded from the SDE.");
    }


    private async Task RemoveAbyssalTraceRequestedAsync(
        Eve.Events.EventContext context,
        CancellationToken cancellationToken)
    {
        var request = context.Event.Payload<AbyssalTraceRemovalRequestedEvent>()
            ?? throw new InvalidDataException("The Abyssal trace-removal event payload is missing.");
        if (request.InstanceId <= 0 || string.IsNullOrWhiteSpace(request.ContentId) ||
            request.SolarSystemId <= 0 || request.TraceItemId <= 0)
        {
            throw new InvalidDataException("The Abyssal trace-removal event identity is invalid.");
        }

        var run = await abyssalRuntime.FindByTraceItemIdAsync(
            context.Connection,
            context.Transaction,
            request.TraceItemId,
            cancellationToken).ConfigureAwait(false);
        if (run is null ||
            run.InstanceId != request.InstanceId ||
            run.Data.ContentId != request.ContentId ||
            run.Data.SpaceLocationId <= 0 ||
            run.Data.SourceSolarSystemId != request.SolarSystemId ||
            run.ContentState == "active")
        {
            throw new InvalidDataException($"Abyssal trace {request.TraceItemId} removal does not match a terminal Domain run.");
        }

        if (await items.GetAsync(request.TraceItemId, cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException($"Abyssal trace item {request.TraceItemId} must be deleted before Space removal.");

        var space = spaceSessions
            ?? throw new InvalidOperationException("Abyssal trace removal requires initialized SpaceSessions.");
        var removed = await space.RemoveAbyssalTraceAsync(
            request.SolarSystemId,
            request.TraceItemId,
            cancellationToken).ConfigureAwait(false);
        if (!removed.Succeeded)
            throw new InvalidOperationException(removed.Error ?? $"Abyssal trace {request.TraceItemId} could not be removed from Space.");
    }

    private static void ValidatePersistedTraceItem(Item traceItem, AbyssalRunData data)
    {
        if (traceItem.Id != data.TraceItemId ||
            traceItem.TypeId != AbyssalTraceFactory.TraceTypeId ||
            traceItem.OwnerId != AbyssalTraceFactory.TraceOwnerId ||
            traceItem.LocationId != data.SourceSolarSystemId ||
            traceItem.Flag != CharacterShips.InSpaceFlag ||
            !traceItem.Singleton ||
            traceItem.Quantity != 1)
        {
            throw new InvalidDataException($"Persisted Abyssal trace item {data.TraceItemId} does not match its ContentRuntime run.");
        }
    }

    private static readonly JsonSerializerOptions TracePoseJson = new(JsonSerializerDefaults.Web);

    private async Task NotifyAbyssalLocationEnteredAsync(
    Eve.Events.EventContext context,
    CancellationToken cancellationToken)
{
    var notification = context.Event.Payload<AbyssalLocationEnteredEvent>()
        ?? throw new InvalidDataException("The Abyssal location-entered event payload is missing.");
    var instance = await foundations.Content.GetForUpdateAsync(context.Connection, context.Transaction,
        notification.InstanceId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("The room-entry run is missing.");
    var data = instance.Data<AbyssalRunData>() ?? throw new InvalidDataException("The room-entry plan is missing.");
    var savedRoom = data.Rooms.SingleOrDefault(value => value.LocationId == notification.LocationId);
    var savedPlan = data.Plan.Rooms.SingleOrDefault(value => value.LocationId == notification.LocationId);
    var savedParticipant = data.Participants.SingleOrDefault(value => value.CharacterId == notification.RecipientCharacterId);
    if (instance.Kind != AbyssalContentRuntime.Kind || data.ContentId != notification.ContentId ||
        data.SpaceLocationId != notification.SpaceLocationId || data.Tier != notification.DifficultyTier ||
        savedRoom is null || savedPlan is null || savedParticipant is null ||
        savedRoom.NebulaGraphicId != notification.NebulaGraphicId || savedPlan.EnvironmentProfileId != notification.EnvironmentProfileId)
        throw new InvalidDataException("The room-entry intent does not match its persisted recipient and room.");
    if (instance.State != "active")
    {
        if (data.Participants.All(AbyssalParticipantSettled)) return;
        throw new InvalidOperationException("A superseded room entry is waiting for terminal settlement receipts.");
    }
    if (data.CurrentRoomIndex > notification.LocationId || data.ExitMode != AbyssalExitMode.None) return;
    if (data.CurrentRoomIndex != notification.LocationId)
        throw new InvalidDataException("The room-entry intent targets an unentered future room.");
    if (characterSessions.Of(notification.RecipientCharacterId) is not { } session)
        return;

    var space = spaceSessions
        ?? throw new InvalidOperationException("Abyssal notifications require initialized SpaceSessions.");
    var shipId = space.ShipIdOf(session)
        ?? throw new InvalidOperationException($"Abyssal character {notification.RecipientCharacterId} has no active SpaceSessions ship.");
    var binding = await abyssalRuntime.FindActiveForParticipantAsync(
        context.Connection,
        context.Transaction,
        notification.RecipientCharacterId,
        shipId,
        cancellationToken).ConfigureAwait(false);
    if (binding is null ||
        binding.Run.InstanceId != notification.InstanceId ||
        !string.Equals(binding.Run.Data.ContentId, notification.ContentId, StringComparison.Ordinal) ||
        binding.Participant.Status != AbyssalParticipantStatus.Active ||
        binding.Participant.CurrentRoomIndex != notification.LocationId ||
        binding.Run.Data.SpaceLocationId != notification.SpaceLocationId)
    {
        return;
    }

    await PrepareAndMaterializeAbyssalRoomAsync(
        context.Connection, context.Transaction, binding.Run, notification.LocationId, cancellationToken).ConfigureAwait(false);
    var destination = new SimSceneKey(notification.SpaceLocationId, notification.InstanceId, notification.LocationId);
    var character = await characters.GetAsync(notification.RecipientCharacterId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException($"Abyssal character {notification.RecipientCharacterId} is missing.");
    if (character.SolarSystemId == binding.Participant.Origin.SolarSystemId)
    {
        var center = await abyssalRoomSpawnReservation.ResolvePrivateSceneOriginAsync(
            binding.Run.Data, cancellationToken).ConfigureAwait(false);
        var moveError = await space.MoveAbyssalParticipantAsync(
            notification.ContentId,
            shipId,
            SimSceneKey.SolarSystem(character.SolarSystemId),
            destination,
            [center.X, center.Y, center.Z],
            [binding.Participant.Origin.ShipDirection.X, binding.Participant.Origin.ShipDirection.Y, binding.Participant.Origin.ShipDirection.Z],
            cancellationToken).ConfigureAwait(false);
        if (moveError is not null)
            throw new InvalidOperationException($"Abyssal entry for ship {shipId} failed: {moveError}");
        await PersistAbyssalCharacterLocationAsync(
            context.Connection, context.Transaction, notification.RecipientCharacterId,
            notification.SpaceLocationId, cancellationToken).ConfigureAwait(false);
    }

    var pose = await space.GetAbyssalParticipantPoseAsync(
        notification.ContentId, shipId, destination, cancellationToken).ConfigureAwait(false);
    if (!pose.Found)
        throw new InvalidOperationException($"Abyssal location {notification.LocationId} for ship {shipId} is not materialized: {pose.Error}");

    var environment = abyssalCatalog.GetEnvironmentRoom(notification.EnvironmentProfileId);
    var (dungeonId, roomId) = AbyssalDungeonProjection.RoomIds(notification.LocationId, environment);
    if (!abyssalNotifications.SendEnteringDungeonRoom(
            notification.RecipientCharacterId,
            dungeonId,
            roomId,
            pose.Position,
            notification.ContentId) ||
        !abyssalNotifications.SendLocationEntered(notification, notification.DifficultyTier))
    {
        throw new InvalidOperationException($"The Abyssal location notification could not be delivered to character {notification.RecipientCharacterId}.");
    }
}


    private async Task<bool> HandleAbyssalClientIsReadyAsync(
        ClientSession session,
        CancellationToken cancellationToken)
    {
        var characterId = session.GetInt("charid") ?? session.GetInt("characterID") ?? 0;
        var shipId = spaceSessions?.ShipIdOf(session);
        if (characterId <= 0 || shipId is not > 0)
            return false;
        var character = await characters.GetAsync(characterId, cancellationToken).ConfigureAwait(false);
        if (character is null)
            return false;

        AbyssalParticipantRunBinding? binding;
        SimAbyssalTrace? sourceTrace = null;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            binding = await abyssalRuntime.FindActiveForParticipantAsync(
                connection, transaction, characterId, shipId.Value, cancellationToken).ConfigureAwait(false);
            if (binding is null)
            {
                var active = await foundations.Content.ActiveByKindReadOnlyAsync(
                    connection, transaction, AbyssalContentRuntime.Kind, cancellationToken).ConfigureAwait(false);
                var candidates = active.Select(instance => (Instance: instance, Data: instance.Data<AbyssalRunData>()))
                    .Where(candidate => candidate.Data is { RunStartedAtMs: null } candidateData &&
                                        candidateData.CreatorCharacterId == characterId &&
                                        candidateData.CreatorShipId == shipId.Value &&
                                        candidateData.SourceSolarSystemId == character.SolarSystemId &&
                                        candidateData.TraceExpiresAtMs > foundations.Scheduler.Now &&
                                        candidate.Instance.ExpiresMs is > 0 &&
                                        candidate.Instance.ExpiresMs > foundations.Scheduler.Now)
                    .ToArray();
                if (candidates.Length > 1)
                    throw new InvalidDataException($"Character {characterId} has more than one unentered Abyssal trace.");

                if (candidates.Length == 1)
                {
                    var traceData = candidates[0].Data!;
                    var traceItem = await items.LockAndGetAsync(
                        connection, transaction, traceData.TraceItemId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Unentered Abyssal trace item {traceData.TraceItemId} is missing.");
                    ValidatePersistedTraceItem(traceItem, traceData);
                    var pose = JsonSerializer.Deserialize<AbyssalTracePoseSnapshot>(
                        traceItem.CustomInfo ?? string.Empty, TracePoseJson)
                        ?? throw new InvalidDataException($"Abyssal trace {traceData.TraceItemId} has no saved pose snapshot.");
                    sourceTrace = AbyssalTraceFactory.Restore(
                        checked((int)traceData.SourceSolarSystemId),
                        traceData.TraceItemId,
                        (int)traceData.GameModeId,
                        traceData.FilamentTypeId,
                        traceData.Tier,
                        traceData.TraceExpiresAtMs,
                        abyssalTraceRadius,
                        pose);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var space = spaceSessions
            ?? throw new InvalidOperationException("ClientIsReady requires initialized SpaceSessions.");
        if (binding is null)
        {
            if (sourceTrace is null)
                return false;
            if (session.SessionId <= 0)
                throw new InvalidDataException("The Abyssal ClientIsReady session id must be positive.");
            var traceReply = await space.EnsureAbyssalTracePublishedAsync(
                sourceTrace,
                checked((ulong)session.SessionId),
                cancellationToken).ConfigureAwait(false);
            if (!traceReply.Succeeded)
                throw new InvalidOperationException(traceReply.Error ?? $"Abyssal trace {sourceTrace.ItemId} could not be republished.");
            return true;
        }

        var data = binding.Run.Data;
        var deadline = data.RunExpiresAtMs
            ?? throw new InvalidDataException($"Active Abyssal run {binding.Run.InstanceId} has no absolute run deadline.");
        var room = data.Plan.Rooms.SingleOrDefault(
            candidate => candidate.LocationId == binding.Participant.CurrentRoomIndex)
            ?? throw new InvalidDataException(
                $"Abyssal run {binding.Run.InstanceId} has no plan for room {binding.Participant.CurrentRoomIndex}.");
        var environment = abyssalCatalog.GetEnvironmentRoom(room.EnvironmentProfileId);
        var (dungeonId, roomId) = AbyssalDungeonProjection.RoomIds(room.LocationId, environment);
        var position = await space.PositionOfAsync(session, shipId.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Abyssal ship {shipId.Value} has no simulation position after ClientIsReady.");

        if (!abyssalNotifications.SendEnteringDungeonRoom(
                characterId, dungeonId, roomId, [position.X, position.Y, position.Z],
                data.ContentId, forceResend: true) ||
            !abyssalNotifications.SendLocationEntered(
                new AbyssalLocationEnteredEvent(
                    binding.Run.InstanceId,
                    data.ContentId,
                    data.Tier,
                    data.SpaceLocationId,
                    characterId,
                    room.LocationId,
                    room.NebulaGraphicId ?? throw new InvalidDataException("The current Abyssal room has no nebula graphic."),
                    room.EnvironmentProfileId),
                data.Tier,
                forceResend: true) ||
            !abyssalNotifications.SendDeadlineAdded(
                new AbyssalDeadlineAddedEvent(binding.Run.InstanceId, data.ContentId, characterId, deadline),
                forceResend: true))
        {
            throw new InvalidOperationException($"The Abyssal client-ready notifications could not be delivered to character {characterId}.");
        }
        return true;
    }


    private async ValueTask<PyValue> HandleAbyssalRpcAsync(
        Eve.Services.ServiceCall call,
        CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"eve-host: abyssal.rpc call={PositiveAbyssalRpcId(call.CallId)} session={call.Session.SessionId} character={call.Session.GetInt("charid") ?? call.Session.GetInt("characterID") ?? 0} ship={spaceSessions?.ShipIdOf(call.Session) ?? 0} method={call.Method}");
        return call.Method switch
        {
            "ClientIsReady" => await HandleAbyssalClientIsReadyRpcAsync(call.Session, cancellationToken).ConfigureAwait(false),
            "AbyssalEntranceDeployment" => await DeployAbyssalTraceAsync(call, cancellationToken).ConfigureAwait(false),
            "AbyssalEntranceGateActivation" => await EnterAbyssalRunAsync(call, cancellationToken).ConfigureAwait(false),
            "AbyssalGateActivation" => await ActivateAbyssalGateAsync(call, cancellationToken).ConfigureAwait(false),
            "AbyssalEndGateActivation" => await ActivateAbyssalEndGateAsync(call, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"The Host received unregistered Abyssal RPC '{call.Method}'."),
        };
    }


    private async ValueTask<PyValue> DeployAbyssalTraceAsync(
        Eve.Services.ServiceCall call,
        CancellationToken cancellationToken)
    {
        var characterId = call.Session.GetInt("charid") ?? call.Session.GetInt("characterID") ?? 0;
        var shipId = spaceSessions?.ShipIdOf(call.Session);
        var filamentItemId = PositiveAbyssalRpcId(call.Arg(0));
        var mode = PositiveAbyssalRpcId(call.Arg(1)) switch
        {
            (long)AbyssalGameMode.SoloCruiser => AbyssalGameMode.SoloCruiser,
            (long)AbyssalGameMode.ThreeFrigates => AbyssalGameMode.ThreeFrigates,
            (long)AbyssalGameMode.TwoDestroyers => AbyssalGameMode.TwoDestroyers,
            _ => throw AbyssalErrorAdapter.FromRule(
                new AbyssalRuleException(AbyssalRuleCode.InvalidGameMode, "the game_mode_id is not a supported PvE Abyss mode"),
                isGateActivation: false),
        };
        if (characterId <= 0 || shipId is not > 0 || filamentItemId <= 0)
            throw AbyssalErrorAdapter.ClientError(7);

        var fleetMember = await fleetStore.MemberForCharacterAsync(characterId, cancellationToken).ConfigureAwait(false);
        if (await abyssalRunAdmission.FindCommittedAsync(characterId, filamentItemId, mode, fleetMember?.FleetId, cancellationToken)
            .ConfigureAwait(false) is { } committed)
        {
            if (committed.Run.Data.RunStartedAtMs is null)
            {
                var retryPublication = await PublishAbyssalDeploymentAsync(
                    new AbyssalTracePublishRequestedEvent(committed.Trace, committed.Consumption),
                    checked((ulong)call.Session.SessionId), cancellationToken).ConfigureAwait(false);
                if (!retryPublication.Succeeded) throw AbyssalErrorAdapter.ClientError(22);
            }
            if (mode == AbyssalGameMode.SoloCruiser)
                await EnterAbyssalRunAsync(new ServiceCall
                {
                    Method = "AbyssalEntranceGateActivation", Session = call.Session,
                    Args = [new PyLong(committed.Trace.ItemId)], Cancellation = cancellationToken,
                }, cancellationToken).ConfigureAwait(false);
            foundations.Events.Wake();
            return PyNone.Instance;
        }
        var character = await characters.GetAsync(characterId, cancellationToken).ConfigureAwait(false)
            ?? throw AbyssalErrorAdapter.ClientError(7);
        if (character.SolarSystemId <= 0 || call.Session.GetInt("stationid") is > 0 ||
            call.Session.GetInt("structureid") is > 0)
        {
            Console.Error.WriteLine($"eve-host: abyssal.deployment character_refused character={characterId} ship={character.ShipId} system={character.SolarSystemId} station={character.StationId} session_system={call.Session.GetInt("solarsystemid") ?? 0} session_system2={call.Session.GetInt("solarsystemid2") ?? 0}");
            throw AbyssalErrorAdapter.ClientError(22);
        }
        ValidateAbyssalActivationSystem(character.SolarSystemId);
        await ValidateAbyssalFilamentSecurityAsync(
            filamentItemId, character.SolarSystemId, cancellationToken).ConfigureAwait(false);

        var space = spaceSessions
            ?? throw new InvalidOperationException("Abyssal trace deployment requires initialized SpaceSessions.");
        var pose = await space.GetAbyssalDeploymentPoseAsync(
            shipId.Value,
            SimSceneKey.SolarSystem(character.SolarSystemId),
            cancellationToken).ConfigureAwait(false);
        if (!pose.Found || pose.ShipId != shipId.Value ||
            pose.Scene != SimSceneKey.SolarSystem(character.SolarSystemId))
        {
            Console.Error.WriteLine($"eve-host: abyssal.deployment pose_refused character={characterId} ship={shipId.Value} filament={filamentItemId} system={character.SolarSystemId} scene={pose.Scene} error={pose.Error ?? "identity_mismatch"}");
            throw AbyssalErrorAdapter.ClientError(22);
        }

        var request = new AbyssalRunAdmissionRequest(
            characterId,
            shipId.Value,
            filamentItemId,
            mode,
            fleetMember?.FleetId,
            character.SolarSystemId,
            new AbyssalPosition(pose.Position[0], pose.Position[1], pose.Position[2]),
            new AbyssalPosition(pose.Direction[0], pose.Direction[1], pose.Direction[2]));

        AbyssalRunAdmissionResult deployment;
        try
        {
            deployment = await abyssalRunAdmission.DeployAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AbyssalRuleException error)
        {
            throw AbyssalErrorAdapter.FromRule(error, isGateActivation: false);
        }

        if (call.Session.SessionId <= 0)
            throw new InvalidDataException("The Abyssal deployment session id must be positive.");
        var publication = await PublishAbyssalDeploymentAsync(
            new AbyssalTracePublishRequestedEvent(deployment.Trace, deployment.Consumption),
            checked((ulong)call.Session.SessionId), cancellationToken).ConfigureAwait(false);
        foundations.Events.Wake();
        if (!publication.Succeeded)
            throw AbyssalErrorAdapter.ClientError(22);
        if (mode == AbyssalGameMode.SoloCruiser)
            await EnterAbyssalRunAsync(new ServiceCall
            {
                Method = "AbyssalEntranceGateActivation",
                Session = call.Session,
                Args = [new PyLong(deployment.Trace.ItemId)],
                Cancellation = cancellationToken,
            }, cancellationToken).ConfigureAwait(false);

        return PyNone.Instance;
    }

    private async ValueTask<PyValue> HandleAbyssalClientIsReadyRpcAsync(
        ClientSession session,
        CancellationToken cancellationToken)
    {
        await HandleAbyssalClientIsReadyAsync(session, cancellationToken).ConfigureAwait(false);
        return PyNone.Instance;
    }


    private async ValueTask<PyValue> ActivateAbyssalGateAsync(
        Eve.Services.ServiceCall call,
        CancellationToken cancellationToken)
    {
        var binding = await FindAbyssalRpcParticipantAsync(call.Session, cancellationToken).ConfigureAwait(false);
        if (binding is null)
            throw AbyssalErrorAdapter.FromRule(
                new AbyssalRuleException(AbyssalRuleCode.ParticipantNotFound, "the character is not in an active Abyssal run"),
                isGateActivation: true);

        var gateMarkerId = PositiveAbyssalRpcId(call.Arg(0));
        await ValidateAbyssalGateRangeAsync(binding, gateMarkerId, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var result = await abyssalRuntime.ActivateRoomGateAsync(
                connection,
                transaction,
                new AbyssalRoomGateActivationRequest(
                    binding.Run.InstanceId,
                    binding.Participant.CurrentRoomIndex,
                    gateMarkerId,
                    binding.Participant.CharacterId),
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status == AbyssalOperationStatus.Applied)
                foundations.Events.Wake();
        }
        catch (AbyssalRuleException error)
        {
            throw AbyssalErrorAdapter.FromRule(error, isGateActivation: true);
        }
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> ActivateAbyssalEndGateAsync(
        Eve.Services.ServiceCall call,
        CancellationToken cancellationToken)
    {
        if (await IsCommittedAbyssalEndGateAsync(call.Session, PositiveAbyssalRpcId(call.Arg(0)), cancellationToken).ConfigureAwait(false))
        {
            foundations.Events.Wake();
            return PyNone.Instance;
        }
        var binding = await FindAbyssalRpcParticipantAsync(call.Session, cancellationToken).ConfigureAwait(false);
        if (binding is null)
            throw AbyssalErrorAdapter.FromRule(
                new AbyssalRuleException(AbyssalRuleCode.ParticipantNotFound, "the character is not in an active Abyssal run"),
                isGateActivation: true);

        var gateMarkerId = PositiveAbyssalRpcId(call.Arg(0));
        await ValidateAbyssalGateRangeAsync(binding, gateMarkerId, cancellationToken).ConfigureAwait(false);
        AbyssalOperationResult result;
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            result = await abyssalRuntime.EndGateActivationAsync(
                connection,
                transaction,
                new AbyssalEndGateActivationRequest(
                    binding.Run.InstanceId,
                    binding.Participant.CurrentRoomIndex,
                    gateMarkerId,
                    binding.Participant.CharacterId),
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AbyssalRuleException error)
        {
            throw AbyssalErrorAdapter.FromRule(error, isGateActivation: true);
        }

        foundations.Events.Wake();
        return PyNone.Instance;
    }

    private async Task<AbyssalParticipantRunBinding?> FindAbyssalRpcParticipantAsync(
        ClientSession session,
        CancellationToken cancellationToken)
    {
        var characterId = session.GetInt("charid") ?? session.GetInt("characterID") ?? 0;
        var shipId = spaceSessions?.ShipIdOf(session);
        if (characterId <= 0 || shipId is not > 0)
            return null;

        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var binding = await abyssalRuntime.FindActiveForParticipantAsync(
            connection, transaction, characterId, shipId.Value, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return binding;
    }


    private static long PositiveAbyssalRpcId(PyValue value)
    {
        var id = value switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => 0L,
        };
        return id > 0 ? id : 0;
    }

    private async Task MoveAbyssalRoomGroupAsync(
        Eve.Events.EventContext context,
        CancellationToken cancellationToken)
    {
        var transition = context.Event.Payload<AbyssalRoomTransitionRequestedEvent>()
            ?? throw new InvalidDataException("The Abyssal room-transition event payload is missing.");
        var space = spaceSessions
            ?? throw new InvalidOperationException("Abyssal room transitions require initialized SpaceSessions.");
        var leader = transition.Recipients.FirstOrDefault(recipient =>
            recipient.Status is AbyssalParticipantStatus.Active or AbyssalParticipantStatus.Disconnected)
            ?? throw new InvalidDataException($"Abyssal transition {transition.GateMarkerId} has no living participant.");
        var runBinding = await abyssalRuntime.FindActiveForParticipantAsync(
            context.Connection, context.Transaction, leader.CharacterId, leader.ShipId, cancellationToken).ConfigureAwait(false);
        if (runBinding is null) return;
        if (runBinding.Run.Data.ExitMode != AbyssalExitMode.None ||
            runBinding.Participant.Status is not (AbyssalParticipantStatus.Active or AbyssalParticipantStatus.Disconnected) ||
            runBinding.Participant.CurrentRoomIndex > transition.ToLocationId) return;
        if (runBinding.Run.InstanceId != transition.InstanceId ||
            runBinding.Run.Data.ContentId != transition.ContentId ||
            runBinding.Participant.CurrentRoomIndex != transition.ToLocationId)
        {
            throw new InvalidDataException($"Abyssal transition {transition.GateMarkerId} disagrees with its persisted run.");
        }
        await PrepareAndMaterializeAbyssalRoomAsync(
            context.Connection, context.Transaction, runBinding.Run, transition.ToLocationId, cancellationToken).ConfigureAwait(false);

        var source = new SimSceneKey(transition.SpaceLocationId, transition.InstanceId, transition.FromLocationId);
        var destination = new SimSceneKey(transition.SpaceLocationId, transition.InstanceId, transition.ToLocationId);
        var sourceRoom = abyssalCatalog.GetEnvironmentRoom(transition.FromEnvironmentProfileId);
        var destinationRoom = abyssalCatalog.GetEnvironmentRoom(transition.ToEnvironmentProfileId);
        var (sourceDungeonId, _) = AbyssalDungeonProjection.RoomIds(transition.FromLocationId, sourceRoom);
        var (destinationDungeonId, destinationRoomId) =
            AbyssalDungeonProjection.RoomIds(transition.ToLocationId, destinationRoom);

        foreach (var recipient in transition.Recipients)
        {
            if (recipient.Status is not (AbyssalParticipantStatus.Active or AbyssalParticipantStatus.Disconnected))
                continue;
            if (characterSessions.Of(recipient.CharacterId) is not { } session)
                continue;

            var position = await space.PositionOfAsync(session, recipient.ShipId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Abyssal ship {recipient.ShipId} has no source pose before gate transfer.");
            if (!abyssalNotifications.SendEnteringDungeonRoom(
                    recipient.CharacterId,
                    destinationDungeonId,
                    destinationRoomId,
                    [position.X, position.Y, position.Z],
                    transition.ContentId) ||
                !abyssalNotifications.SendExitingDungeon(recipient.CharacterId, sourceDungeonId, transition.ContentId, transition.FromLocationId))
            {
                throw new InvalidOperationException($"Abyssal gate {transition.GateMarkerId} client handoff could not be sent to character {recipient.CharacterId}.");
            }
        }

        foreach (var recipient in transition.Recipients)
        {
            if (recipient.Status is not (AbyssalParticipantStatus.Active or AbyssalParticipantStatus.Disconnected))
                continue;

            var destinationPose = await space.GetAbyssalParticipantPoseAsync(
                transition.ContentId, recipient.ShipId, destination, cancellationToken).ConfigureAwait(false);
            if (destinationPose.Found)
                continue;

            var error = await space.MoveAbyssalParticipantAsync(
                transition.ContentId, recipient.ShipId, source, destination, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                destinationPose = await space.GetAbyssalParticipantPoseAsync(
                    transition.ContentId, recipient.ShipId, destination, cancellationToken).ConfigureAwait(false);
                if (!destinationPose.Found)
                    throw new InvalidOperationException($"Abyssal gate {transition.GateMarkerId} could not move ship {recipient.ShipId}: {error}");
            }
        }
        await foundations.Events.PublishAsync(context.Connection, context.Transaction, AbyssalCleanupTopic,
            new AbyssalCleanupRequestedEvent(transition.InstanceId, transition.ContentId, transition.FromLocationId),
            $"abyssal:{transition.ContentId}:room:{transition.FromLocationId}:cleanup", cancellationToken).ConfigureAwait(false);
    }


    private async Task<AbyssalReconnectBinding?> FindAbyssalReconnectAsync(
        Eve.Services.ClientSession session,
        long characterId,
        long shipId,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var binding = await abyssalRuntime.FindActiveForParticipantAsync(
            connection,
            transaction,
            characterId,
            shipId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (binding is null || binding.Participant.Status is not (AbyssalParticipantStatus.Active or AbyssalParticipantStatus.Disconnected))
            return null;

        var scene = new SimSceneKey(
            binding.Run.Data.SpaceLocationId,
            binding.Run.InstanceId,
            binding.Participant.CurrentRoomIndex);
        return new AbyssalReconnectBinding(binding.Run.Data.ContentId, scene);
    }

    private async ValueTask PersistAbyssalDisconnectAsync(
        AbyssalParticipantDisconnectedNotice notice,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await abyssalRuntime.DisconnectAsync(
            connection,
            transaction,
            notice.InstanceId,
            notice.CharacterId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PersistAbyssalNpcDestructionAsync(
        AbyssalNpcDestroyedNotice notice,
        CancellationToken cancellationToken)
    {
        AbyssalOperationResult result;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            result = await abyssalRuntime.NpcDestroyedAsync(
                connection,
                transaction,
                new AbyssalNpcDestroyedRequest(notice.InstanceId, notice.LocationId, notice.EntityId),
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        var room = result.State.Data.Rooms.Single(value => value.LocationId == notice.LocationId);
        if (room.Cleared)
            await SetAbyssalGateOpenAsync(result.State, notice.LocationId, isOpen: true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PersistAbyssalDeathRequestAsync(
    AbyssalParticipantDestroyedNotice notice,
    CancellationToken cancellationToken)
{
    await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    await abyssalRuntime.ParticipantDestroyedAsync(
        connection,
        transaction,
        new AbyssalParticipantDestroyedRequest(
            notice.InstanceId,
            notice.CharacterId,
            notice.ShipId,
            "ship-destroyed",
            notice.Destroyed.WreckKind?.TypeId),
        cancellationToken).ConfigureAwait(false);
    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    foundations.Events.Wake();
}

    private async Task ProcessAbyssalDeathSettlementAsync(
    Eve.Events.EventContext context,
    CancellationToken cancellationToken)
{
    var death = context.Event.Payload<AbyssalParticipantDeathSettlementRequestedEvent>()
        ?? throw new InvalidDataException("The Abyssal death settlement event payload is missing.");
    var instance = await foundations.Content.GetInTransactionAsync(context.Connection, context.Transaction,
        death.InstanceId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("The death settlement run is missing.");
    var data = instance.Data<AbyssalRunData>() ?? throw new InvalidDataException("The death settlement data is missing.");
    var participant = data.Participants.Single(value => value.CharacterId == death.RecipientCharacterId);
    if (data.ContentId != death.ContentId || participant.ShipId != death.ShipId ||
        participant.DeathSettlementKey != death.SettlementKey)
        throw new InvalidDataException("The death settlement does not match its persisted request.");
    if (participant.DeathSettlementReceiptAtMs is not null) return;
    var outcome = await abyssalDeathSettlement.SettleAsync(
        death,
        death.WreckTypeId,
        cancellationToken).ConfigureAwait(false);
    var space = spaceSessions
        ?? throw new InvalidOperationException("Abyssal death settlement requires initialized SpaceSessions.");

    AbyssalParticipantSettlementResult spaceSettlement;
    if (outcome.AuthorityAlreadyCompleted)
    {
        if (outcome.PodDeath is not null)
            throw new InvalidDataException("An authority-completed Abyssal retry cannot claim a new PodDeath.");

        var character = await characters.GetAsync(outcome.CharacterId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Settled Abyssal character {outcome.CharacterId} is missing.");
        var activeShip = await items.GetAsync(outcome.ReplacementShipId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Settled Abyssal replacement ship {outcome.ReplacementShipId} is missing.");
        var rookieShipTypeId = catalog.Race(character.RaceId)?.ShipTypeId
            ?? throw new InvalidDataException($"The rookie ship for race {character.RaceId} is absent from the character catalog.");
        var station = stations.Get(outcome.StationId)
            ?? throw new InvalidDataException($"Settled Abyssal clone station {outcome.StationId} is absent from the SDE.");
        var destroyedShip = await items.GetAsync(death.ShipId, cancellationToken).ConfigureAwait(false);
        if (character.Id != death.RecipientCharacterId ||
            character.ShipId != activeShip.Id ||
            character.StationId != outcome.StationId ||
            character.SolarSystemId != outcome.SolarSystemId ||
            character.ConstellationId != outcome.ConstellationId ||
            character.RegionId != outcome.RegionId ||
            activeShip.OwnerId != character.Id ||
            activeShip.TypeId != rookieShipTypeId ||
            activeShip.LocationId != outcome.StationId ||
            activeShip.Flag != ItemFlags.Hangar ||
            !activeShip.Singleton ||
            activeShip.Quantity != 1 ||
            destroyedShip is not null ||
            station.SolarSystemId != outcome.SolarSystemId ||
            station.ConstellationId != outcome.ConstellationId ||
            station.RegionId != outcome.RegionId)
        {
            throw new InvalidDataException(
                $"The persisted clone state for Abyssal death settlement {death.SettlementKey} does not match its authority result.");
        }

        spaceSettlement = await space.ResumeAbyssalParticipantSettlementAsync(
            death,
            character,
            activeShip,
            shipDeath: outcome.ShipDeath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    else
    {
        var capsuleDeath = outcome.PodDeath
            ?? throw new InvalidDataException("A first-pass Abyssal death settlement has no normal PodDeath result.");
        spaceSettlement = await space.CompleteAbyssalParticipantSettlementAsync(
            death,
            capsuleDeath,
            outcome.ShipDeath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    if (!spaceSettlement.Succeeded)
        throw new InvalidOperationException(spaceSettlement.Error ?? "Abyssal space death settlement failed.");

    await abyssalRuntime.RecordParticipantDestructionSettlementAsync(
        context.Connection,
        context.Transaction,
        new AbyssalParticipantDestructionSettlementRequest(
            death.InstanceId,
            death.RecipientCharacterId,
            death.ShipId,
            death.SettlementKey),
        cancellationToken).ConfigureAwait(false);
}

}
