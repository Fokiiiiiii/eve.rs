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

    private const int BaseWarpSpeed = 1281;

    /// <summary>A ship's warp speed in AU a second: its base warp speed (1 unless something changed it) times its multiplier (3 for a frigate).</summary>
    public static double WarpSpeedAu(double baseWarpSpeed, double multiplier) => (baseWarpSpeed > 0 ? baseWarpSpeed : 1.0) * Math.Max(1, multiplier);

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
            [.. snapshot.Fitted.Select(module => new SimModule(module.Id, module.TypeId, module.Flag))]);
    }
}
