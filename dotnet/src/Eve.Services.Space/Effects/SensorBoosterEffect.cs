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
        // the sensor strengths the booster adds (scanGravimetric, Ladar, Magnetometric and RadarStrengthPercent of the type, and of an ECCM charge the ship runs
        // with it: the SDE gives the Sensor Booster I 40 on each): the same four the remote sensor booster adds to a target
        var gravimetric = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.ScanGravimetricStrengthPercent, 0)));
        var ladar = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.ScanLadarStrengthPercent, 0)));
        var magnetometric = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.ScanMagnetometricStrengthPercent, 0)));
        var radar = Math.Max(0, ModuleAttributes.Six(module.Value(ModuleAttributes.ScanRadarStrengthPercent, 0)));
        SimChange[] told =
        [
            new(moduleId, ModuleAttributes.CapacitorNeed, capacitor),
            new(moduleId, durationAttribute, ModuleAttributes.Three(duration)),
            new(moduleId, ModuleAttributes.MaxTargetRangeBonus, rangeBonus),
            new(moduleId, ModuleAttributes.ScanResolutionBonus, resolutionBonus),
            new(moduleId, ModuleAttributes.ScanGravimetricStrengthPercent, gravimetric),
            new(moduleId, ModuleAttributes.ScanLadarStrengthPercent, ladar),
            new(moduleId, ModuleAttributes.ScanMagnetometricStrengthPercent, magnetometric),
            new(moduleId, ModuleAttributes.ScanRadarStrengthPercent, radar),
        ];

        return new SimEffect(
            moduleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor,
            Math.Max(1, module.Repeat), shipId, null, null, told, told, null,
            new SimAction.Remote(new SimRemote.Damp(rangeBonus, resolutionBonus, gravimetric, ladar, magnetometric, radar), 0, 0, false, "remoteSensorBoost"));
    }
}
