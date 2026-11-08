using Eve.Events;
using Eve.Inventory;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>
/// The site containers an analyzer hacks: the target lookup, and the end of an attempt. A win leaves the loot in the container (<c>looted</c>); the
/// container clears, and is announced (<c>site.container.cleared</c>, the objective of the site's data or relic content), when its last item is taken. A lost attempt counts toward two failures, which
/// </summary>
public sealed class SiteContainerHacks(NpgsqlDataSource source, SiteContainerStore store, PgItemStore items, EventBus events, Func<long> nowMs, Action<long> removeFromScene)
{
    public const int DestroyAfterFailures = 2;

    public async ValueTask<HackableTarget?> FindAsync(long containerId, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(containerId, cancellationToken) is not { State: "secure" } record || record.ExpiresAtMs <= nowMs())
        {
            return null;
        }
        return new HackableTarget(record.ContainerId, record.SolarSystemId, record.Difficulty, record.Relic);
    }

    public ValueTask<bool> TryReserveAsync(HackableTarget target, Guid reservationId, long characterId, long sessionId, CancellationToken cancellationToken) =>
        new(store.TryReserveHackAsync(target.ItemId, reservationId, characterId, sessionId, nowMs(), cancellationToken));

    public ValueTask ReleaseReservationAsync(Guid reservationId, CancellationToken cancellationToken) =>
        new(store.ReleaseHackReservationAsync(reservationId, nowMs(), cancellationToken));

    /// <summary>
    /// An attempt ended. A win puts the loot in the container's cargo (owned by the character who won it) and the container is <c>looted</c>; the
    /// maybeCompleteMaterializedDataRelicSite: the loot is the container's contents). A win with nothing to roll clears it at once.
    /// </summary>
    public async ValueTask<IReadOnlyList<HackingLootItem>> EndedAsync(
        HackableTarget target, Guid reservationId, bool won, long characterId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var attempt = await store.GetHackAttemptAsync(connection, transaction, reservationId, cancellationToken);
        if (attempt is null || attempt.ContainerId != target.ItemId || attempt.CharacterId != characterId)
            throw new InvalidOperationException($"hacking reservation {reservationId} does not own container {target.ItemId}");
        if (attempt.State is "won" or "lost")
        {
            await transaction.CommitAsync(cancellationToken);
            return attempt.Contents;
        }
        if (attempt.State != "active")
            throw new InvalidOperationException($"hacking reservation {reservationId} is {attempt.State}");
        if (await store.GetAsync(connection, transaction, target.ItemId, forUpdate: true, cancellationToken) is not { State: "secure" } record)
            throw new InvalidOperationException($"container {target.ItemId} is no longer secure");

        if (won)
        {
            var profile = SiteLoot.ProfileOf(record.Relic);
            var typeIds = await SiteLoot.TypeIdsAsync(connection, transaction, SiteLoot.TagsOf(profile).SelectMany(tag => tag.Candidates), cancellationToken);
            var loot = SiteLoot.Roll(profile, record.ContainerId, typeIds)
                .Select(item => new HackingLootItem(item.TypeId, item.Quantity)).ToList();
            foreach (var (typeId, quantity) in loot)
            {
                await items.CreateAsync(connection, transaction, new NewItem(typeId, characterId, record.ContainerId, ItemFlags.Hangar, quantity), cancellationToken);
            }
            if (loot.Count == 0)
            {
                await ClearAsync(connection, transaction, record, cancellationToken);
            }
            else
            {
                if (!await store.SetStateIfSecureAsync(connection, transaction, record.ContainerId, "looted", cancellationToken))
                    throw new InvalidOperationException($"container {record.ContainerId} could not be settled as looted");
            }
            if (!await store.CompleteHackAttemptAsync(connection, transaction, reservationId, "won", loot, nowMs(), cancellationToken))
                throw new InvalidOperationException($"hacking reservation {reservationId} was already settled");
            await transaction.CommitAsync(cancellationToken);
            return loot;
        }
        var failures = await store.RecordFailureAsync(connection, transaction, record.ContainerId, cancellationToken);
        if (failures is null)
            throw new InvalidOperationException($"container {record.ContainerId} could not record the hacking failure");
        var destroyed = failures is >= DestroyAfterFailures;
        if (destroyed)
        {
            if (!await store.SetStateIfSecureAsync(connection, transaction, record.ContainerId, "destroyed", cancellationToken))
                throw new InvalidOperationException($"container {record.ContainerId} could not be destroyed after hacking failures");
        }
        if (!await store.CompleteHackAttemptAsync(connection, transaction, reservationId, "lost", [], nowMs(), cancellationToken))
            throw new InvalidOperationException($"hacking reservation {reservationId} was already settled");
        await transaction.CommitAsync(cancellationToken);
        if (destroyed)
        {
            removeFromScene(record.ContainerId);
        }
        return [];
    }

    /// <summary>
    /// An item left a place. When the place is a looted container and nothing is left in it, the container clears in the same transaction and the
    /// site is told (<c>site.container.cleared</c>); the container's ball leaves the scene. Any other place is not this container's concern.
    /// </summary>
    public async ValueTask OnItemLeftAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long previousLocationId, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(previousLocationId, cancellationToken) is not { State: "looted" } record)
        {
            return;
        }
        if (await items.CountInLocationAsync(connection, transaction, previousLocationId, cancellationToken) > 0)
        {
            return;
        }
        await ClearAsync(connection, transaction, record, cancellationToken);
        removeFromScene(record.ContainerId);
    }

    private async Task ClearAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SiteContainerRecord record, CancellationToken cancellationToken)
    {
        await store.SetStateAsync(connection, transaction, record.ContainerId, "cleared", cancellationToken);
        await events.PublishAsync(connection, transaction, SiteContainerTopics.Cleared,
            new { solarSystemId = record.SolarSystemId, site = record.SiteId, container = record.ContainerId }, key: $"system:{record.SolarSystemId}", cancellationToken: cancellationToken);
    }
}

public static class SiteContainerTopics
{
    /// <summary>The topic the cosmic site director counts (<see cref="Eve.Services.Space.Sites.CosmicSiteDirector.ContainerClearedTopic"/>).</summary>
    public const string Cleared = Eve.Services.Space.Sites.CosmicSiteDirector.ContainerClearedTopic;
}
