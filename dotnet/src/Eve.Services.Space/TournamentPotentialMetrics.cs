using Eve.Dogma;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>One fitted module with its actual effect name and authoritative running state.</summary>
internal sealed record TournamentPotentialModule(long ModuleId, string EffectName, bool Running, long TargetId = 0);
/// <summary>Raw theoretical rates, separate from actual applied damage and healing.</summary>
internal readonly record struct TournamentPotentialTotals(double FittedDamagePerSecond, double RunningDamagePerSecond, double FittedRepairHpPerSecond, double RunningRepairHpPerSecond);
internal readonly record struct TournamentPotentialResult(TournamentPotentialTotals? Totals, string? UnavailableReason);

/// <summary>
/// Pure snapshot calculation. The caller must certify that every relevant fitted weapon and repairer
/// has been enumerated and that drone/fighter inputs are complete; unknown is not an empty fit.
/// This does not calculate target application, effective repair, or the Fancy wire tuple.
/// </summary>
internal static class TournamentPotentialMetrics
{
    public static TournamentPotentialResult Calculate(ShipSnapshot snapshot, DogmaStatic statics,
        IReadOnlyList<TournamentPotentialModule> modules, bool complete, IReadOnlySet<long>? friendlyShips = null)
    {
        if (!complete) return new(null, "INCOMPLETE_FIT");
        if (modules.Select(module => module.ModuleId).Distinct().Count() != modules.Count) return new(null, "DUPLICATE_MODULE");
        double potentialDamage = 0, runningDamage = 0, potentialRepair = 0, runningRepair = 0;
        foreach (var module in modules)
        {
            var result = ModulePackages.OfPotential(snapshot, statics, module.ModuleId, module.EffectName);
            if (result.Reason == "NO_AMMO") continue; // Known empty load cannot fire.
            if (result.Reason is not null) return new(null, result.Reason);
            var effect = result;
            if (!double.IsFinite(effect.DurationMs) || effect.DurationMs <= 0) return new(null, "INVALID_DURATION");
            double damage = 0, repair = 0;
            if (effect.Turret is { } turret)
            {
                // Target-specific precursor spool cannot be reconstructed from a fitted snapshot alone.
                if (turret.Spool is not null || turret.RawDamage.Count != 4 || turret.RawDamage.Any(x => !double.IsFinite(x) || x < 0))
                    return new(null, "UNKNOWN_WEAPON_STATE");
                damage = turret.RawDamage.Sum() * 1000d / effect.DurationMs;
            }
            else
            {
                double? amount = effect.Action switch
                {
                    SimAction.Repair local => local.Amount,
                    SimAction.Remote { What: SimRemote.Repair remote } => remote.Amount,
                    _ => null,
                };
                if (amount is null) return new(null, "UNSUPPORTED_EFFECT");
                if (!double.IsFinite(amount.Value) || amount.Value < 0) return new(null, "INVALID_REPAIR");
                repair = amount.Value * 1000d / effect.DurationMs;
            }
            if (!double.IsFinite(damage) || !double.IsFinite(repair)) return new(null, "NONFINITE_RESULT");
            potentialDamage += damage; potentialRepair += repair;
            if (module.Running)
            {
                runningDamage += damage;
                if (effect.Action is not SimAction.Remote || friendlyShips is null || friendlyShips.Contains(module.TargetId)) runningRepair += repair;
            }
        }
        if (!double.IsFinite(potentialDamage) || !double.IsFinite(potentialRepair) || !double.IsFinite(runningDamage) || !double.IsFinite(runningRepair)) return new(null, "NONFINITE_RESULT");
        return new(new(potentialDamage, runningDamage, potentialRepair, runningRepair), null);
    }
}
