using Eve.Inventory;
using Npgsql;

namespace Eve.Ships;

/// <summary>
/// Module fuel: the fuel of one cycle of a module that burns fuel (its consumption type and quantity, attributes 713 and 714) is taken from the ship's fuel bay
/// or its cargo hold, the fuel bay first where the type can be stored there (the reference's order, <c>fuelBayInventory.js</c> <c>getFuelStorageFlagsForType</c>).
/// The debit and the answer to the cycle are written in one transaction, so a cycle the simulation asks again after a restart is answered from the record and
/// is never debited twice (ADR 0013, rule 4).
/// </summary>
public sealed partial class CharacterShips
{
    /// <summary>The fuel bay (flag 133), where a fuel block or an ice product can be stored (the same rule as <see cref="ShipHoldRules"/>).</summary>
    public const int FuelBayFlag = 133;

    private static readonly string[] FuelBayGroups = ["Fuel Block", "Ice Product"];

    /// <summary>
    /// Takes <paramref name="quantity"/> units of <paramref name="moduleId"/>'s cycle fuel from the ship. Accepted when the ship holds
    /// them; otherwise refused with <c>NO_FUEL</c> and nothing is taken. Either way the answer is recorded for the module in the same transaction, and a cycle the
    /// module has already had settled is answered from that record without a second debit.
    /// </summary>
    public async Task<WorkCycleRecord> ConsumeModuleFuelAsync(
        PgWorkCycleLedger ledger, long characterId, long shipId, long moduleId, long cycleId, int fuelTypeId, long quantity, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await ledger.LastAsync(connection, transaction, moduleId, cancellationToken) is { } settled && settled.CycleId == cycleId)
        {
            await transaction.CommitAsync(cancellationToken);
            return settled;
        }

        var shortfall = await TakeFuelAsync(connection, transaction, characterId, shipId, fuelTypeId, quantity, cancellationToken);
        var record = shortfall == 0
            ? new WorkCycleRecord(moduleId, cycleId, true, "", 0, Math.Max(0, quantity), true)
            : new WorkCycleRecord(moduleId, cycleId, false, "NO_FUEL", shortfall, 0, true);
        await ledger.RecordAsync(connection, transaction, record, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    /// <summary>
    /// The shared fuel debit, inside the caller's transaction: takes <paramref name="quantity"/> units of <paramref name="fuelTypeId"/> from the ship, the fuel bay
    /// first where the type can be stored there. Returns how many units the ship lacks, and takes nothing when it lacks any (0 when the units were taken).
    /// </summary>
    internal async Task<long> TakeFuelAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long shipId, int fuelTypeId, long quantity, CancellationToken cancellationToken)
    {
        await statics.EnsureTypesAsync([fuelTypeId], cancellationToken);
        var fuelGroup = statics.Type(fuelTypeId) is { } fuelType ? statics.GroupName(fuelType.GroupId) : "";
        var flags = FuelBayGroups.Contains(fuelGroup, StringComparer.OrdinalIgnoreCase) ? new[] { FuelBayFlag, CargoFlag } : new[] { CargoFlag };
        var stacks = new List<Item>();
        foreach (var flag in flags)
        {
            stacks.AddRange(await items.LockInLocationAsync(connection, transaction, shipId, characterId, flag, [fuelTypeId], cancellationToken));
        }

        var available = stacks.Sum(stack => stack.Quantity);
        if (quantity > 0 && available < quantity)
        {
            return quantity - available;
        }
        var remaining = quantity;
        foreach (var stack in stacks)
        {
            if (remaining <= 0)
            {
                break;
            }
            var taken = Math.Min(remaining, stack.Quantity);
            remaining -= taken;
            if (taken == stack.Quantity)
            {
                await items.DeleteAsync(connection, transaction, stack.Id, cancellationToken);
            }
            else
            {
                await items.SetQuantityAsync(connection, transaction, stack.Id, stack.Quantity - taken, cancellationToken);
            }
        }
        return 0;
    }
}
