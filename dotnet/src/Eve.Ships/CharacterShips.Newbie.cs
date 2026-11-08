using Eve.Characters;
using Eve.Dogma;
using Eve.Inventory;
using Npgsql;

namespace Eve.Ships;

/// <summary>The race's rookie ship selected in the hangar; any starter modules this operation added are listed in slot order.</summary>
public sealed record NewbieShipPreparation(Item Ship, bool Created, IReadOnlyList<ItemChanged> FittedModules);

/// <summary>What the authoritative inventory did while preparing the ship for <c>DogmaIM.CreateNewbieShip</c>.</summary>
public sealed record NewbieShipPreparationResult(NewbieShipPreparation? Preparation, bool AlreadyInNewbieShip);

public sealed partial class CharacterShips
{
    /// <summary>
    /// Finds or creates a race's corvette, with its starter modules, in the character's docked station. The active ship check, inventory
    /// additions and fitting state are serialized with the same character lock used by normal ship transitions.
    /// </summary>
    public async Task<NewbieShipPreparationResult> PrepareNewbieShipAsync(
        long characterId, long stationId, long currentShipId, int rookieShipTypeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await characters.GetAsync(characterId, cancellationToken) is not { ShipId: { } activeShipId, StationId: { } dockedAt }
            || activeShipId != currentShipId || dockedAt != stationId
            || await items.LockAndGetAsync(connection, transaction, activeShipId, cancellationToken) is not { } activeShip
            || activeShip.OwnerId != characterId || activeShip.LocationId != stationId || activeShip.Flag != ItemFlags.Hangar || !activeShip.Singleton)
        {
            return new(null, false);
        }

        await statics.EnsureTypesAsync([activeShip.TypeId, rookieShipTypeId], cancellationToken);
        if (statics.CategoryId("Ship") is not { } shipCategoryId
            || statics.AttributeId("hiSlots") is not { } highSlotsAttributeId
            || statics.AttributeId("medSlots") is not { } mediumSlotsAttributeId
            || statics.Type(rookieShipTypeId) is not { } rookieType
            || rookieType.CategoryId != shipCategoryId
            || statics.Type(activeShip.TypeId) is not { } activeType)
        {
            return new(null, false);
        }
        if (activeType.GroupId == rookieType.GroupId)
        {
            return new(null, true);
        }
        if (rookieType.GroupId <= 0)
        {
            return new(null, false);
        }

        var hangar = await items.LockAllInLocationAsync(connection, transaction, stationId, characterId, cancellationToken);
        var ship = hangar.FirstOrDefault(item => item.TypeId == rookieShipTypeId && item.Flag == ItemFlags.Hangar
            && item.Singleton && item.Quantity == 1);
        var created = ship is null;
        if (ship is null)
        {
            var id = await items.CreateAsync(connection, transaction,
                new NewItem(rookieShipTypeId, characterId, stationId, ItemFlags.Hangar, 1, Singleton: true), cancellationToken);
            ship = new Item(id, rookieShipTypeId, characterId, stationId, ItemFlags.Hangar, 1, true, null);
        }

        var kits = StarterKits.For(rookieShipTypeId);
        await statics.EnsureTypesAsync(kits.Select(module => module.TypeId), cancellationToken);
        var fitted = (await items.LockAllInLocationAsync(connection, transaction, ship.Id, characterId, cancellationToken)).ToList();
        var fittedChanges = new List<ItemChanged>();
        foreach (var module in kits.OrderBy(module => module.Flag))
        {
            if (fitted.Any(item => item.TypeId == module.TypeId && item.Flag >= ItemFlags.LowSlot0 && item.Flag <= ItemFlags.HiSlot0 + 7))
            {
                continue;
            }

            var rackStart = module.Flag >= ItemFlags.HiSlot0 ? ItemFlags.HiSlot0 : ItemFlags.MedSlot0;
            var slotCountAttributeId = module.Flag >= ItemFlags.HiSlot0 ? highSlotsAttributeId : mediumSlotsAttributeId;
            var slotCount = Math.Clamp((int)Math.Floor(rookieType.Attributes.GetValueOrDefault(slotCountAttributeId)), 0, 8);
            var occupied = fitted.Select(item => item.Flag).ToHashSet();
            var targetFlag = Enumerable.Range(rackStart, slotCount).FirstOrDefault(flag => !occupied.Contains(flag));
            if (slotCount == 0 || targetFlag == 0)
            {
                return new(null, false);
            }

            var moduleId = await items.CreateAsync(connection, transaction,
                new NewItem(module.TypeId, characterId, stationId, ItemFlags.Hangar, 1, Singleton: true), cancellationToken);
            await items.MoveAsync(connection, transaction, moduleId, ship.Id, targetFlag, cancellationToken);
            await modules.SetAsync(connection, transaction, moduleId, ItemState.Online, cancellationToken);
            var fittedItem = new Item(moduleId, module.TypeId, characterId, ship.Id, targetFlag, 1, true, null);
            fitted.Add(fittedItem);
            fittedChanges.Add(new ItemChanged(fittedItem, stationId, ItemFlags.Hangar, 1));
        }

        await transaction.CommitAsync(cancellationToken);
        return new(new NewbieShipPreparation(ship, created, fittedChanges), false);
    }
}
