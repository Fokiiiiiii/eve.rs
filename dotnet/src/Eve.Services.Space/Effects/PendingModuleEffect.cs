using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The handler of a module effect whose body is not written yet (the exploration, hacking and scanner effects in <c>ExplorationModuleEffects.cs</c>). It is
/// registered like any other handler, so its module is fitted and onlined as usual. It is not coverage: <see cref="IEffectHandler{THost, TResult}.Implemented"/>
/// is false and the effect stays listed as uncovered. An activation is refused with <see cref="NotImplemented"/>: the module does not cycle, takes no capacitor and reports no success, and the client is told the module cannot be activated
/// (<c>ModuleRefusals.Of</c>). <see cref="ModulePackages.Of"/> asks the refusal after the module is checked to be online and before a target is asked
/// for, so a pending effect is refused the same way whether or not it has a target.
/// </summary>
/// <remarks>
/// EXTENSION POINT. To make an effect real, its concrete handler overrides <see cref="Build"/> to return the <see cref="SimEffect"/> that the simulation runs (the
/// cycle, the cost, the numbers, the action), and overrides <see cref="ShipEffect.Refusal"/> for the checks its effect needs (no charge, no target in range, ...).
/// Until then <see cref="Build"/> throws: a package that cycles and does nothing would be a success that is not one.
/// </remarks>
internal abstract class PendingModuleEffect : ShipEffect
{
    /// <summary>The refusal of an effect whose body is pending. It is not a success, and it is not <c>UNSUPPORTED_EFFECT</c>: the handler exists.</summary>
    public const string NotImplemented = "EFFECT_NOT_IMPLEMENTED";

    private readonly string[] _effectNames;

    /// <param name="effectNames">The static data's effect names this handler serves (case-insensitive, as the registry compares them).</param>
    protected PendingModuleEffect(params string[] effectNames)
    {
        if (effectNames.Length == 0)
        {
            throw new ArgumentException("A pending module effect names at least one effect", nameof(effectNames));
        }
        _effectNames = effectNames;
    }

    public sealed override IReadOnlyCollection<string> EffectNames => _effectNames;

    /// <summary>The pending refusal. An implementation replaces it with its own checks (or null when there are none).</summary>
    protected override string? Refusal(ModuleEffectContext module) => NotImplemented;

    /// <summary>
    /// False: the handler only refuses, so its effect stays uncovered in <see cref="EffectRegistry{THost, TResult}.Uncovered"/>. An implementation that writes the body
    /// removes this override (the default is true).
    /// </summary>
    protected override bool Implemented => false;

    /// <summary>
    /// The body of the effect: the <see cref="SimEffect"/> of a module that may start. Not written yet, so it throws, and it is not reached while
    /// <see cref="Refusal"/> refuses every activation.
    /// </summary>
    protected override SimEffect Build(ModuleEffectContext module) =>
        throw new NotSupportedException(
            $"Effect {module.Effect.Name} has no body yet; it is refused as {NotImplemented} (see docs/project/TODO.md, extension point PendingModuleEffect.Build).");
}
