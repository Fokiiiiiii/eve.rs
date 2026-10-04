using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// A local repairer's package (<c>localCycleRuntime.js</c> of the reference, ADR 0009): the cycle and its cost are the module's own values after dogma, and each cycle mends the layer by
/// the points the module's attribute says (an armor repairer's <c>armorDamageAmount</c>, a shield booster's <c>shieldBonus</c>, a hull repairer's <c>structureDamageAmount</c>). The client
/// is told the cost and the cycle as an effect starts and stops, as for a generic module. Whether the points are given as the cycle begins is the reference's
/// <c>START_OF_CYCLE_EFFECTS</c>: the shield booster at the start, the armor and hull repairers at the end.
/// </summary>
internal sealed class RepairEffect : ShipEffect
{
    private static readonly IReadOnlyDictionary<string, (SimLayer Layer, int Amount, bool AtEnd)> Repairs = new Dictionary<string, (SimLayer, int, bool)>(StringComparer.OrdinalIgnoreCase)
    {
        ["armorRepair"] = (SimLayer.Armor, ArmorDamageAmount, true),
        ["shieldBoosting"] = (SimLayer.Shield, ShieldBonus, false),
        ["structureRepair"] = (SimLayer.Structure, StructureDamageAmount, true),
    };

    public override IReadOnlyCollection<string> EffectNames { get; } = [.. Repairs.Keys];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var repair = Repairs[module.Effect.Name.Trim()];
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var amount = Math.Max(0, NpcArmament.Round6(module.Value(repair.Amount, 0)));
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, Three(duration))];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), 0, null, null, told, told, null,
            new SimAction.Repair(repair.Layer, amount, repair.AtEnd));
    }
}
