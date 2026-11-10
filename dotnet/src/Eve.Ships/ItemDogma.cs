using Eve.Dogma;
using Eve.Inventory;
using Eve.Inventory.Mutation;

namespace Eve.Ships;

/// <summary>
/// The one way an inventory item enters a <see cref="DogmaWorld"/>. A mutated module or drone (ADR 0016) is typed as its result, but its base
/// values are those of the input it was made from and its rolled values are its own; an item that was not mutated is built from its type alone.
/// Every place that puts an item in a world builds it here, with the provenance read from <see cref="ItemMutationStore"/>.
/// </summary>
public static class ItemDogma
{
    /// <summary>The dogma item of an inventory item at a location and flag. <paramref name="mutation"/> is its provenance, or null when it has none.</summary>
    public static DogmaItem Of(
        Item item, long ownerId, long locationId, int flagId, ItemState state = ItemState.Offline, ItemMutationRecord? mutation = null, int? crystalDamageAttributeId = null)
    {
        IReadOnlyDictionary<int, double>? ownValues = mutation?.MutatedValues;
        if (crystalDamageAttributeId is { } damageAttribute && CrystalDamageState.TryDamageOf(item, out var damage))
        {
            var values = ownValues?.ToDictionary() ?? [];
            values[damageAttribute] = damage;
            ownValues = values;
        }
        return new DogmaItem(item.Id, item.TypeId, ownerId, locationId, flagId, state,
            baseTypeId: mutation?.SourceTypeId, ownValues: ownValues);
    }

    /// <summary>The types the static data must hold before <see cref="Of"/> is called: the item's own type, and the input of its mutation.</summary>
    public static IEnumerable<int> TypeIdsOf(Item item, ItemMutationRecord? mutation) =>
        mutation is null ? [item.TypeId] : [item.TypeId, mutation.SourceTypeId];
}
