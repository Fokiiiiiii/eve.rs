using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The salvager (<c>salvaging</c>): a cycle that tries to salvage the wreck it is locked on, through the shared host work cycle. The chance is the wreck's
/// <c>accessDifficulty</c> plus the module's <c>accessDifficultyBonus</c> (the same rule as a salvage drone), decided by the host when the cycle ends. A target
/// that is not a wreck is refused by the host (<c>NO_WRECK</c>), so this handler only builds the cycle.
/// </summary>
internal sealed class SalvagerEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["salvaging"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var bonusAttribute = module.Statics.AttributeId("accessDifficultyBonus");
        var bonus = bonusAttribute is { } id ? module.Value(id, 0) : 0d;
        SimChange[] told =
        [
            new(module.ModuleId, CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, Three(duration)),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor,
            Math.Max(1, module.Repeat), Math.Max(0, module.TargetId), null, null, told, told,
            Action: new SimAction.Salvage(Math.Max(0, bonus)));
    }
}
