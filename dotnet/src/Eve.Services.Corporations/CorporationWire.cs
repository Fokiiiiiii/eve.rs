using Eve.Corporations;
using Eve.Marshal;

namespace Eve.Services.Corporations;

public static class CorporationWire
{
    private static PyValue None => PyNone.Instance;
    public static PyValue Id(long? id) => id is > 0 ? new PyInt(id.Value) : None;
    public static PyValue Time(long ms) => new PyLong(Py.FileTimeFromUnixMs(ms));
    private static PyValue Setting(Corporation c,string key,PyValue? fallback = null) =>
        c.Settings.TryGetProperty(key,out var v) ? v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => new PyStr(v.GetString()!),
            System.Text.Json.JsonValueKind.Number => Py.Number(v.GetDouble()),
            System.Text.Json.JsonValueKind.True => new PyInt(1),
            System.Text.Json.JsonValueKind.False => new PyInt(0),
            _ => None,
        } : fallback ?? None;


    public static long? FactionId(Corporation? corporation)
    {
        return corporation is null ? null : PositiveSetting(corporation, "factionID");
    }

    public static IReadOnlyList<long> AllowedEnlistmentFactions(Corporation? corporation)
    {
        if (corporation is null ||
            !corporation.Settings.TryGetProperty("fw", out var warfare) ||
            warfare.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !warfare.TryGetProperty("allowedEnlistmentFactions", out var values) ||
            values.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];

        return values.EnumerateArray()
            .Where(value => value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt64(out var factionId) && factionId > 0)
            .Select(value => value.GetInt64())
            .ToArray();
    }

    private static long? PositiveSetting(Corporation corporation, string key) =>
        corporation.Settings.TryGetProperty(key, out var value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.Number &&
        value.TryGetInt64(out var factionId) && factionId > 0
            ? factionId
            : null;

    public static (string Key,PyValue Value)[] Fields(Corporation c,int members) =>
    [
        ("corporationID",new PyInt(c.Id)),("corporationName",new PyStr(c.Name)),
        ("ticker",new PyStr(c.Ticker)),("tickerName",new PyStr(c.Ticker)),
        ("ceoID",Id(c.CeoId)),("creatorID",Id(c.CreatorId)),("allianceID",Id(c.AllianceId)),
        ("factionID",Setting(c,"factionID")),("warFactionID",Setting(c,"warFactionID",Setting(c,"factionID"))),
        ("membership",new PyInt(1)),("description",new PyStr(c.Description)),("url",new PyStr(c.Url)),
        ("stationID",Id(c.StationId)),("deleted",new PyInt(c.Deleted ? 1 : 0)),
        ("taxRate",Py.Number((double)c.TaxRate)),("loyaltyPointTaxRate",Py.Number((double)c.LoyaltyPointTaxRate)),
        ("friendlyFire",new PyInt(c.FriendlyFire ? 1 : 0)),("memberCount",new PyInt(members)),("memberLimit",new PyInt(c.MemberLimit)),
        ("shares",Setting(c,"shares",new PyInt(c.IsNpc ? 0 : 1000))),
        // Until structure/war eligibility is ported no corporation is eligible to declare war.
        ("allowWar",PyBool.False),("allowedMemberRaceIDs",Setting(c,"allowedMemberRaceIDs",new PyInt(0))),
        ("corporationType",Setting(c,"corporationType",new PyInt(0))),("minimumJoinStanding",Setting(c,"minimumJoinStanding",new PyInt(0))),
        ("sendCharTerminationMessage",PyBool.True),("createDate",Time(c.CreatedMs)),
        ("aggressionEnableAfter",None),("aggressionDisableAfter",None),
        ("applicationsEnabled",Setting(c,"applicationsEnabled",new PyInt(0))),
        .. Enumerable.Range(1,7).Select(i => ($"division{i}",Division(c,i))),
        .. Enumerable.Range(1,7).Select(i => ($"walletDivision{i}",Division(c,i+7))),
        .. new[]{"shape1","shape2","shape3","color1","color2","color3","typeface"}.Select(key => (key,Setting(c,key))),
        ("isRecruiting",new PyInt(c.Settings.TryGetProperty("applicationsEnabled",out var app) &&
            (app.ValueKind == System.Text.Json.JsonValueKind.False || app.ValueKind == System.Text.Json.JsonValueKind.Number && app.GetInt32() == 0) ? 0 : 1)),
    ];

    private static PyValue Division(Corporation c,int index) =>
        c.Settings.TryGetProperty("divisionNames",out var names) && names.TryGetProperty(index.ToString(),out var name)
            ? new PyStr(name.GetString() ?? "") : new PyStr(index <= 7 ? $"Division {index}" : $"Wallet Division {index-7}");

    public static PyValue Row(Corporation c,int members)
    {
        var fields = Fields(c,members);
        return new PyObject(new PyStr("util.Row"),Py.Dict(
            ("header",Py.Tuple([.. fields.Select(f => (PyValue)new PyStr(f.Key))])),
            ("line",Py.Tuple([.. fields.Select(f => f.Value)]))));
    }
    public static PyValue PublicInfo(Corporation c,int members)
    {
        string[] keys = ["corporationID","corporationName","ticker","tickerName","ceoID","creatorID","allianceID","warFactionID",
            "description","stationID","shares","deleted","url","taxRate","loyaltyPointTaxRate","friendlyFire","allowWar","memberCount",
            "applicationsEnabled","isRecruiting","shape1","shape2","shape3","color1","color2","color3","typeface"];
        var fields = Fields(c,members).ToDictionary(f => f.Key,f => f.Value);
        return Py.KeyVal([.. keys.Select(key => (key,fields[key]))]);
    }

    internal static Descriptor DescriptorOf(params (string Name,int Type)[] columns) => Descriptor.FromHeader(new PyObjectEx(ObjectExKind.Ex1,
        Py.Tuple(new PyToken("blue.DBRowDescriptor"),Py.Tuple(Py.Tuple([.. columns.Select(c => (PyValue)Py.Tuple(new PyStr(c.Name),new PyInt(c.Type)))]))),[],[]));

    private static readonly string[] ShareholderColumns = ["shareholderID","corporationID","shares"];
    private static readonly Descriptor ShareholderDescriptor = DescriptorOf(
        ("shareholderID",3),("corporationID",3),("shares",3));

    public static PyValue ShareholdersRowset(IEnumerable<(long ShareholderId,long CorporationId,long Shares)> holdings) =>
        Py.Rowset(ShareholderColumns,holdings.Select(holding => (PyValue)Py.List(
            new PyInt(holding.ShareholderId),new PyInt(holding.CorporationId),new PyInt(holding.Shares))));

    public static PyValue ShareholderCRowset(IEnumerable<(long ShareholderId,long CorporationId,long Shares)> holdings) =>
        new PyObject(new PyStr("carbon.common.script.sys.crowset.CRowset"),Py.Dict(
            ("header",ShareholderDescriptor.Header),("RowClass",new PyToken("util.Row")),
            ("lines",new PyList([.. holdings.Select(holding => (PyValue)new PyPackedRow(ShareholderDescriptor,
                [new PyInt(holding.ShareholderId),new PyInt(holding.CorporationId),new PyInt(holding.Shares)]))]))));
    private static readonly Descriptor MemberDescriptor = DescriptorOf(
        ("characterID",3),("corporationID",3),("divisionID",3),("squadronID",3),("title",130),("roles",64),("grantableRoles",64),
        ("startDateTime",64),("baseID",3),("rolesAtHQ",64),("grantableRolesAtHQ",64),("rolesAtBase",64),("grantableRolesAtBase",64),
        ("rolesAtOther",64),("grantableRolesAtOther",64),("titleMask",3),("accountKey",3),("rowDate",64),("blockRoles",11),("ownerName",130));
    private static readonly Descriptor TitleDescriptor = DescriptorOf(("titleID",3),("titleName",130),
        ("roles",20),("grantableRoles",20),("rolesAtHQ",20),("grantableRolesAtHQ",20),
        ("rolesAtBase",20),("grantableRolesAtBase",20),("rolesAtOther",20),("grantableRolesAtOther",20));
    private static PyValue[] Masks(RoleMasks m) => [.. new[]{m.Roles,m.GrantableRoles,m.AtHq,m.GrantableHq,m.AtBase,m.GrantableBase,m.AtOther,m.GrantableOther}.Select(v => (PyValue)new PyLong(v))];
    public static PyValue Member(Member m,string name) => new PyPackedRow(MemberDescriptor,[
        new PyInt(m.CharacterId),new PyInt(m.CorporationId),new PyInt(m.DivisionId),new PyInt(m.SquadronId),new PyWString(m.Title),
        new PyLong(m.Masks.Roles),new PyLong(m.Masks.GrantableRoles),Time(m.StartedMs),Id(m.BaseId),
        new PyLong(m.Masks.AtHq),new PyLong(m.Masks.GrantableHq),new PyLong(m.Masks.AtBase),new PyLong(m.Masks.GrantableBase),
        new PyLong(m.Masks.AtOther),new PyLong(m.Masks.GrantableOther),new PyInt(m.TitleMask),new PyInt(m.AccountKey),Time(m.StartedMs),
        m.BlockRoles ? PyBool.True : PyBool.False,new PyWString(name)]);
    public static PyValue Titles(IReadOnlyList<CorporationTitle> titles) => new PyObjectEx(ObjectExKind.Ex2,
        Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset")),
            Py.Dict(("header",TitleDescriptor.Header),("idName",new PyStr("titleID")),("columnName",new PyStr("titleID")))),[],
        [.. titles.Select(t => new KeyValuePair<PyValue,PyValue>(new PyInt(t.Id),
            new PyPackedRow(TitleDescriptor,[new PyInt(t.Id),new PyWString(t.Name),.. Masks(t.Masks)])))]);
}


