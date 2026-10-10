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
/// What is not in this body (named, not run): the cone's graphic (none exists in the static data or in EveJS, so none is sent), the target's capacitor resistance (attribute 2609 names it; no
/// simulation neutralizer applies a resistance, the same as a neutralizer's), and the docking and tethering the reference also forbids in its window (the
/// simulation does not refuse them yet; the jump and cloak are refused). A target that the reference hits but the cone's signature factor scales to nothing is not hit.
/// </remarks>
internal sealed class ConeEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["doomsdayConeDOT"];

    protected override string? Refusal(ModuleEffectContext module)
    {
        if (module.Aim is not { Count: 3 } aim || DoomsdayStrike.Unit(aim) is null)
        {
            return "TARGET_POINT_REQUIRED";
        }
        // the static data says the shape is a cone (doomsdayAOEShape 3); another shape would be a different body, not this one
        if (module.Value(DoomsdayAoeShape, 0) != 3)
        {
            return "SUPERWEAPON_CONE_NOT_IMPLEMENTED";
        }
        return DoomsdayStrike.Of(module, aim) is { IsValid: true } ? null : "SUPERWEAPON_CONE_DATA_MISSING";
    }

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, Three(duration))];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), 0, null, null, told, told, null,
            DoomsdayStrike.Of(module, module.Aim!, beacon: true));
    }
}
