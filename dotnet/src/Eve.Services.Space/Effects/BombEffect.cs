using Eve.Dogma;
using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The bomb launcher (group 862, Bomb Launcher I and II; effect <c>bombLaunching</c> of the charge): each cycle releases a bomb at the ship, which goes off where it is
/// <c>explosionDelay</c> later. Its blast reaches the ships within the charge's explosion cloud (<c>aoeCloudSize</c>, ADR 0011's addendum: one area, the ships of its
/// recipients). What the blast does is the charge's kind: a neutralising bomb (Void) takes the capacitor of the ships it reaches, an ECM bomb (Lockbreaker) jams them with
/// its four strengths, and a damage bomb hurts them with its four damages (the pilot's missile damage multiplier applies, as it does to a missile). The kind is read from
/// the charge: neutralising if it neutralises, ECM if it has a scan strength bonus, otherwise damage (bombs brief, D1).
/// </summary>
internal sealed class BombEffect : ShipEffect
{
    private const int BombLauncherGroup = 862;
    private const int MissileDamageMultiplier = 212;
    private const int ExplosionDelay = 281;
    private const int BlastRadius = 654;

    public override IReadOnlyCollection<string> EffectNames { get; } = [MissileEffect.LauncherEffectName];

    protected override bool Handles(ModuleEffectContext module) => module.Type.GroupId == BombLauncherGroup;

    protected override string? Refusal(ModuleEffectContext module) => Deployables.NoRounds(module);

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var charge = module.Snapshot.ChargeOf(module.Module)!;
        var value = Deployables.ChargeValue(module, charge);
        var pilot = module.World.Attributes(module.Snapshot.Character.Id);
        var multiplier = Math.Max(0, pilot.TryGetValue(MissileDamageMultiplier, out var m) && double.IsFinite(m) ? m : 1);
        var damage = new[] { EmDamage, ThermalDamage, KineticDamage, ExplosiveDamage }.Select(a => NpcArmament.Round6(value(a) * multiplier)).ToArray();
        var ecm = new[] { ScanGravimetricStrengthBonus, ScanLadarStrengthBonus, ScanMagnetometricStrengthBonus, ScanRadarStrengthBonus }.Select(value).ToArray();
        var neutralize = value(EnergyNeutralizerAmount);
        var kind = neutralize > 0 ? SimBombKind.Neutralize : ecm.Any(strength => strength > 0) ? SimBombKind.Ecm : SimBombKind.Damage;
        var bomb = new SimAction.Bomb(
            Deployables.Ball(module, charge, value), kind, Math.Max(1, NpcArmament.Round6(value(ExplosionDelay))),
            Math.Max(1, NpcArmament.Round6(value(BlastRadius))), damage, ecm, neutralize);
        var fx = module.Effect.Guid.Trim() is { Length: > 0 } guid && !guid.Equals("none", StringComparison.OrdinalIgnoreCase) ? guid : Deployables.MissileDeployment;
        return Deployables.Launch(module, charge, bomb, fx);
    }
}
