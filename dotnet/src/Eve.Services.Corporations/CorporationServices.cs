using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Corporations;

[Service("corpmgr")]
public sealed partial class CorpMgrService(CorporationStore store,PgCharacterStore characters)
{
    private async Task<Corporation?> Resolve(ServiceCall call) =>
        await store.GetAsync(call.Arg(0) is PyInt id && id.Value > 0 ? id.Value :
            call.Session.CharacterId is { } character ? (await characters.GetAsync(character,call.Cancellation))?.CorporationId ?? 0 : 0,call.Cancellation);
    [Method("GetPublicInfo")]
    public async ValueTask<PyValue> GetPublicInfo(ServiceCall call) => await Resolve(call) is { } c
        ? CorporationWire.PublicInfo(c,(await store.MembersAsync(c.Id,call.Cancellation)).Count) : PyNone.Instance;
    [Method("GetCorporations")]
    public async ValueTask<PyValue> GetCorporations(ServiceCall call) => await Resolve(call) is { } c
        ? CorporationWire.Row(c,(await store.MembersAsync(c.Id,call.Cancellation)).Count) : PyNone.Instance;
    [Method("GetCorporationIDForCharacter")]
    public async ValueTask<PyValue> GetCorporationIDForCharacter(ServiceCall call) =>
        call.Arg(0) is PyInt id && await characters.GetAsync(id.Value,call.Cancellation) is { } c ? new PyInt(c.CorporationId) : PyNone.Instance;
}

[Service("corporationSvc")]
public sealed partial class CorporationService(CorporationStore store,PgCharacterStore characters)
{
    [Method("GetEmploymentRecord")]
    public async ValueTask<PyValue> GetEmploymentRecord(ServiceCall call)
    {
        var id = call.Arg(0) is PyInt n && n.Value > 0 ? n.Value : call.Session.CharacterId ?? 0;
        var history = await store.EmploymentAsync(id,call.Cancellation);
        if (history.Count == 0 && await characters.GetAsync(id,call.Cancellation) is { } c)
            history = [new Employment(0,id,c.CorporationId,c.CreatedAt.ToUnixTimeMilliseconds(),null,false)];
        return Py.Rowset(["corporationID","startDate","deleted"],
            history.Select(e => (PyValue)Py.List(new PyInt(e.CorporationId),CorporationWire.Time(e.StartedMs),new PyInt(e.Deleted ? 1 : 0))));
    }

    [Method("GetMyCorporationInfo")]
    public async ValueTask<PyValue> GetMyCorporationInfo(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } id || await characters.GetAsync(id,call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId,call.Cancellation) is not { } c) return PyNone.Instance;
        var fields = CorporationWire.Fields(c,(await store.MembersAsync(c.Id,call.Cancellation)).Count).ToDictionary(f => f.Key,f => f.Value);
        string[] keys = ["corporationID","corporationName","ticker","tickerName","allianceID","factionID","ceoID","creatorID",
            "membership","shares","deleted","stationID","memberCount","taxRate","loyaltyPointTaxRate","friendlyFire","isRecruiting",
            "allowWar","shape1","shape2","shape3","color1","color2","color3","typeface","description","url"];
        var row = Py.Tuple(fields["corporationID"],fields["corporationName"],fields["ticker"],fields["ceoID"],new PyInt(1));
        return Py.KeyVal([.. keys.Select(key => (key,fields[key])),
            ("header",Py.Tuple(new PyStr("corporationID"),new PyStr("corporationName"),new PyStr("ticker"),new PyStr("ceoID"),new PyStr("membership"))),
            ("row",row),("line",row)]);
    }
    [Method("GetEmployementRecordAndCharacterTransfers")]
    public async ValueTask<PyValue> GetEmployementRecordAndCharacterTransfers(ServiceCall call) =>
        Py.Tuple(await GetEmploymentRecord(call),new PyList([]));
}

