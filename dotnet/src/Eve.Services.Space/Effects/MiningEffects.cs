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

    // a frequency mining laser mines with the crystal it has loaded: its crystal is checked against the rock at activation (SpaceSessions), its volatility is
    // rolled per cycle (SpaceContainers). Without a crystal it has nothing to mine with, and says so before it asks for a target
    protected override string? Refusal(ModuleEffectContext module) =>
        module.Statics.GroupName(module.Type.GroupId).Equals("Frequency Mining Laser", StringComparison.OrdinalIgnoreCase)
        && module.Snapshot.ChargeOf(module.Module) is not { Quantity: > 0 } ? "NO_AMMO" : null;

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var name = module.Statics.Type(module.Module.TypeId)?.Name ?? string.Empty;
        return MiningEffect.Build(module, name.Contains("ice", StringComparison.OrdinalIgnoreCase) ? SimMiningFamilies.Ice : SimMiningFamilies.Ore);
    }
}

/// <summary>
/// The gas harvester (<c>miningClouds</c>): the cycle of a harvestable gas cloud (<see cref="MiningTargets"/>), the lasers' own cycle with the gas family. The
/// cloud yields its compressed gas, and the host settles it as it settles a rock.
/// </summary>
internal sealed class GasHarvesterEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["miningClouds"];

    protected override SimEffect Build(ModuleEffectContext module) => MiningEffect.Build(module, SimMiningFamilies.Gas);
}

/// <summary>The cycle a mining module runs (a laser or a harvester): its numbers are the module's own, read by attribute name, as the reference reads them.</summary>
internal static class MiningEffect
{
    public static SimEffect Build(ModuleEffectContext module, byte family)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var profile = MiningProfile.From(module.World, module.Statics, module.ModuleId);
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
