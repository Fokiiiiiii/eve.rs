using Eve.Dogma;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Effects;

internal sealed class MicroJumpDriveEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["microJumpDrive"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, _) = module.Cycle();
        var distance = module.Value(ModuleAttributes.MicroJumpDriveRange, 100_000);
        var cooldown = (long)module.Value(ModuleAttributes.ModuleReactivationDelay, 180_000);
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid,
            duration, capacitor, 1, Math.Max(0, module.TargetId), null, null, [], [],
            Action: new SimAction.MicroJumpDrive(distance, cooldown));
    }
}
