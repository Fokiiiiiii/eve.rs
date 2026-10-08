using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The titan doomsdays (<c>superWeaponAmarr</c>, <c>Caldari</c>, <c>Gallente</c>, <c>Minmatar</c>: the Judgment 24550, Gjallarhorn 23674, Oblivion 24552 and
/// Aurora Ominae 24554): a single-target hit on the ship the module is activated on. The client does not aim them (no point: the module's
/// <c>isPointTargeted</c> is not set), it activates them with the locked target, and the reference (<c>superweaponRuntime.js</c>,
/// <c>executeSuperweaponActivation</c>, <c>tickDoomsdayEffect</c>) hits that target alone, once, <c>damageDelayDuration</c> after the start, with the module's
/// one damage kind of 2 400 000 (the other three kinds are 0). The ship is held for the windows the static data gives (immobility 2428, no jump or cloak 2427).
/// </summary>
/// <remarks>
/// The target is checked by the simulation (a locked ship of the scene: TARGET_NOT_LOCKED, TARGET_NOT_FOUND) and the host refuses a missing target
/// (TARGET_REQUIRED). A titan has no lightning attributes (2104 to 2106), so the reference's chain is one ship: no chain is run. The hit takes no
/// signature factor (the reference applies none to a doomsday). The structure variant (category 66) is a different effect, refused by name (O5).
/// Not verified: the titan's graphic (its FX comes from <c>SuperWeaponFx</c>, UNVERIFIED in the client) and the client text of the windows' refusals.
/// </remarks>
internal sealed class DoomsdayTitanEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } =
        ["superWeaponAmarr", "superWeaponCaldari", "superWeaponGallente", "superWeaponMinmatar"];

    protected override string? Refusal(ModuleEffectContext module)
    {
        if (module.TargetId <= 0)
        {
            return "TARGET_REQUIRED";
        }
        return Doomsday(module) is { IsValid: true } ? null : "SUPERWEAPON_DOOMSDAY_DATA_MISSING";
    }

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, Three(duration))];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, 1, Math.Max(0, module.TargetId), null, null, told, told, null,
            Doomsday(module));
    }

    /// <summary>The hit of the module, from its data after dogma: the delay, the four kinds of damage and the two windows.</summary>
    private static SimAction.Doomsday Doomsday(ModuleEffectContext module)
    {
        double Attribute(int attribute) => NpcArmament.Round6(Math.Max(0, module.Value(attribute, 0)));
        return new SimAction.Doomsday(
            Attribute(DamageDelayDuration),
            [Attribute(EmDamage), Attribute(ThermalDamage), Attribute(KineticDamage), Attribute(ExplosiveDamage)],
            Attribute(DoomsdayImmobilityDuration),
            Attribute(DoomsdayNoJumpOrCloakDuration));
    }
}
