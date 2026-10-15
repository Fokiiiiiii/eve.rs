using Eve.Dogma;
using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The entosis link (<c>entosisLink</c>, effect 6063; ST-9 b): a cycle of 300 s on a sovereignty hub the pilot holds the lock on, within the link's reach (attribute 54,
/// 20 km for Entosis Link I). The host checks the target and the alliance before the package is built (<c>SpaceSessions.Modules</c>: ENTOSIS_TARGET_NOT_SOV_HUB and
/// ALLIANCE_MEMBERSHIP_REQUIRED); the simulation checks the lock and the reach, and at each cycle's end the host settles the capture (command 14). The effect is not
/// offensive (EVE.js effect 6063, isOffensive false). Its cycle burns the consumption type of its module (713) through the shared mechanism.
/// </summary>
internal sealed class EntosisLinkEffect : ShipEffect
{
    /// <summary>The effect's name in the static data (dogmaEffects 6063).</summary>
    public const string Name = "entosisLink";

    /// <summary>The reach of a link in metres (attribute 54, the effect's rangeAttributeID).</summary>
    public const int RangeAttribute = 54;

    public override IReadOnlyCollection<string> EffectNames { get; } = [Name];

    /// <summary>A link with no reach in its static data is refused before it starts (its simulation action needs a positive reach).</summary>
    protected override string? Refusal(ModuleEffectContext module) => module.Value(RangeAttribute, 0) > 0 ? null : "UNSUPPORTED_MODULE";

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var range = NpcArmament.Round6(module.Value(RangeAttribute, 0));
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, Three(duration))];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), module.TargetId,
            null, null, told, told, null, new SimAction.Entosis(range));
    }
}
