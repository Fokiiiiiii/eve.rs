using Eve.Inventory;
using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

// The superweapon family of static data group 588 ("Super Weapon", category 7), V24.01 build 3396210, read with psql on sde.typeDogma, sde.dogmaEffects and
// sde.dogmaAttributes. The Pulse Activated Nexus Invulnerability Core (42522, effect 6719) is not here: it has its own handler (IndustrialInvulnerabilityEffect).
//
//   Effect (row)                 Types in group 588                                         Effect category
//   superWeapon{Race} (4489-4492) one titan doomsday per race: 24550, 24552, 24554, 23674     2 (target)
//   doomsdayBeamDOT (6472)        lances 40631, 41439, 41440, 41441                          1 (activation)
//   doomsdaySlash (6201)          reapers 40632, 41442, 41443, 41444 and Catmageddon 37474   1
//   debuffLance (11691)           disruptive lances 77398, 77399, 77400, 77401               1
//   doomsdayConeDOT (6473)        Bosonic Field Generator 40633                              1
//   doomsdayHOG (6474)            Gravitational Transportation Field Oscillator 40634         1 (and a structure variant on category 66, same effect)
//
// What each family is, from the SDE (values after dogma are the module's own; the numbers are the build's): the cone 40633 has capacitorNeed 30000, maxRange 30000,
// duration 300000, consumptionType 16275 (Strontium Clathrates) x consumptionQuantity 2000 per cycle, doomsdayAOEShape 3 (cone), doomsdayWarningDuration 10000,
// doomsdayDamageDuration 20000 at doomsdayDamageCycleTime 1000, doomsdayEnergyNeut amount 30000 within radius 10000, doomsdayImmobilityDuration 30000 and
// doomsdayNoJumpOrCloakDuration 300000, isPointTargeted 1. The HOG 40634 has maxRange 100000, duration 300000, consumption 2000 of 16275, doomsdayAOEShape 4
// (projected sphere), doomsdayAOERange 10000, doomsdayWarningDuration 15000, isPointTargeted 1. Its SDE description (not an attribute) says it moves capsuleer
// ships through a localised wormhole between two points, cannot move capital ships, and leaves the capital ships in its area unable to warp for 60 seconds (the capsuleer ships it moves are carried, not blocked).
//
// The Bosonic Field Generator (40633) has a body: DoomsdayConeEffect.cs (the strike of protocol 25, with the fuel of its cycle through the shared mechanism). The
// Judgment and the other titan doomsdays (protocol 26) are DoomsdayTitanEffect.cs; the lances are DoomsdayLanceEffect.cs (a strike of a cylinder). The other
// families have none in the space host yet. Each handler is registered and REFUSES the activation by name, before any package is built: no simulation
// action, no fuel taken (ShipEffect.Build is never reached, so BurnsFuel is never set), and no success is reported. The reasons are the ones below.
//
// What is still missing, per family (the superweapons spec in docs/project/TODO.md section 12, and the DEVIATIONS it cites):
//   - the lance beacon (EveJS spawnLanceBeacon, a Deployed object, ADR 0012): the lance strikes without it; the beacon is the client's beam target only (DEVIATIONS 136);
//   - the HOG's tether exclusion (CCP Citadel 1.9: a tethered ship is not transported): tethering has no state in the simulation (DEVIATIONS 141);
//   - the structure variant (the Standup oscillator, 47068, category 66): refused by name (StructureHogEffect); no structure host exists (O5).

/// <summary>The static data's categories the superweapon family looks at.</summary>
internal static class SuperWeaponCategories
{
    /// <summary>The structure modules (category 66): a structure's fitting, not a ship's.</summary>
    public const int Structure = 66;
}

/// <summary>
/// A superweapon handler that only refuses: it names the family in its reason, so the refusal says which body is missing rather than a generic
/// <see cref="NotImplemented"/>. It is not coverage (<see cref="PendingModuleEffect"/>): the effect stays in the gap manifest of <c>ModuleEffectsTests</c>.
/// </summary>
internal abstract class SuperWeaponPendingEffect(string reason, params string[] effectNames) : PendingModuleEffect(effectNames)
{
    private readonly string _reason = reason;