[Eve.Services.Service("voteManager")]
public sealed partial class VoteManagerService(Eve.Corporations.CorporationStore store)
{
    private const int VoteCaseStatusAll = 0;
    private const int VoteCaseStatusClosed = 1;
    private const int VoteCaseStatusOpen = 2;
    private const int VoteGeneral = 4;

    [Eve.Services.Method("GetVoteCasesByCorporation")]

    public async ValueTask<PyValue> GetVoteCasesByCorporation(Eve.Services.ServiceCall call)
    {
        var corporationId = Number(call.Arg(0));
        var actorId = call.Session.CharacterId ?? 0;
        var requestedStatus = call.Args.Count > 1 && call.Arg(1) is not PyNone
            ? Number(call.Arg(1))
            : VoteCaseStatusAll;
        IReadOnlyList<Eve.Corporations.CorporationStore.CorporationVoteCase> cases = corporationId > 0
            ? await store.VoteCasesAsync(corporationId,actorId,call.Cancellation)
            : Array.Empty<Eve.Corporations.CorporationStore.CorporationVoteCase>();
        var now = store.VoteNowFiletime;
        var open = cases.Where(vote => vote.EndDateTime > now).ToArray();
        var closed = cases.Where(vote => vote.EndDateTime <= now).Take(20).ToArray();
        if (requestedStatus == VoteCaseStatusOpen)
            return new PyDict([new(new PyInt(VoteCaseStatusOpen),VoteCasesRowset(open))]);
        if (requestedStatus == VoteCaseStatusClosed)
            return new PyDict([new(new PyInt(VoteCaseStatusClosed),VoteCasesRowset(closed))]);
        return new PyDict([
            new(new PyInt(VoteCaseStatusOpen),VoteCasesRowset(open)),
            new(new PyInt(VoteCaseStatusClosed),VoteCasesRowset(closed)),
        ]);
    }

