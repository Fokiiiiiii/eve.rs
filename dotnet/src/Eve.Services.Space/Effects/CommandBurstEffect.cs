using Eve.Dogma;
using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The command bursts (track C6, protocol 42): the warfare link modules of the five families (Shield, Armor, Skirmish, Information, Mining Foreman, effects 6732
/// to 6736) and the expedition bursts, which carry the same effects with the expedition charges (group 4905). The module cycles and spends one of its charge's rounds
/// a cycle (<c>Charged</c>, the charge's rounds). At each cycle's end the host applies the burst's buffs to the ships of the source's fleet in reach
/// (<see cref="SpaceContainers.SettleBurstAsync"/>); the numbers are the SDE's (the charge's buff ids, and the module's values after effect 6737).
/// </summary>
internal sealed class CommandBurstEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } =
        ["moduleBonusWarfareLinkShield", "moduleBonusWarfareLinkArmor", "moduleBonusWarfareLinkSkirmish", "moduleBonusWarfareLinkInfo", "moduleBonusWarfareLinkMining"];

    /// <summary>The reach of a burst in metres (attribute 54, the effect's rangeAttributeID).</summary>
    public const int RangeAttribute = 54;

    /// <summary>A burst with no charge, or with fewer rounds than a cycle takes, is refused before it starts (as a launcher is: no ammunition first).</summary>
    protected override string? Refusal(ModuleEffectContext module) =>
        module.Snapshot.ChargeOf(module.Module) is { Quantity: var rounds } && rounds >= ChargesPerCycle(module.Attributes) ? null : "NO_AMMO";

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var charge = module.Snapshot.ChargeOf(module.Module)!;
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var range = Math.Max(0, NpcArmament.Round6(module.Value(RangeAttribute, 0)));
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, Three(duration))];
        var action = new SimAction.Charged(new SimAction.CommandBurst(range), null, charge.TypeId, charge.Flag, (int)ChargesPerCycle(module.Attributes), charge.Quantity);
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, Math.Max(1, module.Repeat), 0, null, null, told, told, null, action);
    }
}

/// <summary>
/// The buffs a command burst gives, read from its module's values after dogma: the charge's warfare buffs are copied to the module by effect 6737 (the ids), and each
/// id's value is the module's value times the charge's multiplier (the values). The buff lasts the module's buff duration (2535). Four slots, as the SDE has them.
/// </summary>
public static class CommandBurstBuffs
{
    /// <summary>The module's buff ids (warfareBuff1ID to warfareBuff4ID: 2468, 2470, 2472, 2536).</summary>
    private static readonly int[] BuffIds = [2468, 2470, 2472, 2536];

    /// <summary>The module's buff values (warfareBuff1Value to warfareBuff4Value: 2469, 2471, 2473, 2537).</summary>
    private static readonly int[] BuffValues = [2469, 2471, 2473, 2537];

    /// <summary>The buff duration in milliseconds (buffDuration, 2535).</summary>
    public const int BuffDuration = 2535;

    /// <summary>The buffs of a burst module: each collection its charge names, with the value the burst gives it. An unknown collection, or a zero value, is not a buff.</summary>
    public static IReadOnlyList<(BuffCollection Collection, double Value)> Of(DogmaWorld world, DogmaStatic statics, long moduleId)
    {
        var buffs = new List<(BuffCollection, double)>();
        for (var slot = 0; slot < BuffIds.Length; slot++)
        {
            var id = world.Get(moduleId, BuffIds[slot]);
            var value = world.Get(moduleId, BuffValues[slot]);
            if (double.IsFinite(id) && id > 0 && double.IsFinite(value) && value != 0 && statics.BuffCollections.TryGetValue((int)id, out var collection))
            {
                buffs.Add((collection, value));
            }
        }
        return buffs;
    }

    /// <summary>How long a burst's buffs last, in milliseconds (the module's buffDuration), or zero when it has none.</summary>
    public static double DurationOf(DogmaWorld world, long moduleId)
    {
        var duration = world.Get(moduleId, BuffDuration);
        return double.IsFinite(duration) && duration > 0 ? duration : 0;
    }
}
