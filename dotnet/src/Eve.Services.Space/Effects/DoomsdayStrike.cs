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
    /// <param name="aim">The point the strike is aimed at, from the ship (a reaper's start point when <paramref name="sweepEnd"/> is given).</param>
    /// <param name="sweepEnd">A reaper's end point, from the ship: the axis sweeps from <paramref name="aim"/> to it over the window (protocol 28).</param>
    /// <param name="cylinder">A non-cone shape runs as a cylinder (the reference's rule: only shape 3 is a cone). Reapers carry shape 2.</param>
    /// <param name="beacon">A lance or a cone: the beacon is put where the aim ends, clamped to the strike's length (EVE.js <c>clampPointToFixedRange</c>).</param>
    public static SimAction.Strike Of(ModuleEffectContext module, IReadOnlyList<double> aim, IReadOnlyList<double>? sweepEnd = null, bool cylinder = false,
        bool beacon = false)
    {
        double Attribute(int attribute) => NpcArmament.Round6(Math.Max(0, module.Value(attribute, 0)));
        var direction = Unit(aim) ?? [0.0, 0.0, 0.0];
        // a sweep's length is its start point's distance, so that its start is the aim exactly (the simulation runs the axis from there)
        var length = sweepEnd is null ? Attribute(MaxRange) : NpcArmament.Round6(Math.Sqrt(aim.Sum(c => c * c)));
        var beaconOffset = beacon && sweepEnd is null && Unit(aim) is { } unit
            ? unit.Select(c => NpcArmament.Round6(c * Math.Min(length, Math.Sqrt(aim.Sum(a => a * a))))).ToArray()
            : null;
        var pulse = Attribute(DoomsdayDamageCycleTime);
        var window = Attribute(DoomsdayDamageDuration);
        // the reference's window is closed at its end: a pulse at its start and at every cycle time after it, up to and including its end
        var pulses = pulse > 0 ? (int)Math.Floor(window / pulse) + 1 : 0;
        return new SimAction.Strike(
            cylinder ? (byte)1 : (byte)Attribute(DoomsdayAoeShape),
            direction,
            length,
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
            Attribute(DoomsdayEnergyNeutSignatureRadius),
            // the disruptive lance's applied debuff (60 s); 0 for a doomsday with none
            Attribute(DoomsdayAppliedDBuffDuration),
            sweepEnd is null ? null : sweepEnd.Select(NpcArmament.Round6).ToArray(),
            beaconOffset);
    }

    /// <summary>
    /// The sphere of a transport field (the Gravitational Transportation Field Oscillator, shape 4, protocol 29): centred on the aim point, its radius the
    /// static data's <c>doomsdayAOERange</c>, its length the aim's distance so that its centre is the aim point. One pulse at the warning: the static data has no
    /// damage and no cadence, so the cycle's duration stands in for the cadence (it is never reached). The windows and the energy neutralisation are the module's.
    /// </summary>
    public static SimAction.Strike Sphere(ModuleEffectContext module, IReadOnlyList<double> aim)
    {
        double Attribute(int attribute) => NpcArmament.Round6(Math.Max(0, module.Value(attribute, 0)));
        var direction = Unit(aim) ?? [0.0, 0.0, 0.0];
        var length = NpcArmament.Round6(Math.Sqrt(aim.Sum(c => c * c)));
        return new SimAction.Strike(
            4,
            direction,
            length,
            Attribute(DoomsdayAoeRange),
            Attribute(DoomsdayWarningDuration),
            NpcArmament.Round6(Math.Max(1, module.Cycle().Duration)),
            1,
            [0.0, 0.0, 0.0, 0.0],
            // the module has no signature attribute: with no damage there is nothing to scale by it (SimAction.Strike.IsValid accepts that)
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
