using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Dogma;

/// <summary>
/// The targeting calls of the dogma location the client binds in space: locking, giving up and letting go of targets, and asking what is locked and who
/// has the ship locked. The locks themselves are the space simulation's (<see cref="ISpaceTargeting"/>); this puts the calls to it and its refusals in
/// the errors the client shows.
/// </summary>
public sealed partial class DogmaIMService
{
    private const int TypeReference = 4;

    private static PyList Ids(IEnumerable<long> ids) => new([.. ids.Select(InventoryWire.Id)]);

    private async ValueTask<TargetingResult> OrderAsync(ServiceCall call, TargetingOrder order, IReadOnlyList<long> targets)
    {
        if (BoundContextOf(call) is null || space is null || await space.OrderAsync(call.Session, order, targets, call.Cancellation) is not { } result)
        {
            throw MachoErrors.ThrowUserError("DeniedShipChanged");
        }
        return result.Ok ? result : throw TargetingError(result);
    }

    /// <summary>The user error the reference raises for a lock the simulation refused.</summary>
    private static MachoWrappedException TargetingError(TargetingResult refusal)
    {
        PyValue Type(int typeId) => typeId > 0 ? new PyTuple([new PyInt(TypeReference), new PyInt(typeId)]) : new PyInt(0);
        return refusal.Reason switch
        {
            "NOT_IN_SPACE" or "NO_SHIP_IN_SPACE" or "SHIP_NOT_FOUND" => MachoErrors.ThrowUserError("DeniedShipChanged"),
            "TARGET_SELF" => MachoErrors.ThrowUserError("DeniedTargetSelf"),
            "SOURCE_WARPING" => MachoErrors.ThrowUserError("DeniedTargetSelfWarping"),
            "TARGET_WARPING" => MachoErrors.ThrowUserError("DeniedTargetOtherWarping", ("targetName", refusal.TargetType > 0 ? Type(refusal.TargetType) : new PyStr("that target"))),
            "TARGET_OUT_OF_RANGE" => MachoErrors.ThrowUserError(
                "TargetNotWithinRangeGeneric",
                ("desiredRange", Py.Number(Math.Max(0, refusal.DesiredRange))),
                ("targetName", refusal.TargetType > 0 ? Type(refusal.TargetType) : new PyStr("that target")),
                ("actualDistance", Py.Number(Math.Max(0, refusal.ActualDistance)))),
            "TARGET_NOT_FOUND" => MachoErrors.ThrowUserError("TargetingAttemptCancelled"),
            // the simulation says how many targets the ship may hold in the range's place
            "TARGET_LOCK_LIMIT_REACHED" => MachoErrors.ThrowUserError("TargetingSystemsAlreadyFullyUtilized", ("limit", new PyInt((long)Math.Max(0, refusal.DesiredRange)))),
            "TARGET_INVULNERABLE" => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You cannot lock that target while it is still invulnerable after undocking."))),
            _ => MachoErrors.ThrowUserError("DeniedTargetingAttemptFailed", ("target", new PyInt(Math.Max(0, refusal.TargetType)))),
        };
    }

    /// <summary>
    /// <c>AddTarget(targetID)</c>: starts locking a target. The answer is whether the lock is going (1) or had (0) and what is locked; the lock itself
    /// is told later, by <c>OnTarget</c>.
    /// </summary>
    [Method("AddTarget")]
    public async ValueTask<PyValue> AddTarget(ServiceCall call)
    {
        var result = await OrderAsync(call, TargetingOrder.Lock, [IdOf(call.Arg(0)) ?? 0]);
        return Py.Tuple(new PyInt(result.Pending ? 1 : 0), Ids(result.Targets));
    }

    /// <summary><c>CancelAddTarget(targetID)</c>: gives up a lock that is going.</summary>
    [Method("CancelAddTarget")]
    public async ValueTask<PyValue> CancelAddTarget(ServiceCall call)
    {
        await OrderAsync(call, TargetingOrder.Cancel, [IdOf(call.Arg(0)) ?? 0]);
        return None;
    }

    /// <summary><c>RemoveTarget(targetID)</c>: lets go of a locked target.</summary>
    [Method("RemoveTarget")]
    public async ValueTask<PyValue> RemoveTarget(ServiceCall call)
    {
        await OrderAsync(call, TargetingOrder.Remove, [IdOf(call.Arg(0)) ?? 0]);
        return None;
    }

    /// <summary><c>RemoveTargets(targetIDs)</c>: lets go of locked targets.</summary>
    [Method("RemoveTargets")]
    public async ValueTask<PyValue> RemoveTargets(ServiceCall call)
    {
        var items = call.Arg(0) switch { PyList list => list.Items, PyTuple tuple => tuple.Items, _ => [] };
        await OrderAsync(call, TargetingOrder.Remove, [.. items.Select(item => IdOf(item) ?? 0).Where(id => id > 0)]);
        return None;
    }

    /// <summary><c>ClearTargets()</c>: lets go of everything.</summary>
    [Method("ClearTargets")]
    public async ValueTask<PyValue> ClearTargets(ServiceCall call)
    {
        await OrderAsync(call, TargetingOrder.Clear, []);
        return None;
    }

    /// <summary><c>GetTargets()</c>: what the ship has locked.</summary>
    [Method("GetTargets")]
    public async ValueTask<PyValue> GetTargets(ServiceCall call) => Ids((await OrderAsync(call, TargetingOrder.Targets, [])).Targets);

    /// <summary><c>GetTargeters()</c>: who has the ship locked.</summary>
    [Method("GetTargeters")]
    public async ValueTask<PyValue> GetTargeters(ServiceCall call) => Ids((await OrderAsync(call, TargetingOrder.Targeters, [])).Targets);
}