[Service("corpRegistry")]
public sealed partial class CorpRegistryService(CorporationStore store,PgCharacterStore characters,Func<long> now)
{
    private async Task<Corporation?> Current(ServiceCall call) =>
        call.Session.CharacterId is { } id && await characters.GetAsync(id,call.Cancellation) is { } c
            ? await store.GetAsync(c.CorporationId,call.Cancellation) : null;
    [Method("GetCorporation")]
    public async ValueTask<PyValue> GetCorporation(ServiceCall call) => await Current(call) is { } c
        ? CorporationWire.Row(c,(await store.MembersAsync(c.Id,call.Cancellation)).Count) : PyNone.Instance;

    private static long Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyTuple { Items.Count: > 0 } tuple => Number(tuple.Items[0]),
        _ => 0
    };

    private static MachoWrappedException CorporationAccessDenied() =>
        MachoErrors.ThrowUserError("CrpAccessDenied",
            ("reason",Py.List(new PyInt(101),new PyStr("UI/Corporations/AccessRestrictions/InsufficientRoles"))));

    [Method("ApplyToJoinAlliance")]
    public async ValueTask<PyValue> ApplyToJoinAlliance(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId,call.Cancellation) is not { IsNpc:false } corporation ||
            corporation.CeoId != actor)
            throw CorporationAccessDenied();

        var alliance = Number(call.Arg(0));
        var applicationText = JsSemantics.StringValue(call.Arg(1)) ?? "";
        try
        {
            await store.ApplyToAllianceAsync(actor,alliance,applicationText,true,true,call.Cancellation);
        }
        catch (CorporationError error) when (error.Code is "CORPORATION_ACCESS_DENIED" or "CHARACTER_NOT_FOUND" or "ALLIANCE_NOT_FOUND" or "CORPORATION_ALREADY_IN_ALLIANCE" or "ALLIANCE_APPLICATION_TEXT_TOO_LONG")
        {
            throw CorporationAccessDenied();
        }
        catch (CorporationError error) when (error.Code == "ALLIANCE_APPLICATION_ALREADY_EXISTS")
        {
            throw MachoErrors.ThrowUserError("CantApplyTwiceToAlliance");
        }
        return PyNone.Instance;
    }

    [Method("DeleteAllianceApplication")]
    public async ValueTask<PyValue> DeleteAllianceApplication(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is null)
            throw CorporationAccessDenied();

        try
        {
            await store.WithdrawAllianceApplicationAsync(actor,Number(call.Arg(0)),call.Cancellation);
        }
        catch (CorporationError error) when (error.Code is "CORPORATION_ACCESS_DENIED" or "CORPORATION_NOT_FOUND" or "CHARACTER_NOT_FOUND" or "ALLIANCE_APPLICATION_INVALID_STATE")
        {
            throw CorporationAccessDenied();
        }
        return PyNone.Instance;
    }
    [Method("GetMember")]
    public async ValueTask<PyValue> GetMember(ServiceCall call) => call.Arg(0) is PyInt id &&
        await Current(call) is { } c && await store.MemberAsync(id.Value,call.Cancellation) is { } m && m.CorporationId == c.Id
            ? CorporationWire.Member(m,(await characters.GetAsync(m.CharacterId,call.Cancellation))!.Name) : PyNone.Instance;
    [Method("GetMembersByIds")]
    public async ValueTask<PyValue> GetMembersByIds(ServiceCall call)
    {
        var rows = new List<PyValue>();
        foreach (var id in Items(call.Arg(0)))
        {
            var row = await GetMember(new ServiceCall { Method="GetMember",Args=[id],Session=call.Session,Cancellation=call.Cancellation });
            if (row is not PyNone) rows.Add(row);
        }
        return new PyList(rows);
    }

    [Method("GetMembersPaged")]
    public async ValueTask<PyValue> GetMembersPaged(ServiceCall call)
    {
        var members = await Current(call) is { } c ? await store.MembersAsync(c.Id,call.Cancellation) : [];
        var page = call.Arg(0) is PyInt n ? Math.Max(1,n.Value) : 1;
        const int perPage = 50;
        var rows = new List<PyValue>();
        foreach (var m in members.Skip((int)Math.Min(members.Count,(page-1)*perPage)).Take(perPage))
            rows.Add(CorporationWire.Member(m,(await characters.GetAsync(m.CharacterId,call.Cancellation))!.Name));
        return new PyObjectEx(ObjectExKind.Ex1,Py.Tuple(new PyToken("eve.common.script.util.pagedCollection.PagedResultSet"),
            Py.Tuple(new PyList(rows),new PyInt(members.Count),new PyInt(page-1),new PyInt(perPage))),[],[]);
    }
    [Method("GetTitles")]
    public async ValueTask<PyValue> GetTitles(ServiceCall call) =>
        CorporationWire.Titles(await Current(call) is { } c ? await store.TitlesAsync(c.Id,call.Cancellation) : []);
    [Method("GetLocationalRoles")]
    public PyValue GetLocationalRoles(ServiceCall call) => new PyList([.. Enumerable.Range(0,63)
        .Select(bit => 1L << bit).Where(role => (CorporationRoles.Locational & role) != 0).Select(role => (PyValue)new PyLong(role))]);
    [Method("GetMyGrantableRoles")]
    public async ValueTask<PyValue> GetMyGrantableRoles(ServiceCall call)
    {
        var masks = await Current(call) is { } c && call.Session.CharacterId is { } id && await store.MemberAsync(id,call.Cancellation) is { } m
            ? CorporationRoles.GrantableFor(c,m,c.Id,await store.TitlesAsync(c.Id,call.Cancellation)) : new RoleMasks();
        return Py.Tuple(new PyLong(masks.GrantableRoles),new PyLong(masks.GrantableHq),new PyLong(masks.GrantableBase),new PyLong(masks.GrantableOther));
    }

    private static readonly Descriptor ApplicationDescriptor = CorporationWire.DescriptorOf(
        ("applicationID",3),("corporationID",3),("characterID",3),("applicationText",130),
        ("status",3),("applicationDateTime",64),("deleted",11),("responseText",130));
    private static PyValue ApplicationRow(Application application) => new PyPackedRow(ApplicationDescriptor,
    [
        new PyInt(application.Id),new PyInt(application.CorporationId),new PyInt(application.CharacterId),
        new PyWString(application.Text),new PyInt(application.Status),CorporationWire.Time(application.CreatedMs),
        PyBool.False,application.Status == 0 ? PyNone.Instance : new PyWString(application.Response),
    ]);
    private static PyValue GroupApplications(IReadOnlyList<Application> applications,Func<Application,long> key) =>
        new PyDict([.. applications.GroupBy(key).Select(group => new KeyValuePair<PyValue,PyValue>(
            new PyInt(group.Key),new PyList([.. group.Select(ApplicationRow)])))]);
    private static long? PositiveId(PyValue value) => value switch
    {
        PyInt id when id.Value > 0 => id.Value,
        PyLong id when id.Value > 0 => id.Value,
        _ => null,
    };
    private static string Text(PyValue value) => JsSemantics.StringValue(value) ?? "";
    private static int Status(PyValue value) => value switch
    {
        PyInt n when n.Value is >= int.MinValue and <= int.MaxValue => (int)n.Value,
        PyLong n when n.Value is >= int.MinValue and <= int.MaxValue => (int)n.Value,
        _ => 0,
    };
    private async Task<Corporation> RecruitmentCorporation(ServiceCall call)
    {
        var corporation = await Current(call);
        if (corporation is null || corporation.Deleted || corporation.IsNpc ||
            call.Session.CharacterId is not { } actor)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var member = await store.MemberAsync(actor,call.Cancellation);
        if (member is null || member.CorporationId != corporation.Id ||
            (CorporationRoles.Session(corporation,member,null,
                await store.TitlesAsync(corporation.Id,call.Cancellation)).AtAll & CorporationRoles.PersonnelManager) == 0)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return corporation;
    }

    [Method("GetMyApplications")]
    public async ValueTask<PyValue> GetMyApplications(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character) return new PyDict([]);
        return GroupApplications(await store.ApplicationsAsync(character:character,token:call.Cancellation),a => a.CorporationId);
    }
    [Method("GetMyOldApplications")]
    public async ValueTask<PyValue> GetMyOldApplications(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character) return new PyList([]);
        return new PyList([.. (await store.ApplicationsAsync(character:character,archived:true,token:call.Cancellation)).Select(ApplicationRow)]);
    }
    [Method("GetApplications")]
    public async ValueTask<PyValue> GetApplications(ServiceCall call)
    {
        var corporation = await RecruitmentCorporation(call);
        return GroupApplications(await store.ApplicationsAsync(corporation:corporation.Id,token:call.Cancellation),a => a.CharacterId);
    }
    [Method("GetOldApplications")]
    public async ValueTask<PyValue> GetOldApplications(ServiceCall call)
    {
        var corporation = await RecruitmentCorporation(call);
        return new PyList([.. (await store.ApplicationsAsync(corporation:corporation.Id,archived:true,token:call.Cancellation)).Select(ApplicationRow)]);
    }
    [Method("GetMyOpenInvitations")]
    public async ValueTask<PyValue> GetMyOpenInvitations(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character) return new PyList([]);
        var applications = await store.ApplicationsAsync(character:character,token:call.Cancellation);
        return new PyList([.. applications.Where(a => a.Status is 6 or 8).Select(a => (PyValue)Py.KeyVal(
            ("invitationID",new PyInt(a.Id)), // EVE.js exposes applicationID in this field.
            ("corporationID",new PyInt(a.CorporationId)),
            ("characterID",new PyInt(a.CharacterId)),("status",new PyInt(a.Status)),
            ("invitationDateTime",CorporationWire.Time(a.CreatedMs))))]);
    }
    [Method("InsertApplication")]
    public async ValueTask<PyValue> InsertApplication(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character || PositiveId(call.Arg(0)) is not { } corporation)
            return PyNone.Instance;
        var id = await store.ApplyAsync(character,corporation,Text(call.Arg(1)),token:call.Cancellation);
        return new PyInt(id);
    }
    [Method("InsertInvitation")]
    public async ValueTask<PyValue> InsertInvitation(ServiceCall call)
    {
        var corporation = await Current(call);
        if (corporation is null || call.Session.CharacterId is not { } inviter)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (PositiveId(call.Arg(0)) is not { } character)
            throw new CorporationError("CHARACTER_NOT_FOUND");
        var invitation = await store.CreateInvitationAsync(character,corporation.Id,inviter,call.Cancellation);
        return new PyInt(invitation.InvitationId);
    }
    [Method("UpdateApplicationOffer")]
    public async ValueTask<PyValue> UpdateApplicationOffer(ServiceCall call)
    {
        if (PositiveId(call.Arg(0)) is not { } applicationId || PositiveId(call.Arg(2)) is not { } corporation)
            return PyNone.Instance;
        // ponytail: response lookup scans this corporation's open applications; add an ID lookup if that queue grows.
        var application = (await store.ApplicationsAsync(corporation:corporation,token:call.Cancellation))
            .FirstOrDefault(a => a.Id == applicationId);
        if (application is null) return PyNone.Instance;
        if (PositiveId(call.Arg(1)) is { } character && character != application.CharacterId ||
            call.Session.CharacterId is not { } actor)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        await store.RespondAsync(actor,applicationId,Status(call.Arg(4)),Text(call.Arg(3)),Text(call.Arg(5)),call.Cancellation);
        if (Status(call.Arg(4)) == 2)
            await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
        return PyNone.Instance;
    }
    private static IReadOnlyList<PyValue> Items(PyValue v) => v switch { PyTuple t => t.Items,PyList l => l.Items,_ => [] };
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var oid = Py.Tuple(new PyStr($"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}"),CorporationWire.Time(now()));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count:>0 } nested)
            result = await ((IService)this).CallAsync(new ServiceCall { Method=JsSemantics.StringValue(nested.Items[0]) ?? "",Args=nested.Items.Count>1 ? Items(nested.Items[1]) : [],
                Kwargs=nested.Items.Count>2 ? nested.Items[2] : PyNone.Instance,Session=call.Session,CallId=call.CallId,Cancellation=call.Cancellation });
        return Py.Tuple(new PySubStruct(new PySubStream(oid)),result);
    }
}