    [Eve.Services.Method("GetVoteCase")]
    public async ValueTask<PyValue> GetVoteCase(Eve.Services.ServiceCall call)
    {
        var corporationId = Number(call.Arg(0));
        var voteCaseId = Number(call.Arg(1));
        var vote = await store.VoteCaseAsync(corporationId,voteCaseId,call.Session.CharacterId ?? 0,call.Cancellation);
        return vote is null ? PyNone.Instance : VoteCaseRow(vote);
    }

    [Eve.Services.Method("GetVoteCaseOptions")]

    public async ValueTask<PyValue> GetVoteCaseOptions(Eve.Services.ServiceCall call)
    {
        var corporationId = Number(call.Arg(0));
        var voteCaseId = Number(call.Arg(1));
        var vote = await store.VoteCaseAsync(corporationId,voteCaseId,call.Session.CharacterId ?? 0,call.Cancellation);
        if (vote is null) return VoteOptionsRowset([]);
        var options = Eve.Marshal.MarshalCodec.DecodeExact(vote.OptionsMarshal);
        var rows = new List<PyValue>();
        if (vote.VoteType == VoteGeneral && options is PyList list)
        {
            for (var index = 0; index < list.Items.Count; index++)
                rows.Add(OptionRow(vote,index,list.Items[index],null,null,null));
        }
        else if (options is PyTuple tuple && tuple.Items.Count == 5)
        {
            rows.Add(OptionRow(vote,0,tuple.Items[0],vote.Parameter,vote.Parameter1,vote.Parameter2));
            rows.Add(OptionRow(vote,1,tuple.Items[1],0L,vote.Parameter1,vote.Parameter2));
        }
        return VoteOptionsRowset(rows);
    }

