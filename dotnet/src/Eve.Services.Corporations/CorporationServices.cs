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
    public async ValueTask<PyValue> GetCorporationIDForCharacter(ServiceCall call)
    {
        var id = call.Arg(0) switch { PyInt n => n.Value,PyLong n => n.Value,_ => 0 };
        if (id > 0 && await characters.GetAsync(id,call.Cancellation) is { CorporationId: > 0 } character)
            return new PyInt(character.CorporationId);
        return new PyInt(call.Session.GetInt("corporationID") ?? call.Session.GetInt("corpid") ?? 1000004);
    }
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

    [Method("GetMedalsReceived")]
    public async ValueTask<PyValue> GetMedalsReceived(ServiceCall call)
    {
        var characterId = call.Args.Count == 0 ? SessionCharacterId(call.Session) : MedalInteger(call.Arg(0));
        var received = await store.MedalsReceivedAsync(characterId, call.Cancellation);
        var info = received.Select(r => (PyValue)Py.List(
            CorporationWire.Id(r.Medal.Id), CorporationWire.Id(r.Award.IssuerId), CorporationWire.Id(r.Medal.CorporationId),
            CorporationWire.Id(r.Medal.Status), new PyWString(r.Award.Reason),
            CorporationWire.Time(r.Award.CreatedMs != 0 ? r.Award.CreatedMs : r.Medal.CreatedMs),
            new PyInt(r.Medal.IsDeleted ? 1 : 0)));
        var graphics = received.SelectMany(r => r.Medal.Graphics.Select(g => (r.Medal.Id, g)));
        return Py.Tuple(
            Py.Rowset(["medalID","issuerID","ownerID","status","reason","date","isDeleted"], info),
            MedalGraphicsRowset(graphics));
    }

    [Method("GetMedalDetails")]
    public async ValueTask<PyValue> GetMedalDetails(ServiceCall call)
    {
        var medal = await store.MedalAsync(MedalInteger(call.Arg(0)), call.Cancellation);
        var graphics = medal is null
            ? MedalGraphicsRowset([])
            : MedalGraphicsRowset(medal.Graphics.Select(g => (medal.Id, g)));
        var info = medal is null ? new PyList([]) : new PyList([
            Py.KeyVal([
                ("medalID", CorporationWire.Id(medal.Id)),
                ("ownerID", CorporationWire.Id(medal.CorporationId)),
                ("creatorID", CorporationWire.Id(medal.CreatorId)),
                ("title", new PyWString(medal.Title)),
                ("description", new PyWString(medal.Description)),
                ("createDateTime", CorporationWire.Time(medal.CreatedMs)),
                ("numberOfRecipients", CorporationWire.Id(medal.RecipientCount))
            ])
        ]);
        return Py.KeyVal([("info", info), ("graphics", graphics)]);
    }

    [Method("CreateMedal")]
    public async ValueTask<PyValue> CreateMedal(ServiceCall call)
    {
        var graphics = ParseMedalGraphics(call.Arg(2));
        var id = await store.CreateMedalAsync(SessionCorporationId(call.Session), SessionIssuerId(call.Session),
            MedalText(call.Arg(0)), MedalText(call.Arg(1)), graphics, call.Cancellation);
        return CorporationWire.Id(id);
    }

    [Method("SetMedalStatus")]
    public async ValueTask<PyValue> SetMedalStatus(ServiceCall call)
    {
        await store.SetMedalStatusesAsync(SessionCorporationId(call.Session), MedalStatusEntries(call.Arg(0)), call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetAllCorpMedals")]
    public async ValueTask<PyValue> GetAllCorpMedals(ServiceCall call)
    {
        var requested = MedalInteger(call.Arg(0));
        var medals = await store.MedalsAsync(requested > 0 ? requested : 0, call.Cancellation);
        var medalRows = medals.Select(m => (PyValue)Py.List(
            CorporationWire.Id(m.Id), CorporationWire.Id(m.CorporationId), CorporationWire.Id(m.CreatorId),
            new PyWString(m.Title), new PyWString(m.Description), CorporationWire.Time(m.CreatedMs),
            CorporationWire.Id(m.RecipientCount)));
        var payload = Py.Tuple(
            Py.Rowset(["medalID","ownerID","creatorID","title","description","date","noRecepients"], medalRows),
            MedalGraphicsRowset(medals.SelectMany(m => m.Graphics.Select(g => (m.Id, g)))));
        return ObjectCaching.MethodResult(payload, store.ClockMs, "1 hour");
    }

    [Method("GetRecipientsOfMedal")]
    public async ValueTask<PyValue> GetRecipientsOfMedal(ServiceCall call)
    {
        var medalId = MedalInteger(call.Arg(0));
        var recipients = await store.MedalRecipientsAsync(SessionCorporationId(call.Session), medalId, call.Cancellation);
        return Py.Rowset(["medalID","recepientID","issuerID","reason","date"], recipients.Select(a => (PyValue)Py.List(
            CorporationWire.Id(a.MedalId), CorporationWire.Id(a.CharacterId), CorporationWire.Id(a.IssuerId),
            new PyWString(a.Reason), CorporationWire.Time(a.CreatedMs))));
    }

    [Method("GiveMedalToCharacters")]
    public async ValueTask<PyValue> GiveMedalToCharacters(ServiceCall call)
    {
        var rawRecipients = call.Arg(1);
        var recipients = rawRecipients is PyList or PyTuple
            ? MedalSequence(rawRecipients).Select(MedalInteger).ToArray()
            : [MedalInteger(rawRecipients)];
        await store.AwardMedalAsync(SessionCorporationId(call.Session), MedalInteger(call.Arg(0)), recipients,
            SessionIssuerId(call.Session), MedalText(call.Arg(2)), call.Cancellation);
        return PyNone.Instance;
    }

    private static PyValue MedalGraphicsRowset(IEnumerable<(long MedalId, CorporationStore.MedalGraphic Graphic)> graphics) =>
        Py.Rowset(["medalID","part","graphic","color"], graphics.Select(g => (PyValue)Py.List(
            CorporationWire.Id(g.MedalId), CorporationWire.Id(g.Graphic.Part),
            new PyWString(g.Graphic.Graphic), CorporationWire.Id(g.Graphic.Color))));

    private static long SessionCorporationId(ClientSession session)
    {
        var corporationId = session.GetInt("corporationID") ?? 0;
        return corporationId != 0 ? corporationId : session.GetInt("corpid") ?? 0;
    }

    private static long SessionIssuerId(ClientSession session)
    {
        var characterId = session.CharacterId.GetValueOrDefault();
        if (characterId != 0) return characterId;
        characterId = session.GetInt("characterID") ?? 0;
        return characterId != 0 ? characterId : session.GetInt("charid") ?? 0;
    }

    private static long SessionCharacterId(ClientSession session)
    {
        var characterId = session.CharacterId.GetValueOrDefault();
        return characterId != 0 ? characterId : session.GetInt("characterID") ?? 0;
    }

    private static IReadOnlyList<CorporationStore.MedalGraphic> ParseMedalGraphics(PyValue value) =>
        MedalSequence(value).Select(item =>
        {
            if (item is PyList or PyTuple)
            {
                var positional = MedalSequence(item);
                return new CorporationStore.MedalGraphic(
                    positional.Count > 0 ? MedalInteger(positional[0]) : 0,
                    positional.Count > 1 && positional[1] is not PyNone ? JsSemantics.ToJsString(positional[1]) : "",
                    positional.Count > 2 ? MedalInteger(positional[2]) : 0);
            }
            var graphic = MedalField(item, "graphic");
            if (graphic is PyNone) graphic = MedalField(item, "graphicID");
            var color = MedalField(item, "color");
            if (color is PyNone) color = MedalField(item, "colorID");
            return new CorporationStore.MedalGraphic(
                MedalInteger(MedalField(item, "part")), graphic is PyNone ? "" : JsSemantics.ToJsString(graphic), MedalInteger(color));
        }).ToArray();

    private static IReadOnlyList<(long MedalId, long Status)> MedalStatusEntries(PyValue value)
    {
        var entries = MedalDictionary(value)?.Entries ?? [];
        if (MedalText(MedalField(value, "type")) == "dict")
        {
            var encoded = MedalSequence(MedalField(value, "entries"));
            return encoded.Select(pair => MedalSequence(pair))
                .Where(pair => pair.Count > 1)
                .Select(pair => (MedalInteger(pair[0]), MedalInteger(pair[1])))
                .ToArray();
        }
        return entries.Select(entry => (MedalInteger(entry.Key), MedalInteger(entry.Value))).ToArray();
    }

    private static PyDict? MedalDictionary(PyValue value) => value switch
    {
        PyDict dictionary => dictionary,
        PyObject { Args: PyDict dictionary } => dictionary,
        _ => null
    };

    private static PyValue MedalField(PyValue value, string name)
    {
        var entry = MedalDictionary(value)?.Entries.FirstOrDefault(e => JsSemantics.StringValue(e.Key) == name);
        return entry?.Value ?? PyNone.Instance;
    }

    private static IReadOnlyList<PyValue> MedalSequence(PyValue value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        _ => []
    };

    private static string MedalText(PyValue value) =>
        JsSemantics.Truthy(value) ? JsSemantics.ToJsString(value) : "";

    private static long MedalInteger(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyBool boolean => boolean.Value ? 1 : 0,
        PyReal number => Integral(number.Value),
        PyStr text => ParseInteger(text.Value),
        PyWString text => ParseInteger(text.Value),
        PyTuple { Items.Count: > 0 } tuple => MedalInteger(tuple.Items[0]),
        PyList { Items.Count: > 0 } list => MedalInteger(list.Items[0]),
        _ => 0
    };

    private static long ParseInteger(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? Integral(number) : 0;

    private static long Integral(double value) =>
        double.IsFinite(value) && value >= long.MinValue && value <= long.MaxValue ? (long)Math.Truncate(value) : 0;
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
        return CorporationWire.Row(created,(await store.MembersAsync(created.Id,call.Cancellation)).Count);
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
        await store.SetAccountKeyAsync(character,key,call.Cancellation);
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
            await store.LeaveAsync(actor,destination.Id,call.Cancellation);
            await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
            return MoveResult(actor,destination);
        }
        if (await store.MemberAsync(target,call.Cancellation) is not { } member || member.CorporationId != corp.Id || !Kickable(member))
            return PyNone.Instance;
        var kicked = await characters.GetAsync(target,call.Cancellation) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
        var fallback = await store.GetAsync(StarterCorporation(kicked),call.Cancellation) ?? throw new CorporationError("NPC_CORPORATION_REQUIRED");
        await store.KickAsync(actor,target,fallback.Id,call.Cancellation);
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
            await store.KickAsync(actor,target,StarterCorporation(character),call.Cancellation);
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
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { CeoId: var ceo } corp || ceo != actor)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (PositiveId(call.Arg(0)) is { } successor)
        {
            await store.ResignAsync(actor,successor,call.Cancellation);
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
        var desired = await MemberUpdate(target,call.Args,false,corp.Id,call.Cancellation);
        await store.UpdateMemberAsync(actor,desired,call.Cancellation);
        if (target == actor) await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateMembers")]
    public async ValueTask<PyValue> UpdateMembers(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var updates = new List<Member>();
        foreach (var raw in Items(call.Arg(0)))
        {
            var row = Items(raw);
            if (row.Count == 0 || PositiveId(row[0]) is not { } target)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            updates.Add(await MemberUpdate(target,row,true,corp.Id,call.Cancellation));
        }
        if (updates.Select(m => m.CharacterId).Distinct().Count() != updates.Count)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        await store.UpdateMembersAsync(actor,updates,call.Cancellation);
        if (updates.Any(m => m.CharacterId == actor))
            await new CorporationLoginHook(store,characters).OnCharacterEnteredAsync(call.Session,actor,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateTitle")]
    public async ValueTask<PyValue> UpdateTitle(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        await store.UpdateTitleAsync(actor,corp.Id,Title(call.Args),call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UpdateTitles")]
    public async ValueTask<PyValue> UpdateTitles(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var titles = Items(call.Arg(0)).Select(row => Title(Items(row))).ToArray();
        await store.UpdateTitlesAsync(actor,corp.Id,titles,call.Cancellation);
        return PyNone.Instance;
    }

    [Method("DeleteTitle")]
    public async ValueTask<PyValue> DeleteTitle(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor || await Current(call) is not { } corp)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var titleId = Number(call.Arg(0));
        if (titleId is < 1 or > 32768) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        await store.DeleteTitleAsync(actor,corp.Id,(int)titleId,call.Cancellation);
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
        if (corp.CeoId == actor) return true;
        return (CorporationRoles.Session(corp,member,character.StationId,await store.TitlesAsync(corp.Id,call.Cancellation)).AtAll & CorporationRoles.Director) != 0;
    }

    private static long StarterCorporation(Eve.Characters.Character character) =>
        Eve.Characters.CreationCatalog.ResolveSchool(character.SchoolId,character.RaceId)?.CorporationId ?? 1000004;

    private async Task<Member> MemberUpdate(long character,IReadOnlyList<PyValue> values,bool bulk,long corporation,CancellationToken token)
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
        return old with
        {
            Title = bulk || At(1) is PyNone ? old.Title : Text(At(1))[..Math.Min(100,Text(At(1)).Length)],
            DivisionId = bulk || At(2) is PyNone ? old.DivisionId : (int)Number(At(2)),
            SquadronId = bulk || At(3) is PyNone ? old.SquadronId : (int)Number(At(3)),
            Masks = masks,TitleMask = titleMask,BlockRoles = blockRoles,BaseId = baseId
        };
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
