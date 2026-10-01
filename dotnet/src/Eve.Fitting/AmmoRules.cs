using Eve.Dogma;

namespace Eve.Fitting;

/// <summary>
/// Which charges a module takes and how many. A port, as it is, of <c>chargeCompatibilityPolicy.js</c> of EVE.js (ADR 0009), which transcribes the client's
/// <c>dogma/ammoloadingUtils.py</c>: the module names the groups of charges it takes (chargeGroup1 to chargeGroup5), the size of the charge must be the one the module
/// asks for (a charge that declares none is of size 0), and the rounds a module holds are its capacity over the volume of a round (one when either is not known).
/// </summary>
public static class AmmoRules
{
    /// <summary>The attributes that name the groups of the charges a module takes.</summary>
    public static readonly int[] ChargeGroupAttributes = [604, 605, 606, 609, 610];

    public const int ChargeSize = 128;
    public const int CapacityAttribute = 38;
    public const int VolumeAttribute = 161;

    /// <summary>The module's reload time in milliseconds: the attribute <c>reloadTime</c>, which dogma may have changed.</summary>
    public const int ReloadTimeAttribute = 1795;

    /// <summary>The groups of charges the type of a module takes.</summary>
    public static IReadOnlyList<int> ChargeGroups(TypeDef module) =>
        [.. ChargeGroupAttributes.Where(module.Attributes.ContainsKey).Select(a => (int)module.Attributes[a]).Where(g => g > 0).Distinct().Order()];

    /// <summary>
    /// How many rounds of <paramref name="charge"/> a module of this type holds, or 0 when it does not take it (its group is not one the module names, its size is not the
    /// one the module asks for, or one round does not fit).
    /// </summary>
    public static long Capacity(TypeDef module, TypeDef charge)
    {
        if (!ChargeGroups(module).Contains(charge.GroupId))
        {
            return 0;
        }
        var moduleSize = module.Attributes.GetValueOrDefault(ChargeSize, 0);
        var chargeSize = charge.Attributes.GetValueOrDefault(ChargeSize, 0);
        if (moduleSize > 0 && moduleSize != chargeSize)
        {
            return 0;
        }
        var capacity = module.Attributes.GetValueOrDefault(CapacityAttribute, 0);
        var volume = charge.Attributes.GetValueOrDefault(VolumeAttribute, 0);
        if (capacity > 0 && volume > 0)
        {
            return (long)Math.Floor(capacity / volume);
        }
        return 1; // the data gives no usable volume: one round, as the reference does
    }
}
