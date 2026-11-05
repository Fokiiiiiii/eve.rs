using Eve.Dogma;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Effects;

internal sealed class AncillaryRemoteShieldBoosterEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["shipModuleAncillaryRemoteShieldBooster"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var charge = module.Snapshot.ChargeOf(module.Module);
        var chargeRate = Math.Max(1L, (long)module.Value(ModuleAttributes.ChargeRate, 1));
        var amount = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.ShieldBonus, 0)));
        var optimal = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.MaxRange, 0)));
        var falloff = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.FalloffEffectiveness, 0)));
        var remote = new SimAction.Remote(
            new SimRemote.Repair(SimLayer.Shield, amount), optimal, falloff, false, "shieldTransfer");
        SimAction action = charge is { Quantity: > 0 }
            ? new SimAction.Charged(remote, null, charge.TypeId, module.Module.Flag, (int)chargeRate, charge.Quantity)
            : remote;
        SimChange[] told =
        [
            new(module.ModuleId, ModuleAttributes.CapacitorNeed, charge is { Quantity: > 0 } ? 0 : capacitor),
            new(module.ModuleId, durationAttribute, ModuleAttributes.Three(duration)),
            new(module.ModuleId, ModuleAttributes.MaxRange, optimal),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid,
            duration, charge is { Quantity: > 0 } ? 0 : capacitor, Math.Max(1, module.Repeat), Math.Max(0, module.TargetId),
            null, null, told, told, null, action, FallbackCapacitorNeed: capacitor);
    }
}

/// <summary>Starts effects whose activation behavior is defined by the SDE's Dogma modifiers.</summary>
