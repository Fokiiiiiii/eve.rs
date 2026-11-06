using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed partial class TourneyMgrService
{
    [Method("GetTeamMembers")]
    public ValueTask<PyValue> GetTeamMembers(ServiceCall call) => CameraDetails(call, false);

    [Method("GetFancyDetails")]
    public ValueTask<PyValue> GetFancyDetails(ServiceCall call) => CameraDetails(call, true);

    private ValueTask<PyValue> CameraDetails(ServiceCall call, bool fancy)
    {
        if (call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var binding) ||
            !ReferenceEquals(binding.Session, call.Session) || call.Session.CharacterId is not > 0 ||
            call.Session.DockedLocationId is not null || call.Session.GetInt("solarsystemid") != binding.SystemId ||
            call.Args.Count != 1 || Positive(call.Arg(0)) is not { } matchId || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("UnMachoDestination");
        var read = matches?.CameraDetailsAsync ?? throw new InvalidOperationException("Tournament camera details are not wired.");
        return read(binding.SystemId, matchId, call.Session, fancy, call.Cancellation);
    }

    [Method("IsOtherMatchInProgress")]
    public ValueTask<PyValue> IsOtherMatchInProgress(ServiceCall call)
    {
        if (call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var binding) ||
            !ReferenceEquals(binding.Session, call.Session) || !GameMasterRoles.Has(call.Session, GameMasterRoles.Staff) ||
            call.Session.GetInt("solarsystemid") != binding.SystemId || call.Args.Count != 0 || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("UnMachoDestination");
        var read = matches?.OtherMatchAsync ?? throw new InvalidOperationException("Tournament match query is not wired.");
        return read(binding.SystemId, oid, call.Session, call.Cancellation);
    }

    [Method("RemoveSelfFromReferees")]
    public async ValueTask<PyValue> RemoveSelfFromReferees(ServiceCall call)
    {
        if (call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var binding) ||
            !ReferenceEquals(binding.Session, call.Session) || !GameMasterRoles.Has(call.Session, GameMasterRoles.Staff) ||
            call.Session.GetInt("solarsystemid") != binding.SystemId || call.Args.Count != 1 ||
            Positive(call.Arg(0)) is not { } matchId || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("UnMachoDestination");
        var remove = matches?.RemoveRefereeAsync ?? throw new InvalidOperationException("Tournament referee removal is not wired.");
        await remove(oid, binding.Generation, matchId, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetActiveMatchForCamera")]
    public ValueTask<PyValue> GetActiveMatchForCamera(ServiceCall call)
    {
        if (call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var binding) ||
            !ReferenceEquals(binding.Session, call.Session) ||
            call.Session.CharacterId is not > 0 || call.Session.DockedLocationId is not null ||
            call.Session.GetInt("solarsystemid") != binding.SystemId || call.Args.Count != 0 || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("UnMachoDestination");
        var read = matches?.ActiveCameraAsync ?? throw new InvalidOperationException("Tournament camera is not wired.");
        return read(binding.SystemId, call.Session, call.Cancellation);
    }
}
