using Eve.Dogma;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The module effects the space host runs. To run another effect: write a <see cref="ShipEffect"/> that names it and builds its <see cref="SimEffect"/>, and add it here; to run
/// another projected effect add a line to <see cref="ProjectedEffect"/>. <see cref="EffectRegistry{THost, TResult}.Uncovered"/> lists what the static data has that is not here.
/// </summary>
internal static class ModuleEffects
{
    public static SensorBoosterEffect SensorBooster { get; } = new();

    public static EffectRegistry<ShipSnapshot, SimEffect> Default { get; } = new EffectRegistry<ShipSnapshot, SimEffect>()
        .Register(SensorBooster)
        .Register(new PropulsionEffect())
        .Register(new TurretEffect())
        .Register(new MissileEffect())
        .Register(new RepairEffect())
        .Register(new ProjectedEffect())
        .Register(new SmartbombEffect())
        .Register(new EcmBurstEffect())
        .Register(new CloakingEffect())
        .Register(new MicroJumpDriveEffect());
}

internal sealed class SensorBoosterEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["sensorBoost"];

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

internal sealed class MicroJumpDriveEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["microJumpDrive"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, _) = module.Cycle();
        var distance = module.Value(ModuleAttributes.MicroJumpDriveRange, 100_000);
        var cooldown = (long)module.Value(ModuleAttributes.MicroJumpDriveCooldown, 180_000);
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid,
            duration, capacitor, 1, Math.Max(0, module.TargetId), null, null, [], [],
            Action: new SimAction.MicroJumpDrive(distance, cooldown));
    }
}
