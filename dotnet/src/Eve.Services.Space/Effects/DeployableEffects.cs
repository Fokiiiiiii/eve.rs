using Eve.Dogma;
using Eve.Inventory;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// What the launchers that put a deployable in space share (a bomb launcher, an interdiction sphere launcher): the charge's numbers, the deployable's ball, the
/// effect the client plays and what the client is told. The charge is spent a cycle at a time by the simulation (<see cref="SimAction.Charged"/>), as a
/// charged effect's charges are.
/// </summary>
internal static class Deployables
{
    /// <summary>The effect the client plays when a probe or a bomb is released, when the effect has none of its own (the torpedo's deployment for a probe, the missile's for a bomb).</summary>
    public const string TorpedoDeployment = "effects.TorpedoDeployment";

    public const string MissileDeployment = "effects.MissileDeployment";

    /// <summary>The dogma attributes of the loaded charge, read as numbers (zero where the charge says nothing).</summary>
    public static Func<int, double> ChargeValue(ModuleEffectContext module, Item charge)
    {
        var attributes = module.World.Attributes(charge.Id);
        return attribute => attributes.TryGetValue(attribute, out var value) && double.IsFinite(value) ? value : 0;
    }

    /// <summary>The ball of the charge a deployable is: its type, its group, its name and the radius of its ball (the charge's radius, one metre where it has none).</summary>
    public static SimDeployable Ball(ModuleEffectContext module, Item charge, Func<int, double> value)
    {
        var type = module.Statics.Type(charge.TypeId);
        var radius = value(Radius) is var r and > 0 ? r : 1;
        return new SimDeployable(charge.TypeId, type?.GroupId ?? 0, type?.Name ?? "Charge", radius);
    }

    /// <summary>
    /// The launcher's own effect, with the charge it spends a cycle at a time: the action is charged with the rounds the loaded charge has (<paramref name="charge"/>), the
    /// cycle is the module's, and the client is told the cost and the cycle of the launcher as it starts and stops (<see cref="MissileEffect"/>'s pattern).
    /// </summary>
    public static SimEffect Launch(ModuleEffectContext module, Item charge, SimAction deployable, string fx)
    {
        var (duration, capacitor, _) = module.Cycle();
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, Speed, Three(duration))];
        var action = new SimAction.Charged(deployable, null, charge.TypeId, charge.Flag, (int)ChargesPerCycle(module.Attributes), charge.Quantity);
        return new SimEffect(module.ModuleId, module.Module.TypeId, module.Effect.Id, fx, duration, capacitor, Math.Max(1, module.Repeat), 0, null, null, told, told, null, action);
    }

    /// <summary>A launcher with no rounds of its charge is refused before it starts (the reference's order: no ammunition first).</summary>
    public static string? NoRounds(ModuleEffectContext module)
    {
        var charge = module.Snapshot.ChargeOf(module.Module);
        return charge is null || charge.Quantity < ChargesPerCycle(module.Attributes) ? "NO_AMMO" : null;
    }
}
