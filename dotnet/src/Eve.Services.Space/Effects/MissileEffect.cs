using Eve.Dogma;
using Eve.Inventory;
using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// A launcher's package: a port of the missile half of <c>buildWeaponModuleSnapshot</c> (<c>buildMissileModuleSnapshot</c> of <c>space/combat/weaponDogma.js</c>, ADR 0009). A launcher
/// fires a missile in the place of a shot. What a missile does is in its charge after dogma (the skills of the pilot and the bonuses of the ship are in them): its damage times the pilot's
/// <c>missileDamageMultiplier</c>, how fast it flies (<c>maxVelocity</c>) and for how long before it explodes (<c>explosionDelay</c>, which with the speed is the range of the launcher), and the
/// explosion radius, velocity and damage reduction that decide how much of the damage a target takes. The cycle and the cost are the launcher's own values; a cycle burns the launcher's
/// <c>chargeRate</c> rounds. The pilot asks for <c>missileLaunching</c> (or <c>torpedoLaunching</c>), the effect of the charge; what runs is the launcher's own <c>useMissiles</c>, which is what the
/// client is told. The defender missiles (they shoot down other missiles), the guided bombs and the structures' launchers are not run yet.
/// </summary>
internal sealed class MissileEffect : ShipEffect
{
    /// <summary>The effect a launcher runs (<c>useMissiles</c>).</summary>
    public const string LauncherEffectName = "useMissiles";

    /// <summary>The effects of a charge that name the firing of a launcher: the client asks for one of them, the launcher runs <see cref="LauncherEffectName"/>.</summary>
    private static readonly string[] ClientNames = ["missileLaunching", "torpedoLaunching"];

    /// <summary>The attribute of a pilot that scales the damage of every missile (<c>missileDamageMultiplier</c>).</summary>
    private const int MissileDamageMultiplier = 212;

    private const int StructureHitPoints = 9;
    private const int Agility = 70;
    private const int Radius = 162;
    private const int ExplosionDelay = 281;
    private const int AoeVelocity = 653;
    private const int AoeCloudSize = 654;
    private const int AoeDamageReductionFactor = 1353;
    private const int AoeDamageReductionSensitivity = 1354;
    private const double DefaultReductionSensitivity = 5.5;

    private const int TorpedoGroup = 89;
    private const int XlTorpedoGroup = 476;
    private const int TorpedoLauncherGroup = 508;
    private const int XlTorpedoLauncherGroup = 524;

    private const string MissileDeployment = "effects.MissileDeployment";
    private const string TorpedoDeployment = "effects.TorpedoDeployment";

    /// <summary>The launchers that fire missiles at a ship: cruise, rocket, torpedo, light and heavy, rapid, heavy assault and the XL ones.</summary>
    private static readonly HashSet<int> LauncherGroups = [506, 507, 508, 509, 510, 511, 771, 1245, 1674, 524];

    public override IReadOnlyCollection<string> EffectNames { get; } = [LauncherEffectName];

