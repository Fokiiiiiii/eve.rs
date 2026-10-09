using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The passive targeter (<c>targetPassively</c>): a target effect whose cycle locks the target it is aimed at, with the target not told (the simulation's
/// passive lock, <c>SimAction.PassiveLock</c>). The lock is the ordinary one (range, capacity and scan resolution apply); the target's refusal is the lock's.
/// </summary>
internal sealed class PassiveTargetingEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["targetPassively"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        SimChange[] told =
        [
            new(module.ModuleId, CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, Three(duration)),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor,
            Math.Max(1, module.Repeat), Math.Max(0, module.TargetId), null, null, told, told, Action: new SimAction.PassiveLock());
    }
}
