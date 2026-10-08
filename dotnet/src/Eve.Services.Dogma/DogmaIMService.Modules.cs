using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Dogma;

/// <summary>
/// The calls that start and stop the effects of a ship's modules in space (<c>Activate</c>, <c>Deactivate</c>). The effects run in the space simulation
/// (<see cref="ISpaceModules"/>); this puts the calls to it and a refusal in the message the reference shows.
/// </summary>
public sealed partial class DogmaIMService
{
    private async ValueTask<ModuleResult> ModuleOrderAsync(ServiceCall call, Func<ISpaceModules, Task<ModuleResult?>> order, bool starting)
    {
        if (BoundContextOf(call) is null || modules is null || await order(modules) is not { } result)
        {
            throw MachoErrors.ThrowUserError("DeniedShipChanged");
        }
        return result.Ok ? result : throw ModuleError(result, starting);
    }

    /// <summary>The error the reference raises for a module order the simulation refused (its wording).</summary>
    private static MachoWrappedException ModuleError(ModuleResult refusal, bool starting)
    {
        static MachoWrappedException Notify(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
        var name = string.IsNullOrEmpty(refusal.ModuleName) ? "module" : refusal.ModuleName;
        if (!starting)
        {
            return refusal.Reason switch
            {
                "NOT_IN_SPACE" or "NO_SHIP_IN_SPACE" or "MODULE_NOT_FOUND" => MachoErrors.ThrowUserError("DeniedShipChanged"),
                "MODULE_NOT_ACTIVE" => Notify($"{name} is not active."),
                "MODULE_INCAPACITATED" => Notify($"{name} is too damaged to overload."),
                "MODULE_NOT_OVERLOADABLE" => Notify($"{name} cannot be overloaded."),
                "THERMODYNAMICS_SKILL_REQUIRED" => MachoErrors.ThrowUserError("DontHaveThermoDynamicsSkill", ("skillLevel", new PyInt(1))),
                _ => Notify($"Failed to deactivate {name}."),
            };
        }
        return refusal.Reason switch
        {
            "NOT_IN_SPACE" or "NO_SHIP_IN_SPACE" or "MODULE_NOT_FOUND" => MachoErrors.ThrowUserError("DeniedShipChanged"),
            "MODULE_ALREADY_ACTIVE" => MachoErrors.ThrowUserError("EffectAlreadyActive2", ("modulename", new PyTuple([new PyInt(TypeReference), new PyInt(refusal.ModuleType)]))),
            "TARGET_REQUIRED" => Notify("You need an active target to activate that module."),
            "TARGET_NOT_LOCKED" => Notify("That target is not locked."),
            "TARGET_NOT_FOUND" => MachoErrors.ThrowUserError("DeniedActivateTargetNotPresent"),
            "TARGET_OUT_OF_RANGE" => MachoErrors.ThrowUserError(
                "TargetNotWithinRangeGeneric",
                ("desiredRange", Py.Number(Math.Max(0, refusal.DesiredRange))),
                ("targetName", refusal.TargetType > 0 ? new PyTuple([new PyInt(TypeReference), new PyInt(refusal.TargetType)]) : new PyStr("That target")),
                ("shipType", refusal.ShipType > 0 ? new PyTuple([new PyInt(TypeReference), new PyInt(refusal.ShipType)]) : new PyInt(0)),
                ("actualDistance", Py.Number(Math.Max(0, refusal.ActualDistance)))),
            "NO_AMMO" => MachoErrors.ThrowUserError("NoCharges", ("launcher", new PyTuple([new PyInt(TypeReference), new PyInt(refusal.ModuleType)]))),
            "MODULE_NOT_ONLINE" => Notify($"{name} is offline."),
            "MODULE_INCAPACITATED" => Notify($"{name} is too damaged to overload."),
            "MODULE_NOT_OVERLOADABLE" => Notify($"{name} cannot be overloaded."),
            "THERMODYNAMICS_SKILL_REQUIRED" => MachoErrors.ThrowUserError("DontHaveThermoDynamicsSkill", ("skillLevel", new PyInt(1))),
            "NOT_ENOUGH_CAPACITOR" => Notify("You do not have enough capacitor to activate that module."),
            "MICROWARPDRIVE_BLOCKED" => Notify("You cannot activate a microwarpdrive while a warp scrambler holds your ship."),
            "CLOAK_WARP_BLOCKED" => Notify("You cannot activate that cloaking device while warping."),
            "CLOAK_TARGET_LOCKED" => Notify("Your ship is already target locked and cannot activate a cloaking device."),
            "CLOAK_PROXIMITY_BLOCKED" => Notify("You are too close to another object to activate your cloaking device."),
            "MODULE_CLOAKED" => Notify("You cannot activate modules while cloaked."),
            // an effect with no body yet (PendingModuleEffect) is shown as one this module cannot run, the same as an effect with no handler
            "UNSUPPORTED_EFFECT" or "EFFECT_NOT_IMPLEMENTED" => Notify($"{name} cannot be activated."),
            // a launcher whose kind is not run yet names its group's reason (MissileEffect.RefusedLauncherGroups): shown as the same refusal
            _ when refusal.Reason?.EndsWith("_LAUNCHER_NOT_IMPLEMENTED", StringComparison.Ordinal) == true => Notify($"{name} cannot be activated."),
            _ => Notify($"Failed to activate {name}: {refusal.Reason ?? "unknown error"}."),
        };
    }

    /// <summary>
    /// <c>Activate(itemID, effectName, target, repeat)</c>: starts an effect of a fitted module; the answer is 1, and the client is told the effect went on
    /// (<c>OnGodmaShipEffect</c>) and what it changed.
    /// </summary>
    [Method("Activate")]
    public async ValueTask<PyValue> Activate(ServiceCall call)
    {
        var itemId = IdOf(call.Arg(0)) ?? 0;
        var effect = JsSemantics.StringValue(call.Arg(1)) ?? "";
        var target = IdOf(call.Arg(2)) ?? 0;
        var repeat = (int)Math.Clamp(IdOf(call.Arg(3)) ?? 1, 1, int.MaxValue);
        if (BoundContextOf(call) is not null && call.Session.CharacterId is { } character &&
            await activateBooster(character, itemId, effect, call.Session.GetInt("stationid"), call.Session.GetInt("shipid"), call.Cancellation) is true)
        {
            return new PyInt(1);
        }
        await ModuleOrderAsync(call, m => m.ActivateAsync(call.Session, itemId, effect, target, repeat, call.Cancellation), starting: true);
        return new PyInt(1);
    }

    /// <summary><c>Deactivate(itemID, effectName)</c>: asks for an effect to stop; it does at the end of the cycle it is in.</summary>
    [Method("Deactivate")]
    public async ValueTask<PyValue> Deactivate(ServiceCall call)
    {
        var module = IdOf(call.Arg(0)) ?? 0;
        var effect = JsSemantics.StringValue(call.Arg(1)) ?? "";
        await ModuleOrderAsync(call, m => m.DeactivateAsync(call.Session, module, effect, call.Cancellation), starting: false);
        return new PyInt(1);
    }

    /// <summary><c>Overload(itemID, effectID)</c>: starts one fitted module's Dogma overload effect.</summary>
[Method("InjectSkillIntoBrain")]
    public ValueTask<PyValue> InjectSkillIntoBrain(ServiceCall call)
    {
        var ids = call.Arg(0) switch
        {
            PyList list => list.Items.Select(IdOf).OfType<long>().ToArray(),
            PyTuple tuple => tuple.Items.Select(IdOf).OfType<long>().ToArray(),
            _ => [],
        };
        return injectSkillBooks is null
            ? ValueTask.FromResult(None)
            : new ValueTask<PyValue>(injectSkillBooks(call.Session, ids, call.Cancellation));
    }

    [Method("Overload")]
    public async ValueTask<PyValue> Overload(ServiceCall call)
    {
        var module = IdOf(call.Arg(0)) ?? 0;
        var effect = (int)Math.Clamp(IdOf(call.Arg(1)) ?? 0, int.MinValue, int.MaxValue);
        await ModuleOrderAsync(call, modules => modules.OverloadAsync(call.Session, module, effect, true, call.Cancellation), starting: true);
        return new PyInt(1);
    }

    /// <summary><c>StopOverload(itemID, effectID)</c>: stops a fitted module's overload effect at its cycle boundary.</summary>
    [Method("StopOverload")]
    public async ValueTask<PyValue> StopOverload(ServiceCall call)
    {
        var module = IdOf(call.Arg(0)) ?? 0;
        var effect = (int)Math.Clamp(IdOf(call.Arg(1)) ?? 0, int.MinValue, int.MaxValue);
        await ModuleOrderAsync(call, modules => modules.OverloadAsync(call.Session, module, effect, false, call.Cancellation), starting: false);
        return new PyInt(1);
    }

    /// <summary><c>OverloadRack(itemID)</c>: overloads overloadable online modules in the fitted module's rack.</summary>
    [Method("OverloadRack")]
    public async ValueTask<PyValue> OverloadRack(ServiceCall call)
    {
        var module = IdOf(call.Arg(0)) ?? 0;
        var result = await ModuleOrderAsync(call, space => space.OverloadRackAsync(call.Session, module, true, call.Cancellation), starting: true);
        return new PyList([.. result.ModuleIds.Select(id => new PyLong(id))]);
    }

    /// <summary><c>StopOverloadRack(itemID)</c>: stops overload in the fitted module's rack.</summary>
    [Method("StopOverloadRack")]
    public async ValueTask<PyValue> StopOverloadRack(ServiceCall call)
    {
        var module = IdOf(call.Arg(0)) ?? 0;
        var result = await ModuleOrderAsync(call, space => space.OverloadRackAsync(call.Session, module, false, call.Cancellation), starting: false);
        return new PyList([.. result.ModuleIds.Select(id => new PyLong(id))]);
    }
}
