using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The tractor beam (<c>tractorBeamCan</c>): it pulls the wreck or the can its pilot holds a lock on, at the module's <c>maxTractorVelocity</c>, until the
/// wreck is <see cref="HoldDistanceMeters"/> off the ship. The pull, its reach and its stopping are the simulation's (<c>tractor.rs</c>); the host refuses a
/// pull on a wreck the pilot may not take from (<c>TARGET_LOOT_RIGHTS_REQUIRED</c>, <see cref="SpaceSessions"/>) before the activation is sent.
/// The hold distance is EVE.js's <c>TRACTOR_FOLLOW_RANGE_METERS</c> (<c>tractorBeamRuntime.js</c>), a constant of the beam rather than of the module.
/// </summary>
internal sealed class TractorBeamEffect : ShipEffect
{
    /// <summary>The distance the beam holds the wreck at, metres off the ship's surface (EVE.js <c>TRACTOR_FOLLOW_RANGE_METERS</c>).</summary>
    public const double HoldDistanceMeters = 500;

    /// <summary>The SDE attribute <c>maxTractorVelocity</c>, the speed the beam pulls at.</summary>
    private const int MaxTractorVelocity = 1045;

    public override IReadOnlyCollection<string> EffectNames { get; } = ["tractorBeamCan"];

    // a beam without its speed or its reach (the Mobile Tractor Unit's deployable carries them, a ship's module must) cannot pull: it says so before a target is asked
    protected override string? Refusal(ModuleEffectContext module) =>
        module.Value(MaxTractorVelocity, 0) > 0 && module.Value(MaxRange, 0) > 0 ? null : "UNSUPPORTED_MODULE";

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        SimChange[] told =
        [
            new(module.ModuleId, CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, Three(duration)),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor,
            Math.Max(1, module.Repeat), Math.Max(0, module.TargetId), null, null, told, told,
            Action: new SimAction.Tractor(
                module.Value(MaxTractorVelocity, 0), HoldDistanceMeters, module.Value(MaxRange, 0)));
    }
}
