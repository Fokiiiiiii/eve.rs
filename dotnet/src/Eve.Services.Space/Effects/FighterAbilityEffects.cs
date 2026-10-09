using Eve.Dogma;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The effects of the abilities a squadron runs (ADR 0015, WP-K, WP-L, WP-M): each ability family's SDE effect, built into the simulation's effect from the
/// squadron type's own dogma. The projected effects (warp disruption, stasis, neutralizer, ECM) go through <see cref="ProjectedEffect"/>; the self buffs
/// (microwarpdrive, afterburner, evasive maneuvers), the jump drive and the attacks (missiles, AttackM) are built here. An ability whose body the simulation
/// does not run yet is refused with its reason, never run as a no-op.
/// </summary>
internal static class FighterAbilityEffects
{
    /// <summary>Kamikaze: the squadron approaches its target and detonates; the simulation has no approach-and-detonate action yet.</summary>
    public const string KamikazeRefusal = "KAMIKAZE_NOT_IN_SIMULATION";

    /// <summary>LaunchBomb: the bomb's blast needs the superweapons' area mechanism (ADR 0011), which is not in the simulation yet.</summary>
    public const string LaunchBombRefusal = "LAUNCH_BOMB_NOT_IN_SIMULATION";

    private const string UnsupportedEffect = "UNSUPPORTED_EFFECT";

    // the squadron type's attributes (SDE ids): the mass, the top speed and the signature radius, and the self buffs' bonuses and durations
    private const int MassAttribute = 4;
    private const int MaxVelocityAttribute = 37;
    private const int SignatureRadiusAttribute = 552;
    private const int MicroWarpDriveSpeedBonus = 2152;
    private const int MicroWarpDriveSignatureBonus = 2153;
    private const int MicroWarpDriveDuration = 2157;
    private const int AfterburnerSpeedBonus = 2151;
    private const int AfterburnerDuration = 2158;
    private const int EvasiveDuration = 2123;
    private const int EvasiveSpeedBonus = 2224;
    private const int EvasiveSignatureBonus = 2225;
    private const int MicroJumpDriveDistance = 2154;
    private const int MicroJumpDriveDuration = 2155;
    private const int MissilesDamageMultiplier = 2130;
    private const int MissilesDuration = 2182;
    private static readonly int[] MissileDamage = [2131, 2132, 2133, 2134];
    private const int AttackMDamageMultiplier = 2226;
    private const int AttackMDuration = 2233;
    private static readonly int[] AttackMDamage = [2227, 2228, 2229, 2230];

    /// <summary>The SDE effect name of an ability family. The catalogue calls the AttackM family <c>fighterAbilityAttackMissile</c> (EVE.js); the SDE effect is <c>fighterAbilityAttackM</c>.</summary>
    public static string EffectNameOf(string family) => family switch
    {
        "fighterAbilityAttackMissile" => "fighterAbilityAttackM",
        _ => family,
    };

