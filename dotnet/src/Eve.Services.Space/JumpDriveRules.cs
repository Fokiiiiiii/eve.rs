namespace Eve.Services.Space;

/// <summary>
/// The jump drive's rules: the range, the fuel per light year, the activation cooldown and the fatigue, and which cynosural field a ship may jump to. The
/// numbers a hull carries are the SDE's (dogma attributes 866 to 869 and 1971, on the ship type); the skill percentages are the EVE University wiki's
/// ("Jump drive": calibration 20 % range a level, fuel conservation 10 % a level, jump freighters 10 % a level; "Jump fatigue"). Each rule is a pure
/// function of its inputs, so the host checks it without a store.
/// </summary>
public static class JumpDriveRules
{
    /// <summary>The fuel type a drive burns (attribute 866, an isotope type id).</summary>
    public const int FuelTypeAttribute = 866;

    /// <summary>The range in light years a hull can jump at its calibration of zero (attribute 867).</summary>
    public const int RangeAttribute = 867;

    /// <summary>The units of fuel per light year (attribute 868).</summary>
    public const int FuelPerLightYearAttribute = 868;

    /// <summary>The time from the activation to the jump, in milliseconds (attribute 869, 300 000 on every drive of the SDE).</summary>
    public const int SpoolAttribute = 869;

    /// <summary>The multiplier of the distance a hull counts for fatigue (attribute 1971: 0.25 for black ops, 0.1 for the freighters and the Rorqual).</summary>
    public const int FatigueMultiplierAttribute = 1971;

    /// <summary>The share of the full capacitor a drive activation takes (attribute 898, 0.95 on every drive of the SDE).</summary>
    public const int CapacitorNeedAttribute = 898;

    /// <summary>Jump Drive Operation (3456): the capacitor need, as a percentage a level (attribute 899 of the skill, -5).</summary>
    public const int OperationSkill = 3456;
    public const int CapacitorNeedPerLevelAttribute = 899;

    /// <summary>Jump Drive Calibration (21611): the range, as a percentage a level (attribute 870 of the skill, 20).</summary>
    public const int CalibrationSkill = 21611;
    public const int CalibrationPerLevelAttribute = 870;

    /// <summary>Jump Fuel Conservation (21610): the fuel, as a percentage a level (attribute 1296 of the skill, -10).</summary>
    public const int FuelConservationSkill = 21610;

    /// <summary>The fuel bonus percentage (attribute 1296): of a skill (Jump Fuel Conservation, -10 a level) and of an economizer (-4, -7 or -10).</summary>
    public const int FuelBonusAttribute = 1296;

    /// <summary>The Jump Drive Economizer group (SDE 1299): the modules whose fuel bonus the drive takes.</summary>
    public const int EconomizerGroup = 1299;

    /// <summary>Jump Freighters (29029): the fuel of a jump freighter.</summary>
    public const int JumpFreighterSkill = 29029;

    /// <summary>The ship groups the field rules name: black ops battleships (898), jump freighters (902) and capital industrial ships (883).</summary>
    public const int BlackOpsGroup = 898, JumpFreighterGroup = 902, CapitalIndustrialGroup = 883;

    /// <summary>The cynosural field types (SDE): standard 21094, covert 28650, industrial 52696.</summary>
    public const int StandardFieldType = 21094, CovertFieldType = 28650, IndustrialFieldType = 52696;

    /// <summary>The longest a cooldown can run (the wiki: thirty minutes).</summary>
    public const long CooldownCapMs = 30 * 60_000;

    /// <summary>The longest fatigue can run (the wiki: five hours).</summary>
    public const long FatigueCapMs = 5 * 60 * 60_000;

    /// <summary>The minimum fatigue a jump adds, per (1 + light years): ten minutes (the wiki's rule).</summary>
    public const long MinimumFatiguePerDistanceMs = 10 * 60_000;

    /// <summary>The minimum cooldown a jump starts, per (1 + light years): one minute (the wiki's rule).</summary>
    public const long MinimumCooldownPerDistanceMs = 60_000;

