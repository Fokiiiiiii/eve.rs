using Eve.Dogma;
using Eve.Inventory;
using Eve.Inventory.Mutation;

namespace Eve.Ships.Tests;

/// <summary>The one factory every inventory item enters a dogma world through (no database, no static data).</summary>
public class ItemDogmaTests
{
    private static readonly Item Module = new(77, 12052, 9, 4, 4, 1, true, null, false, false, 0, 0, 0, null);

    [Fact]
    public void An_item_without_provenance_is_built_from_its_own_type_alone()
    {
        var dogma = ItemDogma.Of(Module, 9, 4, 27, ItemState.Online);

        Assert.Equal((77L, 12052, 9L, 4L, 27, ItemState.Online), (dogma.Id, dogma.TypeId, dogma.OwnerId, dogma.LocationId, dogma.FlagId, dogma.State));
        Assert.Null(dogma.BaseTypeId);
        Assert.Empty(dogma.OwnValues);
        Assert.Equal([12052], ItemDogma.TypeIdsOf(Module, null));
    }

    [Fact]
    public void A_mutated_item_is_typed_as_its_result_with_its_input_as_base_and_its_rolled_values_as_its_own()
    {
        var provenance = new ItemMutationRecord(77, 47297, 5, 12052, 6, 9, 1_700_000_000_000L,
            new Dictionary<int, double> { [30] = 0.75 }, new Dictionary<int, double> { [30] = 150.0 }, new Dictionary<int, double> { [30] = 198.75 });
        var result = Module with { TypeId = 47408 };

        var dogma = ItemDogma.Of(result, 9, 4, 27, ItemState.Offline, provenance);

        Assert.Equal(47408, dogma.TypeId);
        Assert.Equal(12052, dogma.BaseTypeId);
        Assert.Equal(new Dictionary<int, double> { [30] = 198.75 }, dogma.OwnValues);
        Assert.Equal([47408, 12052], ItemDogma.TypeIdsOf(result, provenance));
    }
}
