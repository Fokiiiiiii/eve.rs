using Npgsql;

namespace Eve.Ships;

public sealed partial class CharacterShips
{
    /// <summary>The type of a ship the character owns, or null when the ship is not theirs.</summary>
    public async Task<int?> ShipTypeOfAsync(long characterId, long shipId, CancellationToken cancellationToken = default) =>
        await items.GetAsync(shipId, cancellationToken) is { } ship && ship.OwnerId == characterId ? ship.TypeId : null;

    /// <summary>How many units of a fuel type the ship holds in its fuel bay and cargo hold (what a jump's debit can take).</summary>
    public async Task<long> FuelHeldAsync(long characterId, long shipId, int fuelTypeId, CancellationToken cancellationToken = default) =>
        (await items.InLocationAsync(shipId, characterId, cancellationToken))
        .Where(item => item.TypeId == fuelTypeId && item.Flag is FuelBayFlag or CargoFlag)
        .Sum(item => item.Quantity);

    /// <summary>
    /// The values (as fractions, the attribute a percentage) of an attribute on the modules fitted to the ship that are of a group: the jump drive reads its economizers
    /// this way (the group and the attribute are the caller's).
    /// </summary>
    public async Task<IReadOnlyList<double>> FittedAttributeFractionsAsync(
        long characterId, long shipId, int groupId, int attribute, CancellationToken cancellationToken = default)
    {
        var fitted = (await items.InLocationAsync(shipId, characterId, cancellationToken))
            .Where(item => item.Flag is >= Eve.Inventory.ItemFlags.LowSlot0 and <= Eve.Inventory.ItemFlags.HiSlot0 + 7)
            .ToList();
        await statics.EnsureTypesAsync(fitted.Select(item => item.TypeId).Distinct().ToArray(), cancellationToken);
        var bonuses = new List<double>();
        foreach (var item in fitted)
        {
            if (statics.Type(item.TypeId) is { } module && module.GroupId == groupId && module.Attributes.TryGetValue(attribute, out var percent))
            {
                bonuses.Add(percent / 100);
            }
        }
        return bonuses;
    }

    /// <summary>
    /// A jump drive's fuel, the character's fatigue state and the spool, in one transaction: the fuel is taken with the shared fuel debit
    /// (<see cref="TakeFuelAsync"/>), the new fatigue state is written and the spool is recorded, or none is. False, with nothing changed, when the ship holds
    /// too little fuel.
    /// </summary>
    public async Task<bool> DebitJumpAsync(
        PgJumpFatigueStore fatigue, PgJumpSpoolStore spools, JumpSpool spool, JumpFatigueState next, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, spool.CharacterId, cancellationToken);
        if (await TakeFuelAsync(connection, transaction, spool.CharacterId, spool.ShipId, spool.FuelTypeId, spool.FuelQuantity, cancellationToken) > 0)
        {
            return false;
        }
        await fatigue.SaveAsync(connection, transaction, spool.CharacterId, next, cancellationToken);
        await spools.InsertAsync(connection, transaction, spool, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Cancels a jump spool that will not complete: the fuel it took goes back into the ship's fuel bay, and the fatigue state the jump replaced is restored,
    /// in one transaction. False when the character has no spool. The fuel is not refunded when the ship no longer exists (the ship was destroyed).
    /// </summary>
    public async Task<bool> CancelJumpAsync(PgJumpFatigueStore fatigue, PgJumpSpoolStore spools, long characterId, long endsAtMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await spools.TakeAsync(connection, transaction, characterId, endsAtMs, cancellationToken) is not { } spool)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        if (await items.GetAsync(spool.ShipId, cancellationToken) is not null)
        {
            await items.CreateAsync(connection, transaction, new Eve.Inventory.NewItem(spool.FuelTypeId, characterId, spool.ShipId, FuelBayFlag, spool.FuelQuantity, Singleton: false), cancellationToken);
        }
        await fatigue.SaveAsync(connection, transaction, characterId, spool.Prior, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
