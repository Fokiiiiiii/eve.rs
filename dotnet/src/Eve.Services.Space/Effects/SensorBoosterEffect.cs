using Eve.Dogma;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Effects;

internal sealed class SensorBoosterEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["sensorBoosterActivePercentage"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var moduleId = module.ModuleId;
        var shipId = module.Snapshot.Ship.Id;
        var rangeBonus = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.MaxTargetRangeBonus, 0)));
        var resolutionBonus = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.ScanResolutionBonus, 0)));
        SimChange[] told =
        [
            new(moduleId, ModuleAttributes.CapacitorNeed, capacitor),
            new(moduleId, durationAttribute, ModuleAttributes.Three(duration)),
            new(moduleId, ModuleAttributes.MaxTargetRangeBonus, rangeBonus),
            new(moduleId, ModuleAttributes.ScanResolutionBonus, resolutionBonus),
        ];

        return new SimEffect(
            moduleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor,
            Math.Max(1, module.Repeat), shipId, null, null, told, told, null,
            new SimAction.Remote(new SimRemote.Damp(rangeBonus, resolutionBonus), 0, 0, false, "remoteSensorBoost"));
    }
}
