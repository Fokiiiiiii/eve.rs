using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// A smartbomb's package (<c>smartbombRuntime.js</c> of the reference, ADR 0009): the cycle and its cost are the module's own values after dogma, the field is its <c>empFieldRange</c> and
/// each cycle does its damage times the damage multiplier to every ship in it. It starts as one that hurts pilots: the host takes that back where the safety of the pilot would stop it.
/// </summary>
internal sealed class SmartbombEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["empWave"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var multiplier = Math.Max(0, module.Value(DamageMultiplier, 1));
        double Damage(int attribute) => NpcArmament.Round6(Math.Max(0, module.Value(attribute, 0)) * multiplier);
        var range = Math.Max(0, NpcArmament.Round6(module.Value(EmpFieldRange, 0)));
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, Three(duration)), new(module.ModuleId, EmpFieldRange, range)];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), 0, null, null, told, told, null,
            new SimAction.Smartbomb(range, [Damage(EmDamage), Damage(ThermalDamage), Damage(KineticDamage), Damage(ExplosiveDamage)], true));
    }
}

/// <summary>Packages the non-targeted ECM burst from dogma's range and racial sensor-jamming strengths.</summary>
internal sealed class EcmBurstEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["ecmBurstJammer"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var range = Math.Max(0, NpcArmament.Round6(module.Value(EcmBurstRange, 0)));
        double Strength(int attribute) => NpcArmament.Round6(Math.Max(0, module.Value(attribute, 0)));
        SimChange[] told =
        [
            new(module.ModuleId, CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, Three(duration)),
            new(module.ModuleId, EcmBurstRange, range)
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), 0, null, null, told, told, null,
            new SimAction.EcmBurst(range, [Strength(ScanGravimetricStrengthBonus), Strength(ScanLadarStrengthBonus), Strength(ScanMagnetometricStrengthBonus), Strength(ScanRadarStrengthBonus)]));
    }
}

/// <summary>
/// A cloaking device (group 330). The refusals the ship's own dogma decides are here: a ship that cannot cloak (<c>canCloak</c>, which a module can take away) and one that
/// disallows it (<c>disallowCloaking</c>), as the client's <c>FakeCloaking.PreStartChecks</c>. A cloak has no duration of its own in the static data, so it tells the
/// client no duration: the speed it used to tell was not the module's (no cloak type carries that attribute).
/// The refusals that need what is running (a second device, a module that cannot be stopped early) are the simulation's, which knows what runs.
/// </summary>
internal sealed class CloakingEffect : ShipEffect
{
    private const string CanCloakName = "canCloak";
    private const string DisallowCloakingName = "disallowCloaking";

    public override IReadOnlyCollection<string> EffectNames { get; } = ["cloaking", "cloakingWarpSafe", "cloakingPrototype"];

    protected override bool Handles(ModuleEffectContext module) => module.Type.GroupId == 330;

    protected override string? Refusal(ModuleEffectContext module)
    {
        var shipId = module.Snapshot.Ship.Id;
        if (module.Statics.AttributeId(CanCloakName) is { } canCloak && module.World.Get(shipId, canCloak) == 0)
        {
            return "CLOAK_SHIP_UNSUITABLE";
        }
        if (module.Statics.AttributeId(DisallowCloakingName) is { } disallowed && module.World.Get(shipId, disallowed) != 0)
        {
            return "CLOAK_DISALLOWED";
        }
        // a fitted disruptive lance disallows the cloak while it is fitted (disruptionLanceDisallowCloaking, 5425: EveJS entityHasCloakDisallowingFittedModule)
        if (module.Snapshot.Fitted.Any(fitted => module.World.Get(fitted.Id, DisruptionLanceDisallowCloaking) != 0))
        {
            return "CLOAK_DISALLOWED";
        }
        return null;
    }

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var told = new List<SimChange> { new(module.ModuleId, CapacitorNeed, capacitor) };
        if (module.Effect.DurationAttributeId > 0)
        {
            told.Add(new SimChange(module.ModuleId, durationAttribute, Three(duration)));
        }
        SimChange[] changes = [.. told];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor,
            Math.Max(1, module.Repeat), 0, null, null, changes, changes, null, new SimAction.Cloak());
    }
}
