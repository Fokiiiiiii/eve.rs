using Eve.Dogma;
using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The modules that are projected at another ship: what each does to its target every cycle, and whether that is done as the cycle ends (the remote armor and hull repairers, the
/// nosferatu) or as it begins (the shield booster, the capacitor transmitter, the neutralizer): <c>shipModuleRemote*</c> and the falloff energy modules of the reference's
/// <c>assistanceModuleRuntime.js</c> and <c>hostileModuleRuntime.js</c>; the timing of a cycle is the game's (EVE University: the armor repairs and the nosferatu come at the end).
/// To add a projected effect, add a line here: its name and what it does to the target.
/// </summary>
internal sealed class ProjectedEffect : ShipEffect
{
    /// <summary>What a projected effect does (from the module's attributes), whether it is done at the end of the cycle, and what the target's client calls it on its HUD (the reference's <c>jammingType</c>).</summary>
    private delegate (SimRemote What, bool AtEnd, string Jam) Spec(Func<int, double> value);

    private static double Amount(Func<int, double> value, int attribute) => Math.Max(0, NpcArmament.Round6(value(attribute)));

    private static double Signed(Func<int, double> value, int attribute) => NpcArmament.Round6(value(attribute));

    internal static MutadaptiveRepairSpool MutadaptiveSpool { get; } = new();

    // What each projected effect does, one delegate for each (a drone that carries the same effect as a module uses the same delegate, under its own effect name).
    private static readonly Spec ShieldBoost = v => (new SimRemote.Repair(SimLayer.Shield, Amount(v, ShieldBonus)), false, "shieldTransfer");
    private static readonly Spec ArmorRepair = v => (new SimRemote.Repair(SimLayer.Armor, Amount(v, ArmorDamageAmount)), true, "remoteArmorRepair");
    private static readonly Spec HullRepair = v => (new SimRemote.Repair(SimLayer.Structure, Amount(v, StructureDamageAmount)), true, "remoteHullRepair");
    private static readonly Spec Neutralize = v => (new SimRemote.Neutralize(Amount(v, EnergyNeutralizerAmount), Amount(v, NeutralizerSignatureResolution)), false, "ewEnergyNeut");
    // what lasts for as long as the effect runs: the percentages are signed as dogma has them (a web is negative), a scrambler holds a microwarpdrive too
    private static readonly Spec Web = v => (new SimRemote.Web(Signed(v, WebSpeedFactor)), false, "webify");
    private static readonly Spec Paint = v => (new SimRemote.Paint(Signed(v, PainterSignatureBonus)), false, "ewTargetPaint");
    private static readonly Spec SensorDamp = v => (new SimRemote.Damp(Signed(v, MaxTargetRangeBonus), Signed(v, ScanResolutionBonus)), false, "ewRemoteSensorDamp");
    // what the target's turrets lose or gain: signed range, falloff and tracking percentages from dogma
    private static readonly Spec TrackingDisrupt = v => (new SimRemote.Disrupt(Signed(v, RangeDisruption), Signed(v, FalloffDisruption), Signed(v, TrackingDisruption)), false, "ewTrackingDisrupt");
    // the target's HUD is told when a jam lands (the simulation rolls for it), not as the effect begins
    private static readonly Spec Jam = v => (new SimRemote.Jam(Amount(v, ScanGravimetricStrengthBonus), Amount(v, ScanLadarStrengthBonus), Amount(v, ScanMagnetometricStrengthBonus), Amount(v, ScanRadarStrengthBonus)), false, "");

    private static readonly IReadOnlyDictionary<string, Spec> Specs = new Dictionary<string, Spec>(StringComparer.OrdinalIgnoreCase)
    {
        ["shipModuleRemoteShieldBooster"] = ShieldBoost,
        ["npcEntityRemoteShieldBooster"] = ShieldBoost,
        ["shipModuleRemoteArmorRepairer"] = ArmorRepair,
        ["npcEntityRemoteArmorRepairer"] = ArmorRepair,
        // The simulation asks the host for each target-specific spool cycle using these SDE multipliers.
        ["ShipModuleRemoteArmorMutadaptiveRepairer"] = v => (new SimRemote.MutadaptiveRepair(SimLayer.Armor, Amount(v, ArmorDamageAmount), Signed(v, RepairMultiplierBonusPerCycle), Signed(v, RepairMultiplierBonusMax)), true, "remoteArmorRepair"),
        ["shipModuleRemoteHullRepairer"] = HullRepair,
        ["npcEntityRemoteHullRepairer"] = HullRepair,
        ["shipModuleRemoteCapacitorTransmitter"] = v => (new SimRemote.Transfer(Amount(v, PowerTransferAmount)), false, "energyTransfer"),
        ["energyNeutralizerFalloff"] = Neutralize,
        ["entityEnergyNeutralizerFalloff"] = Neutralize,
        ["energyNosferatuFalloff"] = v => (new SimRemote.Drain(Amount(v, PowerTransferAmount), Amount(v, NeutralizerSignatureResolution)), true, "ewEnergyVampire"),
        ["remoteWebifierFalloff"] = Web,
        ["remoteWebifierEntity"] = Web,
        ["remoteTargetPaintFalloff"] = Paint,
        ["remoteTargetPaintEntity"] = Paint,
        ["remoteSensorDampFalloff"] = SensorDamp,
        ["remoteSensorDampEntity"] = SensorDamp,
        ["remoteSensorBoostFalloff"] = v => (new SimRemote.Damp(Signed(v, MaxTargetRangeBonus), Signed(v, ScanResolutionBonus), Signed(v, ScanGravimetricStrengthPercent), Signed(v, ScanLadarStrengthPercent), Signed(v, ScanMagnetometricStrengthPercent), Signed(v, ScanRadarStrengthPercent)), false, "remoteSensorBoost"),
        ["warpScrambleBlockMWDWithNPCEffect"] = v => (new SimRemote.Scramble(Amount(v, WarpScrambleStrength), true), false, "warpScramblerMWD"),
        ["warpDisrupt"] = v => (new SimRemote.Scramble(Amount(v, WarpScrambleStrength), false), false, "warpScrambler"),
        ["shipModuleTrackingDisruptor"] = TrackingDisrupt,
        ["shipModuleRemoteTrackingComputer"] = v => (new SimRemote.Disrupt(Signed(v, RangeDisruption), Signed(v, FalloffDisruption), Signed(v, TrackingDisruption)), false, "ewTrackingDisrupt"),
        ["npcEntityWeaponDisruptor"] = TrackingDisrupt,
        // what the target's missiles lose: percentages of their speed, flight time and explosion velocity and radius (the radius grows), as dogma gives them
        ["shipModuleGuidanceDisruptor"] = v => (new SimRemote.GuidanceDisrupt(Signed(v, MissileVelocityBonus), Signed(v, ExplosionDelayBonus), Signed(v, AoeVelocityBonus), Signed(v, AoeCloudSizeBonus)), false, "ewGuidanceDisrupt"),
        ["remoteECMFalloff"] = Jam,
        ["entityECMFalloff"] = Jam,
        // the fighter abilities of the SDE (ADR 0015, WP-K): their attributes are the fighter type's own (the ids of the SDE attributes below)
        ["fighterAbilityWarpDisruption"] = v => (new SimRemote.Scramble(Amount(v, FighterWarpDisruptionStrength), false), false, "warpScrambler"),
        ["fighterAbilityStasisWebifier"] = v => (new SimRemote.Web(Signed(v, FighterWebSpeedPenalty)), false, "webify"),
        ["fighterAbilityEnergyNeutralizer"] = v => (new SimRemote.Neutralize(Amount(v, FighterNeutralizerAmount), 0), false, "ewEnergyNeut"),
        ["fighterAbilityTackle"] = v => (new SimRemote.Tackle(Amount(v, FighterTackleScramble), Signed(v, FighterTackleWeb)), false, "warpScrambler"),
        ["fighterAbilityECM"] = v => (new SimRemote.Jam(Amount(v, FighterGravimetricStrength), Amount(v, FighterLadarStrength), Amount(v, FighterMagnetometricStrength), Amount(v, FighterRadarStrength)), false, ""),
    };

    /// <summary>The fighter attributes the rows above read (SDE dogma attribute ids: warp disruption point strength 2205, stasis speed penalty 2184, neutralized amount 2211, ECM strengths 2246 to 2249).</summary>
    internal const int FighterWarpDisruptionStrength = 2205;
    internal const int FighterWebSpeedPenalty = 2184;
    internal const int FighterNeutralizerAmount = 2211;
    internal const int FighterGravimetricStrength = 2246;
    internal const int FighterLadarStrength = 2247;
    internal const int FighterMagnetometricStrength = 2248;
    internal const int FighterRadarStrength = 2249;
    internal const int FighterTackleScramble = 2425;
    internal const int FighterTackleWeb = 2242;

    public override IReadOnlyCollection<string> EffectNames { get; } = [.. Specs.Keys];

    /// <summary>The projected effects that ramp up while they are held on one target (their cycles are spooled by <see cref="MutadaptiveSpool"/>).</summary>
    private static readonly HashSet<string> Spooling = new(StringComparer.OrdinalIgnoreCase) { "ShipModuleRemoteArmorMutadaptiveRepairer" };

    /// <summary>A spooling module starts counting its ramp only once the simulation has taken its start (a refused start leaves the spool as it was).</summary>
    protected override void Accepted(ModuleEffectContext module)
    {
        if (Spooling.Contains(module.Effect.Name.Trim()))
        {
            MutadaptiveSpool.Start(module.ModuleId, module.TargetId);
        }
    }

    /// <summary>
    /// The remote effect a drone's type carries, when its effect is one of the projected effects above (the drones' rows of <see cref="Specs"/>): what a cycle lands on its
    /// target, how far it reaches (the effect record's range attribute, which is its optimal: a drone's effects have no falloff) and how long what it lands lasts (an ECM jam lasts
    /// its own duration, shorter than its cycle). Null for an effect that is not one of these. <paramref name="value"/> gives an attribute of the drone (0 when it has none).
    /// </summary>
    internal static SimDroneEffect? ForDrone(EffectDef effect, Func<int, double> value)
    {
        if (!Specs.TryGetValue(effect.Name.Trim(), out var spec))
        {
            return null;
        }
        var (what, atEnd, jam) = spec(value);
        var (cycle, _, _) = ShipEffect.CycleOf(effect, (attribute, fallback) => value(attribute) is var v && v != 0 ? v : fallback);
        var range = effect.RangeAttributeId > 0 ? effect.RangeAttributeId : MaxRange;
        var optimal = Math.Max(0, NpcArmament.Round6(value(range)));
        // a module's jam lasts its own duration (shorter than its cycle); a fighter's ECM lasts its cycle (its duration is the effect's, not the module's)
        var lasting = what is SimRemote.Jam && !effect.Name.Trim().StartsWith("fighterAbility", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1, NpcArmament.Round6(value(EcmJamDuration)))
            : cycle;
        return new SimDroneEffect(effect.Id, effect.Guid, cycle, lasting, new SimAction.Remote(what, optimal, 0, atEnd, jam));
    }

    /// <summary>
    /// A projected module's package: the cycle and its cost are the module's own values after dogma, and each cycle does the remote effect to the locked target, weakened over the falloff
    /// beyond the module's range. The client is told the cost, the cycle and the range as it starts and stops, as for any generic module.
    /// </summary>
    protected override SimEffect Build(ModuleEffectContext module)
    {
        var remote = Specs[module.Effect.Name.Trim()](attribute => module.World.Get(module.ModuleId, attribute));
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var optimal = Math.Max(0, NpcArmament.Round6(module.Value(MaxRange, 0)));
        var falloff = Math.Max(0, NpcArmament.Round6(module.Value(FalloffEffectiveness, 0)));
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, Three(duration)), new(module.ModuleId, MaxRange, optimal)];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), Math.Max(0, module.TargetId), null, null, told, told, null,
            new SimAction.Remote(remote.What, optimal, falloff, remote.AtEnd, remote.Jam));
    }
}

