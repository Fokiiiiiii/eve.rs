using Eve.Dogma;
using Eve.Ships;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space;

/// <summary>Fitting-screen theoretical drone DPS, distinct from actual applied damage.</summary>
internal static class TournamentDroneMetrics
{
    private const int DroneBandwidth = 1271, DroneBandwidthUsed = 1272, MaxActiveDrones = 352;
    private sealed record Candidate(long Id, int Bandwidth, long Quantity, double Dps);

    public static (double? Dps, string? Reason) Calculate(ShipSnapshot snapshot, DogmaStatic statics,
        IReadOnlyList<TournamentDroneSource> sources, IReadOnlyList<SimTournamentMetricsCompanion> native)
    {
        var deployed = native.Where(x => x.Kind == SimTournamentCompanionKind.Drone).ToArray();
        if (deployed.Select(x => x.ShipId).Distinct().Count() != deployed.Length) return (null, "DUPLICATE_DRONE");
        var available = sources.ToDictionary(x => x.Item.Id);
        foreach (var drone in deployed)
        {
            if (drone.ControllerShipId != snapshot.Ship.Id || !available.TryGetValue(drone.ShipId, out var found)
                || found.Item.TypeId != drone.TypeId || found.Item.LocationId != snapshot.Character.SolarSystemId
                || found.Item.Flag != CharacterShips.InSpaceFlag || found.Item.Quantity != 1)
                return (null, "DRONE_IDENTITY_MISMATCH");
        }
        var selected = sources.Where(x => x.Item.LocationId == snapshot.Ship.Id && x.Item.Flag == CharacterShips.DroneBayFlag
            || deployed.Any(d => d.ShipId == x.Item.Id)).ToArray();
        var bandwidth = snapshot.World.Get(snapshot.Ship.Id, DroneBandwidth);
        var max = snapshot.World.Get(snapshot.Character.Id, MaxActiveDrones);
        if (!double.IsFinite(bandwidth) || !double.IsFinite(max) || bandwidth < 0 || max < 0) return (null, "INVALID_DRONE_LIMIT");
        var candidates = new List<Candidate>();
        foreach (var source in selected)
        {
            var item = source.Item;
            if (item.OwnerId != snapshot.Character.Id || item.Quantity <= 0 || statics.Type(item.TypeId) is not { CategoryId: 18 } type)
                return (null, "INVALID_DRONE");
            snapshot.World.Add(ItemDogma.Of(item, snapshot.Character.Id, snapshot.Ship.Id, CharacterShips.DroneBayFlag, mutation: source.Mutation));
            var attributes = snapshot.World.Attributes(item.Id);
            double Value(int attribute) => attributes.GetValueOrDefault(attribute, 0);
            var used = Value(DroneBandwidthUsed);
            var damage = new[] { Value(EmDamage), Value(ThermalDamage), Value(KineticDamage), Value(ExplosiveDamage) };
            var multiplier = Value(DamageMultiplier); var cycle = Value(Speed);
            if (damage.Any(x => !double.IsFinite(x) || x < 0)) return (null, "INVALID_DRONE_DAMAGE");
            // Repair/control drones need their other Fancy metrics even if they also deal damage.
            foreach (var effectId in type.EffectIds)
            {
                if (BuiltInEffects.Unwanted.Contains(effectId)) continue;
                if (statics.Effect(effectId) is not { } effect) return (null, "MISSING_DRONE_EFFECT");
                if (Effects.ProjectedEffect.ForDrone(effect, Value) is not null
                    && deployed.FirstOrDefault(x => x.ShipId == item.Id) is { Activity: not (0 or 4) })
                    return (null, "UNSUPPORTED_DRONE_EFFECT");
            }
            // The fitting client skips zero-damage drones before selecting bandwidth.
            if (damage.Sum() == 0) continue;
            if (!double.IsFinite(used) || used < 1 || !double.IsFinite(multiplier) || multiplier <= 0
                || !double.IsFinite(cycle) || cycle <= 0) return (null, "UNSUPPORTED_DRONE");
            var dps = damage.Sum() * multiplier * 1000d / cycle;
            if (!double.IsFinite(dps)) return (null, "INVALID_DRONE_DAMAGE");
            candidates.Add(new(item.Id, (int)used, item.Quantity, dps));
        }
        var remainingBandwidth = (long)bandwidth; var remainingDrones = (long)max; double total = 0;
        foreach (var drone in candidates.OrderByDescending(x => x.Bandwidth).ThenByDescending(x => x.Dps).ThenBy(x => x.Id))
        {
            if (drone.Bandwidth > remainingBandwidth) continue;
            var count = Math.Min(Math.Min(remainingBandwidth / drone.Bandwidth, drone.Quantity), remainingDrones);
            total += count * drone.Dps; remainingBandwidth -= count * drone.Bandwidth; remainingDrones -= count;
            if (remainingDrones == 0) break;
        }
        return double.IsFinite(total) ? (total, null) : (null, "INVALID_DRONE_DAMAGE");
    }
}
