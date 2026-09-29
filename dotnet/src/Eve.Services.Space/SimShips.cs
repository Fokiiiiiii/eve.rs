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

    /// <summary>A ship's warp speed in AU a second with a multiplier of one.</summary>
    private const double BaseWarpSpeedAu = 3.0;

    public static SimShip Of(ShipSnapshot snapshot, DogmaStatic statics)
    {
        var ship = snapshot.Ship;
        var type = statics.Type(ship.TypeId);
        var character = snapshot.Character;
        double Attribute(int id) => snapshot.World.Get(ship.Id, id);
        return new SimShip(
            ship.Id, character.Id, ship.TypeId, type?.GroupId ?? 0, type?.CategoryId ?? 0, ship.CustomName ?? type?.Name ?? "", character.Id,
            (int)character.CorporationId, (int)(character.AllianceId ?? 0), 0, 0,
            Attribute(Radius), Attribute(Mass), Attribute(MaxVelocity), Attribute(Agility), BaseWarpSpeedAu * Math.Max(1, Attribute(WarpSpeedMultiplier)), Attribute(ShieldRechargeRate),
            [.. snapshot.Fitted.Select(module => new SimModule(module.Id, module.TypeId, module.Flag))]);
    }
}
