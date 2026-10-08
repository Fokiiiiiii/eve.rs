namespace Eve.Services.Space.Effects;

/// <summary>The dogma attribute ids and the rounding the module effects share.</summary>
internal static class ModuleAttributes
{
    public const int Mass = 4;
    public const int MaxVelocity = 37;
    public const int SignatureRadius = 552;
    public const int SpeedFactor = 20;
    public const int SignatureBonus = 554;
    public const int SpeedBoostFactor = 567;
    public const int MassAddition = 796;

    public const int CapacitorNeed = 6;
    public const int Speed = 51;
    public const int MaxRange = 54;
    public const int DamageMultiplier = 64;
    public const int EmDamage = 114;
    public const int ExplosiveDamage = 116;
    public const int KineticDamage = 117;
    public const int ThermalDamage = 118;
    public const int ChargeRate = 56;
    public const int Falloff = 158;
    public const int TrackingSpeed = 160;
    public const int OptimalSigRadius = 620;
    public const int ShieldBonus = 68;
    /// <summary>The length an ECM drone's jam lasts on its target (its cycle is the effect's own duration, 20 s).</summary>
    public const int EcmJamDuration = 2822;
    public const int StructureDamageAmount = 83;
    public const int ArmorDamageAmount = 84;
    public const int AoeVelocity = 653;
    public const int AoeCloudSize = 654;
    public const int AoeDamageReductionFactor = 1353;
    public const int AoeDamageReductionSensitivity = 1354;
    public const int VortonArcRange = 3036;
    public const int VortonArcTargets = 3037;
    public const int DamageMultiplierBonusPerCycle = 2733;
    public const int DamageMultiplierBonusMax = 2734;
    public const int RepairMultiplierBonusPerCycle = 2796;
    public const int RepairMultiplierBonusMax = 2797;
    public const int PowerTransferAmount = 90;
    public const int EnergyNeutralizerAmount = 97;
    public const int FalloffEffectiveness = 2044;
    public const int NeutralizerSignatureResolution = 2451;
    public const int WebSpeedFactor = 20;
    public const int EmpFieldRange = 99;
    public const int EcmBurstRange = 142;
    public const int RangeDisruption = 351;
    public const int FalloffDisruption = 349;
    public const int TrackingDisruption = 767;
    public const int MissileVelocityBonus = 547;
    public const int ExplosionDelayBonus = 596;
    public const int AoeVelocityBonus = 847;
    public const int AoeCloudSizeBonus = 848;
    public const int ScanGravimetricStrengthBonus = 238;
    public const int ScanLadarStrengthBonus = 239;
    public const int ScanMagnetometricStrengthBonus = 240;
    public const int ScanRadarStrengthBonus = 241;
    public const int ScanGravimetricStrengthPercent = 1027;
    public const int ScanLadarStrengthPercent = 1028;
    public const int ScanMagnetometricStrengthPercent = 1029;
    public const int ScanRadarStrengthPercent = 1030;
    public const int PainterSignatureBonus = 554;
    public const int MaxTargetRangeBonus = 309;
    public const int ScanResolutionBonus = 566;
    public const int WarpScrambleStrength = 105;
    public const int MicroJumpDriveCooldown = 669;
    public const int MicroJumpDriveRange = 2066;

    /// <summary>The attributes that name the groups of the charges a module takes (chargeGroup1 to chargeGroup5).</summary>
    public static readonly int[] ChargeGroups = [604, 605, 606, 609, 610];

    /// <summary>The numbers the client is told are kept to six places, as the reference keeps them.</summary>
    public static double Six(double value) => Math.Round(value * 1_000_000, MidpointRounding.AwayFromZero) / 1_000_000;

    /// <summary>Round3 of the reference's <c>roundNumber</c>: the duration the client is told.</summary>
    public static double Three(double value) => Math.Round(value * 1_000, MidpointRounding.AwayFromZero) / 1_000;

    /// <summary>How many rounds a cycle burns: the module's <c>chargeRate</c>, one when it names none.</summary>
    public static long ChargesPerCycle(IReadOnlyDictionary<int, double> attributes) =>
        attributes.TryGetValue(ChargeRate, out var rate) && double.IsFinite(rate) ? Math.Max(1, (long)rate) : 1;
}
