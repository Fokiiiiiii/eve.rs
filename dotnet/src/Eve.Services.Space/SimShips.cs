using Eve.Dogma;
using Eve.Fitting;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>What the simulation is told of a ship: the numbers dogma works out for it (ADR 0004) and what its slim item shows.</summary>
internal static class SimShips
{
    private const int Mass = 4;
    private const int MaxVelocity = 37;
    private const int Agility = 70;
    internal const int Radius = 162;
    private const int ShieldRechargeRate = 479;
    private const int WarpSpeedMultiplier = 600;
    private const int MaxTargetRange = 76;
    private const int MaxLockedTargets = 192;
    private const int SignatureRadius = 552;
    private const int ScanResolution = 564;
    private const int GravimetricStrength = 211;
    private const int LadarStrength = 209;
    private const int MagnetometricStrength = 210;
    private const int RadarStrength = 208;
    private const int EcmResistance = 2253;
    private const int CapacitorWarfareResistance = 2045;
    private const int CapacitorCapacity = 482;
    private const int RechargeRate = 55;

    private const int BaseWarpSpeed = 1281;

    private const int ShieldCapacity = 263;
    private const int ArmorHitPoints = 265;
    private const int HullPoints = 9;

    // the resonances of each layer in the order of the link's damage vectors: em, thermal, kinetic, explosive
    private static readonly int[] ShieldResonances = [271, 274, 273, 272];
    private static readonly int[] ArmorResonances = [267, 270, 269, 268];
    private static readonly int[] HullResonances = [113, 110, 109, 111];

    /// <summary>The effect of a Polarized weapon that sets the shield and armor resonances of the ship to one (static data, effect 5995).</summary>
    private const string PolarizedShieldArmorEffect = "resistanceKillerShieldArmorAll";

    /// <summary>A ship's warp speed in AU a second: its base warp speed (1 unless something changed it) times its multiplier (3 for a frigate).</summary>
    public static double WarpSpeedAu(double baseWarpSpeed, double multiplier) => (baseWarpSpeed > 0 ? baseWarpSpeed : 1.0) * Math.Max(1, multiplier);

    /// <summary>How many targets a pilot can hold at once: the lower of what the ship and the pilot allow when both allow some, else the higher.</summary>
    public static int LockLimit(double ship, double pilot)
    {
        var (fromShip, fromPilot) = (Math.Max(0, (int)ship), Math.Max(0, (int)pilot));
        return fromShip > 0 && fromPilot > 0 ? Math.Min(fromShip, fromPilot) : Math.Max(fromShip, fromPilot);
    }

    /// <summary>What the ship holds against a jammer, from its attributes (a ship that does not name <c>ECMResistance</c> takes a jam in full).</summary>
    /// <remarks>A type that does not name <c>energyWarfareResistance</c> is not neutralised less: 1. Zero is immune, not missing.</remarks>
    private static SimSensors SensorsOf(Func<int, double> attribute, Func<int, bool> names)
    {
        var resistance = attribute(EcmResistance);
        var energy = names(CapacitorWarfareResistance) ? attribute(CapacitorWarfareResistance) : 1.0;
        return new SimSensors(
            attribute(GravimetricStrength), attribute(LadarStrength), attribute(MagnetometricStrength), attribute(RadarStrength),
            resistance > 0 ? resistance : 1.0, double.IsFinite(energy) && energy >= 0 ? energy : 1.0);
    }

public static SimHeatProfile HeatProfileOf(ShipSnapshot snapshot, DogmaStatic statics)
    {
        const int Thermodynamics = 28164;
        const int Damage = 3, HitPoints = 9, HeatAbsorptionRate = 1180, HeatDamage = 1211;
        const int HeatCapacityHi = 1178, HeatCapacityMed = 1199, HeatCapacityLow = 1200;
        const int HeatDissipationHi = 1179, HeatDissipationMed = 1196, HeatDissipationLow = 1198;
        const int HeatGenerationMultiplier = 1224;
        const int HeatAttenuationHi = 1259, HeatAttenuationMed = 1261, HeatAttenuationLow = 1262;
        double ShipAttribute(int id, double fallback) {
            var value = snapshot.World.Get(snapshot.Ship.Id, id);
            return double.IsFinite(value) && value > 0 ? value : fallback;
        }
        int thermodynamics = snapshot.Skills.FirstOrDefault(skill => skill.TypeId == Thermodynamics)?.Level ?? 0;
        var modules = snapshot.Fitted.Select(module => {
            var type = statics.Type(module.TypeId);
            var state = snapshot.World.Item(module.Id)?.State;
            var overloadable = type?.EffectIds.Select(statics.Effect).Any(effect => effect?.Category == EffectDef.Overload) == true;
            var heatImmune = type?.EffectIds.Select(statics.Effect).Any(effect =>
                effect?.Name.Contains("mining", StringComparison.OrdinalIgnoreCase) == true ||
                effect?.Name.Contains("harvest", StringComparison.OrdinalIgnoreCase) == true) == true;
            double ModuleAttribute(int id) {
                var value = snapshot.World.Get(module.Id, id);
                return double.IsFinite(value) ? Math.Max(0, value) : 0;
            }
            return new SimHeatModule(module.Id, module.Flag, ModuleAttribute(HitPoints), ModuleAttribute(Damage), ModuleAttribute(HeatDamage),
                ModuleAttribute(HeatAbsorptionRate), overloadable, heatImmune, state is { } online && online >= ItemState.Online);
        }).ToArray();
        return new SimHeatProfile(
            ShipAttribute(HeatGenerationMultiplier, 1), Math.Max(0.75, 1 - thermodynamics * 0.05), thermodynamics,
            [ShipAttribute(HeatCapacityHi, 100), ShipAttribute(HeatCapacityMed, 100), ShipAttribute(HeatCapacityLow, 100)],
            [snapshot.World.Get(snapshot.Ship.Id, 1176), snapshot.World.Get(snapshot.Ship.Id, 1177), snapshot.World.Get(snapshot.Ship.Id, 1175)],
            [ShipAttribute(HeatDissipationHi, 0.01), ShipAttribute(HeatDissipationMed, 0.01), ShipAttribute(HeatDissipationLow, 0.01)],
            [ShipAttribute(HeatAttenuationHi, 1), ShipAttribute(HeatAttenuationMed, 1), ShipAttribute(HeatAttenuationLow, 1)],
            modules);
    }

