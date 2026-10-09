using Eve.Ships;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The mining laser (<c>miningLaser</c>): an ore or ice laser, mining the rock it is locked on. The yield is decided by the host when a cycle ends (the simulation
/// parks the cycle until then); the numbers are the module's own, read by attribute name (<see cref="MiningProfile"/>), as the reference reads them. The family is
/// ice when the module's name says so, as the reference does (<c>miningDogma.js</c> <c>resolveMiningFamily</c>), otherwise ore.
/// </summary>
internal sealed class MiningLaserEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["miningLaser"];

    // the frequency mining lasers take a crystal, whose yield and volatility are not wired yet: refused by name until they are (decisions M3, M9)
    protected override string? Refusal(ModuleEffectContext module) =>
        module.Statics.GroupName(module.Type.GroupId).Equals("Frequency Mining Laser", StringComparison.OrdinalIgnoreCase) ? PendingModuleEffect.NotImplemented : null;

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var profile = MiningProfile.From(module.World, module.Statics, module.ModuleId);
        var name = module.Statics.Type(module.Module.TypeId)?.Name ?? string.Empty;
        var family = name.Contains("ice", StringComparison.OrdinalIgnoreCase) ? SimMiningFamilies.Ice : SimMiningFamilies.Ore;
        SimChange[] told =
        [
            new(module.ModuleId, CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, Three(duration)),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor,
            Math.Max(1, module.Repeat), Math.Max(0, module.TargetId), null, null, told, told,
            Action: new SimAction.Mining(
                family, profile.MiningAmountM3, profile.WasteVolumeMultiplier, profile.WasteProbability,
                profile.CritProbability, profile.CritQuantityMultiplier));
    }
}