    [Eve.Services.Method("GetVotes")]

    public async ValueTask<PyValue> GetVotes(Eve.Services.ServiceCall call)
    {
        var actorId = call.Session.CharacterId ?? 0;
        if (actorId <= 0) throw new Eve.Corporations.CorporationError("CORPORATION_ACCESS_DENIED");

        var voteCaseId = Number(call.Arg(0));
        var ballot = await store.VoteBallotAsync(voteCaseId,actorId,call.Cancellation);
        IEnumerable<PyValue> rows = ballot is null
            ? []
            : [(PyValue)Py.List(new PyInt(ballot.OptionId))];
        return Eve.Services.Py.Rowset(["optionID"],rows);
    }

    [Eve.Services.Method("CanViewVotes")]

    public async ValueTask<PyValue> CanViewVotes(Eve.Services.ServiceCall call)
    {
        var corporationId = Number(call.Arg(0));
        return await store.CanViewVotesAsync(corporationId,call.Session.CharacterId ?? 0,call.Cancellation)
            ? PyBool.True : PyBool.False;
    }

    [Eve.Services.Method("CanRunForCEO")]

    public async ValueTask<PyValue> CanRunForCEO(Eve.Services.ServiceCall call) =>
        await store.CanRunForCEOAsync(CurrentCorporation(call.Session),call.Session.CharacterId ?? 0,call.Cancellation)
            ? PyBool.True : PyBool.False;

    [Eve.Services.Method("InsertVoteCase")]
    public async ValueTask<PyValue> InsertVoteCase(Eve.Services.ServiceCall call)
    {
        var actorId = call.Session.CharacterId ?? 0;
        var corporationId = CurrentCorporation(call.Session);
        if (actorId <= 0 || corporationId <= 0) throw new Eve.Corporations.CorporationError("CORPORATION_ACCESS_DENIED");
        var voteCaseText = Text(call.Arg(0));
        var description = Text(call.Arg(1));
        var voteType = checked((int)Number(call.Arg(2)));
        var options = call.Arg(3);
        var (optionCount,parameter,parameter1,parameter2) = VoteOptions(voteType,options);
        var voteCaseId = await store.InsertVoteCaseAsync(corporationId,actorId,voteCaseText,description,voteType,
            Eve.Marshal.MarshalCodec.Encode(options),optionCount,parameter,parameter1,parameter2,
            OptionalNumber(call.Arg(4)) ?? 1L,call.Cancellation);
        return new PyInt(voteCaseId);
    }

    [Eve.Services.Method("InsertVote")]
    public async ValueTask<PyValue> InsertVote(Eve.Services.ServiceCall call)
    {
        var corporationId = Number(call.Arg(0));
        var voteCaseId = Number(call.Arg(1));
        var actorId = call.Session.CharacterId ?? 0;
        if (corporationId <= 0 || voteCaseId <= 0 || actorId <= 0)
            throw new Eve.Corporations.CorporationError("CORPORATION_ACCESS_DENIED");
        await store.InsertVoteAsync(corporationId,voteCaseId,actorId,
            Number(call.Arg(2)),call.Cancellation);
        return PyNone.Instance;
    }

    [Eve.Services.Method("GetCorpSanctionedActions")]
    public async ValueTask<PyValue> GetCorpSanctionedActions(Eve.Services.ServiceCall call)
    {
        var corporationId = CurrentCorporation(call.Session);
        IReadOnlyList<Eve.Corporations.CorporationStore.CorporationVoteSanction> actions = corporationId > 0
            ? await store.VoteSanctionedActionsAsync(corporationId,call.Session.CharacterId ?? 0,call.Cancellation)
            : Array.Empty<Eve.Corporations.CorporationStore.CorporationVoteSanction>();
        return new PyDict([.. actions.Select(action => new KeyValuePair<PyValue,PyValue>(
            new PyInt(action.VoteCaseId),
            Eve.Services.Py.KeyVal(
                ("voteCaseID",new PyInt(action.VoteCaseId)),
                ("corporationID",new PyInt(action.CorporationId)),
                ("voteType",new PyInt(action.VoteType)),
                ("parameter",Optional(action.Parameter)),
                ("parameter1",Optional(action.Parameter1)),
                ("parameter2",Optional(action.Parameter2)),
                ("inEffect",new PyInt(action.InEffect ? 1 : 0)),
                ("expires",new PyLong(action.Expires)))) )]);
    }

