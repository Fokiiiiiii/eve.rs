using Eve.Fitting;

namespace Eve.Services.Space.Scanning;

/// <summary>
/// What a scanner reveals, worked out from the target's inventory (EVE.js <c>scannerModuleRuntime.js</c>): the cargo rows of a Cargo Scanner (one container deep),
/// the fitted rows of a Ship Scanner (each item rolled against the reveal chance), the reveal chance at a distance, and the quantity a blueprint is told as.
/// The rows are <c>(typeID, quantity)</c>: the client unpacks each as a 2-tuple, and a blueprint's copy or original bit rides in the sign of the quantity.
/// </summary>
public static class ScanRows
{
    /// <summary>The category of blueprints (<c>categoryBlueprint</c>).</summary>
    public const int CategoryBlueprint = 9;

    /// <summary>The cargo groups that hold other items (inventorycommon <c>const.py</c>: secure containers, audit log containers, mobile depots, courier packages).</summary>
    private static readonly HashSet<int> ContainerGroups = [12, 340, 448, 649];

    /// <summary>Plastic wrap: a courier package's outer layer, whose contents a cargo scan reads only through one layer.</summary>
    public const int PlasticWrapTypeId = 3468;

    /// <summary>The cargo flag of the hidden modifier bucket, which is neither a hold nor a fitting (flag 156).</summary>
    private const int HiddenModifierFlag = 156;

    /// <summary>A surface-to-surface distance within this many metres of a range limit is in range (the reference's one metre of slack).</summary>
    private const double RangeGraceMeters = 1;

    /// <summary>One item of a scanned inventory, as the scan reads it. <see cref="Singleton"/> is 0 for a stack, 1 for an original and 2 for a copy.</summary>
    public readonly record struct Scanned(long Id, int TypeId, int GroupId, int CategoryId, int Flag, long Quantity, int Singleton);

    /// <summary>Whether a flag is a fitting slot of a ship (the low, medium, high, rig, subsystem and service slots).</summary>
    public static bool IsFittingFlag(int flag) => Slots.IsSlotFlag(flag);

    /// <summary>Whether an item in this flag is cargo: a hold flag, not a fitting slot and not the hidden modifier bucket.</summary>
    public static bool IsCargoFlag(int flag) => flag >= 0 && !IsFittingFlag(flag) && flag != HiddenModifierFlag;

    /// <summary>Whether the item is a container whose contents a cargo scan reads (the container groups, and plastic wrap).</summary>
    public static bool IsContainer(Scanned item) => ContainerGroups.Contains(item.GroupId) || item.TypeId == PlasticWrapTypeId;

    /// <summary>
    /// Whether a target's scan resistance (0 to 1: the chance it resists a scan) takes the cycle: the roll of the cycle lands under it. A target with no
    /// resistance is never resisted.
    /// </summary>
    public static bool IsResisted(double roll, double resistance) => resistance > 0 && roll < resistance;

    /// <summary>The quantity a row carries: a blueprint's copy and original bit in the sign (-2 copy, -1 original), otherwise the stack or 1.</summary>
    public static long QuantityOf(Scanned item)
    {
        if (item.CategoryId == CategoryBlueprint && item.Singleton > 0)
        {
            return -item.Singleton;
        }
        return item.Quantity > 0 ? item.Quantity : 1;
    }

    /// <summary>
    /// The reveal chance at a surface-to-surface distance: whole inside the optimal range, then the standard falloff (one half at optimal plus one falloff, through
    /// <c>0.5 ^ (beyond / falloff)^2</c>), and nothing beyond. A range with no falloff (a cargo scanner) is a hard cutoff.
    /// </summary>
    public static double RevealChance(double surfaceDistance, double optimal, double falloff)
    {
        if (surfaceDistance <= optimal + RangeGraceMeters)
        {
            return 1;
        }
        if (falloff <= 0)
        {
            return 0;
        }
        var beyond = surfaceDistance - optimal;
        if (beyond > falloff + RangeGraceMeters)
        {
            return 0;
        }
        return Math.Round(Math.Pow(0.5, Math.Pow(beyond / falloff, 2)), 6);
    }

    /// <summary>
    /// The cargo rows of a Cargo Scanner: every item in a cargo flag, and for a container the items inside it (one level only; the container itself is not reported).
    /// <paramref name="contentsOf"/> gives the items inside a container by its id.
    /// </summary>
    public static IReadOnlyList<(int TypeId, long Quantity)> CargoRows(IEnumerable<Scanned> items, Func<long, IEnumerable<Scanned>> contentsOf)
    {
        var rows = new List<(int TypeId, long Quantity)>();
        foreach (var item in items)
        {
            if (!IsCargoFlag(item.Flag))
            {
                continue;
            }
            if (!IsContainer(item))
            {
                AddRow(rows, item);
                continue;
            }
            foreach (var nested in contentsOf(item.Id))
            {
                AddRow(rows, nested);
            }
        }
        return rows;
    }

    /// <summary>
    /// The fitted rows of a Ship Scanner: the items in a fitting slot, summed by type. Each item is revealed when <paramref name="roll"/> is below
    /// <paramref name="chance"/> (the reveal chance of its distance): the roll is drawn only when the chance is short of one, as the reference draws it.
    /// </summary>
    public static IReadOnlyList<(int TypeId, long Quantity)> ShipRows(IEnumerable<Scanned> items, double chance, Func<double> roll)
    {
        var order = new List<int>();
        var quantities = new Dictionary<int, long>();
        foreach (var item in items)
        {
            if (!IsFittingFlag(item.Flag) || item.TypeId <= 0)
            {
                continue;
            }
            if (chance < 1 && roll() >= chance)
            {
                continue;
            }
            var quantity = Math.Max(1, item.Quantity);
            if (!quantities.TryGetValue(item.TypeId, out var total))
            {
                order.Add(item.TypeId);
                total = 0;
            }
            quantities[item.TypeId] = total + quantity;
        }
        return [.. order.Select(typeId => (typeId, quantities[typeId]))];
    }

    private static void AddRow(List<(int TypeId, long Quantity)> rows, Scanned item)
    {
        if (item.TypeId > 0)
        {
            rows.Add((item.TypeId, QuantityOf(item)));
        }
    }
}
