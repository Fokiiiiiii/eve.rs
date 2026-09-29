using Eve.Dogma;
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

    private const int BaseWarpSpeed = 1281;

    /// <summary>A ship's warp speed in AU a second: its base warp speed (1 unless something changed it) times its multiplier (3 for a frigate).</summary>
    public static double WarpSpeedAu(double baseWarpSpeed, double multiplier) => (baseWarpSpeed > 0 ? baseWarpSpeed : 1.0) * Math.Max(1, multiplier);

    /// <summary>How many targets a pilot can hold at once: the lower of what the ship and the pilot allow when both allow some, else the higher.</summary>
    public static int LockLimit(double ship, double pilot)
    {
        var (fromShip, fromPilot) = (Math.Max(0, (int)ship), Math.Max(0, (int)pilot));
        return fromShip > 0 && fromPilot > 0 ? Math.Min(fromShip, fromPilot) : Math.Max(fromShip, fromPilot);
    }

    public static SimShip Of(ShipSnapshot snapshot, DogmaStatic statics)
    {
        var ship = snapshot.Ship;
        var type = statics.Type(ship.TypeId);
        var character = snapshot.Character;
        double Attribute(int id) => snapshot.World.Get(ship.Id, id);
        return new SimShip(
            ship.Id, character.Id, ship.TypeId, type?.GroupId ?? 0, type?.CategoryId ?? 0, ship.CustomName ?? type?.Name ?? "", character.Id,
            (int)character.CorporationId, (int)(character.AllianceId ?? 0), 0, 0,
            Attribute(Radius), Attribute(Mass), Attribute(MaxVelocity), Attribute(Agility), WarpSpeedAu(Attribute(BaseWarpSpeed), Attribute(WarpSpeedMultiplier)), Attribute(ShieldRechargeRate),
            Attribute(MaxTargetRange), Attribute(ScanResolution), Attribute(SignatureRadius), LockLimit(Attribute(MaxLockedTargets), snapshot.World.Get(character.Id, MaxLockedTargets)),
            [.. snapshot.Fitted.Select(module => new SimModule(module.Id, module.TypeId, module.Flag))]);
    }
}
