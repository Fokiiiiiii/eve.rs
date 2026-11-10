using Eve.Dogma;
using Eve.Inventory;
using System.Globalization;

namespace Eve.Ships;

/// <summary>The volatility rule and persistent damage of a type of laser crystal that takes wear.</summary>
public sealed record CrystalWear(double VolatilityChance, double VolatilityDamage, double HitPoints, double Damage)
{
    public bool IsValid => double.IsFinite(VolatilityChance) && VolatilityChance is >= 0 and <= 1
        && double.IsFinite(VolatilityDamage) && VolatilityDamage >= 0
        && double.IsFinite(HitPoints) && HitPoints > 0
        && double.IsFinite(Damage) && Damage is >= 0 && Damage <= HitPoints;
}

/// <summary>Maps the SDE wear attributes and an item's persisted custom info to laser crystal dogma.</summary>
public static class CrystalDamageState
{
    public static bool IsDamageable(DogmaStatic statics, int typeId) =>
        statics.AttributeId("crystalsGetDamaged") is { } damageable
        && statics.Type(typeId)?.Attributes.GetValueOrDefault(damageable) > 0;

    public static CrystalWear? WearOf(DogmaStatic statics, Item item)
    {
        var type = statics.Type(item.TypeId);
        if (type is null || !IsDamageable(statics, item.TypeId)
            || statics.AttributeId("crystalVolatilityChance") is not { } chanceId
            || statics.AttributeId("crystalVolatilityDamage") is not { } wearId
            || statics.AttributeId("hp") is not { } hitPointsId)
        {
            return null;
        }
        var wear = new CrystalWear(
            type.Attributes.GetValueOrDefault(chanceId),
            type.Attributes.GetValueOrDefault(wearId),
            type.Attributes.GetValueOrDefault(hitPointsId),
            DamageOf(item));
        return wear.IsValid ? wear : null;
    }

    public static double DamageOf(Item? item) => TryDamageOf(item, out var damage) ? damage : 0;

    public static bool TryDamageOf(Item? item, out double damage)
    {
        if (!double.TryParse(item?.CustomInfo, NumberStyles.Float, CultureInfo.InvariantCulture, out damage) || !double.IsFinite(damage))
        {
            damage = 0;
            return false;
        }
        damage = Math.Clamp(damage, 0, 1);
        return true;
    }
}
