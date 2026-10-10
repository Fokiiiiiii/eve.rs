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
    /// A jump drive's fuel and the character's fatigue state, in one transaction: the fuel is taken with the shared fuel debit (<see cref="TakeFuelAsync"/>) and the
    /// new fatigue state is written, or neither is. False, with nothing changed, when the ship holds too little fuel.
    /// </summary>
    public async Task<bool> DebitJumpAsync(
        PgJumpFatigueStore fatigue, long characterId, long shipId, int fuelTypeId, long quantity, JumpFatigueState next, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await TakeFuelAsync(connection, transaction, characterId, shipId, fuelTypeId, quantity, cancellationToken) > 0)
        {
            return false;
        }
        await fatigue.SaveAsync(connection, transaction, characterId, next, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
