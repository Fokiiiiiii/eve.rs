using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Corporations;

[Service("corpRegistry")]
public sealed partial class CorpRegistryService(CorporationStore store,PgCharacterStore characters,Func<long> now,CharacterSessions? characterSessions = null)
{
    public AllianceAffiliationMutationCallback? AllianceAffiliationWriter
    {
        get => store.AllianceAffiliationWriter;
        set => store.AllianceAffiliationWriter = value;
    }

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
        var actor = call.Session.CharacterId ?? 0;
        if (actor <= 0 ||
            await characters.GetAsync(actor,call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId,call.Cancellation) is not { IsNpc:false } corporation ||
            corporation.CeoId != actor || corporation.CeoRolesSuspended)
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

    [Method("MoveCompanyShares")]
    public ValueTask<PyValue> MoveCompanyShares(ServiceCall call) => MoveShareholding(call,true);

    [Method("MovePrivateShares")]
    public ValueTask<PyValue> MovePrivateShares(ServiceCall call) => MoveShareholding(call,false);

    private async ValueTask<PyValue> MoveShareholding(ServiceCall call,bool companyShares)
    {
        var corporationId = Number(call.Arg(0));
        var toShareholderId = Number(call.Arg(1));
        var numberOfShares = Number(call.Arg(2));
        var actorId = call.Session.CharacterId ?? 0;
        if (corporationId <= 0 || toShareholderId <= 0 || actorId <= 0)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (numberOfShares <= 0)
            throw new CorporationError("CORPORATION_INVALID_SHARE_TRANSFER");

        var callId = Number(call.CallId);
        var operationKey = callId > 0
            ? $"corporation-share:{corporationId}:{actorId}:{callId}"
            : Guid.NewGuid().ToString("N");
        if (companyShares)
            await store.MoveCompanySharesAsync(corporationId,actorId,toShareholderId,numberOfShares,operationKey,call.Cancellation);
        else
            await store.MovePrivateSharesAsync(corporationId,actorId,toShareholderId,numberOfShares,operationKey,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetShareholders")]
    public async ValueTask<PyValue> GetShareholders(ServiceCall call)
    {
        var corporationId = Number(call.Arg(0));
        if (corporationId <= 0 ||
            await Current(call) is not { IsNpc:false,Deleted:false } corporation ||
            corporation.Id != corporationId ||
            !await CanAdminister(call))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var holdings = await store.ShareholdersAsync(corporationId,call.Cancellation);
        return CorporationWire.ShareholdersRowset(holdings.Select(holding =>
            (holding.ShareholderId,holding.CorporationId,holding.Shares)));
    }

    [Method("GetSharesByShareholder")]
    public async ValueTask<PyValue> GetSharesByShareholder(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await characters.GetAsync(actor,call.Cancellation) is null)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var ownerId = actor;
        long? corporationFilter = null;
        if (call.Args.Count > 0 && Number(call.Arg(0)) == 1)
        {
            if (await Current(call) is not { IsNpc:false,Deleted:false } corporation ||
                !await CanAdminister(call))
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            ownerId = corporation.Id;
            corporationFilter = corporation.Id;
        }

        var holdings = await store.SharesByShareholderAsync(ownerId,corporationFilter,call.Cancellation);
        return CorporationWire.ShareholderCRowset(holdings.Select(holding =>
            (holding.ShareholderId,holding.CorporationId,holding.Shares)));
    }

    [Method("GetEveOwners")]
    public async ValueTask<PyValue> GetEveOwners(ServiceCall call)
    {
        var corporation = await Current(call);
        if (corporation is null)
            return Py.List([]);

        var rows = new List<PyValue>();
        foreach (var member in await store.MembersAsync(corporation.Id,call.Cancellation))
        {
            var character = await characters.GetAsync(member.CharacterId,call.Cancellation);
            var ownerName = string.IsNullOrEmpty(character?.Name) ? $"Character {member.CharacterId}" : character.Name;
            rows.Add(new PyObject(new PyStr("util.Row"),Py.Dict(
                ("header",Py.Tuple([
                    new PyStr("ownerID"),new PyStr("ownerName"),new PyStr("typeID"),
                    new PyStr("gender"),new PyStr("ownerNameID")
                ])),
                ("line",Py.Tuple([
                    new PyInt(member.CharacterId),new PyWString(ownerName),
                    new PyInt(character is { TypeId: > 0 } ? character.TypeId : 1373),
                    new PyInt(character?.Gender ?? 0),PyNone.Instance
                ])))));
        }
        return Py.List([.. rows]);
    }

    [Method("ExecuteActions")]
    public async ValueTask<PyValue> ExecuteActions(ServiceCall call)
    {
        const long ctvAdd = 1;
        const long ctvRemove = 2;
        const long ctvSet = 3;
        const long ctvGive = 4;

        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var targetValues = Items(call.Arg(0));
        var targetIDs = new long[targetValues.Count];
        var distinctTargets = new HashSet<long>();
        for (var index = 0; index < targetValues.Count; index++)
        {
            var target = Number(targetValues[index]);
            if (target <= 0 || !distinctTargets.Add(target))
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            targetIDs[index] = target;
        }

        var actions = new List<(long Verb,PyValue Property,PyValue Value)>();
        foreach (var rawAction in Items(call.Arg(1)))
        {
            var values = Items(rawAction);
            if (values.Count < 3)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            actions.Add((Number(values[0]),values[1],values[2]));
        }

        var hasGive = actions.Any(action => action.Verb == ctvGive);
        if (hasGive && actions.Any(action => action.Verb != ctvGive))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var members = await store.MembersAsync(corp.Id,call.Cancellation);
        var membersById = members.ToDictionary(member => member.CharacterId);
        if (targetIDs.Any(target => !membersById.ContainsKey(target)))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (actions.Count == 0)
            return PyNone.Instance;

        if (hasGive)
        {
            var grants = new List<Eve.Corporations.CorporationMemberGrant>(actions.Count);
            foreach (var action in actions)
            {
                var property = Number(action.Property);
                if (action.Verb != ctvGive || property != 6 ||
                    action.Value is not PyInt and not PyLong and not PyReal)
                    throw new CorporationError("CORPORATION_ACCESS_DENIED");
                if (action.Value is PyReal real && !double.IsFinite(real.Value))
                    throw new CorporationError("CORPORATION_ACCESS_DENIED");

                decimal amount;
                try { amount = DecimalValue(action.Value); }
                catch (OverflowException) { throw new CorporationError("CORPORATION_ACCESS_DENIED"); }
                if (amount <= 0m)
                    throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
                grants.Add(new Eve.Corporations.CorporationMemberGrant((int)property,amount));
            }

            if (grants.Count == 0)
                throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
            var walletAccountKey = CorporationWalletKey(new PyInt(
                call.Session.GetInt("corpAccountKey") ?? call.Session.GetInt("corpaccountkey") ?? 1000));
            var callId = Number(call.CallId);
            var operationKey = callId > 0
                ? $"corporation-share:{corp.Id}:{actor}:{callId}"
                : Guid.NewGuid().ToString("N");
            await store.ExecuteMemberGrantsAsync(corp.Id,actor,targetIDs,grants,walletAccountKey,operationKey,call.Cancellation);
            return PyNone.Instance;
        }

        if (targetIDs.Length == 0)
            return PyNone.Instance;

        var updates = new List<Member>();
        var beforeById = new Dictionary<long,Member>();
        foreach (var target in targetIDs)
        {
            var member = membersById[target];
            var row = new List<PyValue>
            {
                new PyInt(target),PyNone.Instance,PyNone.Instance,PyNone.Instance,
                new PyLong(member.Masks.Roles),new PyLong(member.Masks.GrantableRoles),
                new PyLong(member.Masks.AtHq),new PyLong(member.Masks.GrantableHq),
                new PyLong(member.Masks.AtBase),new PyLong(member.Masks.GrantableBase),
                new PyLong(member.Masks.AtOther),new PyLong(member.Masks.GrantableOther),
                CorporationWire.Id(member.BaseId),new PyInt(member.TitleMask),PyNone.Instance
            };

            foreach (var action in actions)
            {
                var (index,allowed) = Text(action.Property) switch
                {
                    "roles" => (4,CorporationRoles.Administrative),
                    "grantableRoles" => (5,CorporationRoles.Grantable),
                    "rolesAtHQ" => (6,CorporationRoles.Locational),
                    "grantableRolesAtHQ" => (7,CorporationRoles.Locational),
                    "rolesAtBase" => (8,CorporationRoles.Locational),
                    "grantableRolesAtBase" => (9,CorporationRoles.Locational),
                    "rolesAtOther" => (10,CorporationRoles.Locational),
                    "grantableRolesAtOther" => (11,CorporationRoles.Locational),
                    _ => (-1,0L)
                };
                if (index >= 0)
                {
                    var current = RoleMask(row[index],allowed);
                    var value = action.Value is PyNone ? 0 : RoleMask(action.Value,allowed);
                    row[index] = new PyLong(action.Verb switch
                    {
                        ctvAdd => current | value,
                        ctvRemove => current & ~value,
                        ctvSet => value,
                        _ => throw new CorporationError("CORPORATION_ACCESS_DENIED")
                    });
                }
                else if (Text(action.Property) == "titleMask")
                {
                    var current = RoleMask(row[13],65535);
                    var value = action.Value is PyNone ? 0 : RoleMask(action.Value,65535);
                    row[13] = new PyInt((int)(action.Verb switch
                    {
                        ctvAdd => current | value,
                        ctvRemove => current & ~value,
                        ctvSet => value,
                        _ => throw new CorporationError("CORPORATION_ACCESS_DENIED")
                    }));
                }
                else if (Text(action.Property) == "baseID" && action.Verb == ctvSet)
                    row[12] = new PyInt(PositiveId(action.Value) ?? 0);
                else
                    throw new CorporationError("CORPORATION_ACCESS_DENIED");
            }

            var (before,after) = await MemberUpdate(target,row,true,corp.Id,call.Cancellation);
            beforeById.Add(target,before);
            updates.Add(after);
        }

        var changed = await store.UpdateMembersAsync(actor,updates,call.Cancellation);
        foreach (var member in changed)
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,member.CharacterId,beforeById[member.CharacterId],member);
        if (updates.Any(member => member.CharacterId == actor))
            await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetNumberOfPotentialCEOs")]
    public async ValueTask<PyValue> GetNumberOfPotentialCEOs(ServiceCall call)
    {
        var corporation = await Current(call);
        if (corporation is null)
            return Py.List([]);
        var members = await store.MembersAsync(corporation.Id,call.Cancellation);
        return Py.List([.. members.Select(member => (PyValue)new PyInt(member.CharacterId))]);
    }
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


    [Method("GetCorpWelcomeMail")]
    public async ValueTask<PyValue> GetCorpWelcomeMail(ServiceCall call)
    {
        var corporation = await Current(call);
        if (corporation is null || corporation.Deleted || call.Session.CharacterId is not { } actor)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var member = await store.MemberAsync(actor,call.Cancellation);
        if (member is null || member.CorporationId != corporation.Id)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return new PyWString(await store.GetCorpWelcomeMailAsync(corporation.Id,call.Cancellation));
    }

    [Method("SetCorpWelcomeMail")]
    public async ValueTask<PyValue> SetCorpWelcomeMail(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corporation)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        await store.SetCorpWelcomeMailAsync(actor,corporation.Id,Text(call.Arg(0)),call.Cancellation);
        return PyNone.Instance;
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
        var recipients = new HashSet<long> { application.CharacterId, actor };
        if (await store.GetAsync(corporation,call.Cancellation) is { } corp) recipients.Add(corp.CeoId);
        foreach (var member in await store.MembersAsync(corporation,call.Cancellation))
            if ((member.Masks.Roles & (CorporationRoles.Director | CorporationRoles.PersonnelManager)) != 0)
                recipients.Add(member.CharacterId);
        var before = await store.MemberAsync(application.CharacterId,call.Cancellation);
        var status = Status(call.Arg(4));
        var updated = await store.RespondAsync(actor,applicationId,status,Text(call.Arg(3)),Text(call.Arg(5)),call.Cancellation);
        CorporationNotificationAdapter.ApplicationChanged(characterSessions,call,corporation,updated.CharacterId,
            updated.Id,ApplicationRow(updated),recipients);
        Member? after = status == 2
            ? new Member(application.CharacterId,corporation,now(),"",new RoleMasks(),null,0,false,1000)
            : before;
        if (before != after)
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corporation,application.CharacterId,before,after);
        if (status == 2)
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

