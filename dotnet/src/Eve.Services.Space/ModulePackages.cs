using Eve.Dogma;
using Eve.Inventory;
using Eve.Npc;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// What the simulation needs to run an effect of a module: the length of its cycle and what a cycle costs, the numbers of the ship with the effect and
/// without it, and the values the client is told at the start and the end. The rules are the client's; the numbers are dogma's (<c>Eve.Dogma</c>).
/// </summary>
internal static class ModulePackages
{
    private const int Mass = 4;
    private const int MaxVelocity = 37;
    private const int SignatureRadius = 552;
    private const int SpeedFactor = 20;
    private const int SignatureBonus = 554;
    private const int SpeedBoostFactor = 567;
    private const int MassAddition = 796;

    private const int CapacitorNeed = 6;
    private const int Speed = 51;
    private const int MaxRange = 54;
    private const int DamageMultiplier = 64;
    private const int EmDamage = 114;
    private const int ExplosiveDamage = 116;
    private const int KineticDamage = 117;
    private const int ThermalDamage = 118;
    private const int ChargeRate = 56;
    private const int Falloff = 158;
    private const int TrackingSpeed = 160;
    private const int OptimalSigRadius = 620;

    /// <summary>The attributes that name the groups of the charges a module takes (chargeGroup1 to chargeGroup5).</summary>
    private static readonly int[] ChargeGroups = [604, 605, 606, 609, 610];

    private const string Afterburner = "moduleBonusAfterburner";
    private const string Microwarpdrive = "moduleBonusMicrowarpdrive";
    private const string TargetAttack = "targetAttack";
    private const string ProjectileFired = "projectileFired";
    private const string TurretFitted = "turretfitted";

    /// <summary>Why a package cannot be made, or the package.</summary>
    public readonly record struct Result(SimEffect? Effect, string? Reason, int ModuleType, string ModuleName);

    /// <summary>
    /// A propulsion module's numbers. The ship is heavier by the module's mass addition, and faster by the module's speed factor (a percentage) times its
    /// speed boost factor divided by that new mass; a microwarpdrive also makes the ship look bigger (its signature bonus, a percentage).
    /// </summary>
    public static (SimNumbers On, SimNumbers Off) Propulsion(double mass, double maxVelocity, double signatureRadius, double massAddition, double speedFactor, double speedBoostFactor, double signatureBonus)
    {
        var heavier = mass + massAddition;
        var boost = heavier > 0 ? speedFactor / 100.0 * speedBoostFactor / heavier : 0.0;
        return (
            new SimNumbers(Six(heavier), Six(maxVelocity * (1 + boost)), Six(signatureRadius * (1 + signatureBonus / 100.0))),
            new SimNumbers(Six(mass), Six(maxVelocity), Six(signatureRadius)));
    }

    /// <summary>The numbers the client is told are kept to six places, as the reference keeps them.</summary>
    private static double Six(double value) => Math.Round(value * 1_000_000, MidpointRounding.AwayFromZero) / 1_000_000;

