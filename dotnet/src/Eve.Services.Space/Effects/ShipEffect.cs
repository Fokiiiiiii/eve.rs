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
    public (double Duration, double Capacitor, int DurationAttribute) Cycle()
    {
        var durationAttribute = Effect.DurationAttributeId > 0 ? Effect.DurationAttributeId : Speed;
        var duration = Math.Max(1, NpcArmament.Round6(Value(durationAttribute, Value(Speed, 1000))));
        var capacitor = Math.Max(0, NpcArmament.Round6(Value(Effect.DischargeAttributeId > 0 ? Effect.DischargeAttributeId : CapacitorNeed, 0)));
        return (duration, capacitor, durationAttribute);
    }
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

    SimEffect IEffectHandler<ShipSnapshot, SimEffect>.Build(EffectContext<ShipSnapshot> context) => Build(new ModuleEffectContext(context));

    /// <summary>Whether this handler serves this module (default: every module that has the effect).</summary>
    protected virtual bool Handles(ModuleEffectContext module) => true;

    /// <summary>The user-error reason that stops the effect from starting (before the target is asked for), or null.</summary>
    protected virtual string? Refusal(ModuleEffectContext module) => null;

    protected abstract SimEffect Build(ModuleEffectContext module);
}
