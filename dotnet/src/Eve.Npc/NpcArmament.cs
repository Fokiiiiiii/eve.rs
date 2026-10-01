using System.Globalization;
using System.Numerics;
using Eve.Dogma;
using Eve.Space;

namespace Eve.Npc;

/// <summary>
/// What an NPC fights with. A retail entity type carries its weapon on its own dogma record (the effect <c>targetAttack</c> and the attributes of the ship
/// itself), not in a module loadout: 5592 of the 5620 authored profiles point at the empty loadout by design. This is a port, as it is, of the part of
/// <c>buildWeaponModuleSnapshot</c> (<c>server/src/space/combat/weaponDogma.js</c>) and <c>getNpcFittedWeaponModules</c> (<c>npc/npcEquipment.js</c>) of EVE.js
/// that turns such a hull into a turret (ADR 0009). Modules fitted to an NPC are not armed here (see <c>docs/compat/DEVIATIONS.md</c>).
/// </summary>
public static class NpcArmament
{
    private const int CapacitorNeed = 6;
    private const int Speed = 51;
    private const int MaxRange = 54;
    private const int Falloff = 158;
    private const int TrackingSpeed = 160;
    private const int OptimalSigRadius = 620;
    private const int DamageMultiplier = 64;
    private const int EmDamage = 114;
    private const int ExplosiveDamage = 116;
    private const int KineticDamage = 117;
    private const int ThermalDamage = 118;
    private const int EntityAttackRange = 247;

    // what the effects of a type are when one of them is a weapon and what makes an effect one the pilot can run
    private static readonly HashSet<int> ActivatableCategories = [EffectDef.Activation, EffectDef.Target, EffectDef.Area];

    private static readonly HashSet<string> PassiveSlotEffects =
    [
        "online", "onlineforstructures", "anchordropforstructures", "anchorliftforstructures", "hipower", "medpower", "lopower", "rigslot", "subsystem",
        "turretfitted", "launcherfitted",
    ];

    /// <summary>
    /// <c>round6</c>: a number as <c>toFixed(6)</c> gives it, which takes the exact value of the double and, when it lies exactly half way between two
    /// millionths (6.4453125 does), the larger magnitude. The formatter of .NET rounds such a tie to even, so a tie is found and settled here.
    /// </summary>
    public static double Round6(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }
        var bits = BitConverter.DoubleToInt64Bits(value);
        var biased = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xF_FFFF_FFFF_FFFFL;
        var exponent = (biased == 0 ? 1 : biased) - 1075;
        if (biased != 0)
        {
            mantissa |= 1L << 52;
        }
        if (exponent < 0)
        {
            // value = mantissa / 2^-exponent; it is a tie when value * 2,000,000 is an odd whole number
            var scaled = new BigInteger(mantissa) * 2_000_000;
            var divisor = BigInteger.One << -exponent;
            if (scaled % divisor == 0 && !(scaled / divisor).IsEven)
            {
                // odd half-millionths: the larger whole number of millionths, which is divided by a million as parsing its digits would
                var millionths = ((scaled / divisor) + 1) / 2;
                var magnitude = (double)millionths / 1_000_000.0;
                return value < 0 ? -magnitude : magnitude;
            }
        }
        return double.Parse(value.ToString("F6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static double Value(TypeDef type, int attribute, double fallback) => type.Attributes.GetValueOrDefault(attribute, fallback);

    /// <summary><c>typeHasNonzeroTargetAttackDamage</c>: the hull deals damage of some kind and its multiplier (1 when it has none) is above nothing.</summary>
    public static bool HasTargetAttackDamage(TypeDef type)
    {
        var damage = new[] { EmDamage, ThermalDamage, KineticDamage, ExplosiveDamage }.Sum(attribute => Math.Max(0, Value(type, attribute, 0)));
        return damage > 0 && Math.Max(0, Value(type, DamageMultiplier, 1)) > 0;
    }

    /// <summary><c>resolveWeaponActivationEffect</c>: the first effect of the type that a pilot can run and that is not one every module has.</summary>
    public static EffectDef? ActivationEffect(TypeDef type, DogmaStatic statics) =>
        type.EffectIds
            .Select(statics.Effect)
            .FirstOrDefault(effect => effect is not null && ActivatableCategories.Contains(effect.Category) && !PassiveSlotEffects.Contains(effect.Name.Trim().ToLowerInvariant()));

    /// <summary>
    /// What kind of turret an effect of a hull is, by the name of the client's effect it plays: null for what this does not arm (missiles, vorton projectors
    /// and the like) or what is no weapon at all.
    /// </summary>
    public static string? TurretFamily(EffectDef effect)
    {
        var name = effect.Name.Trim().ToLowerInvariant();
        if (name is not ("targetattack" or "targetdisintegratorattack"))
        {
            return null;
        }
        var guid = effect.Guid.Trim().ToLowerInvariant();
        if (guid.Contains("triglavianbeam", StringComparison.Ordinal))
        {
            return "precursorTurret";
        }
        if (guid.Contains("laser", StringComparison.Ordinal))
        {
            return "laserTurret";
        }
        if (guid.Contains("hybrid", StringComparison.Ordinal) || guid.Contains("rail", StringComparison.Ordinal) || guid.Contains("blaster", StringComparison.Ordinal))
        {
            return "hybridTurret";
        }
        if (guid.Contains("projectile", StringComparison.Ordinal) || guid.Contains("artillery", StringComparison.Ordinal) || guid.Contains("autocannon", StringComparison.Ordinal))
        {
            return "projectileTurret";
        }
        return null;
    }

    /// <summary>
    /// The turret a hull carries, as a weapon of the ship's own (the simulation knows the module of an NPC's weapon to be the ship): the first effect of the
    /// hull that is a turret, with the hull's own attributes for its cycle, range, tracking and damage. Null when the hull deals no damage or has no turret.
    /// </summary>
    public static SimNpcWeapon? HullWeapon(TypeDef hull, long shipId, DogmaStatic statics)
    {
        if (!HasTargetAttackDamage(hull))
        {
            return null;
        }
        var effect = hull.EffectIds.Select(statics.Effect).FirstOrDefault(candidate => candidate is not null && TurretFamily(candidate) is not null);
        if (effect is null)
        {
            return null;
        }
        var multiplier = Math.Max(0, Value(hull, DamageMultiplier, 1));
        double Shot(int attribute) => Round6(Math.Max(0, Value(hull, attribute, 0)) * multiplier);
        var guid = ActivationEffect(hull, statics)?.Guid.Trim() ?? "";
        return new SimNpcWeapon(
            shipId, effect.Id, Math.Max(1, Round6(Value(hull, Speed, 1000))), Math.Max(0, Round6(Value(hull, CapacitorNeed, 0))), hull.TypeId,
            guid.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : guid,
            [Shot(EmDamage), Shot(ThermalDamage), Shot(KineticDamage), Shot(ExplosiveDamage)],
            Math.Max(0, Round6(Value(hull, MaxRange, 0))), Math.Max(0, Round6(Value(hull, Falloff, 0))), Math.Max(0, Round6(Value(hull, TrackingSpeed, 0))),
            Math.Max(1, Round6(Value(hull, OptimalSigRadius, 40_000))));
    }

    /// <summary>How near a ship must come for the NPC to attack it: the hull's <c>entityAttackRange</c>, or what that attribute is when a type has none.</summary>
    public static double AggressionRange(TypeDef hull, DogmaStatic statics) =>
        Math.Max(0, hull.Attributes.GetValueOrDefault(EntityAttackRange, statics.Attribute(EntityAttackRange)?.DefaultValue ?? 0));
}
