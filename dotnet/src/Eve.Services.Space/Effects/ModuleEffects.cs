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
    public static EffectRegistry<ShipSnapshot, SimEffect> Default { get; } = new EffectRegistry<ShipSnapshot, SimEffect>()
        .Register(new PropulsionEffect())
        .Register(new TurretEffect())
        .Register(new MissileEffect())
        .Register(new RepairEffect())
        .Register(new ProjectedEffect())
        .Register(new SmartbombEffect());
}