    protected override string? Refusal(ModuleEffectContext module) => _reason;
}



/// <summary>
/// The Gravitational Transportation Field Oscillator (40634, <c>doomsdayHOG</c>), a transport field (protocol 29). Its sphere is centred on the aim point and
/// its radius is <c>doomsdayAOERange</c> (10 km); one pulse after the 15 s warning carries the capsuleer ships of the system inside it to the aim point (the
/// destination) and holds the capital ships inside it: they cannot warp for 60 s (the SDE description). The firing ship is held for its windows and takes the
/// energy neutralisation of its strike. The source conflict and its resolution are DEVIATIONS 141.
/// </summary>
internal sealed class HogEffect : ShipEffect
{
    /// <summary>The transport's warp block on the capital ships it holds, in ms: the SDE description's "60 seconds", not an attribute of the static data.</summary>
    public const double TransportWarpBlockMs = 60_000.0;
    internal const string EffectName = "doomsdayHOG";

    public override IReadOnlyCollection<string> EffectNames { get; } = [EffectName];

    /// <summary>The ship's oscillator. The structure variant (category 66) is <see cref="StructureHogEffect"/>.</summary>
    protected override bool Handles(ModuleEffectContext module) => module.Type.CategoryId != SuperWeaponCategories.Structure;

    protected override string? Refusal(ModuleEffectContext module) => RefusalFor(module);

    internal static string? RefusalFor(ModuleEffectContext module)
    {
        if (module.Aim is not { Count: 3 } aim || DoomsdayStrike.Unit(aim) is null)
        {
            return "TARGET_POINT_REQUIRED";
        }
        if (module.Value(DoomsdayAoeShape, 0) != 4)
        {
            return "SUPERWEAPON_HOG_SHAPE_NOT_IMPLEMENTED";
        }
        return DoomsdayStrike.Sphere(module, aim) is { IsValid: true } ? null : "SUPERWEAPON_HOG_DATA_MISSING";
    }

    protected override SimEffect Build(ModuleEffectContext module) => BuildField(module);

    internal static SimEffect BuildField(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, Three(duration))];
        var aim = module.Aim!;
        // The simulation applies this explicit participant list inside the field; unrelated ships are not picked up by the field's spatial query.
        var moved = module.Neighbours.Where(neighbour => !neighbour.IsCapital).Select(neighbour => neighbour.ShipId).ToArray();
        var held = module.Neighbours.Where(neighbour => neighbour.IsCapital).Select(neighbour => neighbour.ShipId).ToArray();
        var field = new SimAction.Field(DoomsdayStrike.Sphere(module, aim), aim.Select(NpcArmament.Round6).ToArray(), TransportWarpBlockMs, moved, held);
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), 0, null, null, told, told, null,
            field);
    }
}

/// <summary>
/// The structure variant of the transport field (the Standup Gravitational Transportation Field Oscillator, 47068, category 66): module and wiring only (SDE
/// category 66 is the structure modules, and no structure host exists). Refused by name, never a success.
/// </summary>
internal static class StructureDamageModuleRules
{
    public static bool FitsStructureType(ModuleEffectContext module)
    {
        var structureCategory = module.Statics.CategoryId("Structure");
        if (structureCategory is null
            || module.Statics.Type(module.Snapshot.Ship.TypeId)?.CategoryId != structureCategory)
            return false;

        var hasRestriction = false;
        var fits = false;
        for (var index = 1; index <= 11; index++)
        {
            if (module.Statics.AttributeId($"canFitShipType{index}") is not { } attributeId
                || !module.Type.Attributes.TryGetValue(attributeId, out var allowedType)
                || allowedType <= 0)
                continue;
            hasRestriction = true;
            fits |= (int)allowedType == module.Snapshot.Ship.TypeId;
        }
        return !hasRestriction || fits;
    }
}

internal sealed class StructureHogEffect() : ShipEffect
{
    private static readonly string[] Names = ["doomsdayHOG"];

    public override IReadOnlyCollection<string> EffectNames => Names;

    protected override bool Handles(ModuleEffectContext module) =>
        module.Type.CategoryId == SuperWeaponCategories.Structure;

