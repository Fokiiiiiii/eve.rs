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
            "NOT_ENOUGH_CAPACITOR" => Notify("You do not have enough capacitor to activate that module."),
            "UNSUPPORTED_EFFECT" => Notify($"{name} cannot be activated."),
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
        var module = IdOf(call.Arg(0)) ?? 0;
        var effect = JsSemantics.StringValue(call.Arg(1)) ?? "";
        var target = IdOf(call.Arg(2)) ?? 0;
        var repeat = (int)Math.Clamp(IdOf(call.Arg(3)) ?? 1, 1, int.MaxValue);
        await ModuleOrderAsync(call, m => m.ActivateAsync(call.Session, module, effect, target, repeat, call.Cancellation), starting: true);
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
}
