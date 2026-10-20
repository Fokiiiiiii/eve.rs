using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The disruptive lances (<c>debuffLance</c>: 77398, 77399, 77400, 77401): a point-targeted cylinder of <c>doomsdayDamageRadius</c> 2500 m and <c>maxRange</c>
/// 50 km, pulsed through a 15 s window after a 15 s warning, that deals its damage type and leaves a 60 s debuff (<c>doomsdayAppliedDBuffDuration</c> 5412) on
/// expiry. Its cloak is disallowed while fitted (5425), and the lancer is immobile for 30 s and cannot jump or dock for 2 min (SDE 2427 = 120000).
/// </summary>
/// <remarks>
/// The siege requirement: every disruptive lance carries <c>activationRequiresActiveSiegeModule</c> (5426) = 1, and the reference refuses it unless a siege
/// the started modules (the snapshot's <c>StateOf</c>), so the check reads an active module carrying <c>moduleBonusSiegeModule</c>. The cloak disallow (5425) is
/// refused by the cloak handler while the lance is fitted (<see cref="SmartbombEffect"/>).
/// </remarks>
internal sealed class DisruptiveLanceEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["debuffLance"];

    private const string SiegeModuleEffect = "moduleBonusSiegeModule";

    protected override string? Refusal(ModuleEffectContext module)
    {
        if (module.Value(ActivationRequiresActiveSiegeModule, 0) > 0 && !module.IsEffectActiveOnShip(SiegeModuleEffect))
        {
            return "ACTIVE_SIEGE_MODULE_REQUIRED";
        }
        if (module.Aim is not { Count: 3 } aim || DoomsdayStrike.Unit(aim) is null)
        {
            return "TARGET_POINT_REQUIRED";
        }
        if (module.Value(DoomsdayAoeShape, 0) != 1)
        {
            return "SUPERWEAPON_DISRUPTIVE_LANCE_NOT_IMPLEMENTED";
        }
        return DoomsdayStrike.Of(module, aim) is { IsValid: true } ? null : "SUPERWEAPON_DISRUPTIVE_LANCE_DATA_MISSING";
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
