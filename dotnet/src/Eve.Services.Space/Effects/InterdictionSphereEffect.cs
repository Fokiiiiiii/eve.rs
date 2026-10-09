using Eve.Dogma;
using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The interdiction sphere launcher (group 589, effect <c>torpedoLaunching</c> of its charge). A warp disruption probe (warpScrambleRange above zero: the Warp Disrupt
/// Probe, 20 km, and the Surgical, 10 km) is put in space at the ship each cycle: a warp-disruption source of its range for its explosion delay (120 s and 180 s), then
/// gone (ADR 0012's addendum: a simulation-only probe; it stands still where the ship is, the reference's spawn). A stasis webification charge (doomsdayAOERange above
/// zero) is a burst instead: the web of <see cref="SimAction.StasisBurst"/>, on the ships in its area.
/// </summary>
internal sealed class InterdictionSphereEffect : ShipEffect
{
    private const int InterdictionSphereLauncherGroup = 589;
    private const int WarpScrambleRange = 103;
    private const int ExplosionDelay = 281;

    public override IReadOnlyCollection<string> EffectNames { get; } = [MissileEffect.LauncherEffectName];

    protected override bool Handles(ModuleEffectContext module) => module.Type.GroupId == InterdictionSphereLauncherGroup;

    protected override string? Refusal(ModuleEffectContext module)
    {
        if (Deployables.NoRounds(module) is { } none)
        {
            return none;
        }
        var value = Deployables.ChargeValue(module, module.Snapshot.ChargeOf(module.Module)!);
        return value(WarpScrambleRange) > 0 || value(DoomsdayAoeRange) > 0 ? null : PendingModuleEffect.NotImplemented;
    }

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var charge = module.Snapshot.ChargeOf(module.Module)!;
        var value = Deployables.ChargeValue(module, charge);
        var fx = module.Effect.Guid.Trim() is { Length: > 0 } guid && !guid.Equals("none", StringComparison.OrdinalIgnoreCase) ? guid : Deployables.TorpedoDeployment;
        // a stasis charge is a burst on the ships about it (the web of its doomsday area, for its duration, slowing by its speed factor); a warp disruption charge is a probe
        if (value(WarpScrambleRange) <= 0)
        {
            var burst = new SimAction.StasisBurst(value(DoomsdayAoeRange), value(DoomsdayAoeDuration), value(SpeedFactor));
            return Deployables.Launch(module, charge, burst, fx);
        }
        var probe = new SimAction.WarpProbe(
            Deployables.Ball(module, charge, value), value(WarpScrambleRange), Math.Max(1, NpcArmament.Round6(value(ExplosionDelay))));
        return Deployables.Launch(module, charge, probe, fx);
    }
}
