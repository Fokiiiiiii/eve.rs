using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed partial class TourneyMgrService
{
    [Method("TeleportToArena")]
    public ValueTask<PyValue> TeleportToArena(ServiceCall call) => TravelAsync(call, TourneyRefTravelOperation.TeleportToArena);
    [Method("TeleportToStation")]
    public ValueTask<PyValue> TeleportToStation(ServiceCall call) => TravelAsync(call, TourneyRefTravelOperation.TeleportToStation);

    private ValueTask<PyValue> TravelAsync(ServiceCall call, TourneyRefTravelOperation operation)
    {
        if (call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var binding) ||
            !ReferenceEquals(binding.Session, call.Session) || !GameMasterRoles.Has(call.Session, GameMasterRoles.Staff) ||
            call.Session.CharacterId is not > 0 || call.Args.Count != 0 || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("UnMachoDestination");
        var travel = matches?.RefTravelAsync ?? throw new InvalidOperationException("Tournament referee travel is not wired.");
        return travel(new TourneyRefTravelRequest(operation, binding.SystemId, oid, call.Session), call.Cancellation);
    }
}
