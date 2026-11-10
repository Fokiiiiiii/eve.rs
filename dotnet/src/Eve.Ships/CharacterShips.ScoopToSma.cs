using Eve.Inventory;
using Npgsql;

namespace Eve.Ships;

public sealed partial class CharacterShips
{
    /// <summary>
    /// Transfers one unpiloted ship into the active ship's maintenance bay. The caller has checked the scene and range;
    /// the scene removal is acknowledged while the inventory transaction is still open so a refused removal leaves the item untouched.
    /// </summary>
    public async Task<ShipOutcome> ScoopShipToMaintenanceBayAsync(
        long characterId,
        long expectedShipId,
        int solarSystemId,
        long targetShipId,
        Func<CancellationToken, Task<bool>> removeFromScene,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || expectedShipId <= 0 || solarSystemId <= 0 || targetShipId <= 0)
            return new NothingHappened();

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot ||
            snapshot.Ship.Id != expectedShipId || snapshot.Ship.Flag != InSpaceFlag || snapshot.Ship.LocationId != solarSystemId)
            return new NothingHappened();

        var item = await items.LockAndGetAsync(connection, transaction, targetShipId, cancellationToken);
        if (item is null || item.Id == expectedShipId || item.LocationId != solarSystemId || item.Flag != InSpaceFlag ||
            !item.Singleton || item.Quantity != 1 || item.OwnerId <= 0)
            return new NothingHappened();

        await statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        var type = statics.Type(item.TypeId)
            ?? throw new InvalidOperationException($"SDE type {item.TypeId} for ship {targetShipId} is missing.");
        if (type.CategoryId != 6 || !CanAddToShipHold(ShipMaintenanceBayFlag, type, item.Singleton, item.Quantity))
            return new ShipRefused(ShipRefusal.NotAllowedInHold, ModuleTypeId: item.TypeId, ShipTypeId: snapshot.Ship.TypeId);
        if (!await ShipContentsAllowedInBayAsync(item, ShipMaintenanceBayFlag, snapshot.World, characterId, cancellationToken))
            return new ShipRefused(ShipRefusal.NotAllowedInHold, ModuleTypeId: item.TypeId, ShipTypeId: snapshot.Ship.TypeId);
        if (await RefuseWhatDoesNotFitAsync(snapshot, item, 1, ShipMaintenanceBayFlag, cancellationToken) is { } full)
            return full;

        await items.MoveToOwnerAsync(connection, transaction, item.Id, characterId, snapshot.Ship.Id, ShipMaintenanceBayFlag, cancellationToken);
        if (!await removeFromScene(cancellationToken))
            return new NothingHappened();

        await transaction.CommitAsync(cancellationToken);
        var changed = item with { OwnerId = characterId, LocationId = snapshot.Ship.Id, Flag = ShipMaintenanceBayFlag };
        return new ShipChanged(item.Id, snapshot.Ship.Id, [], [], [new ItemChanged(changed, item.LocationId, item.Flag, item.Quantity, item.OwnerId)]);
    }
}
