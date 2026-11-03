using Eve.Dogma;
using Eve.Services.Space.Effects;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>Host identity of a controlled squadron and its launch tube.</summary>
public sealed record TournamentFighterFlight(long FighterId, int TypeId, int TubeFlag);

/// <summary>Fitting-screen primary and secondary fighter ability DPS.</summary>
internal static class TournamentFighterMetrics
{
    // v90 reports squadron size but not which fighter EWAR ability is running.
    public static bool HasUnobservedControl(IReadOnlyList<SimTournamentMetricsCompanion> native) =>
        native.Where(x => x.Kind == SimTournamentCompanionKind.Fighter && x.Members != 0)
            .Any(fighter => FighterAbilityCatalogue.Default.SlotsOf(fighter.TypeId).Any(slot =>
                FighterAbilityCatalogue.Default.Ability(slot.AbilityId)?.EffectFamily is
                    "fighterAbilityStasisWebifier" or "fighterAbilityWarpDisruption" or "fighterAbilityEnergyNeutralizer"
                    or "fighterAbilityECM" or "fighterAbilityTackle" or "fighterTargetPaint"));

    public static (double? Dps, string? Reason) Calculate(ShipSnapshot snapshot, DogmaStatic statics,
        IReadOnlyList<TournamentDroneSource> sources, IReadOnlyList<TournamentFighterFlight> flights,
        IReadOnlyList<SimTournamentMetricsCompanion> native)
    {
        var inSpace = native.Where(x => x.Kind == SimTournamentCompanionKind.Fighter).ToArray();
        if (inSpace.Select(x => x.ShipId).Distinct().Count() != inSpace.Length || flights.Select(x => x.FighterId).Distinct().Count() != flights.Count)
            return (null, "DUPLICATE_FIGHTER");
        var byItem = sources.ToDictionary(x => x.Item.Id);
        var selected = new List<(TournamentDroneSource Source, int Tube, int Members)>();
        foreach (var source in sources.Where(x => x.Item.LocationId == snapshot.Ship.Id && FighterTubes.IsTube(x.Item.Flag)))
        {
            var count = FighterTubes.InventorySize(source.Item.Singleton, source.Item.Quantity);
            if (count is < 1 or > 64) return (null, "INVALID_FIGHTER_SIZE");
            selected.Add((source, source.Item.Flag, (int)count));
        }
        foreach (var member in inSpace)
        {
            if (member.ControllerShipId != snapshot.Ship.Id || member.Members is not { } count || count is < 0 or > 64)
                return (null, "FIGHTER_STATE_UNAVAILABLE");
            if (count == 0) continue;
            var flight = flights.SingleOrDefault(x => x.FighterId == member.ShipId);
            if (flight is null || flight.TypeId != member.TypeId || !FighterTubes.IsTube(flight.TubeFlag)
                || !byItem.TryGetValue(member.ShipId, out var source) || source.Item.TypeId != member.TypeId
                || source.Item.OwnerId != snapshot.Character.Id || source.Item.LocationId != snapshot.Character.SolarSystemId
                || source.Item.Flag != CharacterShips.InSpaceFlag) return (null, "FIGHTER_IDENTITY_MISMATCH");
            selected.Add((source, flight.TubeFlag, count));
        }
        if (flights.Any(f => !inSpace.Any(x => x.ShipId == f.FighterId)) || selected.Select(x => x.Tube).Distinct().Count() != selected.Count)
            return (null, "FIGHTER_TUBE_MISMATCH");
        double total = 0;
        foreach (var (source, tube, count) in selected)
        {
            var item = source.Item;
            if (statics.Type(item.TypeId) is not { CategoryId: CharacterShips.FighterCategory } type
                || !FighterAbilityCatalogue.Default.TypeIds.Contains(item.TypeId)) return (null, "FIGHTER_TYPE_UNAVAILABLE");
            snapshot.World.Add(ItemDogma.Of(item, snapshot.Character.Id, snapshot.Ship.Id, tube, mutation: source.Mutation));
            var attrs = snapshot.World.Attributes(item.Id);
            foreach (var slotId in new[] { 0, 2 })
            {
                var slot = FighterAbilityCatalogue.Default.Slot(item.TypeId, slotId);
                if (slot is null) continue;
                if (FighterAbilityCatalogue.Default.Ability(slot.AbilityId) is not { } ability) return (null, "FIGHTER_ABILITY_UNAVAILABLE");
                var family = FighterAbilityEffects.EffectNameOf(ability.EffectFamily);
                var spec = family switch
                {
                    "fighterAbilityAttackTurret" => (Multiplier: 2178, Duration: 2177, Damage: new[] { 2171, 2172, 2173, 2174 }),
                    "fighterAbilityAttackM" => (Multiplier: 2226, Duration: 2233, Damage: new[] { 2227, 2228, 2229, 2230 }),
                    "fighterAbilityMissiles" => (Multiplier: 2130, Duration: 2182, Damage: new[] { 2131, 2132, 2133, 2134 }),
                    _ => (Multiplier: 0, Duration: 0, Damage: Array.Empty<int>()),
                };
                // The pinned fitting client gives unmapped abilities zero theoretical DPS.
                if (spec.Multiplier == 0) continue;
                var multiplier = attrs.GetValueOrDefault(spec.Multiplier, 0); var duration = attrs.GetValueOrDefault(spec.Duration, 0);
                var damage = spec.Damage.Select(id => attrs.GetValueOrDefault(id, 0)).ToArray();
                if (!double.IsFinite(multiplier) || multiplier < 0 || !double.IsFinite(duration) || duration <= 0
                    || damage.Any(x => !double.IsFinite(x) || x < 0)) return (null, "INVALID_FIGHTER_ABILITY");
                total += count * damage.Sum() * multiplier * 1000d / duration;
            }
        }
        return double.IsFinite(total) ? (total, null) : (null, "INVALID_FIGHTER_ABILITY");
    }
}
