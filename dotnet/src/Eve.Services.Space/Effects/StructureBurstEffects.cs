using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The Standup burst projectors (ST-8b): the structure modules (category 66) whose default effect is a burst family (doomsdayAOE ECM, sensor damp, web,
/// paint, warp disruption bubble, weapon disruption, energy neutralization). The projected effect of the family is what each cycle lands on the ships in its
/// published in the SDE while their effects are not: a published type is not refused for its effect's publication. A ship-fitted burst projector (not a structure
/// module) is not this handler's and stays refused.
/// </summary>
internal sealed class StructureBurstProjectorEffect : ShipEffect
{
    /// <summary>The effect names this handler serves for structure modules (not for the ship-fitted variants of the same names, which have no handler).</summary>
    internal static readonly string[] Names =
    [
        "doomsdayAOEBubble",
        "doomsdayAOEDamp",
        "doomsdayAOEECM",
        "doomsdayAOENeut",
        "doomsdayAOEPaint",
        "doomsdayAOETrack",
        "doomsdayAOEWeb",
    ];

    public override IReadOnlyCollection<string> EffectNames => Names;

    protected override bool Handles(ModuleEffectContext module) => module.Type.CategoryId == SuperWeaponCategories.Structure;

    protected override string? Refusal(ModuleEffectContext module)
    {
        if (!module.Type.Published)
        {
            return "MODULE_NOT_PUBLISHED";
        }
        if (!StructureDamageModuleRules.FitsStructureType(module))
        {
            return "REFUSED";
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
