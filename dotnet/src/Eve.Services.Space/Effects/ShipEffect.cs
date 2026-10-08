using Eve.Dogma;
using Eve.Npc;
using Eve.Ships;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>The context a module effect handler of the space host gets: dogma, the ship's snapshot as the host state.</summary>
internal sealed class ModuleEffectContext
{
    public ModuleEffectContext(EffectContext<ShipSnapshot> context)
    {
        Context = context;
        Snapshot = context.Host;
        Module = Snapshot.Fitted.First(m => m.Id == context.ItemId);
        Attributes = context.World.Attributes(context.ItemId);
    }

    public EffectContext<ShipSnapshot> Context { get; }
    public ShipSnapshot Snapshot { get; }
    public DogmaWorld World => Context.World;
    public DogmaStatic Statics => Context.Statics;
    public EffectDef Effect => Context.Effect;
    public TypeDef Type => Context.Type;
    public long ModuleId => Context.ItemId;
    public Eve.Inventory.Item Module { get; }
    public long TargetId => Context.TargetId;
    public int Repeat => Context.Repeat;

    /// <summary>The module's attributes after dogma.</summary>
    public IReadOnlyDictionary<int, double> Attributes { get; }

    public double Value(int attribute, double fallback) => Attributes.TryGetValue(attribute, out var value) && double.IsFinite(value) ? value : fallback;

    /// <summary>The length of a cycle of the effect, what it costs the capacitor, and the attribute the duration was read from: the module's own values after dogma.</summary>
    public (double Duration, double Capacitor, int DurationAttribute) Cycle() => ShipEffect.CycleOf(Effect, Value);
}

/// <summary>
/// A handler of the space host: it serves effect names and builds what the simulation runs for them (a <see cref="SimEffect"/>). Derive from this, name the effects, and register
/// the handler in <see cref="ModuleEffects.Default"/>.
/// </summary>
internal abstract class ShipEffect : IEffectHandler<ShipSnapshot, SimEffect>
{
    public abstract IReadOnlyCollection<string> EffectNames { get; }

    bool IEffectHandler<ShipSnapshot, SimEffect>.Handles(EffectContext<ShipSnapshot> context) => Handles(new ModuleEffectContext(context));

    string? IEffectHandler<ShipSnapshot, SimEffect>.Refusal(EffectContext<ShipSnapshot> context) => Refusal(new ModuleEffectContext(context));

    bool IEffectHandler<ShipSnapshot, SimEffect>.Implemented => Implemented;

    SimEffect IEffectHandler<ShipSnapshot, SimEffect>.Build(EffectContext<ShipSnapshot> context) => Build(new ModuleEffectContext(context));

    /// <summary>
    /// The length of a cycle of an effect, what it costs the capacitor, and the attribute the duration was read from: the item's own values after dogma (a module's, or a
    /// drone's). <paramref name="value"/> gives an attribute's value, or the fallback when the item has none.
    /// </summary>
    public static (double Duration, double Capacitor, int DurationAttribute) CycleOf(EffectDef effect, Func<int, double, double> value)
    {
        var durationAttribute = effect.DurationAttributeId > 0 ? effect.DurationAttributeId : Speed;
        var duration = Math.Max(1, NpcArmament.Round6(value(durationAttribute, value(Speed, 1000))));
        var capacitor = Math.Max(0, NpcArmament.Round6(value(effect.DischargeAttributeId > 0 ? effect.DischargeAttributeId : CapacitorNeed, 0)));
        return (duration, capacitor, durationAttribute);
    }

    /// <summary>Whether this handler serves this module (default: every module that has the effect).</summary>
    protected virtual bool Handles(ModuleEffectContext module) => true;

    /// <summary>The user-error reason that stops the effect from starting (before the target is asked for), or null.</summary>
    protected virtual string? Refusal(ModuleEffectContext module) => null;

    /// <summary>False for a handler that only refuses (a pending body); see <see cref="IEffectHandler{THost, TResult}.Implemented"/>.</summary>
    protected virtual bool Implemented => true;

    protected abstract SimEffect Build(ModuleEffectContext module);
}
