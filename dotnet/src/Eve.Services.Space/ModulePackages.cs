using Eve.Dogma;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// What the simulation needs to run an effect of a module: the length of its cycle and what a cycle costs, the numbers of the ship with the effect and
/// without it, and the values the client is told at the start and the end. The rules are the client's; the numbers are dogma's (<c>Eve.Dogma</c>).
/// </summary>
internal static class ModulePackages
{
    private const int Mass = 4;
    private const int MaxVelocity = 37;
    private const int SignatureRadius = 552;
    private const int SpeedFactor = 20;
    private const int SignatureBonus = 554;
    private const int SpeedBoostFactor = 567;
    private const int MassAddition = 796;

    private const string Afterburner = "moduleBonusAfterburner";
    private const string Microwarpdrive = "moduleBonusMicrowarpdrive";

    /// <summary>Why a package cannot be made, or the package.</summary>
    public readonly record struct Result(SimEffect? Effect, string? Reason, int ModuleType, string ModuleName);

    /// <summary>
    /// A propulsion module's numbers. The ship is heavier by the module's mass addition, and faster by the module's speed factor (a percentage) times its
    /// speed boost factor divided by that new mass; a microwarpdrive also makes the ship look bigger (its signature bonus, a percentage).
    /// </summary>
    public static (SimNumbers On, SimNumbers Off) Propulsion(double mass, double maxVelocity, double signatureRadius, double massAddition, double speedFactor, double speedBoostFactor, double signatureBonus)
    {
        var heavier = mass + massAddition;
        var boost = heavier > 0 ? speedFactor / 100.0 * speedBoostFactor / heavier : 0.0;
        return (
            new SimNumbers(Six(heavier), Six(maxVelocity * (1 + boost)), Six(signatureRadius * (1 + signatureBonus / 100.0))),
            new SimNumbers(Six(mass), Six(maxVelocity), Six(signatureRadius)));
    }

    /// <summary>The numbers the client is told are kept to six places, as the reference keeps them.</summary>
    private static double Six(double value) => Math.Round(value * 1_000_000, MidpointRounding.AwayFromZero) / 1_000_000;

    public static Result Of(ShipSnapshot snapshot, DogmaStatic statics, long moduleId, string effectName, long targetId, int repeat)
    {
        var module = snapshot.Fitted.FirstOrDefault(m => m.Id == moduleId);
        if (module is null || statics.Type(module.TypeId) is not { } type)
        {
            return new Result(null, "MODULE_NOT_FOUND", 0, "");
        }
        var name = type.Name;
        var effect = type.EffectIds.Select(statics.Effect).FirstOrDefault(e => e is { } found && found.Name.Equals(effectName, StringComparison.OrdinalIgnoreCase));
        if (effect is null || effect.Category is not (EffectDef.Activation or EffectDef.Target))
        {
            return new Result(null, "UNSUPPORTED_EFFECT", module.TypeId, name);
        }
        if (snapshot.World.Item(moduleId) is not { State: >= ItemState.Online })
        {
            return new Result(null, "MODULE_NOT_ONLINE", module.TypeId, name);
        }
        if (effect.Category == EffectDef.Target && targetId <= 0)
        {
            return new Result(null, "TARGET_REQUIRED", module.TypeId, name);
        }
        // only the effects that change how the ship moves are run so far; the others (repairs, weapons, mining) come with the ship's hit points
        if (effect.Name is not (Afterburner or Microwarpdrive))
        {
            return new Result(null, "UNSUPPORTED_EFFECT", module.TypeId, name);
        }

        var world = snapshot.World;
        var ship = snapshot.Ship.Id;
        double Get(long item, int attribute) => world.Get(item, attribute);
        var (on, off) = Propulsion(
            Get(ship, Mass), Get(ship, MaxVelocity), Get(ship, SignatureRadius), Get(moduleId, MassAddition), Get(moduleId, SpeedFactor), Get(moduleId, SpeedBoostFactor),
            effect.Name == Microwarpdrive ? Get(moduleId, SignatureBonus) : 0);
        var duration = effect.DurationAttributeId > 0 ? Get(moduleId, effect.DurationAttributeId) : 0;
        var capacitor = effect.DischargeAttributeId > 0 ? Get(moduleId, effect.DischargeAttributeId) : 0;
        var moduleAttributes = new List<int> { SpeedFactor, effect.DischargeAttributeId, effect.DurationAttributeId };
        if (effect.Name == Microwarpdrive)
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
        var package = new SimEffect(moduleId, module.TypeId, effect.Id, effect.Guid, duration, capacitor, Math.Max(1, repeat), Math.Max(0, targetId), on, off, Changes(on), Changes(off));
        return new Result(package, null, module.TypeId, name);
    }
}
