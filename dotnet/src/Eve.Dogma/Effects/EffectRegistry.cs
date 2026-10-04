namespace Eve.Dogma;

/// <summary>What a handler is told about the effect that is being run: the host's own state (for the space host, the snapshot of the ship), dogma, the item that has the effect and its type.</summary>
public sealed record EffectContext<THost>(THost Host, DogmaWorld World, DogmaStatic Statics, long ItemId, TypeDef Type, EffectDef Effect, long TargetId, int Repeat);

/// <summary>
/// The code behind one or more effects that the static data cannot describe with modifiers alone (what a module DOES each cycle: shoot, repair, web, jam). A handler names the effects it
/// serves and turns a context into the host's package (for the space host, what the simulation runs). Add a handler and register it; do not add a branch to a switch.
/// </summary>
public interface IEffectHandler<THost, TResult>
{
    /// <summary>The effect names (<c>effectName</c> of the static data) this handler serves, compared without regard to case.</summary>
    IReadOnlyCollection<string> EffectNames { get; }

    /// <summary>Whether this handler serves this very item (several handlers may share a name when the type decides: a turret's <c>targetAttack</c> and a drone's).</summary>
    bool Handles(EffectContext<THost> context) => true;

    /// <summary>Why the effect cannot start (no ammunition), or <c>null</c>. Asked before the host checks that there is a target.</summary>
    string? Refusal(EffectContext<THost> context) => null;

    TResult Build(EffectContext<THost> context);
}

/// <summary>
/// Finds the handler of an effect by its name. The registry knows nothing about what a handler builds, so one mechanism serves every host (the space simulation today; drones,
/// structures and the rest tomorrow). It also says which effects of the static data have no handler, so "what is not implemented yet" is a list that can be read, not a guess.
/// </summary>
public sealed class EffectRegistry<THost, TResult>
{
    private readonly Dictionary<string, List<IEffectHandler<THost, TResult>>> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a handler under each of its names. Returns the registry, to chain.</summary>
    public EffectRegistry<THost, TResult> Register(IEffectHandler<THost, TResult> handler)
    {
        if (handler.EffectNames.Count == 0)
        {
            throw new ArgumentException($"{handler.GetType().Name} names no effect", nameof(handler));
        }
        foreach (var name in handler.EffectNames)
        {
            if (!_byName.TryGetValue(name.Trim(), out var list))
            {
                _byName[name.Trim()] = list = [];
            }
            list.Add(handler);
        }
        return this;
    }

    /// <summary>The handler that serves this item's effect, or <c>null</c> when none does.</summary>
    public IEffectHandler<THost, TResult>? Find(EffectContext<THost> context) =>
        _byName.TryGetValue(context.Effect.Name.Trim(), out var list) ? list.FirstOrDefault(h => h.Handles(context)) : null;

    /// <summary>The effect names that have a handler.</summary>
    public IReadOnlyCollection<string> Names => _byName.Keys;

    /// <summary>The effects of the given categories (activation and target, by default) in the static data that no handler serves.</summary>
    public IReadOnlyList<EffectDef> Uncovered(DogmaStatic statics, params int[] categories)
    {
        var wanted = categories.Length > 0 ? categories : [EffectDef.Activation, EffectDef.Target];
        return [.. statics.Effects.Where(e => wanted.Contains(e.Category) && !_byName.ContainsKey(e.Name.Trim())).OrderBy(e => e.Name, StringComparer.Ordinal)];
    }
}
