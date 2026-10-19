using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

// The Data Subverter (hackOrbital) and the projected ECCM have no body yet (the data and relic analyzers have one, DataAnalyzerEffect). Each is wired (fitted, onlined, registered in ModuleEffects.Default) and refused with
// PendingModuleEffect.NotImplemented; none of them cycles, takes capacitor or reports success. (The scanners have bodies: ScannerModuleEffects.cs.) The SDE rows are the published facts; the attributes listed are those
// the real body must read, on the module's values after dogma (ModuleEffectContext.Cycle reads duration and capacitor for every one of them).
//
// Shared for every effect below (SDE effect records of this build, guid as the static data gives it):
//   duration        attribute 73  (the cycle length; the effect's durationAttributeID)
//   capacitorNeed   attribute 6   (the cost of a cycle; the effect's dischargeAttributeID)
// Types are counted from the SDE join of typeDogma.dogmaEffects with dogmaEffects.na/// <summary>
/// <c>doHacking</c> (effect 1738, guid <c>effects.TargetScan</c>, category 2 activeTarget): the Data and Relic Analyzers. One cycle (repeat 1, the
/// module's duration and capacitor after dogma); the attempt itself opens in the host once the simulation accepts the start
/// (<see cref="SpaceSessions.AnalyzerEffectName"/>, <c>SpaceSessions.Hacking.cs</c>), and the container's checks are the host's. The virus values are the
/// module's <c>virusCoherence</c> 1909, <c>virusStrength</c> 1910 and <c>virusElementSlots</c> 1911 after dogma; <c>maxRange</c> 54 is the reach of the window.
/// </summary>
internal sealed class DataAnalyzerEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames => ["doHacking"];

    /// <summary>An analyzer with no reach is refused as unsupported, not run.</summary>
    protected override string? Refusal(ModuleEffectContext module) => module.Value(MaxRange, 0) > 0 ? null : "UNSUPPORTED_EFFECT";

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        SimChange[] told =
        [
            new(module.ModuleId, CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, Three(duration)),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, 1,
            Math.Max(0, module.TargetId), null, null, told, told);
    }
}

/// <summary>
/// <c>hackOrbital</c> (effect 4773, guid <c>effects.TargetScan</c>, category 2): the Data Subverter I (type 3793, unpublished, group 538 Data Miners). It has no
/// effect name; the body is not written yet and needs the client's specification first.
/// </summary>
internal sealed class DataSubverterEffect : PendingModuleEffect
{
    public DataSubverterEffect() : base("hackOrbital") { }
}

/// <summary>
/// <c>scanStrengthTargetPercentBonus</c> (effect 2246, no guid, category 2 activeTarget): the projected ECCMs (7 types in group 289 Projected ECCM and group 1717
/// Unpublished Structure Modules; none published, the Structure ECCM Projector is a structure module). It is a target effect, so it needs a locked target like the
/// other projectors. The SDE effect carries four <c>modifierInfo</c> entries that change the target's scan strengths by the module's percent attributes, as the
/// self-applied ECCM does. The effect's rangeAttributeID is 54 (<c>maxRange</c>, 20 km on ECCM Projector I) and its falloffAttributeID is 158 (<c>falloff</c>).
/// Reads: <c>maxRange</c> 54, <c>falloff</c> 158, <c>duration</c> and <c>capacitorNeed</c> (the shared attributes above).
/// 86-100, the modifier entries at 190-210), and it names no handler by this effect name.
/// </summary>
internal sealed class ScanStrengthTargetEffect : PendingModuleEffect
{
    public ScanStrengthTargetEffect() : base("scanStrengthTargetPercentBonus") { }
}
