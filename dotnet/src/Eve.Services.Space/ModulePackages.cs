using Eve.Dogma;
using Eve.Services.Space.Effects;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// What the simulation needs to run an effect of a module: the length of its cycle and what a cycle costs, the numbers of the ship with the effect and
/// without it, and the values the client is told at the start and the end. The rules are the client's; the numbers are dogma's (<c>Eve.Dogma</c>). What each effect builds is the
/// business of its handler (<c>Effects/</c>, registered in <see cref="ModuleEffects"/>); this class only checks what every effect needs and asks the handler.
/// </summary>
internal static class ModulePackages
{
    /// <summary>
    /// Why a package cannot be made, or the package. <see cref="Accepted"/> is what the handler does once the simulation has accepted the start (null when there is no
    /// package); a refused start does not run it.
    /// </summary>
    public readonly record struct Result(SimEffect? Effect, string? Reason, int ModuleType, string ModuleName, Action? Accepted = null);

    /// <summary>
    /// A propulsion module's numbers. The ship is heavier by the module's mass addition, and faster by the module's speed factor (a percentage) times its
    /// speed boost factor divided by that new mass; a microwarpdrive also makes the ship look bigger (its signature bonus, a percentage).
    /// </summary>
    public static (SimNumbers On, SimNumbers Off) Propulsion(double mass, double maxVelocity, double signatureRadius, double massAddition, double speedFactor, double speedBoostFactor, double signatureBonus) =>
        PropulsionEffect.Numbers(mass, maxVelocity, signatureRadius, massAddition, speedFactor, speedBoostFactor, signatureBonus);

    public static Result Of(ShipSnapshot snapshot, DogmaStatic statics, long moduleId, string effectName, long targetId, int repeat)
    {
        var module = snapshot.Fitted.FirstOrDefault(m => m.Id == moduleId);
        if (module is null || statics.Type(module.TypeId) is not { } type)
        {
            return new Result(null, "MODULE_NOT_FOUND", 0, "");
        }
        var name = type.Name;
        var effect = type.EffectIds.Select(statics.Effect).FirstOrDefault(e => e is { } found && found.Name.Equals(effectName, StringComparison.OrdinalIgnoreCase))
            ?? MissileEffect.LauncherEffectFor(type, statics, effectName);   // a launcher is fired with the effect of its charge, and runs its own
        if (effect is null || effect.Category is not (EffectDef.Activation or EffectDef.Target))
        {
            return new Result(null, "UNSUPPORTED_EFFECT", module.TypeId, name);
        }
        if (snapshot.World.Item(moduleId) is not { State: >= ItemState.Online })
        {
            return new Result(null, "MODULE_NOT_ONLINE", module.TypeId, name);
        }

        var context = new EffectContext<ShipSnapshot>(snapshot, snapshot.World, statics, moduleId, type, effect, targetId, repeat);
        var handler = ModuleEffects.Default.Find(context);
        // the handler may refuse before the target is asked for (a gun with nothing to shoot is refused first: the reference's order)
        if (handler?.Refusal(context) is { } reason)
        {
            return new Result(null, reason, module.TypeId, name);
        }
        if (effect.Category == EffectDef.Target && targetId <= 0)
        {
            return new Result(null, "TARGET_REQUIRED", module.TypeId, name);
        }
        // only the effects that have a handler are run so far (see ModuleEffects.Default.Uncovered); the others come with what they need
        if (handler is null)
        {
            return new Result(null, "UNSUPPORTED_EFFECT", module.TypeId, name);
        }
        return new Result(handler.Build(context), null, module.TypeId, name, () => handler.Accepted(context));
    }
}
