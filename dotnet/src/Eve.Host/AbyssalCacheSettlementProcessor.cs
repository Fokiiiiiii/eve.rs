using System.Text.Json;
using Eve.Content;
using Eve.Events;
using Eve.Inventory;
using Eve.Sde;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Host;

public sealed record AbyssalCacheDestroyedEvent(
    long InstanceId, string ContentId, int RoomOrdinal, long CacheItemId, int TypeId,
    IReadOnlyList<double> Position, IReadOnlyList<double> Direction);

public sealed record AbyssalCacheWreckRequestedEvent(AbyssalCacheDestroyedEvent Destruction);
public sealed record AbyssalCacheReceipt(
    int SchemaVersion, string ContentId, int RoomOrdinal, long? DestroyedAtMs,
    bool Suppressed, long? WreckItemId, IReadOnlyList<long> LootItemIds);

/// <summary>The source item keeps the durable destruction receipt; a different inventory item owns the wreck and its rewards.</summary>
public sealed class AbyssalCacheSettlementProcessor(
    PgItemStore items, ItemTypeCatalog itemTypes, SdeReader sde, ContentRuntime content,
    EventBus events, AbyssalLootMaterializer loot, Func<long> now)
{
    public const string DestroyedTopic = "abyssal.host.cache_destroyed";
    public const string MaterializeTopic = "abyssal.host.cache_materialize_requested";
    public const string WreckTopic = "abyssal.host.cache_wreck_requested";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string InitialMetadata(string contentId, int roomOrdinal) =>
        JsonSerializer.Serialize(new AbyssalCacheReceipt(2, contentId, roomOrdinal, null, false, null, []), Json);

    private async Task<(ContentInstance Instance, AbyssalRunData Data, Item Source, AbyssalCacheReceipt Receipt)> LoadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AbyssalCacheDestroyedEvent request, CancellationToken ct)
    {
        var instance = await content.GetForUpdateAsync(connection, transaction, request.InstanceId, ct)
            ?? throw new InvalidDataException("The cache destruction run is missing.");
        var data = instance.Data<AbyssalRunData>() ?? throw new InvalidDataException("The cache destruction run data is missing.");
        var room = data.Rooms.SingleOrDefault(value => value.LocationId == request.RoomOrdinal);
        if (instance.Kind != AbyssalContentRuntime.Kind || data.ContentId != request.ContentId ||
            room is null || !room.Entities.Any(entity => entity.Role == AbyssalEntityRole.Container &&
                entity.EntityId == request.CacheItemId && entity.TypeId == request.TypeId))
            throw new InvalidDataException("The destroyed cache does not belong to this run and room.");
        var source = await items.LockAndGetAsync(connection, transaction, request.CacheItemId, ct)
            ?? throw new InvalidDataException("The cache destruction source item is missing.");
        if (source.TypeId != request.TypeId || source.LocationId != data.SpaceLocationId ||
            source.OwnerId != AbyssalTraceFactory.TraceOwnerId || source.Flag != CharacterShips.InSpaceFlag ||
            !source.Singleton || source.Quantity != 1 ||
            (await itemTypes.GetAsync(source.TypeId, ct))?.GroupId != 2009)
            throw new InvalidDataException("The cache source inventory identity is invalid.");
        var receipt = JsonSerializer.Deserialize<AbyssalCacheReceipt>(source.CustomInfo ?? "", Json)
            ?? throw new InvalidDataException("The cache source receipt is missing.");
        if (receipt.SchemaVersion != 2 || receipt.ContentId != data.ContentId || receipt.RoomOrdinal != request.RoomOrdinal)
            throw new InvalidDataException("The cache source receipt belongs to another run.");
        if (request.Position.Count != 3 || request.Direction.Count != 3 ||
            request.Position.Concat(request.Direction).Any(value => !double.IsFinite(value)) ||
            request.Direction.All(value => value == 0))
            throw new InvalidDataException("The destroyed cache pose is invalid.");
        return (instance, data, source, receipt);
    }

    private bool Suppressed(ContentInstance instance, AbyssalRunData data) =>
        instance.State != "active" || data.ExitMode != AbyssalExitMode.None ||
        data.RunExpiresAtMs is not { } deadline || deadline <= now();

    private async Task<bool> RemovedSourceIsSupersededAsync(EventContext context, AbyssalCacheDestroyedEvent request, CancellationToken ct)
    {
        var instance = await content.GetForUpdateAsync(context.Connection, context.Transaction, request.InstanceId, ct)
            ?? throw new InvalidDataException("The cache run is missing.");
        var data = instance.Data<AbyssalRunData>() ?? throw new InvalidDataException("The cache plan is missing.");
        if (instance.Kind != AbyssalContentRuntime.Kind || data.ContentId != request.ContentId ||
            !data.Rooms.Any(room => room.LocationId == request.RoomOrdinal &&
                room.Entities.Any(entity => entity.EntityId == request.CacheItemId && entity.TypeId == request.TypeId && entity.Role == AbyssalEntityRole.Container)))
            throw new InvalidDataException("The cache event does not match its saved source.");
        var retired = data.CurrentRoomIndex > request.RoomOrdinal ||
            instance.State != "active" && data.Participants.All(participant =>
                participant.Status == AbyssalParticipantStatus.Returned && participant.ReturnedAtMs is not null ||
                participant.Status == AbyssalParticipantStatus.Destroyed && participant.DeathSettlementReceiptAtMs is not null);
        return retired && await items.LockAndGetAsync(context.Connection, context.Transaction, request.CacheItemId, ct) is null;
    }

    public async Task RecordDestructionAsync(EventContext context, CancellationToken ct)
    {
        var request = context.Event.Payload<AbyssalCacheDestroyedEvent>()
            ?? throw new InvalidDataException("The cache destruction event is missing.");
        if (await RemovedSourceIsSupersededAsync(context, request, ct)) return;
        var state = await LoadAsync(context.Connection, context.Transaction, request, ct);
        if (state.Receipt.DestroyedAtMs is not null)
        {
            if (!state.Receipt.Suppressed && Suppressed(state.Instance, state.Data))
                await SuppressAsync(context.Connection, context.Transaction, state.Source, state.Receipt, ct);
            return;
        }
        var receipt = state.Receipt with
        {
            DestroyedAtMs = state.Receipt.DestroyedAtMs ?? now(),
            Suppressed = state.Receipt.Suppressed || Suppressed(state.Instance, state.Data),
        };
        await items.SetCustomInfoAsync(context.Connection, context.Transaction, state.Source.Id,
            JsonSerializer.Serialize(receipt, Json), ct);
        if (!receipt.Suppressed)
            await events.PublishAsync(context.Connection, context.Transaction, MaterializeTopic, request,
                $"abyssal:{request.ContentId}:cache:{request.CacheItemId}:materialize", ct);
    }

    public async Task MaterializeAsync(EventContext context, CancellationToken ct)
    {
        var request = context.Event.Payload<AbyssalCacheDestroyedEvent>()
            ?? throw new InvalidDataException("The cache materialization event is missing.");
        if (await RemovedSourceIsSupersededAsync(context, request, ct)) return;
        var state = await LoadAsync(context.Connection, context.Transaction, request, ct);
        var receipt = state.Receipt;
        if (receipt.DestroyedAtMs is null) throw new InvalidDataException("Rewards require a durable source destruction receipt.");
        if (receipt.Suppressed || Suppressed(state.Instance, state.Data))
        {
            await SuppressAsync(context.Connection, context.Transaction, state.Source, receipt, ct);
            return;
        }
        if (receipt.WreckItemId is null)
        {
            var wreckTypeId = request.TypeId switch
            {
                47951 => 48120,
                49689 => 49692,
                49661 => 49664,
                49662 => 49665,
                _ => throw new InvalidDataException("Unknown Abyssal main-cache wreck mapping."),
            };
            _ = await itemTypes.GetAsync(wreckTypeId, ct)
                ?? throw new InvalidDataException($"SDE cache wreck type {wreckTypeId} is missing.");
            var wreckId = await items.CreateAsync(context.Connection, context.Transaction,
                new NewItem(wreckTypeId, state.Data.CreatorCharacterId, state.Data.SpaceLocationId,
                    CharacterShips.InSpaceFlag, 1, Singleton: true), ct);
            var plan = state.Data.Plan.Rooms.Single(room => room.LocationId == request.RoomOrdinal);
            var entity = state.Data.Rooms.Single(room => room.LocationId == request.RoomOrdinal).Entities
                .Single(value => value.EntityId == request.CacheItemId);
            var allocation = entity.PlanKey is { } key
                ? plan.Artifacts.Single(artifact => artifact.Key == key && artifact.TypeId == request.TypeId).Loot
                : plan.Artifacts.Count == 0 ? plan.Loot
                : throw new InvalidDataException("An artifact cache requires its persisted allocation key.");
            var ids = await loot.MaterializeAsync(context.Connection, context.Transaction,
                state.Data.CreatorCharacterId, wreckId, allocation, ct);
            receipt = receipt with { WreckItemId = wreckId, LootItemIds = ids };
            await items.SetCustomInfoAsync(context.Connection, context.Transaction, state.Source.Id,
                JsonSerializer.Serialize(receipt, Json), ct);
        }
        await events.PublishAsync(context.Connection, context.Transaction, WreckTopic,
            new AbyssalCacheWreckRequestedEvent(request),
            $"abyssal:{request.ContentId}:cache:{request.CacheItemId}:wreck", ct);
    }

    private async Task SuppressAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Item source, AbyssalCacheReceipt receipt, CancellationToken ct)
    {
        if (receipt.WreckItemId is { } wreckId)
        {
            foreach (var id in receipt.LootItemIds)
                if (await items.LockAndGetAsync(connection, transaction, id, ct) is { } item && item.LocationId == wreckId)
                    await items.DeleteAsync(connection, transaction, id, ct);
            await items.DeleteAsync(connection, transaction, wreckId, ct);
        }
        await items.SetCustomInfoAsync(connection, transaction, source.Id,
            JsonSerializer.Serialize(receipt with { Suppressed = true }, Json), ct);
    }

    public async Task<SimPlaceWreck?> PreparePublicationAsync(EventContext context, CancellationToken ct)
    {
        var request = context.Event.Payload<AbyssalCacheWreckRequestedEvent>()?.Destruction
            ?? throw new InvalidDataException("The cache wreck event is missing.");
        if (await RemovedSourceIsSupersededAsync(context, request, ct)) return null;
        var state = await LoadAsync(context.Connection, context.Transaction, request, ct);
        if (state.Receipt.Suppressed || Suppressed(state.Instance, state.Data))
        {
            await SuppressAsync(context.Connection, context.Transaction, state.Source, state.Receipt, ct);
            return null;
        }
        var wreckId = state.Receipt.WreckItemId ?? throw new InvalidDataException("The cache wreck has not been materialized.");
        var wreck = await items.LockAndGetAsync(context.Connection, context.Transaction, wreckId, ct)
            ?? throw new InvalidDataException("The cache wreck inventory item is missing.");
        var type = await itemTypes.GetAsync(wreck.TypeId, ct) ?? throw new InvalidDataException("The cache wreck SDE type is missing.");
        var row = (await sde.ReadAsync("types", [wreck.TypeId], ct)).Single();
        var name = SdeReader.English(row.Doc.GetProperty("name"));
        var graphic = row.Doc.GetProperty("graphicID").GetInt32();
        var radius = row.Doc.GetProperty("radius").GetDouble();
        if (string.IsNullOrWhiteSpace(name) || !double.IsFinite(radius) || radius < 0)
            throw new InvalidDataException("The cache wreck has invalid SDE scene metadata.");
        var hasLoot = false;
        foreach (var id in state.Receipt.LootItemIds)
            if (await items.LockAndGetAsync(context.Connection, context.Transaction, id, ct) is { } item && item.LocationId == wreckId)
                hasLoot = true;
        return new SimPlaceWreck(new SimSceneKey(state.Data.SpaceLocationId, request.InstanceId, request.RoomOrdinal),
            wreck.Id, type.GroupId, new SimWreckKind(type.TypeId, name, graphic),
            request.Position, request.Direction, radius, request.CacheItemId,
            new SimCredit(state.Data.CreatorCharacterId, 0, 0, 0), !hasLoot);
    }
}
