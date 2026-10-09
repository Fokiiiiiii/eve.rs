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
/// <c>projectileFired</c> (the projectile and hybrid guns); a precursor weapon builds its damage up on the target it keeps shooting at (its module's bonus attributes give the <see cref="SimSpool"/>); a vorton projector's
/// <c>ChainLightning</c> explodes on its target without a roll (<see cref="SimAoe"/>) and arcs to the ships near it (<see cref="SimArc"/>); the mining lasers and barrage are not run yet (barrage is a pending handler: it is refused, not fired as a plain shot).
/// </summary>
internal sealed class TurretEffect : ShipEffect
{
    private const string TurretFitted = "turretfitted";

    /// <summary>Gunnery (typeGunnery): a tracking disruptor or computer reaches a turret whose type requires it (FP-10).</summary>
    private const int GunnerySkill = 3300;

    public override IReadOnlyCollection<string> EffectNames { get; } = ["targetAttack", "projectileFired", "targetDisintegratorAttack", "ChainLightning"];

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

    /// <summary>
    /// A weapon that builds up its damage declares the bonus a cycle adds and the most it reaches (a precursor weapon's <c>damageMultiplierBonusPerCycle</c> and
    /// <c>damageMultiplierBonusMax</c>, after dogma). It is read from the attributes the module carries, not from its type; a module that carries no bonus has no spool.
    /// </summary>
    private static SimSpool? SpoolOf(IReadOnlyDictionary<int, double> attributes) =>
        attributes.TryGetValue(DamageMultiplierBonusPerCycle, out var perCycle) && double.IsFinite(perCycle) && perCycle > 0
            ? new SimSpool(
                NpcArmament.Round6(perCycle),
                NpcArmament.Round6(attributes.TryGetValue(DamageMultiplierBonusMax, out var max) && double.IsFinite(max) ? Math.Max(0, max) : 0))
            : null;

    /// <summary>
    /// A shot that explodes on its target instead of rolling to hit: a weapon that carries an explosion and no tracking speed (a vorton projector). The
    /// explosion's cloud and velocity and the damage reduction are the module's own values after dogma, with the reference's defaults where a value is absent.
    /// </summary>
    private static SimAoe? AoeOf(IReadOnlyDictionary<int, double> attributes)
    {
        if (attributes.TryGetValue(TrackingSpeed, out var tracking) && double.IsFinite(tracking) && tracking > 0
            || !attributes.TryGetValue(AoeCloudSize, out var cloud) || !double.IsFinite(cloud))
        {
            return null;
        }
        return new SimAoe(
            Math.Max(0.000001, NpcArmament.Round6(cloud)),
            Math.Max(0.000001, NpcArmament.Round6(AttributeOr(attributes, AoeVelocity, 1))),
            NpcArmament.Round6(AttributeOr(attributes, AoeDamageReductionFactor, 1)),
            NpcArmament.Round6(AttributeOr(attributes, AoeDamageReductionSensitivity, 5.5)));
    }

    /// <summary>A weapon whose module declares a range and a number of targets for its arc chains to them (a vorton projector's arc, after dogma).</summary>
    private static SimArc? ArcOf(IReadOnlyDictionary<int, double> attributes) =>
        attributes.TryGetValue(VortonArcRange, out var range) && double.IsFinite(range) && range > 0
        && attributes.TryGetValue(VortonArcTargets, out var targets) && double.IsFinite(targets) && targets >= 1
            ? new SimArc(NpcArmament.Round6(range), (int)targets)
            : null;

    private static double AttributeOr(IReadOnlyDictionary<int, double> attributes, int attribute, double fallback) =>
        attributes.TryGetValue(attribute, out var value) && double.IsFinite(value) ? value : fallback;

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
            charge is null ? null : new SimTurretCharge(charge.TypeId, charge.Flag, charge.Quantity, ChargesPerCycle(attributes), Reloads: module.Value(ReloadTime, 0) > 0), Spool: SpoolOf(attributes),
            Aoe: AoeOf(attributes), Arc: ArcOf(attributes), RequiresGunnery: module.Type.RequiredSkills.Contains(GunnerySkill));
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, Speed, Three(duration)), new(module.ModuleId, MaxRange, optimal)];
        return new SimEffect(module.ModuleId, module.Module.TypeId, module.Effect.Id, fx, duration, capacitor, Math.Max(1, module.Repeat), Math.Max(0, module.TargetId), null, null, told, told, gun);
    }
}

/// <summary>
/// A turret's barrage (<c>barrage</c>; its SDE falloff 328 and damage multiplier 326 are not read). It was run as a plain shot, which the reference does not give: it is a
/// pending handler, so the activation is refused as not implemented and the effect stays uncovered (docs/project/TODO.md section 9).
/// </summary>
internal sealed class BarragePendingEffect : PendingModuleEffect
{
    public BarragePendingEffect() : base("barrage")
    {
    }
}
