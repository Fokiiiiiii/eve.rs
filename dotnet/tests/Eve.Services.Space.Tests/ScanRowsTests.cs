using Eve.Services.Space.Scanning;
using Scanned = Eve.Services.Space.Scanning.ScanRows.Scanned;

namespace Eve.Services.Space.Tests;

/// <summary>The scan rules of a scanner (EVE.js scannerModuleRuntime.js), with no database: the reveal chance, the cargo and fitted rows, the resistance and the quantities.</summary>
public class ScanRowsTests
{
    private const int HoldFlag = Eve.Inventory.ItemFlags.Cargo;
    private const int HiSlotFlag = Eve.Inventory.ItemFlags.HiSlot0;
    private const int HiddenFlag = 156;

    [Theory]
    [InlineData(0, 50000, 0, 1)]
    [InlineData(50001, 50000, 0, 1)]          // a metre of slack at the optimal
    [InlineData(50002, 50000, 0, 0)]          // a cargo scan is a hard cutoff
    [InlineData(50000, 50000, 30000, 1)]
    [InlineData(65000, 50000, 30000, 0.840896)]
    [InlineData(80000, 50000, 30000, 0.5)]    // one falloff beyond the optimal is half
    [InlineData(80001, 50000, 30000, 0.499977)] // a metre past that is still in reach, a little under half
    [InlineData(80002, 50000, 30000, 0)]      // beyond one falloff and a metre is out of reach
    public void The_reveal_chance_is_whole_inside_the_optimal_then_falls_off_to_half_at_one_falloff_and_is_nothing_beyond(
        double distance, double optimal, double falloff, double expected)
    {
        Assert.Equal(expected, ScanRows.RevealChance(distance, optimal, falloff));
    }

    [Fact]
    public void A_cargo_scan_reads_the_hold_and_one_level_inside_containers_and_never_the_fittings_or_the_hidden_bucket()
    {
        const int container = 3296;   // a secure container, in the container group 12
        var items = new[]
        {
            new Scanned(1, 34, 18, 4, HoldFlag, 100, 0),              // tritanium in the hold
            new Scanned(2, 2873, 1, 7, HiSlotFlag, 1, 0),             // a fitted module: not cargo
            new Scanned(3, 0, 0, 0, HiddenFlag, 1, 0),                // the hidden bucket: not cargo
            new Scanned(4, container, 12, 6, HoldFlag, 1, 0),         // a container: its contents, not itself
            new Scanned(5, 688, 1, ScanRows.CategoryBlueprint, HoldFlag, 1, 2),   // a blueprint copy: told as -2
        };
        var inside = new Dictionary<long, Scanned[]>
        {
            [4] = [new Scanned(40, 35, 18, 4, 0, 7, 0), new Scanned(41, ScanRows.PlasticWrapTypeId, 0, 0, 0, 1, 0)],
        };

        var rows = ScanRows.CargoRows(items, id => inside.TryGetValue(id, out var contents) ? contents : []);

        Assert.Equal(new[] { (34, 100L), (35, 7L), (ScanRows.PlasticWrapTypeId, 1L), (688, -2L) }, rows);
    }

    [Fact]
    public void A_ship_scan_sums_the_fitted_items_by_type_and_rolls_each_one_only_when_the_chance_is_short_of_one()
    {
        var items = new[]
        {
            new Scanned(1, 2873, 1, 7, HiSlotFlag, 1, 0),
            new Scanned(2, 2873, 1, 7, HiSlotFlag + 1, 1, 0),
            new Scanned(3, 12345, 1, 8, HiSlotFlag + 1, 50, 0),    // a charge in the same module's slot family
            new Scanned(4, 34, 18, 4, HoldFlag, 100, 0),           // cargo: not fitted
        };

        // at a chance of one nothing is drawn and every fitted item is shown, summed by type
        var whole = ScanRows.ShipRows(items, 1.0, () => throw new InvalidOperationException("no roll is drawn at a chance of one"));
        Assert.Equal(new[] { (2873, 2L), (12345, 50L) }, whole);

        // short of one each fitted item is rolled: the first is shown (0.2 under 0.5), the second hidden (0.9 over it)
        var rolls = new Queue<double>([0.2, 0.9, 0.1]);
        var partial = ScanRows.ShipRows(items, 0.5, () => rolls.Dequeue());
        Assert.Equal(new[] { (2873, 1L), (12345, 50L) }, partial);
    }

    [Theory]
    [InlineData(0.0, 0.0, false)]   // no resistance: never resisted
    [InlineData(0.5, 0.0, false)]
    [InlineData(0.2, 1.0, true)]    // a Blockade Runner (resistance 1) always resists
    [InlineData(0.9, 0.5, false)]
    public void A_target_with_a_scan_resistance_resists_when_the_roll_lands_under_it(double roll, double resistance, bool resisted)
    {
        Assert.Equal(resisted, ScanRows.IsResisted(roll, resistance));
    }

    [Fact]
    public void A_blueprint_is_told_as_a_copy_or_an_original_in_the_sign_of_its_quantity_and_a_stack_as_its_size()
    {
        Assert.Equal(-2, ScanRows.QuantityOf(new Scanned(1, 1, 1, ScanRows.CategoryBlueprint, HoldFlag, 1, 2)));
        Assert.Equal(-1, ScanRows.QuantityOf(new Scanned(2, 1, 1, ScanRows.CategoryBlueprint, HoldFlag, 1, 1)));
        Assert.Equal(250, ScanRows.QuantityOf(new Scanned(3, 34, 18, 4, HoldFlag, 250, 0)));
        Assert.Equal(1, ScanRows.QuantityOf(new Scanned(4, 34, 18, 4, HoldFlag, 0, 0)));
    }
}
