using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed partial class TourneyMgrService
{
    [Method("OverrideFleetBossID")]
    public async ValueTask<PyValue> OverrideFleetBossID(ServiceCall call)
    {
        if (call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var binding) ||
            !ReferenceEquals(binding.Session, call.Session) || !GameMasterRoles.Has(call.Session, GameMasterRoles.Staff) ||
            call.Session.CharacterId is not > 0 || call.Session.DockedLocationId is not null ||
            call.Session.GetInt("solarsystemid") != binding.SystemId)
            throw MachoErrors.ThrowUserError("UnMachoDestination");
        static long? Number(PyValue x) => x switch { PyInt n => n.Value, PyLong n => n.Value, _ => null };
        var team = Number(call.Arg(1));
        if (call.Args.Count != 3 || call.Kwargs is not PyNone || Positive(call.Arg(0)) is not { } matchId ||
            team is not (0 or 1) || Positive(call.Arg(2)) is not { } candidate || Number(call.CallId) is not { } callId)
            throw new ArgumentException("Invalid fleet captain nomination.");
        var nominate = matches?.NominateFleetAsync ?? throw new InvalidOperationException("Tournament fleet consent is not wired.");
        await nominate(new TourneyFleetNominationRequest(matchId, (int)team.Value, candidate, binding.SystemId, oid, call.Session, callId), call.Cancellation);
        return PyNone.Instance;
    }

    [Method("ExecuteMemberChange")]
    public async ValueTask<PyValue> ExecuteMemberChange(ServiceCall call)
    {
        if (call.Args.Count != 0 || call.Kwargs is not PyNone || call.Session.CharacterId is not > 0 || call.Session.SessionId <= 0)
            throw new ArgumentException("Invalid fleet consent response.");
        var accept = matches?.AcceptFleetAsync ?? throw new InvalidOperationException("Tournament fleet consent is not wired.");
        await accept(call.Session, call.Cancellation);
        return PyNone.Instance;
    }
}
