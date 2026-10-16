using Eve.Dogma;
using Eve.Inventory;
using Eve.Skills;

namespace Eve.Ships;

/// <summary>
/// The implants a pilot has plugged in and the boosters it has active, as dogma items of the pilot's own world. An implant is an item of
/// category 20 with an implant slot, located in the pilot under the implant flag; an active booster is one with a live row in the booster state.
/// </summary>
public static class PilotAugmentations
{
    public const int BoosterFlag = 88;
    public const int ImplantFlag = 89;
    public const int ImplantCategoryId = 20;
    public const int ImplantSlotAttributeId = 331;

    /// <summary>The dogma item of an implant or an active booster, or null for any other item.</summary>
    public static DogmaItem? ModelOf(DogmaStatic dogma, Item item, long characterId, IReadOnlyDictionary<long, StoredSkillBooster> activeBoosters)
    {
        var type = dogma.Type(item.TypeId);
        if (item.Flag == ImplantFlag && IsImplantType(type))
        {
            return new DogmaItem(item.Id, item.TypeId, characterId, characterId, item.Flag);
        }
        if (item.Flag == BoosterFlag && activeBoosters.TryGetValue(item.Id, out var booster) &&
            type is not null && booster.BoosterTypeId == item.TypeId)
        {
            // a booster's effects run while it is active; the ones with a chance run only when the roll chose them
            var effects = type.EffectIds.Where(effectId =>
                dogma.Effect(effectId) is { ChanceAttributeId: 0 } || booster.SideEffectIds.Contains(effectId)).ToHashSet();
            return new DogmaItem(item.Id, item.TypeId, characterId, characterId, item.Flag, ItemState.Active, effects);
        }
        return null;
    }

    public static bool IsImplantType(TypeDef? type) =>
        type is { CategoryId: ImplantCategoryId } &&
        type.Attributes.TryGetValue(ImplantSlotAttributeId, out var slot) &&
        double.IsFinite(slot) && slot > 0 && slot <= int.MaxValue && slot == Math.Truncate(slot);
}