    private static bool IsTurret(TypeDef type, DogmaStatic statics) =>
        type.EffectIds.Select(statics.Effect).Any(e => e is not null && e.Name.Trim().Equals(TurretFitted, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>isChargeOptionalTurretWeapon</c>: a turret that takes no charge (it names no group of charges) and has damage of its own (the civilian guns) shoots
    /// with its own damage.
    /// </summary>
    private static bool FiresWithoutCharge(DogmaWorld world, long moduleId)
    {
        var attributes = world.Attributes(moduleId);
        return ChargeGroups.All(a => !attributes.TryGetValue(a, out var group) || (int)group <= 0)
            && new[] { EmDamage, ThermalDamage, KineticDamage, ExplosiveDamage }.Any(a => attributes.TryGetValue(a, out var damage) && damage > 0);
    }

    /// <summary>Round3 of the reference's <c>roundNumber</c>: the duration the client is told.</summary>
    private static double Three(double value) => Math.Round(value * 1_000, MidpointRounding.AwayFromZero) / 1_000;

    /// <summary>
    /// A turret's package: a port of the turret half of <c>buildWeaponModuleSnapshot</c> (<c>space/combat/weaponDogma.js</c>, ADR 0009). The cycle, the cost, the range,
    /// the falloff and the tracking are the module's own values after dogma (the skills of the pilot, the bonuses of the ship and what the loaded charge does to the module
    /// are in them); a shot's damage is the damage of the charge (after dogma: what the module does to it) times the module's damage multiplier, or, for a gun that takes
    /// no charge, the module's own damage. The client is told the cost, the cycle and the range as it starts and stops (<c>notifyGenericDerivedAttributesToSession</c>).
    /// The charge is told to the simulation, which spends its rounds cycle by cycle (a cycle burns the module's <c>chargeRate</c>, one when it names none).
    /// </summary>
    private static SimEffect Turret(
        DogmaWorld world, DogmaStatic statics, int moduleType, TypeDef type, EffectDef effect, long moduleId, long targetId, int repeat, Item? charge)
    {
        var attributes = world.Attributes(moduleId);
        double Value(int attribute, double fallback) => attributes.TryGetValue(attribute, out var value) && double.IsFinite(value) ? value : fallback;
        var damageSource = charge is null ? attributes : world.Attributes(charge.Id);
        var multiplier = Math.Max(0, Value(DamageMultiplier, 1));
        double Shot(int attribute) => NpcArmament.Round6(Math.Max(0, damageSource.TryGetValue(attribute, out var damage) && double.IsFinite(damage) ? damage : 0) * multiplier);
        var guid = NpcArmament.ActivationEffect(type, statics)?.Guid.Trim() ?? "";
        var fx = guid.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : guid;
        var duration = Math.Max(1, NpcArmament.Round6(Value(Speed, 1000)));
        var capacitor = Math.Max(0, NpcArmament.Round6(Value(CapacitorNeed, 0)));
        var optimal = Math.Max(0, NpcArmament.Round6(Value(MaxRange, 0)));
        var gun = new SimTurret(
            moduleType, fx, [Shot(EmDamage), Shot(ThermalDamage), Shot(KineticDamage), Shot(ExplosiveDamage)], optimal, Math.Max(0, NpcArmament.Round6(Value(Falloff, 0))),
            Math.Max(0, NpcArmament.Round6(Value(TrackingSpeed, 0))), Math.Max(1, NpcArmament.Round6(Value(OptimalSigRadius, 40_000))),
            charge is null ? null : new SimTurretCharge(charge.TypeId, charge.Flag, charge.Quantity, ChargesPerCycle(attributes)));
        SimChange[] told = [new(moduleId, CapacitorNeed, capacitor), new(moduleId, Speed, Three(duration)), new(moduleId, MaxRange, optimal)];
        return new SimEffect(moduleId, moduleType, effect.Id, fx, duration, capacitor, Math.Max(1, repeat), Math.Max(0, targetId), null, null, told, told, gun);
    }

    /// <summary>How many rounds a cycle burns: the module's <c>chargeRate</c>, one when it names none.</summary>
    public static long ChargesPerCycle(IReadOnlyDictionary<int, double> attributes) =>
        attributes.TryGetValue(ChargeRate, out var rate) && double.IsFinite(rate) ? Math.Max(1, (long)rate) : 1;

    public static Result Of(ShipSnapshot snapshot, DogmaStatic statics, long moduleId, string effectName, long targetId, int repeat)
    {
        var module = snapshot.Fitted.FirstOrDefault(m => m.Id == moduleId);
        if (module is null || statics.Type(module.TypeId) is not { } type)
        {
            return new Result(null, "MODULE_NOT_FOUND", 0, "");
        }
        var name = type.Name;
        var effect = type.EffectIds.Select(statics.Effect).FirstOrDefault(e => e is { } found && found.Name.Equals(effectName, StringComparison.OrdinalIgnoreCase));
        if (effect is null || effect.Category is not (EffectDef.Activation or EffectDef.Target))
        {
            return new Result(null, "UNSUPPORTED_EFFECT", module.TypeId, name);
        }
        if (snapshot.World.Item(moduleId) is not { State: >= ItemState.Online })
        {
            return new Result(null, "MODULE_NOT_ONLINE", module.TypeId, name);
        }
        // a gun fires its own effect: targetAttack (the lasers and the starter guns) or projectileFired (the projectile and hybrid guns). The other target effects of a
        // turret type (the mining lasers, the disintegrators, barrage) are not run yet
        var turret = effect.Name is TargetAttack or ProjectileFired && IsTurret(type, statics);
        var charge = snapshot.ChargeOf(module);
        // a turret with nothing to shoot is refused before it is asked for a target (the reference's order): no charge in it, or fewer rounds than a cycle burns
        if (turret && (charge is null
            ? !FiresWithoutCharge(snapshot.World, moduleId)
            : charge.Quantity < ChargesPerCycle(snapshot.World.Attributes(moduleId))))
        {
            return new Result(null, "NO_AMMO", module.TypeId, name);
        }
        if (effect.Category == EffectDef.Target && targetId <= 0)
        {
            return new Result(null, "TARGET_REQUIRED", module.TypeId, name);
        }
        if (turret)
        {
            return new Result(Turret(snapshot.World, statics, module.TypeId, type, effect, moduleId, targetId, repeat, charge), null, module.TypeId, name);
        }
        // only the effects that change how the ship moves and the turrets are run so far; the others (repairs, mining) come with what they need
        if (effect.Name is not (Afterburner or Microwarpdrive))
        {
            return new Result(null, "UNSUPPORTED_EFFECT", module.TypeId, name);
        }

        var world = snapshot.World;
        var ship = snapshot.Ship.Id;
        double Get(long item, int attribute) => world.Get(item, attribute);
        var (on, off) = Propulsion(
            Get(ship, Mass), Get(ship, MaxVelocity), Get(ship, SignatureRadius), Get(moduleId, MassAddition), Get(moduleId, SpeedFactor), Get(moduleId, SpeedBoostFactor),
            effect.Name == Microwarpdrive ? Get(moduleId, SignatureBonus) : 0);
        var duration = effect.DurationAttributeId > 0 ? Get(moduleId, effect.DurationAttributeId) : 0;
        var capacitor = effect.DischargeAttributeId > 0 ? Get(moduleId, effect.DischargeAttributeId) : 0;
        var moduleAttributes = new List<int> { SpeedFactor, effect.DischargeAttributeId, effect.DurationAttributeId };
        if (effect.Name == Microwarpdrive)
        {
            moduleAttributes.Add(SignatureBonus);
        }
        IReadOnlyList<SimChange> Changes(SimNumbers numbers) =>
        [
            .. moduleAttributes.Where(a => a > 0).Select(a => new SimChange(moduleId, a, Six(Get(moduleId, a)))),
            new SimChange(ship, Mass, numbers.Mass),
            new SimChange(ship, MaxVelocity, numbers.MaxVelocity),
            new SimChange(ship, SignatureRadius, numbers.SignatureRadius),
        ];
        var package = new SimEffect(moduleId, module.TypeId, effect.Id, effect.Guid, duration, capacitor, Math.Max(1, repeat), Math.Max(0, targetId), on, off, Changes(on), Changes(off));
        return new Result(package, null, module.TypeId, name);
    }
}
