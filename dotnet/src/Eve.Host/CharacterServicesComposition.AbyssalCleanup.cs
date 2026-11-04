using System.Text.Json;
using Eve.Content;
using Eve.Events;
using Eve.Inventory;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Host;

public sealed record AbyssalCleanupRequestedEvent(long InstanceId, string? ContentId = null, int? RoomOrdinal = null);
public sealed record AbyssalCleanupCompletedEvent(long InstanceId, string ContentId, int? RoomOrdinal,
    bool Terminal, IReadOnlyList<long> OwnedItemIds)
{
    public IReadOnlyList<int> ReleasedRoomOrdinals { get; init; } = [];
}

internal sealed partial class CharacterServicesComposition
{
    internal const string AbyssalCleanupTopic = "abyssal.host.cleanup_requested";
    private const string AbyssalCleanupCompletedTopic = "abyssal.host.cleanup_completed";

    private static bool AbyssalParticipantSettled(AbyssalParticipantData participant) =>
        participant.Status == AbyssalParticipantStatus.Returned && participant.ReturnedAtMs is not null ||
        participant.Status == AbyssalParticipantStatus.Destroyed && participant.DeathSettlementReceiptAtMs is not null;

    private void RegisterAbyssalCleanup()
    {
        foundations.Events.Subscribe("abyssal.host.cleanup",
            ["content.abyssal.completed", "content.abyssal.failed", "content.abyssal.expired", "content.abyssal.abandoned",
             AbyssalContentRuntime.ParticipantContentFinishedTopic, AbyssalCleanupTopic],
            CleanupAbyssalAsync, StartAt.Beginning);
        foundations.Events.Subscribe("abyssal.host.cleanup-completed", [AbyssalCleanupCompletedTopic],
            LogAbyssalCleanupCompletedAsync, StartAt.Beginning);
    }

