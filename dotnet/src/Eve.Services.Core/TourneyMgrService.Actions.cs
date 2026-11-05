using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed partial class TourneyMgrService
{
    [Method("BanShipType")]
    public ValueTask<PyValue> BanShipType(ServiceCall call) => ActionAsync(call, TourneyMatchAction.BanShipType);
    [Method("UnBanShipType")]
    public ValueTask<PyValue> UnBanShipType(ServiceCall call) => ActionAsync(call, TourneyMatchAction.UnBanShipType);
    [Method("StartAutobanning")]
    public ValueTask<PyValue> StartAutobanning(ServiceCall call) => ActionAsync(call, TourneyMatchAction.StartAutobanning);
    [Method("FinalizeBans")]
    public ValueTask<PyValue> FinalizeBans(ServiceCall call) => ActionAsync(call, TourneyMatchAction.FinalizeBans);
    [Method("CompletePregameChecks")]
    public ValueTask<PyValue> CompletePregameChecks(ServiceCall call) => ActionAsync(call, TourneyMatchAction.CompletePregameChecks);
    [Method("StartCountdown")]
    public ValueTask<PyValue> StartCountdown(ServiceCall call) => ActionAsync(call, TourneyMatchAction.StartCountdown);
    [Method("AbortCountdown")]
    public ValueTask<PyValue> AbortCountdown(ServiceCall call) => ActionAsync(call, TourneyMatchAction.AbortCountdown);
    [Method("MatchCompleted")]
    public ValueTask<PyValue> MatchCompleted(ServiceCall call) => ActionAsync(call, TourneyMatchAction.MatchCompleted);
    [Method("ResetMatch")]
    public ValueTask<PyValue> ResetMatch(ServiceCall call) => ActionAsync(call, TourneyMatchAction.ResetMatch);

    [Method("TeleportPlayers")]
    public ValueTask<PyValue> TeleportPlayers(ServiceCall call) => ActionAsync(call, TourneyMatchAction.TeleportPlayers);
    [Method("ReturnTeamPlayers")]
    public ValueTask<PyValue> ReturnTeamPlayers(ServiceCall call) => ActionAsync(call, TourneyMatchAction.ReturnTeamPlayers);
    [Method("ReturnAllPlayers")]
    public ValueTask<PyValue> ReturnAllPlayers(ServiceCall call) => ActionAsync(call, TourneyMatchAction.ReturnAllPlayers);
    [Method("TeleportRefHome")]
    public ValueTask<PyValue> TeleportRefHome(ServiceCall call) => ActionAsync(call, TourneyMatchAction.TeleportRefHome);
    [Method("CleanupGrid")]
    public ValueTask<PyValue> CleanupGrid(ServiceCall call) => ActionAsync(call, TourneyMatchAction.CleanupGrid);
    [Method("OverrideMoveRestrict")]
    public ValueTask<PyValue> OverrideMoveRestrict(ServiceCall call) => ActionAsync(call, TourneyMatchAction.OverrideMoveRestrict);
    [Method("OverrideWarpRestrict")]
    public ValueTask<PyValue> OverrideWarpRestrict(ServiceCall call) => ActionAsync(call, TourneyMatchAction.OverrideWarpRestrict);
    [Method("OverrideLockRestrict")]
    public ValueTask<PyValue> OverrideLockRestrict(ServiceCall call) => ActionAsync(call, TourneyMatchAction.OverrideLockRestrict);

    [Method("UpdateFleetDetails")]
    public ValueTask<PyValue> UpdateFleetDetails(ServiceCall call) => ActionAsync(call, TourneyMatchAction.UpdateFleetDetails);
    [Method("LockMemberList")]
    public ValueTask<PyValue> LockMemberList(ServiceCall call) => ActionAsync(call, TourneyMatchAction.LockMemberList);
    [Method("UnlockMemberList")]
    public ValueTask<PyValue> UnlockMemberList(ServiceCall call) => ActionAsync(call, TourneyMatchAction.UnlockMemberList);
    [Method("AddPlayer")]
    public ValueTask<PyValue> AddPlayer(ServiceCall call) => ActionAsync(call, TourneyMatchAction.AddPlayer);
    [Method("RemovePlayer")]
    public ValueTask<PyValue> RemovePlayer(ServiceCall call) => ActionAsync(call, TourneyMatchAction.RemovePlayer);
    [Method("OverrideError")]
    public ValueTask<PyValue> OverrideError(ServiceCall call) => ActionAsync(call, TourneyMatchAction.OverrideError);

    private ValueTask<PyValue> ActionAsync(ServiceCall call, TourneyMatchAction operation)
    {
        if (call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var binding) ||
            !ReferenceEquals(binding.Session, call.Session) || !GameMasterRoles.Has(call.Session, GameMasterRoles.Staff) ||
            call.Session.CharacterId is not > 0 || call.Session.DockedLocationId is not null ||
            call.Session.GetInt("solarsystemid") != binding.SystemId)
            throw MachoErrors.ThrowUserError("UnMachoDestination");
        if (call.Args.Count == 0 || Positive(call.Arg(0)) is not { } matchId)
            throw new ArgumentException("Invalid tournament match ID.");
        var spaceOperation = operation is TourneyMatchAction.TeleportPlayers or TourneyMatchAction.ReturnTeamPlayers or
            TourneyMatchAction.ReturnAllPlayers or TourneyMatchAction.TeleportRefHome or TourneyMatchAction.CleanupGrid or
            TourneyMatchAction.OverrideMoveRestrict or TourneyMatchAction.OverrideWarpRestrict or TourneyMatchAction.OverrideLockRestrict;
        var rosterOperation = operation is TourneyMatchAction.UpdateFleetDetails or TourneyMatchAction.LockMemberList or
            TourneyMatchAction.UnlockMemberList or TourneyMatchAction.AddPlayer or TourneyMatchAction.RemovePlayer or TourneyMatchAction.OverrideError;
        var action = (operation == TourneyMatchAction.TeleportRefHome ? matches?.RefHomeAsync :
            spaceOperation ? matches?.SpaceActionAsync : rosterOperation ? matches?.RosterActionAsync : matches?.ActionAsync)
            ?? throw new InvalidOperationException("Tournament match action is not wired.");
        return action(new TourneyMatchActionRequest(operation, matchId, call.Args, call.Kwargs, binding.SystemId, oid, call.Session), call.Cancellation);
    }
}
