using Eve.Dogma;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The Warp Disruption Field Generator (group 899; effect <c>warpDisruptSphere</c>): while it runs, the ship is a warp-disruption source of the field's range around
/// itself (<see cref="SimAction.WarpBubble"/>, <c>WARP_DISRUPTED_BY_BUBBLE</c> for a ship in it that is not immune), and it carries the generator's penalties on
/// the ship (client <c>warpDisruptSphere.py</c> MODIFIER_CHANGES): the mass bonus, the velocity modifier and the signature bonus, each a percentage of the ship's own
/// number. The penalty values are the generator's own attributes in the static data (the Generator I: signature +50 %, the others zero), so no table is written.
/// The speed factor and speed boost bonuses of the propulsion modules are zero on every generator in the static data; a generator that has one is refused as
/// <see cref="PendingModuleEffect.NotImplemented"/> until those bonuses are written. The hazard restriction (attribute 5561) is not enforced: the client does not
/// check it (DEVIATIONS). The empire restriction is the host's (<c>ModuleRestrictions</c>).
/// </summary>
internal sealed class WarpDisruptSphereEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["warpDisruptSphere"];

    protected override bool Handles(ModuleEffectContext module) => module.Type.GroupId == FieldGeneratorGroup;

    private const int FieldGeneratorGroup = 899;

    protected override string? Refusal(ModuleEffectContext module) =>
        module.World.Get(module.ModuleId, SpeedFactorBonus) != 0 || module.World.Get(module.ModuleId, SpeedBoostFactorBonus) != 0
            ? PendingModuleEffect.NotImplemented
            : null;

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var shipId = module.Snapshot.Ship.Id;
        var range = module.World.Get(module.ModuleId, WarpScrambleRange);
        double Ship(int attribute) => module.World.Get(shipId, attribute);
        double Percent(int attribute) => module.World.Get(module.ModuleId, attribute) / 100.0;
        var mass = Ship(Mass);
        var maxVelocity = Ship(MaxVelocity);
        var signature = Ship(SignatureRadius);
        var on = new SimNumbers(mass * (1 + Percent(MassBonusPercentage)), maxVelocity * (1 + Percent(VelocityModifier)), signature * (1 + Percent(SignatureBonus)));
        var off = new SimNumbers(mass, maxVelocity, signature);
        IReadOnlyList<SimChange> Told(SimNumbers numbers) =>
        [
            new(module.ModuleId, CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, Three(duration)),
            new(shipId, Mass, numbers.Mass),
            new(shipId, MaxVelocity, numbers.MaxVelocity),
            new(shipId, SignatureRadius, numbers.SignatureRadius),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), 0,
            on, off, Told(on), Told(off), null, new SimAction.WarpBubble(range));
    }
}
