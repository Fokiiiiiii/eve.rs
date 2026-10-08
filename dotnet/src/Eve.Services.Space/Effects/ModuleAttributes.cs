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
    /// <summary>The damage a module has taken (dogma attribute <c>damage</c>): a module whose damage reaches its hit points is burned out.</summary>
    public const int ModuleDamage = 3;
    /// <summary>The hit points of a module (dogma attribute <c>hp</c>).</summary>
    public const int ModuleHitPoints = 9;
    public const int MaxTargetRangeBonus = 309;
    public const int ScanResolutionBonus = 566;
    public const int WarpScrambleStrength = 105;
    /// <summary>The time a module must wait after it stops before it starts again (the generic reactivation delay; 0 on most types, and the micro jump drive family's cooldown).</summary>
    public const int ModuleReactivationDelay = 669;
    public const int MicroJumpDriveRange = 2066;

    // Module rules the static data states as attributes (docs/project/TODO.md section 9; the names are the SDE's, verified by ModuleRuleAttributesTests).
    public const int WarpScrambleStatus = 104;
    public const int SiegeModeWarpStatus = 852;
    public const int DisallowDocking = 2354;
    public const int DisallowTethering = 2343;
    public const int DisallowEarlyDeactivation = 906;
    public const int DisallowActivateOnWarp = 1245;
    public const int DeactivateIfOffensive = 1934;
    public const int ActivationBlocked = 1349;
    public const int ActivationBlockedStrength = 1350;
    public const int ActivationRequiresActiveIndustrialCore = 3265;
    public const int ActivationRequiresActiveSiegeModule = 5426;
    public const int NetworkedSensorArrayDisallowCapitalMicroJump = 5700;
    public const int CanActivateInGateCloak = 3123;
    public const int MaxGroupActive = 763;
    public const int MaxGroupOnline = 978;
    public const int MaxGroupFitted = 1544;
    public const int MaxVelocityActivationLimit = 1334;
    public const int MinVelocityActivationLimit = 2608;
    /// <summary>The kind of resource a module consumes each cycle (the fuel's type); 0 on a module that burns none.</summary>
    public const int ConsumptionType = 713;
    /// <summary>How much of the resource each cycle consumes.</summary>
    public const int ConsumptionQuantity = 714;
    /// <summary>On a target: it cannot be assisted (a pilot's ship, or an NPC's type, that disallows assistance).</summary>
    public const int DisallowAssistance = 854;
    /// <summary>On a target: it cannot be hit by offensive modules (the reference's <c>disallowOffensiveAct</c>).</summary>
    public const int DisallowOffensiveModifiers = 872;
    /// <summary>On an assistance module: it cannot be activated against a target that disallows offensive modifiers.</summary>
    public const int DisallowAgainstEwImmuneTarget = 1798;
    public const int DisallowInHazardSystem = 5561;
    public const int DisallowActivateInForcefield = 1920;
    public const int RemoteRepairImpedance = 2116;
    public const int RemoteAssistanceImpedance = 2135;
    public const int SensorDampenerResistance = 2112;
    public const int WeaponDisruptionResistance = 2113;
    public const int TargetPainterResistance = 2114;
    public const int EcmResistance = 2253;

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