    /// <summary>
    /// The effect a launcher runs when the pilot asks for the firing effect of its charge: the launcher's own, or null when the module is no launcher or the name is not one of the firing effects.
    /// </summary>
    public static EffectDef? LauncherEffectFor(TypeDef type, DogmaStatic statics, string requested) =>
        LauncherGroups.Contains(type.GroupId) && ClientNames.Any(n => n.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ? type.EffectIds.Select(statics.Effect).FirstOrDefault(e => e is not null && e.Name.Equals(LauncherEffectName, StringComparison.OrdinalIgnoreCase))
            : null;

    protected override bool Handles(ModuleEffectContext module) => LauncherGroups.Contains(module.Type.GroupId);

    /// <summary>A launcher with no missile in it is refused before it is asked for a target (the reference's order: <c>NO_AMMO</c> first, then the target).</summary>
    protected override string? Refusal(ModuleEffectContext module)
    {
        var charge = module.Snapshot.ChargeOf(module.Module);
        if (charge is null || charge.Quantity < ChargesPerCycle(module.Attributes))
        {
            return "NO_AMMO";
        }
        return module.TargetId <= 0 ? "TARGET_REQUIRED" : null;
    }

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var charge = module.Snapshot.ChargeOf(module.Module)!;
        var attributes = module.Attributes;
        var shot = module.World.Attributes(charge.Id);
        var pilot = module.World.Attributes(module.Snapshot.Character.Id);
        var multiplier = Math.Max(0, pilot.TryGetValue(MissileDamageMultiplier, out var m) && double.IsFinite(m) ? m : 1);
        double Damage(int attribute) => NpcArmament.Round6(Math.Max(0, shot.TryGetValue(attribute, out var damage) && double.IsFinite(damage) ? damage : 0) * multiplier);
        double Charge(int attribute, double fallback) => shot.TryGetValue(attribute, out var value) && double.IsFinite(value) ? value : fallback;

        var maxVelocity = Math.Max(0, NpcArmament.Round6(Charge(MaxVelocity, 0)));
        var flightTime = Math.Max(1, NpcArmament.Round6(Charge(ExplosionDelay, 1000)));
        // the ball of the missile: what the charge's type says of its size and weight, and its agility (a missile turns at once, so it is small); the reference's defaults where it says nothing
        double Positive(int attribute, double fallback) => Charge(attribute, 0) is var value and > 0 ? value : fallback;
        var missile = new SimMissile(
            charge.TypeId, module.Statics.Type(charge.TypeId)?.GroupId ?? 0, module.Statics.Type(charge.TypeId)?.Name ?? "Missile",
            Positive(Radius, 1), Positive(Mass, 1), Positive(Agility, 0.1), Math.Max(0, Charge(StructureHitPoints, 0)), maxVelocity, flightTime,
            Math.Max(1, NpcArmament.Round6(Charge(AoeCloudSize, 1))),
            Math.Max(0.001, NpcArmament.Round6(Charge(AoeVelocity, 0.001))),
            Math.Clamp(Charge(AoeDamageReductionFactor, 1), 0.000001, 1),
            Math.Max(0.000001, NpcArmament.Round6(Charge(AoeDamageReductionSensitivity, DefaultReductionSensitivity))));
        var range = NpcArmament.Round6(maxVelocity * (flightTime / 1000));

        var (duration, capacitor, _) = module.Cycle();
        var fx = FxOf(module, charge);
        var launcher = new SimTurret(
            module.Module.TypeId, fx, [Damage(EmDamage), Damage(ThermalDamage), Damage(KineticDamage), Damage(ExplosiveDamage)], range, 0, 0, 40_000,
            new SimTurretCharge(charge.TypeId, charge.Flag, charge.Quantity, ChargesPerCycle(attributes)), missile);
        // the client is told the cost and the cycle of the launcher as it starts and stops (and no range: a missile's is its own)
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, Speed, Three(duration))];
        return new SimEffect(module.ModuleId, module.Module.TypeId, module.Effect.Id, fx, duration, capacitor, Math.Max(1, module.Repeat), Math.Max(0, module.TargetId), null, null, told, told, launcher);
    }

    /// <summary>What the client plays: the effect's own, else the deployment of the missiles, or of the torpedoes for a torpedo launcher or charge (<c>resolveWeaponSpecialFxGUID</c>).</summary>
    private static string FxOf(ModuleEffectContext module, Item charge)
    {
        var guid = module.Effect.Guid.Trim();
        if (guid.Length > 0 && !guid.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return guid;
        }
        var torpedo = module.Type.GroupId is TorpedoLauncherGroup or XlTorpedoLauncherGroup
            || module.Statics.Type(charge.TypeId)?.GroupId is TorpedoGroup or XlTorpedoGroup;
        return torpedo ? TorpedoDeployment : MissileDeployment;
    }
}
