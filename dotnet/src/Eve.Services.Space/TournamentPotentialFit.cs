using Eve.Dogma;
using Eve.Services.Space.Effects;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>Enumerates the actual online fit before calculating theoretical rates.</summary>
internal static class TournamentPotentialFit
{
    // Server definition: number of online fitted offensive EWAR sources, and how many run.
    // These are SDE effect names, not CCP's unpublished tournament weights.
    private static readonly HashSet<string> ControlEffects = new(StringComparer.OrdinalIgnoreCase)
    {
        "energyNeutralizerFalloff", "energyNosferatuFalloff", "remoteWebifierFalloff",
        "remoteTargetPaintFalloff", "remoteSensorDampFalloff", "warpScrambleBlockMWDWithNPCEffect",
        "warpDisrupt", "warpDisruptSphere", "shipModuleFocusedWarpDisruptionScript", "shipModuleFocusedWarpScramblingScript",
        "shipModuleTrackingDisruptor", "shipModuleGuidanceDisruptor", "remoteECMFalloff"
    };

    public static (int Fitted, int Running) Controls(ShipSnapshot snapshot, DogmaStatic statics, IReadOnlyDictionary<long, string> running)
    {
        var modules = snapshot.Fitted.Where(m => snapshot.StateOf(m.Id) >= ItemState.Online && statics.Type(m.TypeId) is { } type
            && type.EffectIds.Select(statics.Effect).Any(e => e is not null && ControlEffects.Contains(e.Name))).ToArray();
        return (modules.Length, modules.Count(m => running.TryGetValue(m.Id, out var name) && ControlEffects.Contains(name)));
    }

    public static TournamentPotentialResult Calculate(ShipSnapshot snapshot, DogmaStatic statics,
        IReadOnlyDictionary<long, string> running, bool auxiliaryComplete,
        IReadOnlyDictionary<long, long>? targets = null, IReadOnlySet<long>? friendlyShips = null)
    {
        var candidates = new List<TournamentPotentialModule>();
        foreach (var module in snapshot.Fitted)
        {
            if (snapshot.World.Item(module.Id) is not { } item) return new(null, "MISSING_MODULE_STATE");
            if (item.State < ItemState.Online) continue;
            if (statics.Type(module.TypeId) is not { } type) return new(null, "MISSING_MODULE_TYPE");
            var effects = new List<EffectDef>();
            foreach (var id in type.EffectIds)
            {
                // Dogma deliberately removes these SDE slot/skill markers; their classification is known.
                if (BuiltInEffects.Unwanted.Contains(id)) continue;
                if (statics.Effect(id) is not { } effect) return new(null, "MISSING_EFFECT");
                // SDE effect 16 is the online state transition, not a cycle action.
                if (effect.Id == 16 && effect.Name.Equals("online", StringComparison.OrdinalIgnoreCase)) continue;
                if (effect.Category is EffectDef.Activation or EffectDef.Target or EffectDef.Area) effects.Add(effect);
            }
            // Some guns carry only the passive turretfitted marker, but activate targetAttack.
            if (type.EffectIds.Select(statics.Effect).Any(e => e?.Name.Equals("turretfitted", StringComparison.OrdinalIgnoreCase) == true)
                && statics.Effects.FirstOrDefault(e => e.Name.Equals("targetAttack", StringComparison.OrdinalIgnoreCase) && e.Category == EffectDef.Target) is { } attack
                && !effects.Any(e => e.Id == attack.Id)) effects.Add(attack);
            var selected = new List<string>();
            foreach (var effect in effects)
            {
                var context = new EffectContext<ShipSnapshot>(snapshot, snapshot.World, statics, module.Id, type, effect, 0, 1);
                var handler = ModuleEffects.Default.Find(context);
                if (handler is null || !handler.Implemented) return new(null, "UNSUPPORTED_EFFECT");
                if (handler is WarpDisruptSphereEffect && ControlEffects.Contains(effect.Name)) continue;
                if (handler is TurretEffect or MissileEffect or RepairEffect or FueledArmorRepairEffect
                    or FueledShieldBoostingEffect or AncillaryRemoteArmorRepairEffect or AncillaryRemoteShieldBoosterEffect)
                    selected.Add(effect.Name);
                else if (handler is ProjectedEffect)
                {
                    if (effect.Name.Contains("Repair", StringComparison.OrdinalIgnoreCase)
                        || effect.Name.Equals("shipModuleRemoteShieldBooster", StringComparison.OrdinalIgnoreCase))
                        selected.Add(effect.Name);
                    else if (!ControlEffects.Contains(effect.Name))
                        return new(null, "UNSUPPORTED_EFFECT");
                }
                // Only explicitly known non-damage, non-repair families may be ignored.
                // Unknown or newly implemented handlers must not silently become zero DPS.
                else if (handler is not (PropulsionEffect or SensorBoosterEffect or CloakingEffect
                    or MicroJumpDriveEffect or DogmaModifierEffect or WarpModifierEffect
                    or TypeAttributeResistanceEffect or ReactiveArmorHardenerEffect))
                    return new(null, "UNSUPPORTED_EFFECT");
            }
            if (selected.Count > 1) return new(null, "AMBIGUOUS_EFFECT");
            if (selected.Count == 1)
            {
                var isRunning = running.TryGetValue(module.Id, out var runningEffect);
                // A launcher is requested by its charge's effect name but runs useMissiles.
                var normalized = isRunning ? MissileEffect.LauncherEffectFor(type, statics, runningEffect!)?.Name ?? runningEffect : null;
                if (isRunning && !string.Equals(normalized, selected[0], StringComparison.OrdinalIgnoreCase))
                    return new(null, "RUNNING_EFFECT_MISMATCH");
                candidates.Add(new(module.Id, selected[0], isRunning, targets?.GetValueOrDefault(module.Id) ?? 0));
            }
        }
        return TournamentPotentialMetrics.Calculate(snapshot, statics, candidates, auxiliaryComplete, friendlyShips);
    }
}
