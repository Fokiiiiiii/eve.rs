using Eve.Dogma;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>Starts the explicitly supported effects whose active behavior is supplied by SDE Dogma modifiers: the emergency hull energizer.</summary>
internal sealed class TypeAttributeResistanceEffect : ShipEffect
{
    internal const string EffectName = "emergencyHullEnergizer";

    /// <summary>
    /// The hull resonances of the emergency hull energizer: the module's own hull attribute multiplies (PostMul) the ship's resonance of the same damage type
    /// (the client's <c>MODIFIER_CHANGES</c>, <c>emergencyHullEnergizer.py</c>). Both are found by name in the static data.
    /// </summary>
    private static readonly (string Module, string Ship)[] HullResonanceMultipliers =
    [
        ("hullEmDamageResonance", "emDamageResonance"),
        ("hullExplosiveDamageResonance", "explosiveDamageResonance"),
        ("hullKineticDamageResonance", "kineticDamageResonance"),
        ("hullThermalDamageResonance", "thermalDamageResonance"),
    ];

    public override IReadOnlyCollection<string> EffectNames { get; } = [EffectName];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var shipId = module.Snapshot.Ship.Id;
        // the (module attribute, ship attribute) pairs: the energizer's hull value multiplies the ship's resonance of the same damage type
        var pairs = HullResonanceMultipliers
            .Select(pair => (Module: module.Statics.AttributeId(pair.Module), Ship: module.Statics.AttributeId(pair.Ship)))
            .Where(pair => pair.Module is not null && pair.Ship is not null)
            .Select(pair => (Module: pair.Module!.Value, Ship: pair.Ship!.Value))
            .ToArray();
        var shipAttributeIds = pairs.Select(pair => pair.Ship).ToArray();
        // each resonance is multiplied by the module's own value, as a modifier of the world: a resonance that is pinned (a PostAssign, a Polarized
        // weapon fitted) keeps its pinned value, because the world applies its modifiers in order and the assignment comes last
        var multipliers = pairs.Select(pair => new Modifier(ModifierFunc.Item, "shipID", Operation.PostMul, pair.Ship, pair.Module)).ToArray();
        var on = ModuleEffects.ToldValues(module.World, module.ModuleId, ItemState.Active, multipliers, shipId, shipAttributeIds, round: true);
        var off = ModuleEffects.ToldValues(module.World, module.ModuleId, ItemState.Online, [], shipId, shipAttributeIds, round: false);
        // the energizer burns its own module out as it starts (emergencyHullEnergizer.py: damage = hp), and the module stays burned out when it stops
        var burnOut = new SimChange(module.ModuleId, ModuleAttributes.ModuleDamage, module.World.Get(module.ModuleId, ModuleAttributes.ModuleHitPoints));
        on.Add(burnOut);
        off.Add(burnOut);

        var repeatAttribute = module.Statics.AttributeId("disallowRepeatingActivation");
        var repeat = repeatAttribute is { } id && module.Value(id, 0) > 0
            ? 1
            : Math.Max(1, module.Repeat);
        SimChange[] told =
        [
            new(module.ModuleId, ModuleAttributes.CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, ModuleAttributes.Three(duration)),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid,
            duration, capacitor, repeat, Math.Max(0, module.TargetId),
            null, null, [.. told, .. on], [.. told, .. off]);
    }
}
