using Eve.Dogma;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The defender launcher (group 512, Defender Launcher I; effect <c>defenderMissileLaunching</c> of its charge), and the countermeasure launcher (group 308, unpublished)
/// when it is loaded with defenders (its missiles and torpedoes are <see cref="MissileEffect"/>'s). Each cycle its defender missile (the charge's flight,
/// <see cref="MissileEffect.MissileOf"/>) goes for the nearest hostile missile in its reach and shoots it down, or hurts it by the charge's four damages when it does not
/// destroy it (<see cref="SimAction.Defender"/>). The launcher is target-less: nothing is locked, and the static data names no target for the defender (DEVIATIONS).
/// The damages are the charge's own, not scaled by the pilot's missile damage multiplier, which is for missiles that hit ships.
/// </summary>
internal sealed class DefenderEffect : ShipEffect
{
    private const int DefenderLauncherGroup = 512;
    private const int CountermeasureLauncherGroup = 308;

    public override IReadOnlyCollection<string> EffectNames { get; } = [MissileEffect.LauncherEffectName];

    protected override bool Handles(ModuleEffectContext module) =>
        module.Type.GroupId == DefenderLauncherGroup || module.Type.GroupId == CountermeasureLauncherGroup && MissileEffect.IsDefenderCharge(module);

    protected override string? Refusal(ModuleEffectContext module) => Deployables.NoRounds(module);

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var charge = module.Snapshot.ChargeOf(module.Module)!;
        var value = Deployables.ChargeValue(module, charge);
        var damage = new[] { EmDamage, ThermalDamage, KineticDamage, ExplosiveDamage }.Select(value).ToArray();
        var fx = module.Effect.Guid.Trim() is { Length: > 0 } guid && !guid.Equals("none", StringComparison.OrdinalIgnoreCase) ? guid : Deployables.MissileDeployment;
        return Deployables.Launch(module, charge, new SimAction.Defender(MissileEffect.MissileOf(module, charge), damage), fx);
    }
}
