using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The auto-targeting system (<c>targetHostiles</c>): an activation with no target, whose cycles lock the ships that target the pilot within its range
/// (<c>targetHostileRange</c>, 50 km for every module of the group). The simulation sweeps at each cycle start (<c>SimAction.AutoTarget</c>).
/// </summary>
internal sealed class AutoTargetingEffect : ShipEffect
{
    /// <summary>The attribute of the sweep range (targetHostileRange, SDE 143); the range the reference gives all six modules when it is absent.</summary>
    private const string RangeAttribute = "targetHostileRange";
    private const double DefaultRangeMeters = 50_000;

    public override IReadOnlyCollection<string> EffectNames { get; } = ["targetHostiles"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var rangeAttribute = module.Statics.AttributeId(RangeAttribute);
        var range = rangeAttribute is { } id ? module.Value(id, DefaultRangeMeters) : DefaultRangeMeters;
        SimChange[] told =
        [
            new(module.ModuleId, ModuleAttributes.CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, ModuleAttributes.Three(duration)),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor,
            Math.Max(1, module.Repeat), 0, null, null, told, told, Action: new SimAction.AutoTarget(Math.Max(0, range)));
    }
}