    public static SimShip Of(ShipSnapshot snapshot, DogmaStatic statics, double securityStatus = 0)
    {
        var ship = snapshot.Ship;
        var type = statics.Type(ship.TypeId);
        var character = snapshot.Character;
        var isStructure = type is not null && statics.CategoryId("Structure") == type.CategoryId;
        var ownerId = isStructure ? ship.OwnerId : character.Id;
        var corporationId = isStructure ? checked((int)ship.OwnerId) : (int)character.CorporationId;
        double Attribute(int id) => snapshot.World.Get(ship.Id, id);
        return new SimShip(
            ship.Id, character.Id, ship.TypeId, type?.GroupId ?? 0, type?.CategoryId ?? 0, ship.CustomName ?? type?.Name ?? "", ownerId,
            corporationId, isStructure ? 0 : (int)(character.AllianceId ?? 0), 0, isStructure ? 0 : securityStatus,
            Attribute(Radius), Attribute(Mass), Attribute(MaxVelocity), Attribute(Agility), WarpSpeedAu(Attribute(BaseWarpSpeed), Attribute(WarpSpeedMultiplier)), Attribute(ShieldRechargeRate),
            Attribute(MaxTargetRange), Attribute(ScanResolution), Attribute(SignatureRadius), LockLimit(Attribute(MaxLockedTargets), snapshot.World.Get(character.Id, MaxLockedTargets)),
            Attribute(CapacitorCapacity), Attribute(RechargeRate) / 1000.0,
            [.. snapshot.Fitted.Select(module => new SimModule(module.Id, module.TypeId, module.Flag))],
            VitalsOf(snapshot), ConditionOf(snapshot.Condition), SensorsOf(Attribute, _ => true), DroneDamageMultiplierOf(snapshot), StanceOf(snapshot));
    }

    /// <summary>
    /// The ship's stance for its slim item (protocol 55): the stance, and when it switched as a FILETIME. A ship that was never switched has no stance and no field.
    /// The simulation keeps the old stance of a switch it is told (the host sends the new one only), so the old stance here is the current one.
    /// </summary>
    public static SimShipStance? StanceOf(ShipSnapshot snapshot) => snapshot.Stance is { } stance
        ? new SimShipStance(stance.Stance, Eve.Services.Py.FileTimeFromUnixMs(stance.SwitchedAtMs), stance.Stance)
        : null;

    /// <summary>The attribute of a ship that a drone bonus or penalty writes (industrialBonusDroneDamage, SDE 2580).</summary>
    public const int IndustrialBonusDroneDamage = 2580;