    private async Task LogAbyssalCleanupCompletedAsync(EventContext context, CancellationToken ct)
    {
        var receipt = context.Event.Payload<AbyssalCleanupCompletedEvent>() ?? throw new InvalidDataException("Missing cleanup completion receipt.");
        var instance = await foundations.Content.GetInTransactionAsync(context.Connection, context.Transaction, receipt.InstanceId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Missing cleanup completion run.");
        var data = instance.Data<AbyssalRunData>() ?? throw new InvalidDataException("Missing cleanup completion data.");
        if (instance.Kind != AbyssalContentRuntime.Kind || data.ContentId != receipt.ContentId ||
            receipt.Terminal && (instance.State == "active" || !data.Participants.All(AbyssalParticipantSettled)))
            throw new InvalidDataException("The cleanup completion receipt is not settled.");
        foreach (var id in receipt.OwnedItemIds)
            if (await items.LockAndGetAsync(context.Connection, context.Transaction, id, ct).ConfigureAwait(false) is not null)
                throw new InvalidDataException("A committed cleanup item still exists.");
        if (!receipt.Terminal && (receipt.ReleasedRoomOrdinals.Count == 0 ||
            receipt.ReleasedRoomOrdinals.Any(ordinal => ordinal is < 1 or > 3 || ordinal >= data.CurrentRoomIndex)))
            throw new InvalidDataException("The partial cleanup receipt does not identify departed rooms.");
        foreach (var room in data.Rooms.Where(room => receipt.Terminal || receipt.ReleasedRoomOrdinals.Contains(room.LocationId)))
            foreach (var scope in AbyssalItemScopes(data, instance.Id, room.LocationId))
                if ((await items.FindRunSpaceItemsAsync(context.Connection, context.Transaction, scope, ct).ConfigureAwait(false))
                    .Any(item => item.LocationId == data.SpaceLocationId))
                    throw new InvalidDataException("A committed cleanup still has scoped private inventory.");
        Console.Error.WriteLine($"eve-host: abyssal.cleanup committed content={data.ContentId} instance={instance.Id} location={data.SpaceLocationId} room={(receipt.Terminal ? "all" : string.Join(",", receipt.ReleasedRoomOrdinals))} terminal={receipt.Terminal} participant_receipts={data.Participants.Count(AbyssalParticipantSettled)} validated_owned_ids_remaining=0 validated_ids={receipt.OwnedItemIds.Count}");
    }

    private async Task CleanupAbyssalAsync(EventContext context, CancellationToken ct)
    {
        var request = context.Event.Payload<AbyssalCleanupRequestedEvent>()
            ?? throw new InvalidDataException("Missing Abyssal cleanup identity.");
        var instance = await foundations.Content.GetForUpdateAsync(context.Connection, context.Transaction, request.InstanceId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Abyssal cleanup run is missing.");
        var data = instance.Data<AbyssalRunData>() ?? throw new InvalidDataException("The Abyssal cleanup plan is missing.");
        if (instance.Kind != AbyssalContentRuntime.Kind || request.ContentId is { } contentId && contentId != data.ContentId)
            throw new InvalidDataException("The cleanup request belongs to another run.");
        var terminal = instance.State is "completed" or "failed" or "expired" or "abandoned";
        var settled = data.Participants.All(AbyssalParticipantSettled);
        // Generic terminal announcements precede actual destruction settlement. The last participant receipt wakes cleanup again.
        if (terminal && !settled) return;
        if (request.RoomOrdinal is { } requested && (requested is < 1 or > 3 || requested >= data.CurrentRoomIndex && !terminal))
            throw new InvalidDataException("Only a departed room can be released before the run ends.");

        var rooms = terminal ? data.Rooms : data.Rooms.Where(room =>
            room.LocationId < data.CurrentRoomIndex &&
            !data.Participants.Any(participant => participant.CurrentRoomIndex == room.LocationId && !AbyssalParticipantSettled(participant)));
        if (!terminal && request.RoomOrdinal is { } ordinal) rooms = rooms.Where(room => room.LocationId == ordinal);
        var selectedRooms = rooms.ToArray();
        var ownedIds = new HashSet<long>();
        foreach (var room in selectedRooms)
        {
            ownedIds.UnionWith(room.Entities.Select(entity => entity.EntityId));
            ownedIds.UnionWith(await CleanupAbyssalRoomAsync(context.Connection, context.Transaction, instance.Id, data, room, ct).ConfigureAwait(false));
        }
        if (!terminal)
        {
            if (ownedIds.Count > 0)
                await foundations.Events.PublishAsync(context.Connection, context.Transaction, AbyssalCleanupCompletedTopic,
                    new AbyssalCleanupCompletedEvent(instance.Id, data.ContentId, request.RoomOrdinal, false, ownedIds.ToArray())
                    { ReleasedRoomOrdinals = selectedRooms.Select(room => room.LocationId).ToArray() },
                    $"abyssal:{data.ContentId}:cleanup-committed", ct).ConfigureAwait(false);
            return;
        }

        var trace = await items.LockAndGetAsync(context.Connection, context.Transaction, data.TraceItemId, ct).ConfigureAwait(false);
        if (trace is not null)
        {
            ValidatePersistedTraceItem(trace, data);
            if ((await items.LockContainerAsync(context.Connection, context.Transaction, trace.Id, ct).ConfigureAwait(false)).Count != 0)
                throw new InvalidDataException("An Abyssal trace contains items with no cleanup authority.");
        }
        var removed = await (spaceSessions ?? throw new InvalidOperationException("Cleanup requires SpaceSessions."))
            .RemoveAbyssalTraceAsync(checked((int)data.SourceSolarSystemId), data.TraceItemId, ct).ConfigureAwait(false);
        Console.Error.WriteLine($"eve-host: abyssal.cleanup trace_ack content={data.ContentId} instance={instance.Id} source={data.SourceSolarSystemId} trace={data.TraceItemId} result={(removed.Succeeded ? removed.AlreadyAbsent ? "already_absent" : "removed" : removed.Error)}");
        if (!removed.Succeeded) throw new InvalidOperationException(removed.Error ?? "Abyssal trace removal failed.");
        if (trace is not null)
            await items.DeleteAsync(context.Connection, context.Transaction, trace.Id, ct).ConfigureAwait(false);
        abyssalDogmaEffectsByContentId.TryRemove(data.ContentId, out _);
        ownedIds.Add(data.TraceItemId);
        await foundations.Events.PublishAsync(context.Connection, context.Transaction, AbyssalCleanupCompletedTopic,
            new AbyssalCleanupCompletedEvent(instance.Id, data.ContentId, null, true, ownedIds.ToArray())
            { ReleasedRoomOrdinals = selectedRooms.Select(room => room.LocationId).ToArray() },
            $"abyssal:{data.ContentId}:cleanup-committed", ct).ConfigureAwait(false);
    }

    private static IEnumerable<RunSpaceItemScope> AbyssalItemScopes(AbyssalRunData data, long instanceId, int roomOrdinal)
    {
        var contentId = Guid.Parse(data.ContentId);
        foreach (var participant in data.Participants)
        {
            yield return new RunSpaceItemScope(contentId, instanceId, roomOrdinal, data.SpaceLocationId, participant.CharacterId);
            if (participant.DeathSettlementKey is { } key)
                yield return new RunSpaceItemScope(contentId, instanceId, roomOrdinal, data.SpaceLocationId, participant.CharacterId, key);
        }
    }

    private async Task<IReadOnlyList<long>> CleanupAbyssalRoomAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long instanceId, AbyssalRunData data, AbyssalRoomState room, CancellationToken ct)
    {
        var deletions = new HashSet<long>();
        var settledLootIds = new HashSet<long>();
        var containers = new HashSet<long>();
        var observedContents = new HashSet<long>();
        foreach (var entity in room.Entities.OrderBy(value => value.EntityId))
        {
            var item = await items.LockAndGetAsync(connection, transaction, entity.EntityId, ct).ConfigureAwait(false);
            if (item is null) continue;
            var owner = entity.Role switch
            {
                AbyssalEntityRole.Npc => 0,
                AbyssalEntityRole.Gate => data.CreatorCharacterId,
                AbyssalEntityRole.Cloud or AbyssalEntityRole.Container => AbyssalTraceFactory.TraceOwnerId,
                _ => throw new InvalidDataException("Unknown Abyssal reservation role."),
            };
            if (item.TypeId != entity.TypeId || item.OwnerId != owner || item.LocationId != data.SpaceLocationId ||
                item.Flag != CharacterShips.InSpaceFlag || !item.Singleton || item.Quantity != 1)
                throw new InvalidDataException($"Abyssal reservation {item.Id} changed authority; cleanup refused.");
            if ((await items.LockContainerAsync(connection, transaction, item.Id, ct).ConfigureAwait(false)).Count != 0)
                throw new InvalidDataException($"Abyssal source {item.Id} contains unowned cleanup contents.");
            if (entity.Role == AbyssalEntityRole.Container)
            {
                var receipt = JsonSerializer.Deserialize<AbyssalCacheReceipt>(item.CustomInfo ?? "", TracePoseJson)
                    ?? throw new InvalidDataException("The cache cleanup receipt is missing.");
                if (receipt.SchemaVersion != 2 || receipt.ContentId != data.ContentId || receipt.RoomOrdinal != room.LocationId)
                    throw new InvalidDataException("The cache cleanup receipt belongs to another source.");
                if (receipt.WreckItemId is { } recordedWreckId) settledLootIds.Add(recordedWreckId);
                if (receipt.WreckItemId is { } wreckId && await items.LockAndGetAsync(connection, transaction, wreckId, ct).ConfigureAwait(false) is { } wreck)
                {
                    var expected = item.TypeId switch { 47951 => 48120, 49689 => 49692, 49661 => 49664, 49662 => 49665,
                        _ => throw new InvalidDataException("Unknown Abyssal artifact wreck.") };
                    if (wreck.TypeId != expected || wreck.OwnerId != data.CreatorCharacterId || wreck.LocationId != data.SpaceLocationId ||
                        wreck.Flag != CharacterShips.InSpaceFlag || !wreck.Singleton || wreck.Quantity != 1)
                        throw new InvalidDataException("The Abyssal reward wreck changed authority.");
                    foreach (var child in await items.LockContainerAsync(connection, transaction, wreckId, ct).ConfigureAwait(false))
                        observedContents.Add(child.Id);
                    containers.Add(wreck.Id);
                }
            }
            deletions.Add(item.Id);
        }
        foreach (var scope in AbyssalItemScopes(data, instanceId, room.LocationId))
        foreach (var scopedItem in await items.FindRunSpaceItemsAsync(connection, transaction, scope, ct).ConfigureAwait(false))
        {
            if (scopedItem.LocationId != data.SpaceLocationId) continue; // Already claimed or transported out of this private room.
            var provenance = scopedItem.RunSpaceProvenance;
            if (provenance is null || provenance.Scope != scope || !Enum.IsDefined(provenance.Role) ||
                provenance.SourceCharacterId != scope.RecipientCharacterId || provenance.SourceShipId <= 0 || provenance.SourceItemId <= 0 ||
                scopedItem.OwnerId != scope.RecipientCharacterId || !scopedItem.Singleton || scopedItem.Quantity != 1 ||
                scopedItem.Flag != (provenance.Role == RunSpaceItemRole.Corpse ? 0 : CharacterShips.InSpaceFlag))
                throw new InvalidDataException("The shared space item no longer matches its exact creation authority.");
            foreach (var child in await items.LockContainerAsync(connection, transaction, scopedItem.Id, ct).ConfigureAwait(false))
                observedContents.Add(child.Id);
            containers.Add(scopedItem.Id);
            settledLootIds.Add(scopedItem.Id);
        }
        var error = await (spaceSessions ?? throw new InvalidOperationException("Cleanup requires SpaceSessions."))
            .RemoveAbyssalSceneAsync(new SimSceneKey(data.SpaceLocationId, instanceId, room.LocationId),
                room.Entities.Where(entity => entity.Role == AbyssalEntityRole.Npc).Select(entity => entity.EntityId).ToArray(),
                settledLootIds.ToArray(), ct).ConfigureAwait(false);
        Console.Error.WriteLine($"eve-host: abyssal.cleanup scene_ack content={data.ContentId} instance={instanceId} location={data.SpaceLocationId} room={room.LocationId} result={error ?? "success_or_already_absent"}");
        if (error is not null) throw new InvalidOperationException($"Abyssal room cleanup requires confirmed scene removal: {error}");
        foreach (var id in containers)
            await ships.RemoveContainerAsync(connection, transaction, id, withContents: true, cancellationToken: ct).ConfigureAwait(false);
        foreach (var id in deletions)
            await items.DeleteAsync(connection, transaction, id, ct).ConfigureAwait(false);
        return deletions.Concat(containers).Concat(observedContents).Distinct().ToArray();
    }
}
