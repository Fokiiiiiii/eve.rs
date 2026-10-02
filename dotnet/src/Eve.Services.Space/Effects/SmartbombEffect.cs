using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// A smartbomb's package (<c>smartbombRuntime.js</c> of the reference, ADR 0009): the cycle and its cost are the module's own values after dogma, the field is its <c>empFieldRange</c> and
/// each cycle does its damage times the damage multiplier to every ship in it. It starts as one that hurts pilots: the host takes that back where the safety of the pilot would stop it.
/// </summary>
internal sealed class SmartbombEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["empWave"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var multiplier = Math.Max(0, module.Value(DamageMultiplier, 1));
        double Damage(int attribute) => NpcArmament.Round6(Math.Max(0, module.Value(attribute, 0)) * multiplier);
        var range = Math.Max(0, NpcArmament.Round6(module.Value(EmpFieldRange, 0)));
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, Three(duration)), new(module.ModuleId, EmpFieldRange, range)];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), 0, null, null, told, told, null,
            new SimAction.Smartbomb(range, [Damage(EmDamage), Damage(ThermalDamage), Damage(KineticDamage), Damage(ExplosiveDamage)], true));
    }
}