    /// <summary>The Drones skill (SDE 3436), which the SDE's industrialBonusDroneDamage effect (6789) requires of a drone's owner.</summary>
    public const int DronesSkill = 3436;

    /// <summary>
    /// The damage multiplier of the drones the ship launches (protocol 47). The SDE's effect 6789 adds the ship's industrialBonusDroneDamage (2580) to the drone's
    /// damage multiplier (PostPercent) for an owner with the Drones skill, so a bonus needs the skill. A penalty (the wiki's P.A.N.I.C.: a protected ship cannot deal
    /// damage) applies to every pilot. Neither moves a ship with no bonus.
    /// </summary>
    public static double DroneDamageMultiplierOf(ShipSnapshot snapshot)
    {
        var bonus = snapshot.World.Get(snapshot.Ship.Id, IndustrialBonusDroneDamage);
        var hasSkill = snapshot.Skills.Any(skill => skill.TypeId == DronesSkill && skill.Level > 0);
        var effective = hasSkill ? bonus : Math.Min(bonus, 0d);
        return double.IsFinite(effective) ? Math.Max(0d, 1d + effective / 100d) : 1d;
    }

    /// <summary>A ship as its type has it and nobody flies it (a hull spawned for <c>/deathtest</c>): the type's own numbers, no pilot, no modules.</summary>
    public static SimShip OfType(long shipId, TypeDef type)
    {
        double Attribute(int id, double fallback = 0) => type.Attributes.GetValueOrDefault(id, fallback);
        double[] Layer(int[] ids) => [.. ids.Select(id => Math.Max(0, Attribute(id, 1.0)))];
        var vitals = new SimVitals(
            Math.Max(0, Attribute(ShieldCapacity)), Math.Max(0, Attribute(ArmorHitPoints)), Math.Max(0, Attribute(HullPoints)),
            new SimResonances(Layer(ShieldResonances), Layer(ArmorResonances), Layer(HullResonances)));
        return new SimShip(
            shipId, 0, type.TypeId, type.GroupId, type.CategoryId, type.Name, 0, 0, 0, 0, 0,
            Attribute(Radius), Attribute(Mass), Attribute(MaxVelocity), Attribute(Agility), WarpSpeedAu(Attribute(BaseWarpSpeed), Attribute(WarpSpeedMultiplier)), Attribute(ShieldRechargeRate),
            Attribute(MaxTargetRange), Attribute(ScanResolution), Attribute(SignatureRadius), LockLimit(Attribute(MaxLockedTargets), 0),
            Attribute(CapacitorCapacity), Attribute(RechargeRate) / 1000.0, [], vitals, SimCondition.Full, SensorsOf(id => Attribute(id), type.Attributes.ContainsKey),
            DroneDamageMultiplier: 1d);
    }

    /// <summary>What the ship can take and how it resists, as dogma has it (a resonance the ship has none of lets everything through).</summary>
    public static SimVitals VitalsOf(ShipSnapshot snapshot)
    {
        var attributes = snapshot.World.Attributes(snapshot.Ship.Id).ToDictionary(a => a.Key, a => a.Value);
        double[] Layer(int[] ids) => [.. ids.Select(id => Math.Max(0, attributes.GetValueOrDefault(id, 1.0)))];
        return new SimVitals(
            Math.Max(0, attributes.GetValueOrDefault(ShieldCapacity)), Math.Max(0, attributes.GetValueOrDefault(ArmorHitPoints)), Math.Max(0, attributes.GetValueOrDefault(HullPoints)),
            new SimResonances(Layer(ShieldResonances), Layer(ArmorResonances), Layer(HullResonances)), ArmorPinned(snapshot),
            attributes.GetValueOrDefault(MaxVelocity), attributes.GetValueOrDefault(Mass));
    }

    /// <summary>
    /// Whether a fitted Polarized weapon pins the armor's resonances: its effect (<c>resistanceKillerShieldArmorAll</c>, the static data's) runs on the ship. The
    /// simulation keeps the armor at one while it is so, whatever a reactive armor hardener does (the world already has them at one).
    /// </summary>
    public static bool ArmorPinned(ShipSnapshot snapshot) =>
        snapshot.Fitted.Any(module => snapshot.World.Item(module.Id) is { } item
            && snapshot.World.RunningEffects(item).Any(effect => effect.Name == PolarizedShieldArmorEffect));

    public static SimCondition ConditionOf(ShipCondition condition) => new(condition.Damage, condition.ArmorDamage, condition.ShieldCharge, condition.Charge);
}
