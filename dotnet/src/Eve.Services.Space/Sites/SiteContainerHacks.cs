using Eve.Events;
using Eve.Inventory;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>
/// The site containers an analyzer hacks: the target lookup, and the end of an attempt. A win clears the container and announces it
/// (<c>site.container.cleared</c>, the objective of the site's data or relic content). A lost attempt counts toward two failures, which
/// destroy the container (wiki: Data or Relic Site container, destroyed after two failures). The counts are in PostgreSQL, so a restart keeps them.
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

    /// <summary>
    /// An attempt ended. A win puts the loot in the container's cargo (owned by the character who won it), clears the container and announces it
    /// (EveJS maybeCompleteMaterializedDataRelicSite: the loot is the container's contents).
    /// </summary>
    public async ValueTask EndedAsync(HackableTarget target, bool won, long characterId, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(target.ItemId, cancellationToken) is not { } record)
        {
            return;
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (won)
        {
            var profile = SiteLoot.ProfileOf(record.Relic);
            var typeIds = await SiteLoot.TypeIdsAsync(connection, transaction, SiteLoot.TagsOf(profile).SelectMany(tag => tag.Candidates), cancellationToken);
            foreach (var (typeId, quantity) in SiteLoot.Roll(profile, record.ContainerId, typeIds))
            {
                await items.CreateAsync(connection, transaction, new NewItem(typeId, characterId, record.ContainerId, ItemFlags.Hangar, quantity), cancellationToken);
            }
            await store.SetStateAsync(connection, transaction, record.ContainerId, "cleared", cancellationToken);
            await events.PublishAsync(connection, transaction, SiteContainerTopics.Cleared,
                new { solarSystemId = record.SolarSystemId, site = record.SiteId, container = record.ContainerId }, key: $"system:{record.SolarSystemId}", cancellationToken: cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        var failures = await store.RecordFailureAsync(connection, transaction, record.ContainerId, cancellationToken);
        var destroyed = failures is >= DestroyAfterFailures;
        if (destroyed)
        {
            await store.SetStateAsync(connection, transaction, record.ContainerId, "destroyed", cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        if (destroyed)
        {
            removeFromScene(record.ContainerId);
        }
    }
}

public static class SiteContainerTopics
{
    /// <summary>The topic the cosmic site director counts (<see cref="Eve.Services.Space.Sites.CosmicSiteDirector.ContainerClearedTopic"/>).</summary>
    public const string Cleared = Eve.Services.Space.Sites.CosmicSiteDirector.ContainerClearedTopic;
}
