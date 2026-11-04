using Eve.Dogma;
using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The cynosural field generators (group 658; effect <c>cynosuralGeneration</c>, 2857). The generator runs one cycle of its activation time (the SDE's duration:
/// (<see cref="SimAction.CynoField"/>), and the field lives its explosionDelay. The cycle burns the generator's fuel (713, 714) through the shared mechanism
/// (<c>BurnsFuel</c>, set by <see cref="ShipEffect"/>). The generator's own modifiers (its ship speed changes while it runs) are the dogma modifiers, as they are
/// for any module (<see cref="DogmaModifierEffect.BuildModifier"/>).
/// The lighting rules (a fleet member, not in high-security space) are checked before activation (<c>SpaceSessions.Modules.cs</c>); the speed ceiling is the
/// rule of attribute 1334.
/// </summary>
internal sealed class CynoGeneratorEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["cynosuralGeneration"];

    protected override string? Refusal(ModuleEffectContext module)
    {
        // no body without the field type's radius and lifetime in the static data
        var field = CynoFieldTypes.Of(module.Statics, CynoFieldTypes.FieldTypeOf(module.Module.TypeId));
        return field is { } known
            && module.Statics.Type(known.TypeId)!.Attributes.TryGetValue(CynoFieldTypes.Lifetime, out var lifetime) && lifetime > 0
            ? null
            : PendingModuleEffect.NotImplemented;
    }

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var fieldTypeId = CynoFieldTypes.FieldTypeOf(module.Module.TypeId);
        var field = CynoFieldTypes.Of(module.Statics, fieldTypeId)!.Value;
        var lifetime = module.Statics.Type(fieldTypeId)!.Attributes[CynoFieldTypes.Lifetime];
        var ball = new SimDeployable(field.TypeId, field.GroupId, module.Statics.Type(field.TypeId)?.Name ?? "Cynosural Field", field.Radius);
        // one cycle lights the field once: the generator stops after it (its repeat is one), and the fuel is taken before that cycle (BurnsFuel)
        return DogmaModifierEffect.BuildModifier(module) with
        {
            Action = new SimAction.CynoField(ball, lifetime),
            Repeat = 1,
        };
    }
}
