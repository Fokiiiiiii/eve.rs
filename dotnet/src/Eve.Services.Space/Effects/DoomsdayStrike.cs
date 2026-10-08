using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The strike of a point-targeted doomsday (protocol 25, <see cref="SimAction.Strike"/>), built from the module's data after dogma. The cone (40633) and the
/// lances (40631 and the racial lances) run the same strike: the shape is the static data's (<c>doomsdayAOEShape</c>: 1 a cylinder of
/// <c>doomsdayDamageRadius</c>, 3 a cone), the length is <c>maxRange</c>, the warning and the pulses are <c>doomsdayWarningDuration</c>,
/// <c>doomsdayDamageCycleTime</c> and <c>doomsdayDamageDuration</c> (one pulse at the start of the window and one every cycle time up to and including its end,
/// as the reference's window is closed), the damage is the four damage attributes, and the windows and the energy neutralisation are the module's.
/// </summary>
internal static class DoomsdayStrike
{
    /// <summary>The strike the module's data gives when aimed along <paramref name="aim"/> (a point from the ship; its direction is the aim).</summary>
    public static SimAction.Strike Of(ModuleEffectContext module, IReadOnlyList<double> aim)
    {
        double Attribute(int attribute) => NpcArmament.Round6(Math.Max(0, module.Value(attribute, 0)));
        var direction = Unit(aim) ?? [0.0, 0.0, 0.0];
        var pulse = Attribute(DoomsdayDamageCycleTime);
        var window = Attribute(DoomsdayDamageDuration);
        // the reference's window is closed at its end: a pulse at its start and at every cycle time after it, up to and including its end
        var pulses = pulse > 0 ? (int)Math.Floor(window / pulse) + 1 : 0;
        return new SimAction.Strike(
            (byte)Attribute(DoomsdayAoeShape),
            direction,
            Attribute(MaxRange),
            Attribute(DoomsdayDamageRadius),
            Attribute(DoomsdayWarningDuration),
            pulse,
            pulses,
            [Attribute(EmDamage), Attribute(ThermalDamage), Attribute(KineticDamage), Attribute(ExplosiveDamage)],
            Attribute(SignatureRadius),
            Attribute(DoomsdayImmobilityDuration),
            Attribute(DoomsdayNoJumpOrCloakDuration),
            Attribute(DoomsdayEnergyNeutAmount),
            Attribute(DoomsdayEnergyNeutRadius),
            Attribute(DoomsdayEnergyNeutSignatureRadius));
    }

    /// <summary>The unit vector of an aim, or null for a zero or non-finite aim.</summary>
    public static double[]? Unit(IReadOnlyList<double> aim)
    {
        if (aim.Count != 3 || !aim.All(double.IsFinite))
        {
            return null;
        }
        var length = Math.Sqrt(aim.Sum(c => c * c));
        return length > 0 ? [aim[0] / length, aim[1] / length, aim[2] / length] : null;
    }
}
