using Eve.Dogma;
using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>
/// A reactive armor hardener (<c>adaptiveArmorHardener</c>): while it runs the armor's resonances are the ship's own times the module's (0.85 each for the
/// reference's 4403), and each cycle they move as the damage the armor took says, by the static data's <c>resistanceShiftAmount</c> (6 points, 0.06). The
/// resonances at the start and the end. Nothing is written onto the ship's world, so the ship the simulation is sent is the ship's own resonances, and a
/// Polarized weapon that pins them is honoured through the same world (<see cref="ModuleEffects.ToldValues"/>).
/// </summary>
internal sealed class ReactiveArmorHardenerEffect : ShipEffect
{
    /// <summary>The armor's resonances in the order of the damage vectors of the simulation: em, thermal, kinetic, explosive.</summary>
    private static readonly string[] ArmorResonances =
    [
        "armorEmDamageResonance",
        "armorThermalDamageResonance",
        "armorKineticDamageResonance",
        "armorExplosiveDamageResonance",
    ];

    private const string ResistanceShiftAmount = "resistanceShiftAmount";

    public override IReadOnlyCollection<string> EffectNames { get; } = ["adaptiveArmorHardener"];

    /// <summary>Refused, with the effect as not implemented, when the static data does not give its resonances or its shift (a hardener that moves nothing is not one).</summary>
    protected override string? Refusal(ModuleEffectContext module) =>
        ArmorAttributes(module) is null || ShiftOf(module) <= 0 ? PendingModuleEffect.NotImplemented : null;

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var moduleId = module.ModuleId;
        var shipId = module.Snapshot.Ship.Id;
        var ids = ArmorAttributes(module) ?? throw new InvalidOperationException("A reactive armor hardener is built only with its resonances in the static data: its refusal is checked first.");
        // the module's own resonances are the seed of the profile, clamped to a resonance (0..1)
        var seed = ids.Select(id => Math.Clamp(module.Value(id, 1.0), 0.0, 1.0)).ToArray();
        // what the client is told of the armor at the start and at the end (the world, with the module's multiplier and with it off, a Polarized pin winning)
        var multipliers = ids.Select(id => new Modifier(ModifierFunc.Item, "shipID", Operation.PostMul, id, id)).ToArray();
        var on = ModuleEffects.ToldValues(module.World, moduleId, ItemState.Active, multipliers, shipId, ids, round: true);
        var off = ModuleEffects.ToldValues(module.World, moduleId, ItemState.Online, [], shipId, ids, round: false);
        var repeatAttribute = module.Statics.AttributeId("disallowRepeatingActivation");
        var repeat = repeatAttribute is { } disallow && module.Value(disallow, 0) > 0
            ? 1
            : Math.Max(1, module.Repeat);
        SimChange[] told =
        [
            new(moduleId, ModuleAttributes.CapacitorNeed, capacitor),
            new(moduleId, durationAttribute, ModuleAttributes.Three(duration)),
        ];
        return new SimEffect(
            moduleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid,
            duration, capacitor, repeat, Math.Max(0, module.TargetId),
            null, null, [.. told, .. on], [.. told, .. off],
            null, new SimAction.ResistanceShift(seed, ShiftOf(module), ids));
    }

    /// <summary>The armor's resonance attributes in the simulation's order, or None when the static data has not one of them.</summary>
    private static int[]? ArmorAttributes(ModuleEffectContext module)
    {
        var ids = ArmorResonances.Select(name => module.Statics.AttributeId(name)).ToArray();
        return ids.All(id => id is not null) ? ids.Select(id => id!.Value).ToArray() : null;
    }

    /// <summary>The most a resonance moves in a cycle: the module's <c>resistanceShiftAmount</c> (6 for the reference) as a fraction (0.06).</summary>
    private static double ShiftOf(ModuleEffectContext module) =>
        module.Statics.AttributeId(ResistanceShiftAmount) is { } id ? module.Value(id, 0) / 100.0 : 0.0;
}
