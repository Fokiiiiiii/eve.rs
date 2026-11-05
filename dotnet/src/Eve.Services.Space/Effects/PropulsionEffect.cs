using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The afterburner and the microwarpdrive. The ship is heavier by the module's mass addition, and faster by the module's speed factor (a percentage) times its speed boost factor divided by
/// that new mass; a microwarpdrive also makes the ship look bigger (its signature bonus, a percentage). The numbers with the effect on and off are told to the simulation and to the client.
/// </summary>
internal sealed class PropulsionEffect : ShipEffect
{
    private const string Microwarpdrive = "moduleBonusMicrowarpdrive";

    public override IReadOnlyCollection<string> EffectNames { get; } = ["moduleBonusAfterburner", Microwarpdrive];

    /// <summary>A propulsion module's numbers: see the class summary.</summary>
    public static (SimNumbers On, SimNumbers Off) Numbers(double mass, double maxVelocity, double signatureRadius, double massAddition, double speedFactor, double speedBoostFactor, double signatureBonus)
    {
        var heavier = mass + massAddition;
        var boost = heavier > 0 ? speedFactor / 100.0 * speedBoostFactor / heavier : 0.0;
        return (
            new SimNumbers(Six(heavier), Six(maxVelocity * (1 + boost)), Six(signatureRadius * (1 + signatureBonus / 100.0))),
            new SimNumbers(Six(mass), Six(maxVelocity), Six(signatureRadius)));
    }

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var world = module.World;
        var ship = module.Snapshot.Ship.Id;
        var moduleId = module.ModuleId;
        var effect = module.Effect;
        double Get(long item, int attribute) => world.Get(item, attribute);
        var microwarpdrive = effect.Name.Trim().Equals(Microwarpdrive, StringComparison.OrdinalIgnoreCase);
        var (on, off) = Numbers(
            Get(ship, Mass), Get(ship, MaxVelocity), Get(ship, SignatureRadius), Get(moduleId, MassAddition), Get(moduleId, SpeedFactor), Get(moduleId, SpeedBoostFactor),
            microwarpdrive ? Get(moduleId, SignatureBonus) : 0);
        var duration = effect.DurationAttributeId > 0 ? Get(moduleId, effect.DurationAttributeId) : 0;
        var capacitor = effect.DischargeAttributeId > 0 ? Get(moduleId, effect.DischargeAttributeId) : 0;
        var moduleAttributes = new List<int> { SpeedFactor, effect.DischargeAttributeId, effect.DurationAttributeId };
        if (microwarpdrive)
        {
            moduleAttributes.Add(SignatureBonus);
        }
        IReadOnlyList<SimChange> Changes(SimNumbers numbers) =>
        [
            .. moduleAttributes.Where(a => a > 0).Select(a => new SimChange(moduleId, a, Six(Get(moduleId, a)))),
            new SimChange(ship, Mass, numbers.Mass),
            new SimChange(ship, MaxVelocity, numbers.MaxVelocity),
            new SimChange(ship, SignatureRadius, numbers.SignatureRadius),
        ];
        return new SimEffect(moduleId, module.Module.TypeId, effect.Id, effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), Math.Max(0, module.TargetId), on, off, Changes(on), Changes(off));
    }
}