    /// <summary>The kind of a cynosural field a jump drive goes to, and so who may use it.</summary>
    public enum FieldKind
    {
        Standard,
        Covert,
        Industrial,
    }

    /// <summary>The kind of a field by its type. An unknown type is a standard field.</summary>
    public static FieldKind KindOf(int fieldTypeId) => fieldTypeId switch
    {
        CovertFieldType => FieldKind.Covert,
        IndustrialFieldType => FieldKind.Industrial,
        _ => FieldKind.Standard,
    };

    /// <summary>
    /// Whether a ship of a group may jump to a field of a kind. The wiki: a covert field takes only black ops battleships; an industrial field takes jump
    /// freighters, capital industrial ships and black ops battleships; a standard field takes every ship with a drive.
    /// </summary>
    public static bool MayJumpTo(FieldKind kind, int shipGroupId) => kind switch
    {
        FieldKind.Covert => shipGroupId == BlackOpsGroup,
        FieldKind.Industrial => shipGroupId is JumpFreighterGroup or CapitalIndustrialGroup or BlackOpsGroup,
        _ => true,
    };

    /// <summary>
    /// The range in light years at a calibration level: the hull's range times (1 + the skill's percentage a level, as a fraction: 0.2 for Jump Drive Calibration).
    /// </summary>
    public static double Range(double baseRange, int calibrationLevel, double perLevel) => baseRange * (1 + perLevel * calibrationLevel);

    /// <summary>
    /// The share of the full capacitor a drive activation takes: the hull's share (0.95) times (1 + the Jump Drive Operation percentage a level, as a fraction:
    /// -0.05). Level five gives 0.7125, the wiki's "71.25 %".
    /// </summary>
    public static double CapacitorNeed(double hullShare, int operationLevel, double perLevel) => hullShare * (1 + perLevel * operationLevel);

    /// <summary>
    /// The units of fuel a jump of <paramref name="lightYears"/> takes: light years times the fuel per light year, times (1 + the Jump Fuel Conservation percentage
    /// a level as a fraction: -0.1), and less 10 % a level of jump freighters for a jump freighter (pass 0 otherwise), rounded up to whole units.
    /// shortcut: the wiki's jump drive economizer terms are not applied (no economizer is fitted by the host yet).
    /// </summary>
    public static long FuelQuantity(
        double lightYears, double fuelPerLightYear, int fuelConservationLevel, double conservationPerLevel, int jumpFreighterLevel, double economizers) =>
        (long)Math.Ceiling(Math.Round(lightYears * fuelPerLightYear * (1 + conservationPerLevel * fuelConservationLevel) * (1 - 0.1 * jumpFreighterLevel) * economizers, 6));

    /// <summary>
    /// The fuel multiplier of the fitted economizers (their bonuses as fractions, -0.04 to -0.10). The wiki: stacking penalized, strongest first; the n-th modifier
    /// (from 0) counts e^(-(n/2.67)^2), the stacking penalty (the wiki, "Stacking penalty": 100 %, 86.9 %, 57.1 %, 28.3 %).
    /// </summary>
    public static double EconomizerMultiplier(IEnumerable<double> bonuses)
    {
        var multiplier = 1.0;
        var position = 0;
        foreach (var bonus in bonuses.OrderByDescending(bonus => Math.Abs(bonus)))
        {
            multiplier *= 1 + bonus * Math.Exp(-Math.Pow(position / 2.67, 2));
            position++;
        }
        return multiplier;
    }

    /// <summary>The Jump Portal Generation skill (24562): its generator fits need level one; its percentage a level on the portal's Strontium is attribute 1775 (-10).</summary>
    public const int JumpPortalGenerationSkill = 24562;
    public const int JumpPortalGenerationPerLevelAttribute = 1775;

    /// <summary>The Jump Portal Generator I of a Titan (SDE 23953, group 590): its portal is the standard bridge.</summary>
    public const int TitanGeneratorType = 23953;

