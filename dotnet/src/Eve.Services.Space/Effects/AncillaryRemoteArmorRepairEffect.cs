using Eve.Dogma;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Effects;

internal sealed class AncillaryRemoteArmorRepairEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["shipModuleAncillaryRemoteArmorRepairer"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var charge = module.Snapshot.ChargeOf(module.Module);
        var chargeRate = Math.Max(1L, (long)module.Value(ModuleAttributes.ChargeRate, 1));
        var amount = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.ArmorDamageAmount, 0)));
        var chargedAmount = FueledArmorRepairEffect.ChargedAmount(module);
        var optimal = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.MaxRange, 0)));
        var falloff = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.FalloffEffectiveness, 0)));
        SimAction Remote(double repair) =>
            new SimAction.Remote(new SimRemote.Repair(SimLayer.Armor, repair), optimal, falloff, true, "remoteArmorRepair");
        var fallback = Remote(amount);
        SimAction action = charge is { Quantity: > 0 }
            ? new SimAction.Charged(Remote(chargedAmount), fallback, charge.TypeId, module.Module.Flag, (int)chargeRate, charge.Quantity)
            : fallback;
        SimChange[] told =
        [
            new(module.ModuleId, ModuleAttributes.CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, ModuleAttributes.Three(duration)),
            new(module.ModuleId, ModuleAttributes.MaxRange, optimal),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid,
            duration, capacitor, Math.Max(1, module.Repeat), Math.Max(0, module.TargetId),
            null, null, told, told, null, action);
    }
}
