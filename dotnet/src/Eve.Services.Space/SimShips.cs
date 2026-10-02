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
    private const int Radius = 162;
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

    /// <summary>A ship's warp speed in AU a second: its base warp speed (1 unless something changed it) times its multiplier (3 for a frigate).</summary>
    public static double WarpSpeedAu(double baseWarpSpeed, double multiplier) => (baseWarpSpeed > 0 ? baseWarpSpeed : 1.0) * Math.Max(1, multiplier);

    /// <summary>How many targets a pilot can hold at once: the lower of what the ship and the pilot allow when both allow some, else the higher.</summary>
    public static int LockLimit(double ship, double pilot)
    {
        var (fromShip, fromPilot) = (Math.Max(0, (int)ship), Math.Max(0, (int)pilot));
        return fromShip > 0 && fromPilot > 0 ? Math.Min(fromShip, fromPilot) : Math.Max(fromShip, fromPilot);
    }

    /// <summary>What the ship holds against a jammer, from its attributes (a ship that does not name <c>ECMResistance</c> takes a jam in full).</summary>
    private static SimSensors SensorsOf(Func<int, double> attribute)
    {
        var resistance = attribute(EcmResistance);
        return new SimSensors(attribute(GravimetricStrength), attribute(LadarStrength), attribute(MagnetometricStrength), attribute(RadarStrength), resistance > 0 ? resistance : 1.0);
    }

    public static SimShip Of(ShipSnapshot snapshot, DogmaStatic statics, double securityStatus = 0)
    {
        var ship = snapshot.Ship;
        var type = statics.Type(ship.TypeId);
        var character = snapshot.Character;
        double Attribute(int id) => snapshot.World.Get(ship.Id, id);
        return new SimShip(
            ship.Id, character.Id, ship.TypeId, type?.GroupId ?? 0, type?.CategoryId ?? 0, ship.CustomName ?? type?.Name ?? "", character.Id,
            (int)character.CorporationId, (int)(character.AllianceId ?? 0), 0, securityStatus,
            Attribute(Radius), Attribute(Mass), Attribute(MaxVelocity), Attribute(Agility), WarpSpeedAu(Attribute(BaseWarpSpeed), Attribute(WarpSpeedMultiplier)), Attribute(ShieldRechargeRate),
            Attribute(MaxTargetRange), Attribute(ScanResolution), Attribute(SignatureRadius), LockLimit(Attribute(MaxLockedTargets), snapshot.World.Get(character.Id, MaxLockedTargets)),
            Attribute(CapacitorCapacity), Attribute(RechargeRate) / 1000.0,
            [.. snapshot.Fitted.Select(module => new SimModule(module.Id, module.TypeId, module.Flag))],
            VitalsOf(snapshot), ConditionOf(snapshot.Condition), SensorsOf(Attribute));
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
            Attribute(CapacitorCapacity), Attribute(RechargeRate) / 1000.0, [], vitals, SimCondition.Full, SensorsOf(id => Attribute(id)));
    }

    /// <summary>What the ship can take and how it resists, as dogma has it (a resonance the ship has none of lets everything through).</summary>
    public static SimVitals VitalsOf(ShipSnapshot snapshot)
    {
        var attributes = snapshot.World.Attributes(snapshot.Ship.Id).ToDictionary(a => a.Key, a => a.Value);
        double[] Layer(int[] ids) => [.. ids.Select(id => Math.Max(0, attributes.GetValueOrDefault(id, 1.0)))];
        return new SimVitals(
            Math.Max(0, attributes.GetValueOrDefault(ShieldCapacity)), Math.Max(0, attributes.GetValueOrDefault(ArmorHitPoints)), Math.Max(0, attributes.GetValueOrDefault(HullPoints)),
            new SimResonances(Layer(ShieldResonances), Layer(ArmorResonances), Layer(HullResonances)));
    }

    public static SimCondition ConditionOf(ShipCondition condition) => new(condition.Damage, condition.ArmorDamage, condition.ShieldCharge, condition.Charge);
}
