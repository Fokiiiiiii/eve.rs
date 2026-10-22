using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

[Service("fleetProxy")]
public sealed partial class FleetProxyService(FleetStore store, Func<string, long, IReadOnlyList<PyValue>, ClientSession, CancellationToken, Task<PyValue?>>? fleetAdvertOperation = null, Func<long, ClientSession?>? sessionForCharacter = null)
{
    [Method("ApplyToJoinFleet")]
    public async ValueTask<PyValue> ApplyToJoinFleet(ServiceCall call)
    {
        var fleetId = call.Arg(0) switch
        {
            PyInt number => number.Value,
            PyLong number => number.Value,
            PyStr text when long.TryParse(text.Value, out var parsed) => parsed,
            PyWString text when long.TryParse(text.Value, out var parsed) => parsed,
            _ => 0
        };
        var characterId = CharacterId(call.Session);
        if (fleetId <= 0 || characterId <= 0)
            throw MachoErrors.ThrowUserError("FleetNotFound");
        var autoAccept = call.Arg(1) switch
        {
            PyBool boolean => boolean.Value,
            PyInt number => number.Value != 0,
            _ => false
        };
        var snapshot = await store.SnapshotAsync(fleetId, call.Cancellation);
        var advert = await store.ReadJoinAdvertPolicyAsync(fleetId, call.Session, call.Cancellation);
        if (snapshot is null || !snapshot.IsRegistered || advert is null)
            throw MachoErrors.ThrowUserError("FleetNotFound");
        if (!advert.Value.IsVisible)
            throw MachoErrors.ThrowUserError("FleetNotAllowed");
        if (advert.Value.JoinLimit is { } joinLimit && snapshot.Members.Count >= joinLimit)
            throw MachoErrors.ThrowUserError("FleetTooManyMembers");
        if (await store.FleetIdForCharacterAsync(characterId, call.Cancellation) is not null)
            throw MachoErrors.ThrowUserError("FleetError");

        var bossId = FleetObjectService.BossCharacterId(snapshot);
        if (bossId is null)
            throw MachoErrors.ThrowUserError("FleetNotFound");
        if (advert.Value.JoinNeedsApproval)
        {
            var request = FleetObjectService.JoinRequestFromSession(call.Session);
            try
            {
                if (!await store.RequestJoinAsync(fleetId, request, call.Cancellation))
                    return PyBool.False;
            }
            catch (FleetJoinRequestLimitException)
            {
                throw MachoErrors.ThrowUserError("FleetError");
            }
            sessionForCharacter?.Invoke(bossId.Value)?.SendNotification(
                "OnFleetJoinRequest", "fleetid",
                FleetObjectService.JoinRequestValue(request, call.Session));
            return PyBool.True;
        }

        var (wingId, squadId) = DefaultPlacement(snapshot);
        if (await store.InviteAsync(fleetId, bossId.Value, characterId, wingId, squadId,
                FleetStore.Member, call.Cancellation))
            FleetObjectService.SendInviteNotification(call.Session, fleetId, bossId.Value, autoAccept);
        return PyBool.False;
    }

    private static (long WingId, long SquadId) DefaultPlacement(FleetSnapshot snapshot)
    {
        if (snapshot.AutoJoinSquadId is { } autoJoinSquadId)
            foreach (var wing in snapshot.Wings)
                if (wing.Squads.Any(squad => squad.SquadId == autoJoinSquadId))
                    return (wing.WingId, autoJoinSquadId);
        var firstWing = snapshot.Wings.FirstOrDefault();
        if (firstWing is null || firstWing.Squads.Count == 0)
            throw MachoErrors.ThrowUserError("FleetNoPositionFound");
        return (firstWing.WingId, firstWing.Squads[0].SquadId);
    }
}

public sealed partial class FleetProxyService
{
    [Method("GetAvailableFleetAds")]
    public async ValueTask<PyValue> GetAvailableFleetAds(ServiceCall call)
    {
        if (CharacterId(call.Session) <= 0) return new PyDict([]);
        var result = await AdvertOperationAsync("GetAvailableFleetAds", call, []);
        return result is PyDict
            ? result
            : throw new InvalidOperationException("GetAvailableFleetAds persistence callback must return a fleetID-to-advert PyDict.");
    }

