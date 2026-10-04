using Eve.Dogma;
using Eve.Inventory;
using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// A turret's package: a port of the turret half of <c>buildWeaponModuleSnapshot</c> (<c>space/combat/weaponDogma.js</c>, ADR 0009). The cycle, the cost, the range, the falloff and the
/// tracking are the module's own values after dogma (the skills of the pilot, the bonuses of the ship and what the loaded charge does to the module are in them); a shot's damage is the
/// damage of the charge (after dogma: what the module does to it) times the module's damage multiplier, or, for a gun that takes no charge, the module's own damage. The client is
/// told the cost, the cycle and the range as it starts and stops (<c>notifyGenericDerivedAttributesToSession</c>). The charge is told to the simulation, which spends its rounds cycle
/// by cycle (a cycle burns the module's <c>chargeRate</c>, one when it names none). A gun fires its own effect: <c>targetAttack</c> (the lasers and the starter guns) or
/// <c>projectileFired</c> (the projectile and hybrid guns); the other target effects of a turret type (the mining lasers, the disintegrators, barrage) are not run yet.
/// </summary>
internal sealed class TurretEffect : ShipEffect
{
    private const string TurretFitted = "turretfitted";

    public override IReadOnlyCollection<string> EffectNames { get; } = ["targetAttack", "projectileFired"];

    protected override bool Handles(ModuleEffectContext module) =>
        module.Type.EffectIds.Select(module.Statics.Effect).Any(e => e is not null && e.Name.Trim().Equals(TurretFitted, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A turret with nothing to shoot is refused before it is asked for a target (the reference's order): no charge in it, or fewer rounds than a cycle burns. A gun that takes no charge
    /// (it names no group of charges) and has damage of its own (the civilian guns) shoots with its own damage (<c>isChargeOptionalTurretWeapon</c>).
    /// </summary>
    protected override string? Refusal(ModuleEffectContext module)
    {
        var charge = module.Snapshot.ChargeOf(module.Module);
        return (charge is null ? !FiresWithoutCharge(module) : charge.Quantity < ChargesPerCycle(module.Attributes)) ? "NO_AMMO" : null;
    }

    private static bool FiresWithoutCharge(ModuleEffectContext module) =>
        ChargeGroups.All(a => !module.Attributes.TryGetValue(a, out var group) || (int)group <= 0)
        && new[] { EmDamage, ThermalDamage, KineticDamage, ExplosiveDamage }.Any(a => module.Attributes.TryGetValue(a, out var damage) && damage > 0);

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var charge = module.Snapshot.ChargeOf(module.Module);
        var attributes = module.Attributes;
        var damageSource = charge is null ? attributes : module.World.Attributes(charge.Id);
        var multiplier = Math.Max(0, module.Value(DamageMultiplier, 1));
        double Shot(int attribute) => NpcArmament.Round6(Math.Max(0, damageSource.TryGetValue(attribute, out var damage) && double.IsFinite(damage) ? damage : 0) * multiplier);
        var guid = NpcArmament.ActivationEffect(module.Type, module.Statics)?.Guid.Trim() ?? "";
        var fx = guid.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : guid;
        var duration = Math.Max(1, NpcArmament.Round6(module.Value(Speed, 1000)));
        var capacitor = Math.Max(0, NpcArmament.Round6(module.Value(CapacitorNeed, 0)));
        var optimal = Math.Max(0, NpcArmament.Round6(module.Value(MaxRange, 0)));
        var gun = new SimTurret(
            module.Module.TypeId, fx, [Shot(EmDamage), Shot(ThermalDamage), Shot(KineticDamage), Shot(ExplosiveDamage)], optimal, Math.Max(0, NpcArmament.Round6(module.Value(Falloff, 0))),
            Math.Max(0, NpcArmament.Round6(module.Value(TrackingSpeed, 0))), Math.Max(1, NpcArmament.Round6(module.Value(OptimalSigRadius, 40_000))),
            charge is null ? null : new SimTurretCharge(charge.TypeId, charge.Flag, charge.Quantity, ChargesPerCycle(attributes)));
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, Speed, Three(duration)), new(module.ModuleId, MaxRange, optimal)];
        return new SimEffect(module.ModuleId, module.Module.TypeId, module.Effect.Id, fx, duration, capacitor, Math.Max(1, module.Repeat), Math.Max(0, module.TargetId), null, null, told, told, gun);
    }
}
