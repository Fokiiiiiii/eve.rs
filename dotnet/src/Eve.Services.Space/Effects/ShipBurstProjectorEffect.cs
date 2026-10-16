using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// ST-17 d: the ship-fitted burst projectors (SDE group 842 "Burst Projectors", category 7: 27678, 40635, 40636, 40696-40699; 41350 stays unpublished and refused).
/// Their effect is the structure burst engine's (<see cref="StructureBurstProjectorEffect"/>): each cycle's pulses reach the ships in range that the Host grants for
/// that pulse, the pilot's own ship excluded. Their activation follows the ship area effects' policy (SpaceSessions.Modules): an ECM burst's family is refused in
/// high security unless the pilot's safety is none, and the offensive area effect is recorded with the crimewatch as a smartbomb's is.
/// </summary>
internal sealed class ShipBurstProjectorEffect : ShipEffect
{
    /// <summary>The SDE group of the burst projectors (842 "Burst Projectors", category 7 Module).</summary>
    internal const int BurstProjectorGroupId = 842;

    /// <summary>The SDE category of fitted modules (7 Module).</summary>
    internal const int ModuleCategoryId = 7;

    public override IReadOnlyCollection<string> EffectNames => StructureBurstProjectorEffect.Names;

    protected override bool Handles(ModuleEffectContext module) =>
        module.Type.CategoryId == ModuleCategoryId && module.Type.GroupId == BurstProjectorGroupId;

    protected override string? Refusal(ModuleEffectContext module)
    {
        if (!module.Type.Published)
        {
            return "MODULE_NOT_PUBLISHED";
        }
        if (!(module.Value(ProjectedEffect.DoomsdayAoeRange, 0) > 0))
        {
            return "UNSUPPORTED_MODULE";
        }
        return What(module) is null ? "UNSUPPORTED_EFFECT" : null;
    }

    protected override bool Implemented => true;

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var what = What(module)!;
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var range = Math.Max(0, NpcArmament.Round6(module.Value(ProjectedEffect.DoomsdayAoeRange, 0)));
        SimChange[] told =
        [
            new(module.ModuleId, CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, Three(duration)),
            new(module.ModuleId, ProjectedEffect.DoomsdayAoeRange, range),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), 0,
            null, null, told, told, null, new SimAction.StructureBurst(range, duration, what));
    }

    private static SimRemote? What(ModuleEffectContext module) =>
        ProjectedEffect.StructureBurstWhat(module.Effect.Name, attribute => module.World.Get(module.ModuleId, attribute));
}
