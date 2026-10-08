using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>
/// A capacitor booster (<c>powerBooster</c>): each cycle moves the amount its loaded charge carries into the capacitor of the pilot's own ship (a self
/// transfer). The cycle spends the charge, and the charge's units limit how many cycles the booster repeats. With no charge loaded it is refused
/// (NO_AMMO) before anything else is asked of it.
/// </summary>
internal sealed class PowerBoosterEffect : ShipEffect
{
    private const int CapacitorBonus = 67;

    public override IReadOnlyCollection<string> EffectNames { get; } = ["powerBooster"];

    protected override string? Refusal(ModuleEffectContext module) =>
        module.Snapshot.ChargeOf(module.Module) is { Quantity: > 0 } ? null : "NO_AMMO";

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var charge = module.Snapshot.ChargeOf(module.Module)
            ?? throw new InvalidOperationException("A capacitor booster is built only with a charge loaded: its refusal is checked first.");
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var amount = Math.Max(0, ModuleAttributes.Six(module.World.Get(charge.Id, CapacitorBonus)));
        var chargeRate = Math.Max(1L, (long)module.Value(ModuleAttributes.ChargeRate, 1));
        var chargedCycles = 1 + ((charge.Quantity - 1) / chargeRate);
        var repeat = (int)Math.Min(Math.Max(1, module.Repeat), chargedCycles);
        SimChange[] told = [new(module.ModuleId, ModuleAttributes.CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, ModuleAttributes.Three(duration))];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, repeat, module.Snapshot.Ship.Id, null, null, told, told, null,
            new SimAction.Charged(new SimAction.Remote(new SimRemote.Transfer(amount), 0, 0, false, "energyTransfer"), null,
                charge.TypeId, module.Module.Flag, (int)chargeRate, charge.Quantity));
    }
}
