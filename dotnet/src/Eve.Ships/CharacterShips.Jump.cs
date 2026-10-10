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

    /// <summary>The modules fitted to the ship (its low, medium and high slots), with their type and the dogma attributes each carries.</summary>
    public async Task<IReadOnlyList<(int TypeId, IReadOnlyDictionary<int, double> Attributes)>> FittedModulesAsync(
        long characterId, long shipId, CancellationToken cancellationToken = default)
    {
        var fitted = (await items.InLocationAsync(shipId, characterId, cancellationToken))
            .Where(item => item.Flag is >= Eve.Inventory.ItemFlags.LowSlot0 and <= Eve.Inventory.ItemFlags.HiSlot0 + 7)
            .ToList();
        await statics.EnsureTypesAsync(fitted.Select(item => item.TypeId).Distinct().ToArray(), cancellationToken);
        var modules = new List<(int, IReadOnlyDictionary<int, double>)>();
        foreach (var item in fitted)
        {
            if (statics.Type(item.TypeId) is { } module)
            {
                modules.Add((item.TypeId, module.Attributes));
            }
        }
        return modules;
    }

    /// <summary>Takes units of a fuel type from the ship in its own transaction (the fuel bay first); false, with nothing taken, when the ship holds too little.</summary>
    public async Task<bool> DebitFuelAsync(long characterId, long shipId, int fuelTypeId, long quantity, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await TakeFuelAsync(connection, transaction, characterId, shipId, fuelTypeId, quantity, cancellationToken) > 0)
        {
            return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// A conduit jump's isotopes and the fatigue of every ship in it, in one transaction: the anchor's fuel is taken (the fuel bay first) and each ship's new
    /// fatigue state is written (the anchor included), or none is. False, with nothing changed, when the anchor holds too little fuel.
    /// </summary>
    public async Task<bool> DebitGroupJumpAsync(
        PgJumpFatigueStore fatigue, long anchorCharacterId, long anchorShipId, int fuelTypeId, long quantity,
        IReadOnlyDictionary<long, JumpFatigueState> states, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var character in states.Keys.Append(anchorCharacterId).Distinct().OrderBy(id => id))
        {
            await LockAsync(connection, transaction, character, cancellationToken);
        }
        if (await TakeFuelAsync(connection, transaction, anchorCharacterId, anchorShipId, fuelTypeId, quantity, cancellationToken) > 0)
        {
            return false;
        }
        foreach (var (character, state) in states)
        {
            await fatigue.SaveAsync(connection, transaction, character, state, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// A passenger's jump through a bridge: the isotopes come from the bridging ship (the wiki: "use up isotopes from the bridger's fuel hold") and the
    /// passenger's fatigue state is written, in one transaction. False, with nothing changed, when the bridging ship holds too little.
    /// </summary>
    public async Task<bool> DebitBridgeAsync(
        PgJumpFatigueStore fatigue, long bridgerCharacterId, long bridgerShipId, int fuelTypeId, long quantity,
        long passengerCharacterId, JumpFatigueState passengerNext, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // both characters are locked in one order, so two passages cannot wait on each other
        foreach (var character in new[] { bridgerCharacterId, passengerCharacterId }.Distinct().OrderBy(id => id))
        {
            await LockAsync(connection, transaction, character, cancellationToken);
        }
        if (await TakeFuelAsync(connection, transaction, bridgerCharacterId, bridgerShipId, fuelTypeId, quantity, cancellationToken) > 0)
        {
            return false;
        }
        await fatigue.SaveAsync(connection, transaction, passengerCharacterId, passengerNext, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
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
