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
        return result.Ok ? result : throw ModuleRefusals.Of(result, starting);
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
