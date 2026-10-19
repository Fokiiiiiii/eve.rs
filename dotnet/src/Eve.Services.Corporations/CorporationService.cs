using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Corporations;

[Service("corporationSvc")]
public sealed partial class CorporationService(CorporationStore store,PgCharacterStore characters,CharacterSessions? characterSessions = null)
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

    [Method("GetCorpInfo")]
    public async ValueTask<PyValue> GetCorpInfo(ServiceCall call)
    {
        var id = MedalInteger(call.Arg(0));
        return id > 0 && await store.GetAsync(id,call.Cancellation) is not null
            ? new PyList([])
            : PyNone.Instance;
    }

    [Method("GetEmployementRecordAndCharacterTransfers")]
    public async ValueTask<PyValue> GetEmployementRecordAndCharacterTransfers(ServiceCall call) =>
        Py.Tuple(await GetEmploymentRecord(call),new PyList([]));

    [Method("GetMedalsReceived")]
    public async ValueTask<PyValue> GetMedalsReceived(ServiceCall call)
    {
        var characterId = call.Args.Count == 0 ? SessionCharacterId(call.Session) : MedalInteger(call.Arg(0));
        var received = await store.MedalsReceivedAsync(characterId, call.Cancellation);
        if (characterId != SessionCharacterId(call.Session))
            received = received.Where(r => r.Medal.Status == 3).ToArray();
        var info = received.Select(r => (PyValue)Py.List(
            CorporationWire.Id(r.Medal.Id), CorporationWire.Id(r.Award.IssuerId), CorporationWire.Id(r.Medal.CorporationId),
            CorporationWire.Id(r.Medal.Status), new PyWString(r.Award.Reason),
            CorporationWire.Time(r.Award.CreatedMs != 0 ? r.Award.CreatedMs : r.Medal.CreatedMs),
            new PyInt(r.Medal.IsDeleted ? 1 : 0)));
        var graphics = received.DistinctBy(r => r.Medal.Id)
            .SelectMany(r => r.Medal.Graphics.Select(g => (r.Medal.Id, g)));
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
                ("numberOfRecipients", new PyInt(medal.RecipientCount))
            ])
        ]);
        return Py.KeyVal([("info", info), ("graphics", graphics)]);
    }

    private async Task<(long CorporationId, long CharacterId)> RequireMedalManagerAsync(ServiceCall call)
    {
        var actor = SessionCharacterId(call.Session);
        if (actor <= 0 || await characters.GetAsync(actor,call.Cancellation) is not { } character ||
            await store.GetAsync(character.CorporationId,call.Cancellation) is not { IsNpc:false,Deleted:false } corporation ||
            await store.MemberAsync(actor,call.Cancellation) is not { } member || member.CorporationId != corporation.Id)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var roles = CorporationRoles.Session(corporation,member,character.StationId,
            await store.TitlesAsync(corporation.Id,call.Cancellation)).AtAll;
        if ((roles & (CorporationRoles.Director | CorporationRoles.PersonnelManager)) == 0)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return (corporation.Id,actor);
    }

    [Method("CreateMedal")]
    public async ValueTask<PyValue> CreateMedal(ServiceCall call)
    {
        var (corporationId,actor) = await RequireMedalManagerAsync(call);
        var graphics = ParseMedalGraphics(call.Arg(2));
        var id = await store.CreateMedalAsync(corporationId,actor,
            MedalText(call.Arg(0)),MedalText(call.Arg(1)),graphics,call.Cancellation);
        return CorporationWire.Id(id);
    }

    [Method("SetMedalStatus")]
    public async ValueTask<PyValue> SetMedalStatus(ServiceCall call)
    {
        var (corporationId, _) = await RequireMedalManagerAsync(call);
        await store.SetMedalStatusesAsync(corporationId, MedalStatusEntries(call.Arg(0)), call.Cancellation);
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
            new PyInt(m.RecipientCount)));
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
        var (corporationId,actor) = await RequireMedalManagerAsync(call);
        var rawRecipients = call.Arg(1);
        var recipients = rawRecipients is PyList or PyTuple
            ? MedalSequence(rawRecipients).Select(MedalInteger).ToArray()
            : [MedalInteger(rawRecipients)];
        var medalId = MedalInteger(call.Arg(0));
        var medal = await store.MedalAsync(medalId,call.Cancellation);
        if (medalId <= 0 || medal?.CorporationId != corporationId ||
            recipients.Length == 0 || recipients.Any(characterId => characterId <= 0))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        foreach (var recipientId in recipients)
        {
            if (await store.MemberAsync(recipientId,call.Cancellation) is not { } recipient ||
                recipient.CorporationId != corporationId)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
        }

        await store.AwardMedalAsync(corporationId,medalId,recipients,
            actor,MedalText(call.Arg(2)),call.Cancellation);
        CorporationNotificationAdapter.MedalsReceived(characterSessions,call,medalId,recipients);
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
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => ParseInteger(Eve.Macho.JsSemantics.StringValue(text)!),
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
