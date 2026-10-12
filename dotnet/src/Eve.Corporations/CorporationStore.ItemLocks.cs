using Npgsql;

namespace Eve.Corporations;

/// <summary>A corporation item that a blueprint vote locked: the item row carries the lock, this is the corporation's list of them.</summary>
public sealed record ItemLockChange(long ItemId, long CorporationId, long LocationId, bool Locked);

/// <summary>A locked corporation item, as the client's <c>itemLocking</c> service lists it.</summary>
public sealed record LockedCorporationItem(long ItemId, int TypeId, long CorporationId, long LocationId);

/// <summary>
/// Corporation item locks. The V24.01 client sets none: a blueprint vote (lockdown) locks a corporation's blueprint for the
/// vote and an unlock vote releases it. Industry jobs also set the blueprint's lock flag, so the locks the client sees are kept
/// here, apart from the job locks, and written in the same transaction as the flag.
/// </summary>
public sealed partial class CorporationStore
{
    private static readonly AsyncLocal<List<ItemLockChange>?> PendingItemLockChanges = new();

    /// <summary>Raised once per lock change after its transaction has committed; never inside the transaction.</summary>
    public event Action<ItemLockChange>? ItemLockChanged;

    private async Task SetBlueprintLockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long corporationId,
        long itemId, long typeId, long locationId, bool locked, CancellationToken token)
    {
        if (_setBlueprintLock is null)
            throw new InvalidOperationException("CorporationStore requires setBlueprintLock for blueprint votes.");
        await _setBlueprintLock(connection, tx, corporationId, itemId, typeId, locationId, locked, token);
        if (locked)
        {
            await using var insert = Command(connection, tx,
                $"INSERT INTO {_schema}.item_locks(item_id,corporation_id,type_id,location_id,locked_filetime) VALUES($1,$2,$3,$4,$5)",
                itemId, corporationId, typeId, locationId, VoteNowFiletime);
            await insert.ExecuteNonQueryAsync(token);
        }
        else
        {
            await using var delete = Command(connection, tx,
                $"DELETE FROM {_schema}.item_locks WHERE item_id=$1 AND corporation_id=$2", itemId, corporationId);
            await delete.ExecuteNonQueryAsync(token);
        }
        var change = new ItemLockChange(itemId, corporationId, locationId, locked);
        if (PendingItemLockChanges.Value is { } pending) pending.Add(change);
        else ItemLockChanged?.Invoke(change);
    }

    private void NotifyItemLockChanges(IEnumerable<ItemLockChange> changes)
    {
        foreach (var change in changes)
            ItemLockChanged?.Invoke(change);
    }

    /// <summary>The corporation's locked items, at one location or at all of them (by item id).</summary>
    public async Task<IReadOnlyList<LockedCorporationItem>> LockedItemsAsync(long corporationId, long? locationId, CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection, null,
            $"SELECT item_id,type_id,corporation_id,location_id FROM {_schema}.item_locks WHERE corporation_id=$1 AND ($2::bigint IS NULL OR location_id=$2) ORDER BY item_id",
            corporationId, locationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var items = new List<LockedCorporationItem>();
        while (await reader.ReadAsync(token))
            items.Add(new LockedCorporationItem(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3)));
        return items;
    }

    /// <summary>The locations where the corporation has a locked item.</summary>
    public async Task<IReadOnlyList<long>> LockedItemLocationsAsync(long corporationId, CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection, null,
            $"SELECT DISTINCT location_id FROM {_schema}.item_locks WHERE corporation_id=$1 ORDER BY location_id", corporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var locations = new List<long>();
        while (await reader.ReadAsync(token))
            locations.Add(reader.GetInt64(0));
        return locations;
    }
}
