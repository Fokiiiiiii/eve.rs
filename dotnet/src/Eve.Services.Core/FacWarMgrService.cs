using Eve.FactionWarfare;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>
/// The character-side reads and writes of facWarMgr, supplied by the host: the character's state (direct enlistment and cooldown), the standing
/// of a subject toward a faction or militia corporation (the relation store's <c>standing</c>), the saved character state, and the clock.
/// </summary>
public sealed record FacWarMgrCallbacks(
    Func<long, CancellationToken, ValueTask<FwCharacterWarfareState?>> ReadCharacterAsync,
    Func<long, long?, long, CancellationToken, ValueTask<bool>> SaveCharacterAsync,
    Func<long, long, CancellationToken, ValueTask<double>> ReadStandingAsync,
    Func<long>? NowMs = null);

/// <summary>A refusal the client shows: the message key and its entries (the same shape as the other services' user errors).</summary>
public sealed class FacWarMgrUserErrorException(string messageKey, params (string Name, PyValue Value)[] entries) : Exception(messageKey)
{
    public string MessageKey { get; } = messageKey;

    public (string Name, PyValue Value)[] Entries { get; } = entries;
}

/// <summary>
/// facWarMgr (V24.01 client <c>facWarSvc.py</c> calls): corporation and alliance enlistment, character enlistment, the militia rank from the
/// standing, the system LP pools and the LP donation, and the character and corporation statistics. The faction, militia, alliance, top and
/// rank-overview statistics are not served (TODO, DEVIATIONS "Factional warfare"); the kill producer that fills the statistics is not connected.
/// </summary>
public sealed class FacWarMgrService(FwService factionWarfare, FacWarMgrCallbacks callbacks) : IService
{
    private const long CorporationKind = FwStore.CorporationKind;
    private const long AllianceKind = FwStore.AllianceKind;
    private const long CorporationRoleDirector = 1;

    private static readonly HashSet<string> Methods =
    [
        "CheckForRankChange", "DonateLPsToSolarSystem", "GetAllianceWarFactionID", "GetCharacterRankInfo", "GetControlBunkerIDInSystem",
        "GetCorpFactionalWarStatus", "GetCorporationWarFactionID", "GetFactionCorporations", "GetFactionMilitiaCorporation", "GetMyCharacterRankInfo",
        "GetSolarSystemLPs", "GetSolarSystemLPsEx", "GetStats_Character", "GetStats_Corp", "JoinFactionAsAlliance", "JoinFactionAsCharacter", "JoinFactionAsCorporation",
        "LeaveFactionAsAlliance", "LeaveFactionAsCorporation", "RefreshCorps", "WithdrawJoinFactionAsAlliance", "WithdrawJoinFactionAsCorporation",
        "WithdrawLeaveFactionAsAlliance", "WithdrawLeaveFactionAsCorporation",
    ];

    public string Name => "facWarMgr";

