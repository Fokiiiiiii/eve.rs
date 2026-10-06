using Eve.Dogma;
using Eve.Services.Space.Effects;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// What the simulation needs to run an effect of a module: the length of its cycle and what a cycle costs, the numbers of the ship with the effect and
/// without it, and the values the client is told at the start and the end. The rules are the client's; the numbers are dogma's (<c>Eve.Dogma</c>). What each effect builds is the
/// business of its handler (<c>Effects/</c>, registered in <see cref="ModuleEffects"/>); this class only checks what every effect needs and asks the handler.
/// </summary>
internal static class ModulePackages
{
    private const int CapacitorBonus = 67;
    private const int ChargedArmorDamageMultiplier = 1886;

    /// <summary>Why a package cannot be made, or the package.</summary>
    public readonly record struct Result(SimEffect? Effect, string? Reason, int ModuleType, string ModuleName);

    /// <summary>
    /// A propulsion module's numbers. The ship is heavier by the module's mass addition, and faster by the module's speed factor (a percentage) times its
    /// speed boost factor divided by that new mass; a microwarpdrive also makes the ship look bigger (its signature bonus, a percentage).
    /// </summary>
    public static (SimNumbers On, SimNumbers Off) Propulsion(double mass, double maxVelocity, double signatureRadius, double massAddition, double speedFactor, double speedBoostFactor, double signatureBonus) =>
        PropulsionEffect.Numbers(mass, maxVelocity, signatureRadius, massAddition, speedFactor, speedBoostFactor, signatureBonus);

    public static Result Of(ShipSnapshot snapshot, DogmaStatic statics, long moduleId, string effectName, long targetId, int repeat)
    {
        var module = snapshot.Fitted.FirstOrDefault(m => m.Id == moduleId);
        if (module is null || statics.Type(module.TypeId) is not { } type)
        {
            return new Result(null, "MODULE_NOT_FOUND", 0, "");
        }
        var name = type.Name;
        var effect = type.EffectIds.Select(statics.Effect).FirstOrDefault(e => e is { } found && found.Name.Equals(effectName, StringComparison.OrdinalIgnoreCase))
            ?? MissileEffect.LauncherEffectFor(type, statics, effectName);   // a launcher is fired with the effect of its charge, and runs its own
        if (effect is null || effect.Category is not (EffectDef.Activation or EffectDef.Target))
        {
            return new Result(null, "UNSUPPORTED_EFFECT", module.TypeId, name);
        }
        if (snapshot.World.Item(moduleId) is not { State: >= ItemState.Online })
        {
            return new Result(null, "MODULE_NOT_ONLINE", module.TypeId, name);
        }

        var context = new EffectContext<ShipSnapshot>(snapshot, snapshot.World, statics, moduleId, type, effect, targetId, repeat);
        if (effect.Name.Equals("powerBooster", StringComparison.OrdinalIgnoreCase))
        {
            var charge = snapshot.ChargeOf(module);
            if (charge is not { Quantity: > 0 })
            {
                return new Result(null, "NO_AMMO", module.TypeId, name);
            }
            return new Result(PowerBooster(context, charge), null, module.TypeId, name);
        }

        // EVE's ancillary armor and shield modules have their own dogma effect names but use the
        // same cycle packages as the corresponding standard repairers.
        var handlerEffect = effect.Name.Trim().ToLowerInvariant() switch
        {
            "fueledarmorrepair" => effect with { Name = "armorRepair" },
            "fueledshieldboosting" => effect with { Name = "shieldBoosting" },
            _ => effect,
        };
        var handlerContext = ReferenceEquals(handlerEffect, effect) ? context : context with { Effect = handlerEffect };
        var handler = ModuleEffects.Default.Find(handlerContext);
        // the handler may refuse before the target is asked for (a gun with nothing to shoot is refused first: the reference's order)
        if (handler?.Refusal(handlerContext) is { } reason)
        {
            return new Result(null, reason, module.TypeId, name);
        }
        if (effect.Category == EffectDef.Target && targetId <= 0)
        {
            return new Result(null, "TARGET_REQUIRED", module.TypeId, name);
        }
        // only the effects that have a handler are run so far (see ModuleEffects.Default.Uncovered); the others come with what they need
        if (handler is null)
        {
            return new Result(null, "UNSUPPORTED_EFFECT", module.TypeId, name);
        }

        var package = handler.Build(handlerContext);
        if (effect.Name.Equals("fueledArmorRepair", StringComparison.OrdinalIgnoreCase) && package.Action is SimAction.Repair repair)
        {
            var moduleContext = new ModuleEffectContext(context);
            var charge = moduleContext.Snapshot.ChargeOf(moduleContext.Module);
            var chargeRate = Math.Max(1L, (long)moduleContext.Value(ModuleAttributes.ChargeRate, 1));
            var fallback = repair with { Amount = Math.Max(0, moduleContext.Value(ModuleAttributes.ArmorDamageAmount, 0)) };
            var charged = repair with { Amount = FueledArmorRepairAmount(moduleContext) };
            package = charge is { Quantity: > 0 }
                ? package with { Action = new SimAction.Charged(charged, fallback, charge.TypeId, moduleContext.Module.Flag, (int)chargeRate, charge.Quantity) }
                : package with { Action = fallback };
        }
        return new Result(package, null, module.TypeId, name);
    }

    internal static double FueledArmorRepairAmount(ModuleEffectContext module)
    {
        var baseAmount = Math.Max(0, module.Value(ModuleAttributes.ArmorDamageAmount, 0));
        var multiplier = Math.Max(1, module.Value(ChargedArmorDamageMultiplier, 1));
        var chargeRate = Math.Max(1L, (long)module.Value(ModuleAttributes.ChargeRate, 1));
        var charge = module.Snapshot.ChargeOf(module.Module);
        var chargedUnits = Math.Min(chargeRate, Math.Max(0, charge?.Quantity ?? 0));
        var chargedFraction = (double)chargedUnits / chargeRate;
        return Math.Max(0, ModuleAttributes.Six(baseAmount + (baseAmount * (multiplier - 1) * chargedFraction)));
    }

    private static SimEffect PowerBooster(EffectContext<ShipSnapshot> context, Eve.Inventory.Item charge)
    {
        var module = new ModuleEffectContext(context);
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var amount = Math.Max(0, ModuleAttributes.Six(module.World.Get(charge.Id, CapacitorBonus)));
        var chargeRate = Math.Max(1L, (long)module.Value(ModuleAttributes.ChargeRate, 1));
        var chargedCycles = 1 + ((charge.Quantity - 1) / chargeRate);
        var repeat = (int)Math.Min(Math.Max(1, context.Repeat), chargedCycles);
        SimChange[] told = [new(module.ModuleId, ModuleAttributes.CapacitorNeed, capacitor), new(module.ModuleId, durationAttribute, ModuleAttributes.Three(duration))];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor, repeat, context.Host.Ship.Id, null, null, told, told, null,
            new SimAction.Charged(new SimAction.Remote(new SimRemote.Transfer(amount), 0, 0, false, "energyTransfer"), null,
                charge.TypeId, module.Module.Flag, (int)chargeRate, charge.Quantity));
    }
}
