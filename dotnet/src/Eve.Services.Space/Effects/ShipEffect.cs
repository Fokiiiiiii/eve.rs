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

    /// <summary>The other capsuleer ships of the system (the host's list): what a transport field carries or holds.</summary>
    public IReadOnlyList<TransportNeighbour> Neighbours => Snapshot.Neighbours;
    public long TargetId => Context.TargetId;
    public int Repeat => Context.Repeat;

    /// <summary>The point a point-targeted effect is aimed at (an offset from the ship's centre), or null.</summary>
    public IReadOnlyList<double>? Aim => Context.Aim;

    /// <summary>The second point of a two-point activation (a reaper's slash end), or null.</summary>
    public IReadOnlyList<double>? AimEnd => Context.AimEnd;

    /// <summary>The module's attributes after dogma.</summary>
    public IReadOnlyDictionary<int, double> Attributes { get; }

    /// <summary>
    /// Whether a fitted module of the ship is switched on now and carries the effect <paramref name="effectName"/> (the host's state of the ship: the
    /// modules the pilot has started are active in the snapshot). An active siege module (<c>moduleBonusSiegeModule</c>) is read this way.
    /// </summary>
    public bool IsEffectActiveOnShip(string effectName) => Snapshot.Fitted.Any(fitted =>
        Snapshot.StateOf(fitted.Id) is ItemState.Active or ItemState.Overloaded
        && Statics.Type(fitted.TypeId)?.EffectIds.Select(Statics.Effect).Any(effect => effect is { } found && found.Name.Equals(effectName, StringComparison.OrdinalIgnoreCase)) == true);

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

    /// <summary>
    /// Builds the effect, and marks it as burning fuel when the module's consumption type (713) names one: the common mechanism of every handler (module_rules
    /// WP18), so a fuel-burning effect asks the host for its fuel before each cycle and does not run one the host refuses. Nothing is marked for a module with no type.
    /// </summary>
    SimEffect IEffectHandler<ShipSnapshot, SimEffect>.Build(EffectContext<ShipSnapshot> context)
    {
        var module = new ModuleEffectContext(context);
        var built = Build(module) with { BurnsFuel = module.Value(ModuleAttributes.ConsumptionType, 0) > 0, Rules = RulesOf(module) };

        // moduleCycleSchedule.js). shortcut: the repeat count the client is told for the effect's FX is one too; EveJS (moduleCycleSchedule.js) keeps the client's count, and the V24.01 client's FX repeat for these effects is not traced here, upgrade with a client FX trace.
        return module.Value(ModuleAttributes.DisallowRepeatingActivation, 0) == 1 ? built with { Repeat = 1 } : built;
    }

    void IEffectHandler<ShipSnapshot, SimEffect>.Accepted(EffectContext<ShipSnapshot> context) => Accepted(new ModuleEffectContext(context));

    /// <summary>
    /// The module rules the simulation enforces, worked out from the module's type (module_rules WP3/WP4): the group and its limit (763), the early deactivation
    /// (906), the industrial core a module needs (3265) and the cores themselves (their effects), and the reactivation delay (669). Every handler's effect carries them.
    /// </summary>
    private static SimModuleRules RulesOf(ModuleEffectContext module) => new(
        module.Type.GroupId,
        (int)Math.Max(0, module.Value(ModuleAttributes.MaxGroupActive, 0)),
        module.Value(ModuleAttributes.DisallowEarlyDeactivation, 0) == 1,
        module.Value(ModuleAttributes.ActivationRequiresActiveIndustrialCore, 0) == 1,
        module.Effect.Name is "industrialCoreEffect2" or "industrialCompactCoreEffect2",
        Math.Max(0, module.Value(ModuleAttributes.ModuleReactivationDelay, 0)),
        Math.Max(0, module.Value(ModuleAttributes.MaxVelocityActivationLimit, 0)),
        Math.Max(0, module.Value(ModuleAttributes.MinVelocityActivationLimit, 0)));

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

    /// <summary>
    /// Called once the simulation has accepted the start of the effect (see <see cref="IEffectHandler{THost, TResult}.Accepted"/>): what a refused start must not do.
    /// The default does nothing.
    /// </summary>
    protected virtual void Accepted(ModuleEffectContext module)
    {
    }

    protected abstract SimEffect Build(ModuleEffectContext module);
}