    [Method("AddCorporation")]
    public async ValueTask<PyValue> AddCorporation(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } founder) return PyNone.Instance;
        var settings = new Dictionary<string,object?>
        {
            ["shape1"] = SettingValue(call.Arg(5)),["shape2"] = SettingValue(call.Arg(6)),["shape3"] = SettingValue(call.Arg(7)),
            ["color1"] = SettingValue(call.Arg(8)),["color2"] = SettingValue(call.Arg(9)),["color3"] = SettingValue(call.Arg(10)),
            ["typeface"] = SettingValue(call.Arg(11)),
        };
        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(settings));
        var created = await store.CreateAsync(founder,new NewCorporation(Text(call.Arg(0)),Text(call.Arg(1)),Text(call.Arg(2)),Text(call.Arg(3)),
            DecimalValue(call.Arg(4)),DecimalValue(call.Arg(14)),Truth(call.Arg(12)),Truth(call.Arg(13)),document.RootElement.Clone()),
            $"corp-found:{founder}:{call.Session.SessionId}:{Number(call.CallId)}",call.Cancellation);
        await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,founder,call.Cancellation);
        var members = await store.MembersAsync(created.Id,call.Cancellation);
        if (members.FirstOrDefault(m => m.CharacterId == founder) is { } member)
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,created.Id,founder,null,member);
        return CorporationWire.Row(created,members.Count);
    }

    [Method("UpdateCorporation")]
    public async ValueTask<PyValue> UpdateCorporation(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        await store.UpdateCorporationAsync(actor,corp.Id,Text(call.Arg(0)),Text(call.Arg(1)),DecimalValue(call.Arg(2)),
            DecimalValue(call.Arg(4)),Truth(call.Arg(3)),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateLogo")]
    public async ValueTask<PyValue> UpdateLogo(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var patch = new Dictionary<string,long?>
        {
            ["shape1"] = PositiveId(call.Arg(0)),["shape2"] = PositiveId(call.Arg(1)),["shape3"] = PositiveId(call.Arg(2)),
            ["color1"] = PositiveId(call.Arg(3)),["color2"] = PositiveId(call.Arg(4)),["color3"] = PositiveId(call.Arg(5)),
            ["typeface"] = PositiveId(call.Arg(6))
        };
        await store.UpdateSettingsAsync(actor,corp.Id,System.Text.Json.JsonSerializer.Serialize(patch),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateDivisionNames")]
    public async ValueTask<PyValue> UpdateDivisionNames(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var names = new Dictionary<string,string>();
        for (var i = 0; i < 14; i++)
            if (call.Arg(i) is not PyNone) names[(i+1).ToString(System.Globalization.CultureInfo.InvariantCulture)] = Text(call.Arg(i));
        await store.UpdateDivisionNamesAsync(actor,corp.Id,System.Text.Json.JsonSerializer.Serialize(names),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateCorporationAbilities")]
    public async ValueTask<PyValue> UpdateCorporationAbilities(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        await store.UpdateSettingsAsync(actor,corp.Id,
            System.Text.Json.JsonSerializer.Serialize(new { applicationsEnabled = NormalizeBoolean(call.Arg(0),true) }),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("SetAccountKey")]
    public async ValueTask<PyValue> SetAccountKey(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character) return PyBool.True;
        var key = CorporationWalletKey(call.Arg(0));
        var before = await store.MemberAsync(character,call.Cancellation);
        await store.SetAccountKeyAsync(character,key,call.Cancellation);
        if (before is not null)
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,before.CorporationId,character,before,before with { AccountKey=key });
        call.Session.Set("corpaccountkey",new PyInt(key));
        await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,character,call.Cancellation);
        return PyBool.True;
    }

    [Method("CanLeaveCurrentCorporation")]
    public async ValueTask<PyValue> CanLeaveCurrentCorporation(ServiceCall call)
    {
        var corp = await Current(call);
        var actor = call.Session.CharacterId;
        var member = actor is { } id ? await store.MemberAsync(id,call.Cancellation) : null;
        if (corp is null || actor is null || member is null || member.CorporationId != corp.Id)
            return Py.Tuple(new PyInt(0),new PyStr("CrpAccessDenied"),new PyDict([]));
        if (corp.CeoId == actor)
            return Py.Tuple(new PyInt(0),new PyStr("CrpCEOCanNotQuit"),new PyDict([]));
        var roles = RoleFields(member);
        var active = roles.Where(r => r.Value != 0).Select(r => (PyValue)new PyStr(r.Name)).ToList();
        if (member.TitleMask != 0) active.Add(new PyStr("titleMask"));
        if (active.Count != 0)
            return Py.Tuple(new PyInt(0),new PyStr("CrpCantQuitNotInStasis"),Py.Dict(("rolelist",new PyList(active))));
        return Py.Tuple(new PyInt(1),PyNone.Instance,new PyDict([]));
    }

    [Method("CanBeKickedOut")]
    public async ValueTask<PyValue> CanBeKickedOut(ServiceCall call)
    {
        var corp = await Current(call);
        var target = PositiveId(call.Arg(0));
        if (corp is null || target is null || await store.MemberAsync(target.Value,call.Cancellation) is not { } member ||
            member.CorporationId != corp.Id || corp.CeoId == target || !Kickable(member))
            return new PyInt(0);
        return new PyInt(1);
    }

    [Method("KickOutMember")]
    public async ValueTask<PyValue> KickOutMember(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp || PositiveId(call.Arg(0)) is not { } target)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (target == actor)
        {
            if (!AssertCanLeave(await CanLeaveCurrentCorporation(call))) return PyNone.Instance;
            var character = await characters.GetAsync(actor,call.Cancellation) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var destination = await store.GetAsync(StarterCorporation(character),call.Cancellation) ?? throw new CorporationError("NPC_CORPORATION_REQUIRED");
            var before = await store.MemberAsync(actor,call.Cancellation);
            await store.LeaveAsync(actor,destination.Id,call.Cancellation);
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,actor,before,null);
            await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
            return MoveResult(actor,destination);
        }
        if (await store.MemberAsync(target,call.Cancellation) is not { } member || member.CorporationId != corp.Id || !Kickable(member))
            return PyNone.Instance;
        var kicked = await characters.GetAsync(target,call.Cancellation) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
        var fallback = await store.GetAsync(StarterCorporation(kicked),call.Cancellation) ?? throw new CorporationError("NPC_CORPORATION_REQUIRED");
        await store.KickAsync(actor,target,fallback.Id,call.Cancellation);
        CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,target,member,null);
        return MoveResult(target,fallback);
    }

    [Method("KickOutMembers")]
    public async ValueTask<PyValue> KickOutMembers(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || !await CanAdminister(call))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var kicked = new List<PyValue>();
        var notKicked = new List<PyValue>();
        foreach (var raw in Items(call.Arg(0)))
        {
            if (PositiveId(raw) is not { } target || target == actor ||
                await CanBeKickedOut(new ServiceCall { Method="CanBeKickedOut",Args=[new PyInt(target)],Session=call.Session,Cancellation=call.Cancellation }) is not PyInt { Value: 1 })
            {
                if (PositiveId(raw) is { } skipped) notKicked.Add(new PyInt(skipped));
                continue;
            }
            var character = await characters.GetAsync(target,call.Cancellation);
            if (character is null)
            {
                notKicked.Add(new PyInt(target));
                continue;
            }
            var before = await store.MemberAsync(target,call.Cancellation);
            if (before is null)
            {
                notKicked.Add(new PyInt(target));
                continue;
            }
            var corporationId = before.CorporationId;
            await store.KickAsync(actor,target,StarterCorporation(character),call.Cancellation);
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corporationId,target,before,null);
            kicked.Add(new PyInt(target));
        }
        return Py.Dict(("kicked",new PyList(kicked)),("notKicked",new PyList(notKicked)));
    }

    private static PyValue MoveResult(long character,Corporation corporation) => Py.Dict(
        ("success",PyBool.True),
        ("data",Py.Dict(("characterID",new PyInt(character)),("corporationID",new PyInt(corporation.Id)),
            ("allianceID",corporation.AllianceId is { } alliance ? new PyInt(alliance) : PyNone.Instance))));

    [Method("ResignFromCEO")]
    public async ValueTask<PyValue> ResignFromCEO(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor ||
            await Current(call) is not { CeoId: var ceo,CeoRolesSuspended:false } corp || ceo != actor)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (PositiveId(call.Arg(0)) is { } successor)
        {
            var actorBefore = await store.MemberAsync(actor,call.Cancellation);
            var successorBefore = await store.MemberAsync(successor,call.Cancellation);
            await store.ResignAsync(actor,successor,call.Cancellation);
            var actorAfter = actorBefore is null ? null : actorBefore with
            {
                Masks = actorBefore.Masks with { Roles=actorBefore.Masks.Roles & ~CorporationRoles.Director }
            };
            var successorAfter = successorBefore is null ? null : successorBefore with
            {
                Masks = successorBefore.Masks with { Roles=successorBefore.Masks.Roles | CorporationRoles.Director }
            };
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,actor,actorBefore,actorAfter);
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,successor,successorBefore,successorAfter);
            await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
            return PyNone.Instance;
        }
        var members = await store.MembersAsync(corp.Id,call.Cancellation);
        if (members.Count <= 1) return PyNone.Instance;
        return new PyList([.. members.Where(m => m.CharacterId != actor).Select(m => (PyValue)new PyInt(m.CharacterId))]);
    }

    [Method("UpdateMember")]
    public async ValueTask<PyValue> UpdateMember(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp ||
            PositiveId(call.Arg(0)) is not { } target)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var (before,desired) = await MemberUpdate(target,call.Args,false,corp.Id,call.Cancellation);
        var after = await store.UpdateMemberAsync(actor,desired,call.Cancellation);
        CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,target,before,after);
        if (target == actor) await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateMembers")]
    public async ValueTask<PyValue> UpdateMembers(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var updates = new List<Member>();
        var beforeById = new Dictionary<long,Member>();
        foreach (var raw in Items(call.Arg(0)))
        {
            var row = Items(raw);
            if (row.Count == 0 || PositiveId(row[0]) is not { } target)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var (before,after) = await MemberUpdate(target,row,true,corp.Id,call.Cancellation);
            beforeById.Add(target,before);
            updates.Add(after);
        }
        if (updates.Select(m => m.CharacterId).Distinct().Count() != updates.Count)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var changed = await store.UpdateMembersAsync(actor,updates,call.Cancellation);
        foreach (var member in changed)
            CorporationNotificationAdapter.MemberChanged(characterSessions,call,corp.Id,member.CharacterId,beforeById[member.CharacterId],member);
        if (updates.Any(m => m.CharacterId == actor))
            await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateTitle")]
    public async ValueTask<PyValue> UpdateTitle(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var title = Title(call.Args);
        var before = (await store.TitlesAsync(corp.Id,call.Cancellation)).FirstOrDefault(t => t.Id == title.Id);
        var updated = title with { Name=title.Name.Length > 100 ? title.Name[..100] : title.Name };
        await store.UpdateTitleAsync(actor,corp.Id,updated,call.Cancellation);
        CorporationNotificationAdapter.TitleChanged(characterSessions,call,corp.Id,before,updated);
        return PyNone.Instance;
    }

    [Method("UpdateTitles")]
    public async ValueTask<PyValue> UpdateTitles(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var titles = Items(call.Arg(0)).Select(row => Title(Items(row)))
            .Select(title => title with { Name=title.Name.Length > 100 ? title.Name[..100] : title.Name }).ToArray();
        var before = await store.TitlesAsync(corp.Id,call.Cancellation);
        await store.UpdateTitlesAsync(actor,corp.Id,titles,call.Cancellation);
        foreach (var title in titles)
            CorporationNotificationAdapter.TitleChanged(characterSessions,call,corp.Id,
                before.FirstOrDefault(t => t.Id == title.Id),title);
        return PyNone.Instance;
    }

    [Method("DeleteTitle")]
    public async ValueTask<PyValue> DeleteTitle(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var titleId = Number(call.Arg(0));
        if (titleId is < 1 or > 32768) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var before = (await store.TitlesAsync(corp.Id,call.Cancellation)).FirstOrDefault(t => t.Id == (int)titleId);
        await store.DeleteTitleAsync(actor,corp.Id,(int)titleId,call.Cancellation);
        if (before is not null)
            CorporationNotificationAdapter.TitleChanged(characterSessions,call,corp.Id,before,null);
        return PyNone.Instance;
    }

    private static bool AssertCanLeave(PyValue value) =>
        value is PyTuple { Items.Count: > 0 } tuple && tuple.Items[0] is PyInt { Value: 1 };

    private bool Kickable(Member member) =>
        RoleFields(member).All(r => r.Value == 0) && member.TitleMask == 0 &&
        (member.StasisUntilMs is null || member.StasisUntilMs <= now());

    private static (string Name,long Value)[] RoleFields(Member m) =>
    [
        ("roles",m.Masks.Roles),("grantableRoles",m.Masks.GrantableRoles),
        ("rolesAtHQ",m.Masks.AtHq),("grantableRolesAtHQ",m.Masks.GrantableHq),
        ("rolesAtBase",m.Masks.AtBase),("grantableRolesAtBase",m.Masks.GrantableBase),
        ("rolesAtOther",m.Masks.AtOther),("grantableRolesAtOther",m.Masks.GrantableOther),
    ];

    private async Task<bool> CanAdminister(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await characters.GetAsync(actor,call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId,call.Cancellation) is not { IsNpc: false,Deleted: false } corp ||
            await store.MemberAsync(actor,call.Cancellation) is not { } member || member.CorporationId != corp.Id)
            return false;
        if (corp.CeoId == actor && !corp.CeoRolesSuspended) return true;
        return (CorporationRoles.Session(corp,member,character.StationId,await store.TitlesAsync(corp.Id,call.Cancellation)).AtAll & CorporationRoles.Director) != 0;
    }

    private static long StarterCorporation(Eve.Characters.Character character) =>
        Eve.Characters.CreationCatalog.ResolveSchool(character.SchoolId,character.RaceId)?.CorporationId ?? 1000004;

    private async Task<(Member Before,Member After)> MemberUpdate(long character,IReadOnlyList<PyValue> values,bool bulk,long corporation,CancellationToken token)
    {
        var old = await store.MemberAsync(character,token);
        if (old is null || old.CorporationId != corporation) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        PyValue At(int index) => index < values.Count ? values[index] : PyNone.Instance;
        long OptionalMask(int index,long current,long allowed) => At(index) is PyNone ? current : RoleMask(At(index),allowed);
        var masks = new RoleMasks(OptionalMask(4,old.Masks.Roles,CorporationRoles.Administrative),
            OptionalMask(5,old.Masks.GrantableRoles,CorporationRoles.Grantable),
            OptionalMask(6,old.Masks.AtHq,CorporationRoles.Locational),OptionalMask(7,old.Masks.GrantableHq,CorporationRoles.Locational),
            OptionalMask(8,old.Masks.AtBase,CorporationRoles.Locational),OptionalMask(9,old.Masks.GrantableBase,CorporationRoles.Locational),
            OptionalMask(10,old.Masks.AtOther,CorporationRoles.Locational),OptionalMask(11,old.Masks.GrantableOther,CorporationRoles.Locational));
        var titleMask = At(13) is PyNone ? old.TitleMask : (int)RoleMask(At(13),65535);
        var blockRoles = At(14) is PyNone ? old.BlockRoles : BlockRoles(At(14));
        var baseId = At(12) is PyNone ? old.BaseId : PositiveId(At(12));
        var after = old with
        {
            Title = bulk || At(1) is PyNone ? old.Title : Text(At(1))[..Math.Min(100,Text(At(1)).Length)],
            DivisionId = bulk || At(2) is PyNone ? old.DivisionId : (int)Number(At(2)),
            SquadronId = bulk || At(3) is PyNone ? old.SquadronId : (int)Number(At(3)),
            Masks = masks,TitleMask = titleMask,BlockRoles = blockRoles,BaseId = baseId
        };
        return (old,after);
    }

    private static CorporationTitle Title(IReadOnlyList<PyValue> values)
    {
        if (values.Count == 0 || PositiveId(values[0]) is not { } id || id > 32768 || (id & (id-1)) != 0)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        long At(int index,long allowed) => index < values.Count ? RoleMask(values[index],allowed) : throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return new CorporationTitle((int)id,values.Count > 1 ? Text(values[1]) : "",new RoleMasks(
            At(2,CorporationRoles.Grantable),At(3,CorporationRoles.Grantable),
            At(4,CorporationRoles.Locational),At(5,CorporationRoles.Locational),
            At(6,CorporationRoles.Locational),At(7,CorporationRoles.Locational),
            At(8,CorporationRoles.Locational),At(9,CorporationRoles.Locational)));
    }

    private static long RoleMask(PyValue value,long allowed)
    {
        var mask = value switch { PyInt n => n.Value,PyLong n => n.Value,_ => throw new CorporationError("CORPORATION_ACCESS_DENIED") };
        if (!CorporationRoles.IsKnown(mask,allowed)) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return mask;
    }

    private static int CorporationWalletKey(PyValue value)
    {
        var number = Number(value);
        if (number is >= 1000 and <= 1006) return (int)number;
        var text = Text(value).Trim().ToLowerInvariant();
        return text switch
        {
            "cash2" => 1001,"cash3" => 1002,"cash4" => 1003,"cash5" => 1004,
            "cash6" => 1005,"cash7" => 1006,_ => 1000
        };
    }

    private static bool BlockRoles(PyValue value) => value switch
    {
        PyBool boolean => boolean == PyBool.True,
        PyInt number when number.Value is 0 or 1 => number.Value == 1,
        PyLong number when number.Value is 0 or 1 => number.Value == 1,
        _ => throw new CorporationError("CORPORATION_ACCESS_DENIED")
    };

    private static decimal DecimalValue(PyValue value) => value switch
    {
        PyInt n => n.Value,PyLong n => n.Value,PyReal n when double.IsFinite(n.Value) => (decimal)n.Value,_ => 0m
    };

    private static bool Truth(PyValue value) => value switch
    {
        PyNone => false,PyBool boolean => boolean == PyBool.True,PyInt n => n.Value != 0,PyLong n => n.Value != 0,
        PyReal n => n.Value != 0,PyStr s => s.Value.Length != 0,PyWString => !string.IsNullOrEmpty(JsSemantics.StringValue(value)),_ => true
    };

    private static bool NormalizeBoolean(PyValue value,bool fallback)
    {
        if (value is PyNone) return fallback;
        if (value is PyStr or PyWString)
        {
            var text = JsSemantics.StringValue(value)?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(text)) return fallback;
            if (text is "0" or "false") return false;
            if (text is "1" or "true") return true;
        }
        return Truth(value);
    }

    private static object? SettingValue(PyValue value) => value switch
    {
        PyInt n => n.Value,PyLong n => n.Value,PyReal n => n.Value,PyBool b => b == PyBool.True,
        PyStr s => s.Value,PyWString => JsSemantics.StringValue(value),_ => null
    };
}