    [Method("AddFleetFinderAdvert")]
    public ValueTask<PyValue> AddFleetFinderAdvert(ServiceCall call) =>
        MutateFleetAdvertAsync("AddFleetFinderAdvert", call, [call.Arg(0)]);

    [Method("UpdateFleetAdvertWithNewLeader")]
    public ValueTask<PyValue> UpdateFleetAdvertWithNewLeader(ServiceCall call) =>
        MutateFleetAdvertAsync("UpdateFleetAdvertWithNewLeader", call, [call.Arg(0)]);

    [Method("RemoveFleetFinderAdvert")]
    public ValueTask<PyValue> RemoveFleetFinderAdvert(ServiceCall call) =>
        MutateFleetAdvertAsync("RemoveFleetFinderAdvert", call, []);

    [Method("GetMyFleetFinderAdvert")]
    public ValueTask<PyValue> GetMyFleetFinderAdvert(ServiceCall call) =>
        AdvertOperationAsync("GetMyFleetFinderAdvert", call, []);

    [Method("UpdateAdvertInfo")]
    public ValueTask<PyValue> UpdateAdvertInfo(ServiceCall call) =>
        MutateFleetAdvertAsync("UpdateAdvertInfo", call, [call.Arg(0), call.Arg(1)]);

    [Method("UpdateAdvertAllowedEntities")]
    public ValueTask<PyValue> UpdateAdvertAllowedEntities(ServiceCall call) =>
        MutateFleetAdvertAsync("UpdateAdvertAllowedEntities", call, [call.Arg(0)]);

    private async ValueTask<PyValue> MutateFleetAdvertAsync(string operation, ServiceCall call, IReadOnlyList<PyValue> args)
    {
        if (await BossFleetAsync(call) is not { } fleet) return PyNone.Instance;
        var result = await AdvertOperationAsync(operation, call, args);
        var after = await store.SnapshotAsync(fleet.FleetId, call.Cancellation);
        if (after is not null && fleet.Snapshot.IsRegistered != after.IsRegistered)
        {
            var oldOptions = FleetOptionsValue(fleet.Snapshot);
            var newOptions = FleetOptionsValue(after);
            foreach (var member in after.Members)
                sessionForCharacter?.Invoke(member.CharacterId)?.SendNotification(
                    "OnFleetOptionsChanged", "charid", oldOptions, newOptions);
        }
        return result;
    }

    private async ValueTask<PyValue> AdvertOperationAsync(string operation, ServiceCall call, IReadOnlyList<PyValue> args)
    {
        var characterId = CharacterId(call.Session);
        if (characterId <= 0) return PyNone.Instance;
        if (fleetAdvertOperation is null)
            throw new InvalidOperationException($"FleetFinder operation '{operation}' requires the Host FleetStore callback.");
        return await fleetAdvertOperation(operation, characterId, args, call.Session, call.Cancellation)
            ?? PyNone.Instance;
    }

    private async Task<(long FleetId, FleetSnapshot Snapshot)?> BossFleetAsync(ServiceCall call)
    {
        var characterId = CharacterId(call.Session);
        if (characterId <= 0 ||
            await store.FleetIdForCharacterAsync(characterId, call.Cancellation) is not { } fleetId ||
            await store.SnapshotAsync(fleetId, call.Cancellation) is not { } snapshot ||
            snapshot.Members.FirstOrDefault(member => member.CharacterId == characterId) is not { } member ||
            (member.Role != FleetStore.Leader && !FleetStore.HasBossJob(member.Job)))
            return null;
        return (fleetId, snapshot);
    }

    private static PyValue FleetOptionsValue(FleetSnapshot snapshot) => Py.KeyVal([
        ("autoJoinSquadID", snapshot.AutoJoinSquadId is { } squadId ? new PyLong(squadId) : PyNone.Instance),
        ("isFreeMove", snapshot.IsFreeMove ? PyBool.True : PyBool.False),
        ("isRegistered", snapshot.IsRegistered ? PyBool.True : PyBool.False)
    ]);

    private static long CharacterId(ClientSession session) => session.CharacterId ?? session.GetInt("charid") ?? 0;
}
