using Eve.Inventory;
using Npgsql;

namespace Eve.Ships;

public sealed partial class CharacterShips
{
    /// <summary>
    /// Takes a marked space artifact into the active ship's cargo, reassigns every contained item to the pilot, and forgets its space row in one transaction.
    /// The caller verifies the artifact marker, scene visibility, range and loot rights before entering this authority.
    /// </summary>
    public async Task<ShipOutcome> ScoopSpaceArtifactAsync(
        long characterId,
        long corporationId,
        long expectedShipId,
        int solarSystemId,
        long itemId,
        string expectedMarker,
        PgSpaceItemStore spaceItems,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || corporationId <= 0 || expectedShipId <= 0 || solarSystemId <= 0 || itemId <= 0 || string.IsNullOrEmpty(expectedMarker))
        {
            return new NothingHappened();
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot ||
            snapshot.Ship.Id != expectedShipId || snapshot.Ship.Flag != InSpaceFlag || snapshot.Ship.LocationId != solarSystemId)
        {
            return new NothingHappened();
        }

        var item = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
        if (item is null || item.LocationId != solarSystemId || item.Flag != InSpaceFlag ||
            !item.Singleton || item.Quantity != 1 || !StringComparer.Ordinal.Equals(item.CustomInfo, expectedMarker))
        {
            return new NothingHappened();
        }

        var spaceItem = await spaceItems.GetAsync(connection, transaction, itemId, cancellationToken);
        if (spaceItem is null || spaceItem.ItemId != item.Id || spaceItem.SolarSystemId != solarSystemId)
        {
            return new NothingHappened();
        }

        await statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        var type = statics.Type(item.TypeId)
            ?? throw new InvalidOperationException($"SDE type {item.TypeId} for space artifact {itemId} is missing.");
        if (!CanAddToShipHold(CargoFlag, type, item.Singleton, 1))
        {
            return new ShipRefused(ShipRefusal.NotAllowedInHold, ModuleTypeId: item.TypeId, ShipTypeId: snapshot.Ship.TypeId);
        }
        if (spaceItem.GroupId != type.GroupId || spaceItem.LauncherId <= 0 || spaceItem.CorporationId != corporationId ||
            item.OwnerId != characterId && item.OwnerId != corporationId)
        {
            throw new InvalidOperationException($"Space artifact {itemId} disagrees with its SDE identity or custody record.");
        }

        if (await RefuseWhatDoesNotFitAsync(snapshot, item, 1, CargoFlag, cancellationToken) is { } full)
        {
            return full;
        }

        var contents = await LockScoopContentsAsync(connection, transaction, itemId, cancellationToken);
        var movedContents = new List<ItemMoved>(contents.Count);
        foreach (var child in contents)
        {
            if (child.OwnerId == characterId)
            {
                continue;
            }

            await items.MoveToOwnerAsync(connection, transaction, child.Id, characterId, child.LocationId, child.Flag, cancellationToken);
            movedContents.Add(new ItemMoved(child with { OwnerId = characterId }, child.LocationId, child.Flag));
        }

        await items.MoveToOwnerAsync(connection, transaction, item.Id, characterId, snapshot.Ship.Id, CargoFlag, cancellationToken);
        if (!await spaceItems.RemoveAsync(connection, transaction, item.Id, cancellationToken))
        {
            return new NothingHappened();
        }

        await transaction.CommitAsync(cancellationToken);

        var movedItem = item with { OwnerId = characterId, LocationId = snapshot.Ship.Id, Flag = CargoFlag };
        var stacks = new[] { new ItemChanged(movedItem, item.LocationId, item.Flag, item.Quantity) };
        return new ShipChanged(item.Id, snapshot.Ship.Id, movedContents, [], stacks);
    }

    private async Task<IReadOnlyList<Item>> LockScoopContentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long rootItemId,
        CancellationToken cancellationToken)
    {
        var contents = new Dictionary<long, Item>();
        var pendingContainers = new Queue<long>();
        pendingContainers.Enqueue(rootItemId);

        while (pendingContainers.TryDequeue(out var containerId))
        {
            foreach (var child in await items.LockContainerAsync(connection, transaction, containerId, cancellationToken))
            {
                if (!contents.TryAdd(child.Id, child))
                {
                    throw new InvalidOperationException($"Space artifact {rootItemId} contains a cycle or duplicate item {child.Id}.");
                }
                pendingContainers.Enqueue(child.Id);
            }
        }

        return contents.Values.OrderBy(item => item.Id).ToArray();
    }
}