    /// <summary>The Covert Jump Portal Generator I of a Black Ops (SDE 28652, group 4127).</summary>
    public const int CovertGeneratorType = 28652;

    /// <summary>The portal attributes (SDE): the module's capacitor need in GJ (6), its Strontium per activation (713 the type, 714 the quantity), the portal
    /// consumption mass factor (1001), the portal duration in ms (1002), the passenger flag it requires (3318), and the additional isotopes a portal takes (2793).</summary>
    public const int ModuleCapacitorAttribute = 6, PortalFuelTypeAttribute = 713, PortalFuelQuantityAttribute = 714, PortalMassFactorAttribute = 1001,
        PortalDurationAttribute = 1002, PortalPassengerAttribute = 3318, PortalAdditionalConsumptionAttribute = 2793;

    /// <summary>The mass of a ship (dogma attribute 4, the SDE mass) and the jump-through fatigue multiplier of a bridging hull (1972, 1.0 by default).</summary>
    public const int MassAttribute = 4, JumpThroughFatigueAttribute = 1972;

    /// <summary>The passenger flags of a portal (SDE): Titan 3319 (default 1: every ship but the capitals), Black Ops 3320 and industrial 3325 (default 0).</summary>
    public const int TitanPassengerAttribute = 3319, CovertPassengerAttribute = 3320, IndustrialPassengerAttribute = 3325;

    /// <summary>Whether a ship may pass through a portal that requires a passenger flag: the declared value of its own attribute when it has one, else the SDE default.</summary>
    public static bool IsPassenger(int flagAttribute, IReadOnlyDictionary<int, double> attributes) =>
        attributes.TryGetValue(flagAttribute, out var declared) ? declared > 0 : flagAttribute == TitanPassengerAttribute;

    /// <summary>
    /// The isotopes a jump through a bridge takes from the bridging ship: light years times the passenger's mass, the portal consumption mass factor and the
    /// hull's isotopes a light year (its jump drive's 868, less the skills), plus the portal's additional isotopes, rounded up (the wiki: "Fuelusage = Mass x
    /// Portal Consumption Mass Factor x Base cost x Skill mod x Distance in LY"; the base cost is the hull's 868).
    /// </summary>
    public static long BridgeFuelQuantity(double lightYears, double passengerMass, double massFactor, double fuelPerLightYear, double additional) =>
        (long)Math.Ceiling(Math.Round(lightYears * passengerMass * massFactor * fuelPerLightYear + additional, 6));

    /// <summary>
    /// Whether an economizer (SDE group 1299) fits a hull: its canFitShipGroup attributes (1298 and 1299, the groups it fits) name the hull's group. The
    /// wiki: only Jump Freighters and Rorquals, which are the two groups the SDE gives.
    /// </summary>
    public static bool EconomizerFits(int hullGroupId, IReadOnlyDictionary<int, double> economizerAttributes) =>
        (economizerAttributes.TryGetValue(1298, out var first) && (int)first == hullGroupId)
        || (economizerAttributes.TryGetValue(1299, out var second) && (int)second == hullGroupId);

    /// <summary>
    /// The fatigue and the activation cooldown a jump leaves, in milliseconds, from the fatigue that is left before the jump. The distance is the light years
    /// the hull counts (the jump's light years times its fatigue multiplier). The fatigue is recalculated after the cooldown, which takes a tenth of the fatigue
    /// as it was before the jump (the wiki's order).
    /// </summary>
    public static (long FatigueMs, long CooldownMs) AfterJump(long fatigueBeforeMs, double distance)
    {
        var steps = 1 + distance;
        var cooldown = Math.Min(CooldownCapMs, Math.Max((long)(MinimumCooldownPerDistanceMs * steps), fatigueBeforeMs / 10));
        var fatigue = Math.Min(FatigueCapMs, Math.Max((long)(MinimumFatiguePerDistanceMs * steps), (long)(fatigueBeforeMs * steps)));
        return (fatigue, cooldown);
    }
}