    protected override string? Refusal(ModuleEffectContext module)
    {
        if (!module.Type.Published)
        {
            return "MODULE_NOT_PUBLISHED";
        }
        if (!StructureDamageModuleRules.FitsStructureType(module))
        {
            return "REFUSED";
        }

        // The structure's shared HOG body is implemented by HogEffect, but its area participants
        // need the per-pulse owner-corporation permission reply before the simulation may apply it.
        return "STRUCTURE_DAMAGE_PERMISSION_REQUIRED";
    }

    protected override bool Implemented => false;

    protected override SimEffect Build(ModuleEffectContext module) => HogEffect.BuildField(module);
}

internal sealed class PointDefenseEffect : ShipEffect
{
    private static readonly string[] Names = ["pointDefense"];

    public override IReadOnlyCollection<string> EffectNames => Names;

    protected override bool Handles(ModuleEffectContext module) =>
        module.Type.CategoryId == SuperWeaponCategories.Structure;

    protected override string? Refusal(ModuleEffectContext module)
    {
        if (!module.Type.Published)
            return "MODULE_NOT_PUBLISHED";
        if (!StructureDamageModuleRules.FitsStructureType(module))
            return "REFUSED";

        var charge = module.Snapshot.ChargeOf(module.Module);
        if (charge is null)
            return "NO_AMMO";

        var chargeRate = ModuleAttributes.ChargesPerCycle(module.Attributes);
        if (chargeRate > int.MaxValue
            || charge.Quantity < chargeRate
            || !IsCompatibleCharge(module, charge))
            return "NO_AMMO";

        var (duration, _, _) = module.Cycle();
        var range = module.Value(ModuleAttributes.EmpFieldRange, 0);
        var damage = DamageVector(module, charge, chargeRate);
        if (duration <= 0 || range <= 0 || damage.Sum() <= 0)
            return "UNSUPPORTED_MODULE";

        return null;
    }

    protected override bool Implemented => true;

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var charge = module.Snapshot.ChargeOf(module.Module)!;
        var chargeRate = (int)ModuleAttributes.ChargesPerCycle(module.Attributes);
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var range = ModuleAttributes.Three(Math.Max(0, module.Value(ModuleAttributes.EmpFieldRange, 0)));
        var damage = DamageVector(module, charge, chargeRate);

        SimChange[] told =
        [
            new(module.ModuleId, ModuleAttributes.CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, ModuleAttributes.Three(duration)),
            new(module.ModuleId, ModuleAttributes.EmpFieldRange, range),
        ];
        var fx = string.IsNullOrWhiteSpace(module.Effect.Guid) ? "pointDefense" : module.Effect.Guid;
        var pointDefense = new SimAction.PointDefense(range, damage);
        var action = new SimAction.Charged(
            pointDefense, null, charge.TypeId, charge.Flag, chargeRate, charge.Quantity);

        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, fx, duration, capacitor, Math.Max(1, module.Repeat), 0,
            null, null, told, told, null, action);
    }

    private static bool IsCompatibleCharge(ModuleEffectContext module, Item charge)
    {
        var chargeGroup = module.Statics.Type(charge.TypeId)?.GroupId;
        return chargeGroup is > 0
            && ModuleAttributes.ChargeGroups.Any(attribute => (int)module.Value(attribute, 0) == chargeGroup);
    }

    private static IReadOnlyList<double> DamageVector(ModuleEffectContext module, Item charge, long chargeRate)
    {
        var attributes = module.World.Attributes(charge.Id);
        var multiplier = Math.Max(0, module.Value(ModuleAttributes.DamageMultiplier, 1)) * chargeRate;
        double Damage(int attribute)
        {
            var amount = attributes.TryGetValue(attribute, out var value) && double.IsFinite(value) ? value : 0;
            return ModuleAttributes.Six(Math.Max(0, amount * multiplier));
        }

        return
        [
            Damage(ModuleAttributes.EmDamage),
            Damage(ModuleAttributes.ThermalDamage),
            Damage(ModuleAttributes.KineticDamage),
            Damage(ModuleAttributes.ExplosiveDamage),
        ];
    }
}
