using Eve.Dogma;
using Eve.Inventory;
using Npgsql;

namespace Eve.Ships;

/// <summary>
/// The fuel of a player control tower (EVE.js <c>controlTowerRuntime.js</c>): the fuel block of its race, 10 blocks an hour for a small tower and doubling with each
/// size, paid from the tower's own container. The SDE gives the size (attribute 1031), the period (722, one hour) and the unanchoring delay (676).
/// </summary>
public static class ControlTowerFuelRules
{
    public const int SizeAttribute = 1031;
    public const int PeriodAttribute = 722;
    public const int UnanchoringDelayAttribute = 676;

    /// <summary>The period when the SDE gives none: one hour (EVE.js <c>DEFAULT_CONTROL_TOWER_FUEL_PERIOD_MS</c>).</summary>
    public const long DefaultPeriodMs = 3_600_000;

    /// <summary>The fuel block of each race (race id, SDE <c>raceID</c>): Caldari Nitrogen, Minmatar Hydrogen, Amarr Helium, Gallente Oxygen.</summary>
    private static readonly IReadOnlyDictionary<int, int> FuelBlockByRace = new Dictionary<int, int>
    {
        [1] = 4051,
        [2] = 4246,
        [4] = 4247,
        [8] = 4312,
    };

    /// <summary>The fuel block type of a tower's race, or 0 when the race has none.</summary>
    public static int FuelBlockOf(TypeDef tower) => FuelBlockByRace.GetValueOrDefault(tower.RaceId);

    /// <summary>The size of a tower: 1 small, 2 medium, 3 large (EVE.js clamps it to 1 to 3).</summary>
    public static int SizeOf(TypeDef tower) => Math.Clamp((int)tower.Attributes.GetValueOrDefault(SizeAttribute, 1), 1, 3);

    /// <summary>The fuel blocks a tower burns each period: 10, 20 or 40 (EVE.js <c>fuelBlockQuantity</c>).</summary>
    public static long FuelPerPeriod(TypeDef tower) => 10L << (SizeOf(tower) - 1);

    /// <summary>The time of one fuel period (the SDE's 722, one hour).</summary>
    public static long PeriodMs(TypeDef tower) => tower.Attributes.GetValueOrDefault(PeriodAttribute) is > 0 and var period ? (long)period : DefaultPeriodMs;

    /// <summary>The time an unanchoring takes (the SDE's 676: one hour for the Amarr Control Tower).</summary>
    public static long UnanchoringDelayMs(TypeDef tower) => tower.Attributes.GetValueOrDefault(UnanchoringDelayAttribute) is > 0 and var delay ? (long)delay : DefaultPeriodMs;
}

public sealed partial class CharacterShips
{
    /// <summary>
    /// Takes <paramref name="quantity"/> fuel blocks of one type from a control tower's container (its fuel bay), all or nothing, in the caller's transaction. A
    /// tower's fuel blocks are the items at its id: EVE.js <c>listTypeStacks(towerID)</c>. Returns false, and takes nothing, when the bay cannot pay.
    /// </summary>
    public async Task<bool> TakeTowerFuelAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long towerId, int fuelTypeId, long quantity, CancellationToken cancellationToken = default)
    {
        if (quantity <= 0)
        {
            return true;
        }
        var stacks = new List<Item>();
        foreach (var stack in await items.InContainerAsync(connection, transaction, towerId, cancellationToken))
        {
            if (stack.TypeId == fuelTypeId && await items.LockAndGetAsync(connection, transaction, stack.Id, cancellationToken) is { Quantity: > 0 } locked)
            {
                stacks.Add(locked);
            }
        }
        if (stacks.Sum(stack => stack.Quantity) < quantity)
        {
            return false;
        }
        var remaining = quantity;
        foreach (var stack in stacks)
        {
            if (remaining <= 0)
            {
                break;
            }
            var part = Math.Min(remaining, stack.Quantity);
            if (part == stack.Quantity)
            {
                await items.DeleteAsync(connection, transaction, stack.Id, cancellationToken);
            }
            else
            {
                await items.SetQuantityAsync(connection, transaction, stack.Id, stack.Quantity - part, cancellationToken);
            }
            remaining -= part;
        }
        return true;
    }

    /// <summary>Sets an item's custom info (a control tower's unanchor drop marker), in its own transaction.</summary>
    public async Task SetCustomInfoAsync(long itemId, string? customInfo, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await items.SetCustomInfoAsync(connection, transaction, itemId, customInfo, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