    public bool HasMethod(string method) => Methods.Contains(method);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        try
        {
            return await Dispatch(call);
        }
        catch (FacWarMgrUserErrorException error)
        {
            throw MachoErrors.ThrowUserError(error.MessageKey, error.Entries);
        }
    }

    private async ValueTask<PyValue> Dispatch(ServiceCall call)
    {
        var session = call.Session;
        var cancellation = call.Cancellation;
        var now = (callbacks.NowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))();
        var characterId = session.GetInt("charid") ?? session.GetInt("characterID") ?? 0;
        var corporationId = session.GetInt("corpid") ?? session.GetInt("corporationID") ?? 0;
        var warFactionId = session.GetInt("warfactionid") ?? 0;
        switch (call.Method)
        {
            case "GetCorporationWarFactionID":
                return Positive(await factionWarfare.FactionOfAsync((int)CorporationKind, Id(call, 0), now, cancellation));
            case "GetAllianceWarFactionID":
                return Positive(await factionWarfare.FactionOfAsync((int)AllianceKind, Id(call, 0), now, cancellation));
            case "GetFactionCorporations":
                return new PyList([.. (await factionWarfare.CorporationsInFactionAsync(Id(call, 0), now, cancellation)).Select(id => (PyValue)new PyInt(id))]);
            case "GetFactionMilitiaCorporation":
                return Positive(FwFactions.MilitiaCorporationOf(Id(call, 0)));
            case "GetCorpFactionalWarStatus":
            {
                var status = await factionWarfare.StatusAsync((int)CorporationKind, corporationId, now, cancellation);
                if (status is null)
                    return PyNone.Instance;
                return Py.KeyVal(
                    ("status", new PyInt(status.Status)),
                    ("factionID", new PyInt(status.FactionId)),
                    ("startDate", new PyLong(UnixMsToFileTime(status.StartMs))));
            }
            case "JoinFactionAsCorporation":
                RequireDirector(session);
                return await JoinAsync(CorporationKind, corporationId, Id(call, 0), now, cancellation);
            case "JoinFactionAsAlliance":
                RequireDirector(session);
                return await JoinAsync(AllianceKind, session.GetInt("allianceid") ?? 0, Id(call, 0), now, cancellation);
            case "LeaveFactionAsCorporation":
                RequireDirector(session);
                return Refused(await factionWarfare.LeaveAsync((int)CorporationKind, corporationId, now, cancellation));
            case "LeaveFactionAsAlliance":
                RequireDirector(session);
                return Refused(await factionWarfare.LeaveAsync((int)AllianceKind, session.GetInt("allianceid") ?? 0, now, cancellation));
            case "WithdrawJoinFactionAsCorporation":
                RequireDirector(session);
                return Refused(await factionWarfare.WithdrawJoinAsync((int)CorporationKind, corporationId, now, cancellation));
            case "WithdrawJoinFactionAsAlliance":
                RequireDirector(session);
                return Refused(await factionWarfare.WithdrawJoinAsync((int)AllianceKind, session.GetInt("allianceid") ?? 0, now, cancellation));
            case "WithdrawLeaveFactionAsCorporation":
                RequireDirector(session);
                return Refused(await factionWarfare.WithdrawLeaveAsync((int)CorporationKind, corporationId, now, cancellation));
            case "WithdrawLeaveFactionAsAlliance":
                RequireDirector(session);
                return Refused(await factionWarfare.WithdrawLeaveAsync((int)AllianceKind, session.GetInt("allianceid") ?? 0, now, cancellation));
            case "JoinFactionAsCharacter":
                return await JoinAsCharacterAsync(session, characterId, warFactionId, Id(call, 0), now, cancellation);
            case "GetMyCharacterRankInfo":
                return await RankInfoAsync(characterId, warFactionId, cancellation);
            case "GetCharacterRankInfo":
            {
                var state = await callbacks.ReadCharacterAsync(Id(call, 0), cancellation);
                return await RankInfoAsync(Id(call, 0), state?.DirectFactionId ?? 0, cancellation);
            }
            case "GetStats_Character":
                return StatsPayload(await factionWarfare.PilotStatsAsync(characterId, now, cancellation));
            case "GetStats_Corp":
                return StatsPayload(await factionWarfare.CorporationStatsAsync(corporationId, now, cancellation));
            case "CheckForRankChange":
                return PyNone.Instance;
            case "GetSolarSystemLPs":
            case "GetSolarSystemLPsEx":
                return new PyInt(await factionWarfare.SolarSystemLpsAsync(Id(call, 0), cancellation));
            case "DonateLPsToSolarSystem":
                return await DonateAsync(session, characterId, corporationId, warFactionId, call, now, cancellation);
            case "GetControlBunkerIDInSystem":
                return PyNone.Instance;
            case "RefreshCorps":
                return PyNone.Instance;
            default:
                return PyNone.Instance;
        }
    }

    private async ValueTask<PyValue> JoinAsync(long kind, long ownerId, long factionId, long now, CancellationToken cancellation)
    {
        var standing = await callbacks.ReadStandingAsync(ownerId, factionId, cancellation);
        return Refused(await factionWarfare.JoinAsync((int)kind, ownerId, factionId, standing, now, cancellation));
    }

    private async ValueTask<PyValue> JoinAsCharacterAsync(ClientSession session, long characterId, long warFactionId, long factionId, long now, CancellationToken cancellation)
    {
        if (characterId <= 0 || !FwFactions.IsOccupier(factionId))
            return PyNone.Instance;
        if (warFactionId != 0)
            throw Notice("UI/FactionWarfare/AlreadyInMilitia");
        var state = await callbacks.ReadCharacterAsync(characterId, cancellation);
        if (state?.DirectFactionId is > 0)
            throw Notice("UI/FactionWarfare/AlreadyInMilitia");
        if (state is not null && state.DirectEnlistmentCooldownFileTime > DateTimeOffset.UtcNow.ToFileTime())
            throw Notice("UI/FactionWarfare/CooldownActive");
        var standing = await callbacks.ReadStandingAsync(characterId, factionId, cancellation);
        if (!FwRules.MeetsEnlistmentStanding(standing))
            throw Notice("UI/FactionWarfare/StandingTooLow");
        if (await callbacks.SaveCharacterAsync(characterId, factionId, 0, cancellation))
            session.Change(("warfactionid", new PyInt(factionId)));
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> RankInfoAsync(long characterId, long factionId, CancellationToken cancellation)
    {
        if (characterId <= 0 || !FwFactions.IsOccupier(factionId))
            return PyNone.Instance;
        var standing = await callbacks.ReadStandingAsync(characterId, FwFactions.MilitiaCorporationOf(factionId), cancellation);
        return Py.KeyVal(("currentRank", new PyInt(FwRules.RankOf(standing))));
    }

    private async ValueTask<PyValue> DonateAsync(ClientSession session, long characterId, long corporationId, long warFactionId, ServiceCall call, long now,
        CancellationToken cancellation)
    {
        var pointsDonated = Id(call, 0);
        var pointsToHub = Id(call, 1);
        var systemId = session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0;
        var result = await factionWarfare.DonateAsync(systemId, characterId, corporationId, warFactionId, pointsDonated, pointsToHub, now, cancellation);
        return result.Outcome switch
        {
            DonateOutcome.Accepted => PyNone.Instance,
            DonateOutcome.TaxChanged => throw new FacWarMgrUserErrorException("FacWarDonationTaxChanged", ("taxNow", new PyInt(result.Figure))),
            DonateOutcome.PoolOverloaded => throw Notice("UI/FactionWarfare/PoolOverloaded"),
            DonateOutcome.InsufficientLp => throw Notice("UI/FactionWarfare/NotEnoughLP"),
            DonateOutcome.NotOccupied => throw Notice("UI/FactionWarfare/NotOccupiedSystem"),
            _ => throw Notice("UI/FactionWarfare/NotEnlisted"),
        };
    }

    private static PyValue Refused(EnlistOutcome outcome) => outcome switch
    {
        EnlistOutcome.Accepted => PyNone.Instance,
        EnlistOutcome.NotAnOccupier => throw Notice("UI/FactionWarfare/NotAnOccupier"),
        EnlistOutcome.AlreadyEnlisted => throw Notice("UI/FactionWarfare/AlreadyInMilitia"),
        EnlistOutcome.BelowStanding => throw Notice("UI/FactionWarfare/StandingTooLow"),
        _ => throw Notice("UI/FactionWarfare/NothingToWithdraw"),
    };

    private static void RequireDirector(ClientSession session)
    {
        if (((session.GetInt("corprole") ?? 0) & CorporationRoleDirector) == 0)
            throw Notice("UI/FactionWarfare/DirectorRequired");
    }

    /// <summary>The statistics dict the client reads by key (killsY, killsLW, killsTotal, vpY, vpLW, vpTotal).</summary>
    private static PyDict StatsPayload(FwStatsWindow window) => new(
    [
        new KeyValuePair<PyValue, PyValue>(new PyStr("killsY"), new PyInt(window.KillsYesterday)),
        new KeyValuePair<PyValue, PyValue>(new PyStr("killsLW"), new PyInt(window.KillsLastWeek)),
        new KeyValuePair<PyValue, PyValue>(new PyStr("killsTotal"), new PyInt(window.KillsTotal)),
        new KeyValuePair<PyValue, PyValue>(new PyStr("vpY"), new PyInt(window.VictoryPointsYesterday)),
        new KeyValuePair<PyValue, PyValue>(new PyStr("vpLW"), new PyInt(window.VictoryPointsLastWeek)),
        new KeyValuePair<PyValue, PyValue>(new PyStr("vpTotal"), new PyInt(window.VictoryPointsTotal)),
    ]);

    private static FacWarMgrUserErrorException Notice(string text) =>
        new("CustomInfo", ("info", new PyStr(text)));

    private static long Id(ServiceCall call, int index) => RWManagerService.PositiveInteger(call.Arg(index));

    private static PyValue Positive(long value) => value > 0 ? new PyInt(value) : PyNone.Instance;

    private static long UnixMsToFileTime(long unixMs) => unixMs * 10_000 + 116_444_736_000_000_000L;
}
