using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The titan lances (<c>doomsdayBeamDOT</c>: 40631 and the racial 41439, 41440, 41441): a cylinder of <c>doomsdayDamageRadius</c> 2500 m that runs the
/// fixed <c>maxRange</c> (200 km) along the aim from the hull, hit by a pulse each <c>doomsdayDamageCycleTime</c> through the 15 s window after a warning of
/// <c>doomsdayWarningDuration</c>. The reference (<c>superweaponRuntime.js</c>, <c>tickLanceEffect</c>) hits every ship inside the cylinder with the
/// module's damage, scaled by the target's signature factor, and sends the energy neutralisation wave at the start; the ship is held for its windows.
/// </summary>
/// <remarks>
/// The empire-space rule is the client's own (<c>disallowInEmpireSpace</c> 1074 is set, so low and high security refuse it, as <see cref="ModuleRestrictions"/>
/// states: lances are used in zero security only, which is what the reference's <c>MODULE_RESTRICTED_IN_LOWSEC</c> says too).
/// NOT BUILT: the lance beacon (EveJS <c>spawnLanceBeacon</c>, a transient entity at the target point that the client's beam is drawn to). The damage does not
/// depend on it, so the activation runs; the beacon's graphic and its client messages are UNVERIFIED and remain a gap (docs/project/TODO.md section 12).
/// </remarks>
internal sealed class LanceEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["doomsdayBeamDOT"];

    protected override string? Refusal(ModuleEffectContext module)
    {
        if (module.Aim is not { Count: 3 } aim || DoomsdayStrike.Unit(aim) is null)
        {
            return "TARGET_POINT_REQUIRED";
        }
        // the static data says the shape is a cylinder (doomsdayAOEShape 1); another shape would be a different body, not this one
        if (module.Value(DoomsdayAoeShape, 0) != 1)
        {
            return "SUPERWEAPON_LANCE_NOT_IMPLEMENTED";
        }
        return DoomsdayStrike.Of(module, aim) is { IsValid: true } ? null : "SUPERWEAPON_LANCE_DATA_MISSING";
    }

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, Three(duration))];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), 0, null, null, told, told, null,
            DoomsdayStrike.Of(module, module.Aim!));
    }
}
