using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The Bosonic Field Generator (40633, <c>doomsdayConeDOT</c>): a cone of damage over time, aimed at a point (the client's <c>ActivateSinglePointTargetedModule</c>).
/// The reference (<c>superweaponRuntime.js</c>, the lance family with <c>coneDotDoomsday</c>, <c>tickLanceEffect</c>) runs it as a warning, then one pulse per
/// <c>doomsdayDamageCycleTime</c> through the <c>doomsdayDamageDuration</c> window, each hitting every ship inside the cone with the module's four damage
/// values, scaled by the target's signature factor; the energy neutralisation wave is sent at the start; the ship is immobile and cannot jump or cloak for its
/// windows. The cone's graphic is empty in the static data and in the reference, so the strike names none (<c>SuperWeaponFx</c> has none for this family).
/// The fuel (consumption 713 and 714) is taken by the shared mechanism of <see cref="ShipEffect"/> (<c>BurnsFuel</c>): one cycle's fuel before each cycle.
/// </summary>
/// <remarks>
/// What is not in this body (named, not run): the cone's graphic (none exists, UNVERIFIED), the target's capacitor resistance (attribute 2609 names it; no
/// simulation neutralizer applies a resistance, the same as a neutralizer's), and the docking and tethering the reference also forbids in its window (the
/// simulation does not refuse them yet; the jump and cloak are refused). A target that the reference hits but the cone's signature factor scales to nothing is not hit.
/// </remarks>
internal sealed class ConeEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["doomsdayConeDOT"];

    protected override string? Refusal(ModuleEffectContext module)
    {
        if (module.Aim is not { Count: 3 } aim || Unit(aim) is null)
        {
            return "TARGET_POINT_REQUIRED";
        }
        // the static data says the shape is a cone (doomsdayAOEShape 3); another shape would be a different body, not this one
        if (module.Value(DoomsdayAoeShape, 0) != 3)
        {
            return "SUPERWEAPON_CONE_NOT_IMPLEMENTED";
        }
        return Strike(module, aim) is { IsValid: true } ? null : "SUPERWEAPON_CONE_DATA_MISSING";
    }

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, Three(duration))];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), 0, null, null, told, told, null,
            Strike(module, module.Aim!));
    }

    /// <summary>The strike of the cone: the aim, the cone's length (its range is fixed), its damage per pulse and its windows, all from the module's data after dogma.</summary>
    private static SimAction.Strike Strike(ModuleEffectContext module, IReadOnlyList<double> aim)
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
            0,
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
    internal static double[]? Unit(IReadOnlyList<double> aim)
    {
        if (aim.Count != 3 || !aim.All(double.IsFinite))
        {
            return null;
        }
        var length = Math.Sqrt(aim.Sum(c => c * c));
        return length > 0 ? [aim[0] / length, aim[1] / length, aim[2] / length] : null;
    }
}