    /// <summary>
    /// The simulation effect of an ability of a squadron: the squadron's own type numbers, the members it has now (an attack lands per member), the target it
    /// is held on (0 for a self buff) and the slot's cooldown (a jump drive's). Null with the reason when the ability cannot be run.
    /// </summary>
    public static (SimEffect? Effect, string? Refusal) Build(
        string effectName, TypeDef type, DogmaStatic statics, long moduleId, long targetId, int squadronSize, double? cooldownSeconds)
    {
        var effect = type.EffectIds.Select(statics.Effect).FirstOrDefault(e => e is not null && e.Name.Trim().Equals(effectName, StringComparison.OrdinalIgnoreCase));
        if (effect is null)
        {
            return (null, UnsupportedEffect);
        }
        double Value(int attribute) => type.Attributes.GetValueOrDefault(attribute, 0);
        // the cycle is the effect's duration (the squadron's own value of the attribute, else the effect's default)
        double CycleOr(int attribute)
        {
            var (cycle, _, _) = ShipEffect.CycleOf(effect, (id, fallback) => Value(id) is var v && v != 0 ? v : fallback);
            return cycle > 0 ? cycle : Value(attribute);
        }

        switch (effect.Name.Trim())
        {
            case "fighterAbilityWarpDisruption":
            case "fighterAbilityTackle":
            case "fighterAbilityStasisWebifier":
            case "fighterAbilityEnergyNeutralizer":
            case "fighterAbilityECM":
                if (ProjectedEffect.ForDrone(effect, Value) is not { } remote)
                {
                    return (null, UnsupportedEffect);
                }
                return (new SimEffect(moduleId, type.TypeId, remote.EffectId, remote.Fx, remote.CycleMs, 0, 1, targetId, null, null, [], [], null, remote.Action), null);

            case "fighterAbilityMicroWarpDrive":
            case "fighterAbilityAfterburner":
            case "fighterAbilityEvasiveManeuvers":
            {
                // a plain percentage of the squadron's own top speed and signature (EVE.js fighters, not the module's mass formula)
                var (speedBonus, signatureBonus, duration) = effect.Name.Trim() switch
                {
                    "fighterAbilityMicroWarpDrive" => (MicroWarpDriveSpeedBonus, MicroWarpDriveSignatureBonus, MicroWarpDriveDuration),
                    "fighterAbilityAfterburner" => (AfterburnerSpeedBonus, 0, AfterburnerDuration),
                    _ => (EvasiveSpeedBonus, EvasiveSignatureBonus, EvasiveDuration),
                };
                var mass = Value(MassAttribute);
                var maxVelocity = Value(MaxVelocityAttribute);
                var signature = Value(SignatureRadiusAttribute);
                var on = new SimNumbers(mass, maxVelocity * (1 + Value(speedBonus) / 100), signature * (1 + (signatureBonus == 0 ? 0 : Value(signatureBonus)) / 100));
                var off = new SimNumbers(mass, maxVelocity, signature);
                return (new SimEffect(moduleId, type.TypeId, effect.Id, effect.Guid, CycleOr(duration), 0, 1, 0, on, off, [], []), null);
            }

            case "fighterAbilityMicroJumpDrive":
            {
                if (cooldownSeconds is not > 0)
                {
                    return (null, "NO_COOLDOWN");
                }
                var distance = Value(MicroJumpDriveDistance);
                var cooldownMs = (long)Math.Round(cooldownSeconds.Value * 1000);
                return (new SimEffect(moduleId, type.TypeId, effect.Id, effect.Guid, CycleOr(MicroJumpDriveDuration), 0, 1, 0, null, null, [], [],
                    null, new SimAction.MicroJumpDrive(distance, cooldownMs)), null);
            }

            case "fighterAbilityMissiles":
            case "fighterAbilityAttackM":
            {
                // an attack lands on its locked target at each cycle, the damage of every member that is in the squadron (the reference's
                // resolveMissileAppliedDamage: instant, no flying missile)
                if (targetId <= 0)
                {
                    return (null, "TARGET_REQUIRED");
                }
                var multiplier = effect.Name.Trim() == "fighterAbilityMissiles" ? MissilesDamageMultiplier : AttackMDamageMultiplier;
                var kinds = effect.Name.Trim() == "fighterAbilityMissiles" ? MissileDamage : AttackMDamage;
                var duration = effect.Name.Trim() == "fighterAbilityMissiles" ? MissilesDuration : AttackMDuration;
                var damage = kinds.Select(kind => Value(kind) * Value(multiplier) * Math.Max(1, squadronSize)).ToArray();
                return (new SimEffect(moduleId, type.TypeId, effect.Id, effect.Guid, CycleOr(duration), 0, 1, targetId, null, null, [], [], null,
                    new SimAction.Doomsday(0, damage, 0, 0)), null);
            }

            case "fighterAbilityKamikaze":
                return (null, KamikazeRefusal);
            case "fighterAbilityLaunchBomb":
                return (null, LaunchBombRefusal);
            default:
                return (null, UnsupportedEffect);
        }
    }
}