internal sealed class MutadaptiveRepairSpool
{
    private sealed record Entry(
        long TargetId,
        long CycleCount,
        long AtMs,
        double Amount,
        ulong CycleId,
        int CycleNumber,
        bool HasCycle,
        bool Active,
        bool StopAfterNextCycle);

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _byModule = [];

    public void Start(long moduleId, long targetId)
    {
        if (moduleId <= 0 || targetId <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_byModule.TryGetValue(moduleId, out var previous))
            {
                _byModule[moduleId] = previous with
                {
                    TargetId = targetId,
                    CycleCount = 0,
                    Active = true,
                    StopAfterNextCycle = false,
                };
            }
            else
            {
                _byModule[moduleId] = new Entry(targetId, 0, 0, 0, 0, 0, false, true, false);
            }
        }
    }

    public bool TryNextCycle(
        long moduleId,
        long targetId,
        ulong cycleId,
        int cycleNumber,
        double baseAmount,
        double bonusPerCycle,
        double maximumBonus,
        long atMs,
        long cycleDurationMs,
        out double amount)
    {
        amount = 0;
        if (moduleId <= 0 || targetId <= 0 || cycleDurationMs <= 0)
        {
            return false;
        }

        baseAmount = Math.Max(0, NpcArmament.Round6(baseAmount));
        bonusPerCycle = Math.Max(0, bonusPerCycle);
        maximumBonus = Math.Max(0, maximumBonus);

        lock (_gate)
        {
            if (!_byModule.TryGetValue(moduleId, out var previous)
                || !previous.Active
                || previous.TargetId != targetId)
            {
                return false;
            }
            if (previous.HasCycle && cycleId < previous.CycleId)
            {
                return false;
            }
            if (previous.HasCycle && cycleId == previous.CycleId)
            {
                if (previous.CycleCount == 0)
                {
                    return false;
                }
                amount = previous.Amount;
                return true;
            }
            if (previous.HasCycle && atMs < previous.AtMs)
            {
                return false;
            }
            if (previous.CycleCount > 0
                && cycleNumber <= previous.CycleNumber
                && atMs - previous.AtMs <= cycleDurationMs)
            {
                return false;
            }

            var consecutive = previous.CycleCount > 0
                && cycleNumber == previous.CycleNumber + 1
                && atMs >= previous.AtMs
                && atMs - previous.AtMs <= cycleDurationMs;
            // the bonus is applied to the cycle that lands and only then advanced (the reference's advanceAssistanceRepairSpool): the first delivery is x1.0, so
            // cycle n carries (n - 1) bonuses; the count stops one past the cycles that reach the maximum
            var maximumCycles = MaximumCycles(bonusPerCycle, maximumBonus) + 1;
            var cycleCount = consecutive
                ? Math.Min(previous.CycleCount + 1, maximumCycles)
                : 1;
            var bonus = Math.Min(maximumBonus, (cycleCount - 1) * bonusPerCycle);
            amount = NpcArmament.Round6(baseAmount * (1 + bonus));
            _byModule[moduleId] = previous with
            {
                CycleCount = cycleCount,
                AtMs = atMs,
                Amount = amount,
                CycleId = cycleId,
                CycleNumber = cycleNumber,
                HasCycle = true,
            };
            return true;
        }
    }

    public void StopAfterCurrentCycle(long moduleId)
    {
        lock (_gate)
        {
            if (_byModule.TryGetValue(moduleId, out var previous) && previous.Active)
            {
                _byModule[moduleId] = previous with { StopAfterNextCycle = true };
            }
        }
    }

    public void CompleteCycle(long moduleId, ulong cycleId)
    {
        lock (_gate)
        {
            if (_byModule.TryGetValue(moduleId, out var previous)
                && previous.Active
                && previous.HasCycle
                && previous.CycleId == cycleId)
            {
                _byModule[moduleId] = previous with
                {
                    CycleCount = 0,
                    Active = false,
                    StopAfterNextCycle = false,
                };
            }
        }
    }

    public void Reset(long moduleId)
    {
        lock (_gate)
        {
            if (_byModule.TryGetValue(moduleId, out var previous))
            {
                _byModule[moduleId] = previous with
                {
                    CycleCount = 0,
                    Active = false,
                    StopAfterNextCycle = false,
                };
            }
        }
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            _byModule.Clear();
        }
    }

    private static long MaximumCycles(double bonusPerCycle, double maximumBonus) =>
        bonusPerCycle > 0 && maximumBonus > 0
            ? Math.Max(1, (long)Math.Ceiling(maximumBonus / bonusPerCycle - 0.000000001))
            : 1;
}