    [Eve.Services.Method("ActivateSanctionedAction")]
    public async ValueTask<PyValue> ActivateSanctionedAction(Eve.Services.ServiceCall call)
    {
        await store.ActivateSanctionedVoteAsync(CurrentCorporation(call.Session),call.Session.CharacterId ?? 0,
            Number(call.Arg(0)),call.Cancellation);
        return PyNone.Instance;
    }

    private static PyValue VoteCasesRowset(IReadOnlyList<Eve.Corporations.CorporationStore.CorporationVoteCase> cases) =>
        Eve.Services.Py.Rowset(VoteCaseHeader,cases.Select(vote => (PyValue)Py.List(
            new PyInt(vote.VoteCaseId),new PyInt(vote.CorporationId),new PyWString(vote.VoteCaseText),
            new PyWString(vote.Description),new PyInt(vote.VoteType),new PyLong(vote.StartDateTime),
            new PyLong(vote.EndDateTime))));

    private static PyValue VoteCaseRow(Eve.Corporations.CorporationStore.CorporationVoteCase vote) =>
        new PyObject(new PyStr("util.Row"),Py.Dict(
            ("header",Py.Tuple([.. VoteCaseHeader.Select(name => (PyValue)new PyStr(name))])),
            ("line",Py.Tuple(
                new PyInt(vote.VoteCaseId),new PyInt(vote.CorporationId),new PyWString(vote.VoteCaseText),
                new PyWString(vote.Description),new PyInt(vote.VoteType),new PyLong(vote.StartDateTime),
                new PyLong(vote.EndDateTime)))));

    private static readonly string[] VoteCaseHeader =
        ["voteCaseID","corporationID","voteCaseText","description","voteType","startDateTime","endDateTime"];

    private static PyValue VoteOptionsRowset(IEnumerable<PyValue> rows) =>
        Eve.Services.Py.Rowset(["voteCaseID","optionID","optionText","parameter","parameter1","parameter2"],rows);

    private static PyValue OptionRow(Eve.Corporations.CorporationStore.CorporationVoteCase vote,long optionId,
        PyValue text,long? parameter,long? parameter1,long? parameter2) =>
        Py.List(new PyInt(vote.VoteCaseId),new PyInt(optionId),text,Optional(parameter),Optional(parameter1),Optional(parameter2));

    private static (int Count,long? Parameter,long? Parameter1,long? Parameter2) VoteOptions(int voteType,PyValue options)
    {
        if (voteType == VoteGeneral)
        {
            if (options is not PyList list || list.Items.Count is < 2 or > 50 ||
                list.Items.Any(option => option is not PyStr and not PyWString))
                throw new Eve.Corporations.CorporationError("CORPORATION_INVALID_VOTE_OPTIONS");
            return (list.Items.Count,null,null,null);
        }
        if (voteType is < 0 or > 6 || options is not PyTuple tuple || tuple.Items.Count != 5)
            throw new Eve.Corporations.CorporationError("CORPORATION_INVALID_VOTE_OPTIONS");
        return (2,OptionalNumber(tuple.Items[2]),OptionalNumber(tuple.Items[3]),OptionalNumber(tuple.Items[4]));
    }

    private static long? OptionalNumber(PyValue value) => value is PyNone ? null : Number(value);
    private static PyValue Optional(long? value) => value is { } number ? new PyInt(number) : PyNone.Instance;
    private static long Number(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyBool boolean => boolean.Value ? 1 : 0,
        PyReal real when double.IsFinite(real.Value) && Math.Truncate(real.Value) == real.Value &&
                         real.Value >= long.MinValue && real.Value <= long.MaxValue => (long)real.Value,
        _ => throw new Eve.Corporations.CorporationError("CORPORATION_INVALID_VOTE_ARGUMENT"),
    };
    private static string Text(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        _ => throw new Eve.Corporations.CorporationError("CORPORATION_INVALID_VOTE_ARGUMENT"),
    };
    private static long CurrentCorporation(Eve.Services.ClientSession session) =>
        session.GetInt("corporationID") ?? session.GetInt("corpid") ?? 0;
}
