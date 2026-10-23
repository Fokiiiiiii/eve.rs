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
    Func<long>? NowMs = null,
    Func<long, IReadOnlyList<long>, CancellationToken, ValueTask<FwFactionMembers>>? FactionMembersAsync = null,
    Func<CancellationToken, ValueTask<IReadOnlyList<(long CorporationId, long AllianceId)>>>? AffiliationsAsync = null,
    Func<long, long, CancellationToken, ValueTask<bool>>? CanActAsCorporationDirectorAsync = null,
    Func<long, long, CancellationToken, ValueTask<bool>>? CanActAsAllianceExecutorDirectorAsync = null);

/// <summary>The members of a war faction: all of them, those of its militia corporation, and the largest enlisted corporation's members.</summary>
public sealed record FwFactionMembers(long Total, long Militia, long Top);

/// <summary>A refusal the client shows: the message key and its entries (the same shape as the other services' user errors).</summary>
public sealed class FacWarMgrUserErrorException(string messageKey, params (string Name, PyValue Value)[] entries) : Exception(messageKey)
{
    public string MessageKey { get; } = messageKey;

    public (string Name, PyValue Value)[] Entries { get; } = entries;
}

/// <summary>
/// facWarMgr (V24.01 client <c>facWarSvc.py</c> calls): corporation and alliance enlistment, character enlistment, the militia rank from the
/// standing, the system LP pools and the LP donation, and the character and corporation statistics. The faction, militia, alliance, top and

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
        "GetStats_FactionInfo", "GetStats_Militia", "GetStats_Alliance", "GetStats_TopAndAllKillsAndVPs", "GetMyCharacterRankOverview", "GetCharacterRankOverview",
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
                await RequireCorporationDirectorAsync(call, corporationId);
                return await JoinAsync(CorporationKind, corporationId, Id(call, 0), now, cancellation);
            case "JoinFactionAsAlliance":
                await RequireAllianceExecutorDirectorAsync(call, session.GetInt("allianceid") ?? session.GetInt("allianceID") ?? 0);
                return await JoinAsync(AllianceKind, session.GetInt("allianceid") ?? 0, Id(call, 0), now, cancellation);
            case "LeaveFactionAsCorporation":
                await RequireCorporationDirectorAsync(call, corporationId);
                return Refused(await factionWarfare.LeaveAsync((int)CorporationKind, corporationId, now, cancellation));
            case "LeaveFactionAsAlliance":
                await RequireAllianceExecutorDirectorAsync(call, session.GetInt("allianceid") ?? session.GetInt("allianceID") ?? 0);
                return Refused(await factionWarfare.LeaveAsync((int)AllianceKind, session.GetInt("allianceid") ?? 0, now, cancellation));
            case "WithdrawJoinFactionAsCorporation":
                await RequireCorporationDirectorAsync(call, corporationId);
                return Refused(await factionWarfare.WithdrawJoinAsync((int)CorporationKind, corporationId, now, cancellation));
            case "WithdrawJoinFactionAsAlliance":
                await RequireAllianceExecutorDirectorAsync(call, session.GetInt("allianceid") ?? session.GetInt("allianceID") ?? 0);
                return Refused(await factionWarfare.WithdrawJoinAsync((int)AllianceKind, session.GetInt("allianceid") ?? 0, now, cancellation));
            case "WithdrawLeaveFactionAsCorporation":
                await RequireCorporationDirectorAsync(call, corporationId);
                return Refused(await factionWarfare.WithdrawLeaveAsync((int)CorporationKind, corporationId, now, cancellation));
            case "WithdrawLeaveFactionAsAlliance":
                await RequireAllianceExecutorDirectorAsync(call, session.GetInt("allianceid") ?? session.GetInt("allianceID") ?? 0);
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
            case "GetStats_FactionInfo":
                return await FactionInfoAsync(now, cancellation);
            case "GetStats_Militia":
                return await MilitiaAsync(now, cancellation);
            case "GetStats_Alliance":
                return StatsPayload(await AllianceWindowAsync(session.GetInt("allianceid") ?? 0, now, cancellation));
            case "GetStats_TopAndAllKillsAndVPs":
                return await TopAndAllAsync(now, cancellation);
            case "GetMyCharacterRankOverview":
                return await RankOverviewAsync(characterId, cancellation);
            case "GetCharacterRankOverview":
                return await RankOverviewAsync(Id(call, 0), cancellation);
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

    /// <summary>
    /// The faction statistics (the client's dashboard): per occupier faction, its members, its militia members, the largest enlisted corporation's members and the
    /// systems it holds. Each entry is read by attribute (util.KeyVal).
    /// </summary>
    private async ValueTask<PyValue> FactionInfoAsync(long now, CancellationToken cancellation)
    {
        var members = callbacks.FactionMembersAsync ?? throw new InvalidOperationException("the faction members are not wired");
        var systems = await factionWarfare.OccupiedSystemsAsync(cancellation);
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var faction in FwFactions.Occupiers)
        {
            var corporations = await factionWarfare.CorporationsInFactionAsync(faction, now, cancellation);
            var count = await members(faction, corporations, cancellation);
            var held = systems.Count(system => system.OccupierId == faction);
            entries.Add(new KeyValuePair<PyValue, PyValue>(new PyInt(faction), Py.KeyVal(
                ("totalMembersCount", new PyInt(count.Total)),
                ("militiaMembersCount", new PyInt(count.Militia)),
                ("topMemberCount", new PyInt(count.Top)),
                ("systemsCount", new PyInt(held)))));
        }
        return new PyDict([.. entries]);
    }

    /// <summary>The militia statistics: the header is the occupier factions, and each key's data holds one value per faction (its enlisted corporations' sum).</summary>
    private async ValueTask<PyValue> MilitiaAsync(long now, CancellationToken cancellation)
    {
        var windows = await factionWarfare.OwnerWindowsAsync(corporations: true, now, cancellation);
        var perFaction = new List<(long Faction, FwStatsWindow Window)>();
        foreach (var faction in FwFactions.Occupiers)
        {
            var enlisted = (await factionWarfare.CorporationsInFactionAsync(faction, now, cancellation)).ToHashSet();
            perFaction.Add((faction, FwStore.Sum(windows.Where(row => enlisted.Contains(row.OwnerId)).Select(row => row.Window))));
        }
        var header = new PyList([.. FwFactions.Occupiers.Select(faction => (PyValue)new PyInt(faction))]);
        PyDict PerFaction(Func<FwStatsWindow, long> value) => new([.. perFaction.Select(entry => new KeyValuePair<PyValue, PyValue>(new PyInt(entry.Faction), new PyInt(value(entry.Window))))]);
        var data = new PyDict(
        [
            new KeyValuePair<PyValue, PyValue>(new PyStr("killsY"), PerFaction(w => w.KillsYesterday)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("killsLW"), PerFaction(w => w.KillsLastWeek)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("killsTotal"), PerFaction(w => w.KillsTotal)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("vpY"), PerFaction(w => w.VictoryPointsYesterday)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("vpLW"), PerFaction(w => w.VictoryPointsLastWeek)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("vpTotal"), PerFaction(w => w.VictoryPointsTotal)),
        ]);
        return new PyDict(
        [
            new KeyValuePair<PyValue, PyValue>(new PyStr("header"), header),
            new KeyValuePair<PyValue, PyValue>(new PyStr("data"), data),
        ]);
    }

    /// <summary>The window of an alliance: the sum over the corporations that are members of it now (an alliance of 0 has none).</summary>
    private async ValueTask<FwStatsWindow> AllianceWindowAsync(long allianceId, long now, CancellationToken cancellation)
    {
        if (allianceId <= 0)
            return FwStore.Sum([]);
        var members = await MemberCorporationsAsync(allianceId, cancellation);
        var windows = await factionWarfare.OwnerWindowsAsync(corporations: true, now, cancellation);
        return FwStore.Sum(windows.Where(row => members.Contains(row.OwnerId)).Select(row => row.Window));
    }

    private async ValueTask<HashSet<long>> MemberCorporationsAsync(long allianceId, CancellationToken cancellation)
    {
        var affiliations = callbacks.AffiliationsAsync ?? throw new InvalidOperationException("the alliance affiliations are not wired");
        return (await affiliations(cancellation)).Where(row => row.AllianceId == allianceId).Select(row => row.CorporationId).ToHashSet();
    }

    /// <summary>


    /// </summary>
    private async ValueTask<PyValue> TopAndAllAsync(long now, CancellationToken cancellation)
    {
        var pilots = (await factionWarfare.OwnerWindowsAsync(corporations: false, now, cancellation)).Select(row => row.Window).ToList();
        var corporations = await factionWarfare.OwnerWindowsAsync(corporations: true, now, cancellation);
        var affiliations = callbacks.AffiliationsAsync ?? throw new InvalidOperationException("the alliance affiliations are not wired");
        var allianceOf = (await affiliations(cancellation)).Where(row => row.AllianceId > 0).ToDictionary(row => row.CorporationId, row => row.AllianceId);
        var alliances = corporations.Where(row => allianceOf.ContainsKey(row.OwnerId))
            .GroupBy(row => allianceOf[row.OwnerId], row => row.Window)
            .Select(group => FwStore.Sum(group)).ToList();
        var corporationWindows = corporations.Select(row => row.Window).ToList();
        var top = new PyDict(
        [
            new KeyValuePair<PyValue, PyValue>(new PyInt(GroupCharacter), StatsPayload(FwStore.Max(pilots))),
            new KeyValuePair<PyValue, PyValue>(new PyInt(GroupCorporation), StatsPayload(FwStore.Max(corporationWindows))),
            new KeyValuePair<PyValue, PyValue>(new PyInt(GroupAlliance), StatsPayload(FwStore.Max(alliances))),
        ]);
        var all = new PyDict(
        [
            new KeyValuePair<PyValue, PyValue>(new PyInt(GroupCharacter), StatsPayload(FwStore.Sum(pilots))),
            new KeyValuePair<PyValue, PyValue>(new PyInt(GroupCorporation), StatsPayload(FwStore.Sum(corporationWindows))),
            new KeyValuePair<PyValue, PyValue>(new PyInt(GroupAlliance), StatsPayload(FwStore.Sum(alliances))),
        ]);
        return new PyTuple([top, all]);
    }

    /// <summary>The factions a character holds a rank in (the rank from its standing with the militia corporation), as util.KeyVal entries.</summary>
    private async ValueTask<PyValue> RankOverviewAsync(long characterId, CancellationToken cancellation)
    {
        var ranks = new List<PyValue>();
        if (characterId <= 0)
            return new PyList(ranks);
        foreach (var faction in FwFactions.Occupiers)
        {
            var standing = await callbacks.ReadStandingAsync(characterId, FwFactions.MilitiaCorporationOf(faction), cancellation);
            var rank = FwRules.RankOf(standing);
            if (rank > 0)
                ranks.Add(Py.KeyVal(("factionID", new PyInt(faction)), ("currentRank", new PyInt(rank))));
        }
        return new PyList(ranks);
    }

    private const long GroupCharacter = 1;
    private const long GroupCorporation = 2;
    private const long GroupAlliance = 32;

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

    private async ValueTask RequireCorporationDirectorAsync(ServiceCall call, long corporationId)
    {
        if (((call.Session.GetInt("corprole") ?? 0) & CorporationRoleDirector) == 0 || corporationId <= 0)
            throw Notice("UI/FactionWarfare/DirectorRequired");
        if (callbacks.CanActAsCorporationDirectorAsync is { } canAct &&
            !await canAct(call.Session.CharacterId ?? 0, corporationId, call.Cancellation))
            throw Notice("UI/FactionWarfare/DirectorRequired");
    }

    private async ValueTask RequireAllianceExecutorDirectorAsync(ServiceCall call, long allianceId)
    {
        if (((call.Session.GetInt("corprole") ?? 0) & CorporationRoleDirector) == 0 || allianceId <= 0)
            throw Notice("UI/FactionWarfare/DirectorRequired");
        if (callbacks.CanActAsAllianceExecutorDirectorAsync is { } canAct &&
            !await canAct(call.Session.CharacterId ?? 0, allianceId, call.Cancellation))
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
