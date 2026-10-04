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

    private static readonly IReadOnlyDictionary<string, Spec> Specs = new Dictionary<string, Spec>(StringComparer.OrdinalIgnoreCase)
    {
        ["shipModuleRemoteShieldBooster"] = v => (new SimRemote.Repair(SimLayer.Shield, Amount(v, ShieldBonus)), false, "shieldTransfer"),
        ["shipModuleRemoteArmorRepairer"] = v => (new SimRemote.Repair(SimLayer.Armor, Amount(v, ArmorDamageAmount)), true, "remoteArmorRepair"),
        ["shipModuleRemoteHullRepairer"] = v => (new SimRemote.Repair(SimLayer.Structure, Amount(v, StructureDamageAmount)), true, "remoteHullRepair"),
        ["shipModuleRemoteCapacitorTransmitter"] = v => (new SimRemote.Transfer(Amount(v, PowerTransferAmount)), false, "energyTransfer"),
        ["energyNeutralizerFalloff"] = v => (new SimRemote.Neutralize(Amount(v, EnergyNeutralizerAmount), Amount(v, NeutralizerSignatureResolution)), false, "ewEnergyNeut"),
        ["energyNosferatuFalloff"] = v => (new SimRemote.Drain(Amount(v, PowerTransferAmount), Amount(v, NeutralizerSignatureResolution)), true, "ewEnergyVampire"),
        // what lasts for as long as the effect runs: the percentages are signed as dogma has them (a web is negative), a scrambler holds a microwarpdrive too
        ["remoteWebifierFalloff"] = v => (new SimRemote.Web(Signed(v, WebSpeedFactor)), false, "webify"),
        ["remoteTargetPaintFalloff"] = v => (new SimRemote.Paint(Signed(v, PainterSignatureBonus)), false, "ewTargetPaint"),
        ["remoteSensorDampFalloff"] = v => (new SimRemote.Damp(Signed(v, DampenerRangeBonus), Signed(v, DampenerResolutionBonus)), false, "ewRemoteSensorDamp"),
        ["warpScrambleBlockMWDWithNPCEffect"] = v => (new SimRemote.Scramble(Amount(v, WarpScrambleStrength), true), false, "warpScramblerMWD"),
        ["warpDisrupt"] = v => (new SimRemote.Scramble(Amount(v, WarpScrambleStrength), false), false, "warpScrambler"),
        // what the target's turrets lose: percentages of their range, falloff and tracking speed, as dogma gives them (negative)
        ["shipModuleTrackingDisruptor"] = v => (new SimRemote.Disrupt(Signed(v, RangeDisruption), Signed(v, FalloffDisruption), Signed(v, TrackingDisruption)), false, "ewTrackingDisrupt"),
        // what the target's missiles lose: percentages of their speed, flight time and explosion velocity and radius (the radius grows), as dogma gives them
        ["shipModuleGuidanceDisruptor"] = v => (new SimRemote.GuidanceDisrupt(Signed(v, MissileVelocityBonus), Signed(v, ExplosionDelayBonus), Signed(v, AoeVelocityBonus), Signed(v, AoeCloudSizeBonus)), false, "ewGuidanceDisrupt"),
        // the target's HUD is told when a jam lands (the simulation rolls for it), not as the effect begins
        ["remoteECMFalloff"] = v => (new SimRemote.Jam(Amount(v, GravimetricJam), Amount(v, LadarJam), Amount(v, MagnetometricJam), Amount(v, RadarJam)), false, ""),
    };

    public override IReadOnlyCollection<string> EffectNames { get; } = [.. Specs.Keys];

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
