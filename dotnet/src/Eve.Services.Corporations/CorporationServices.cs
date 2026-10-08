using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Corporations;

[Service("corpmgr")]
public sealed partial class CorpMgrService(
    CorporationStore store,
    PgCharacterStore characters,
    Eve.Inventory.PgItemStore? items = null,
    Eve.Inventory.ItemTypeCatalog? itemTypes = null,
    Eve.Corporations.CorporationOfficeStore? offices = null,
    Func<long, (long SolarSystemId, int? TypeId)?>? resolveAssetLocation = null,
    Func<long, CancellationToken, ValueTask<CorpMgrAggressionSettings?>>? readAggressionSettings = null,
    Func<long, long, long, long, int, CancellationToken, ValueTask<IReadOnlyList<CorpMgrAuditEvent>>>? readAuditEvents = null,
    Func<long, CancellationToken, ValueTask<IReadOnlyList<Eve.Inventory.Item>>>? readAssetWraps = null)
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
        var rawRecipients = call.Arg(1);
        var recipients = rawRecipients is PyList or PyTuple
            ? MedalSequence(rawRecipients).Select(MedalInteger).ToArray()
            : [MedalInteger(rawRecipients)];
        var corporationId = SessionCorporationId(call.Session);
        var medalId = MedalInteger(call.Arg(0));
        var medal = await store.MedalAsync(medalId,call.Cancellation);
        await store.AwardMedalAsync(corporationId,medalId,recipients,
            SessionIssuerId(call.Session),MedalText(call.Arg(2)),call.Cancellation);
        if (medal?.CorporationId == corporationId)
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


/// <summary>Client contract for station corporation offices and their impounded assets.</summary>
[Service("officeManager")]
public sealed partial class OfficeManagerService(
    CorporationOfficeStore store,
    CorporationStore corporations,
    PgCharacterStore characters,
    Eve.Inventory.PgItemStore items,
    Eve.Wallet.WalletTransactions wallets,
    Func<long,CorporationOfficeStation?> readStation,
    Func<long> now)
{
    private const long RentOfficeRole = CorporationRoles.RentOffice;
    // The SDE inventory flag for the station's Impounded location.
    private const int ImpoundedFlag = CorporationOfficeStore.ImpoundedFlag;
    private const int OfficeRentReferenceType = 13;
    private const int ImpoundReleaseReferenceType = 41;
    private const long EveDayMs = 24L * 60 * 60 * 1000;

    [Method("GetMyCorporationsOffices")]
    public async ValueTask<PyValue> GetMyCorporationsOffices(ServiceCall call)
    {
        var corporationId = SessionCorporationId(call.Session);
        if (corporationId <= 0) return new PyList([]);
        var offices = await store.ForCorporationAsync(corporationId, call.Cancellation);
        return new PyList([.. offices.Select(OfficeRow)]);
    }

    [Method("GetPriceQuote")]
    public async ValueTask<PyValue> GetPriceQuote(ServiceCall call)
    {
        var corporationId = Integral(call.Arg(0)) ?? 0;
        var sessionCorporationId = SessionCorporationId(call.Session);
        if (corporationId <= 0 || corporationId != sessionCorporationId)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var station = CurrentStation(call.Session);
        var price = await QuoteAsync(station, call.Cancellation);
        return new PyReal((double)price);
    }

    [Method("RentOffice")]
    public async ValueTask<PyValue> RentOffice(ServiceCall call)
    {
        var actor = RequireActor(call.Session);
        var corporationId = SessionCorporationId(call.Session);
        var station = CurrentStation(call.Session);
        var authority = await RequireAuthorityAsync(actor, corporationId, station.Id, call.Cancellation);
        if ((authority.Roles.AtAll & RentOfficeRole) == 0)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var submittedQuote = Money(call.Arg(0)) ?? throw new CorporationError("OFFICE_PRICE_REQUIRED");
        if (submittedQuote < CorporationOfficeStore.MinimumRentIsk)
            throw new CorporationError("OFFICE_PRICE_INVALID");
        var existingItems = await items.InLocationAsync(station.Id, corporationId, call.Cancellation);
        var atStation = now();
        await store.WriteAsync(async (connection, transaction) =>
        {
            await store.LockStationAsync(connection, transaction, station.Id, call.Cancellation);
            if (await store.GetAsync(connection, transaction, corporationId, station.Id, forUpdate: true,
                cancellationToken: call.Cancellation) is not null)
                return 0;

            var occupied = await store.CountAtStationAsync(connection, transaction, station.Id, call.Cancellation);
            if (occupied >= station.OfficeCapacity) throw new CorporationError("OFFICE_SLOTS_FULL");
            var price = await store.PriceQuoteAsync(connection, transaction, station, occupied, atStation, call.Cancellation);
            if (submittedQuote != price) throw new CorporationError("OFFICE_PRICE_CHANGED");

            var officeFolderId = await items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(CorporationOfficeStore.OfficeFolderTypeId, corporationId, station.Id,
                    Eve.Inventory.ItemFlags.Hangar, Singleton: true), call.Cancellation);
            var office = await store.RentAsync(connection, transaction, corporationId, officeFolderId, station, price, atStation,
                call.Cancellation);
            await wallets.AdjustAsync(connection, transaction, new Eve.Wallet.WalletAccount(corporationId), -price,
                station.OwnerId, OfficeRentReferenceType, "Corporation office rental", atStation,
                $"office-rent:{office.OfficeId}:charge", "office_rental_fee", call.Cancellation);

            foreach (var item in existingItems.Where(item => item.Flag == ImpoundedFlag))
                await MoveIfCurrentAsync(connection, transaction, item, corporationId, station.Id,
                    ImpoundedFlag, Eve.Inventory.ItemFlags.CorpSAG1, call.Cancellation);
            return 0;
        }, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("UnrentOffice")]
    public async ValueTask<PyValue> UnrentOffice(ServiceCall call)
    {
        var actor = RequireActor(call.Session);
        var corporationId = SessionCorporationId(call.Session);
        var station = CurrentStation(call.Session);
        var authority = await RequireAuthorityAsync(actor, corporationId, station.Id, call.Cancellation);
        if (!IsCeoOrDirector(actor, authority))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var assets = await items.InLocationAsync(station.Id, corporationId, call.Cancellation);
        var atStation = now();
        await store.WriteAsync(async (connection, transaction) =>
        {
            await store.LockStationAsync(connection, transaction, station.Id, call.Cancellation);
            var office = await store.GetAsync(connection, transaction, corporationId, station.Id,
                forUpdate: true, cancellationToken: call.Cancellation);
            if (office is null) return 0;

            foreach (var item in assets.Where(item => Eve.Inventory.ItemFlags.IsCorporationHangar(item.Flag)))
                await MoveIfCurrentAsync(connection, transaction, item, corporationId, station.Id,
                    item.Flag, ImpoundedFlag, call.Cancellation);
            var folder = await items.LockAndGetAsync(connection, transaction, office.OfficeFolderId, call.Cancellation);
            if (folder is { OwnerId: var folderOwner, LocationId: var folderLocation, TypeId: CorporationOfficeStore.OfficeFolderTypeId } &&
                folderOwner == corporationId && folderLocation == station.Id)
                await items.DeleteAsync(connection, transaction, folder.Id, call.Cancellation);
            await store.UnrentAsync(connection, transaction, office, atStation, call.Cancellation);
            return 0;
        }, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetItemsFromImpound")]
    public async ValueTask<PyValue> GetItemsFromImpound(ServiceCall call)
    {
        var actor = RequireActor(call.Session);
        var corporationId = SessionCorporationId(call.Session);
        var station = CurrentStation(call.Session);
        var authority = await RequireAuthorityAsync(actor, corporationId, station.Id, call.Cancellation);
        if (!IsCeoOrDirector(actor, authority)) throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var impounded = (await items.InLocationAsync(station.Id, corporationId, call.Cancellation))
            .Where(item => item.Flag == ImpoundedFlag).ToArray();
        if (impounded.Length == 0) return PyNone.Instance;
        var quotedFee = Money(call.Arg(0)) ?? throw new CorporationError("IMPOUND_PRICE_REQUIRED");
        var atStation = now();
        await store.WriteAsync(async (connection, transaction) =>
        {
            await store.LockStationAsync(connection, transaction, station.Id, call.Cancellation);
            var occupied = await store.CountAtStationAsync(connection, transaction, station.Id, call.Cancellation);
            var rent = await store.PriceQuoteAsync(connection, transaction, station, occupied, atStation, call.Cancellation);
            var fee = decimal.Round(rent / 2m, 2, MidpointRounding.AwayFromZero);
            if (quotedFee != fee) throw new CorporationError("IMPOUND_PRICE_CHANGED");

            await wallets.AdjustAsync(connection, transaction, new Eve.Wallet.WalletAccount(corporationId), -fee,
                station.OwnerId, ImpoundReleaseReferenceType, "Corporation impound release", atStation,
                $"office-impound-release:{corporationId}:{station.Id}:{atStation}",
                "office_impound_release", call.Cancellation);
            foreach (var item in impounded)
                await MoveIfCurrentAsync(connection, transaction, item, corporationId, station.Id,
                    ImpoundedFlag, Eve.Inventory.ItemFlags.CorpSAG1, call.Cancellation);
            return 0;
        }, call.Cancellation);
        return PyNone.Instance;
    }

    public async ValueTask<PyValue> GetCorporationsWithOffices(ServiceCall call)
    {
        var stationId = call.Session.GetInt("stationid") ?? call.Session.GetInt("structureid");
        if (stationId is not > 0) return new PyList([]);
        var ids = await store.CorporationsAtStationAsync(stationId.Value, call.Cancellation);
        return new PyList([.. ids.Select(id => (PyValue)new PyLong(id))]);
    }

    public async ValueTask<PyValue> GetEmptyOfficeCount(ServiceCall call)
    {
        var station = CurrentStation(call.Session);
        var occupied = await store.WriteAsync((connection, transaction) =>
            store.CountAtStationAsync(connection, transaction, station.Id, call.Cancellation), call.Cancellation);
        return new PyInt(Math.Max(0, station.OfficeCapacity - occupied));
    }

    public async ValueTask<PyValue> HasCorpImpoundedItems(ServiceCall call)
    {
        var corporationId = SessionCorporationId(call.Session);
        if (corporationId <= 0) return PyBool.False;
        var corporation = await corporations.GetAsync(corporationId, call.Cancellation);
        if (corporation is null || corporation.IsNpc) return PyBool.False;
        var station = CurrentStation(call.Session);
        if (await store.WriteAsync(async (connection, transaction) =>
        {
            var office = await store.GetAsync(connection, transaction, corporationId, station.Id,
                cancellationToken: call.Cancellation);
            return office is not null;
        }, call.Cancellation))
            return PyBool.False;
        var contents = await items.InLocationAsync(station.Id, corporationId, call.Cancellation);
        return contents.Any(item => item.Flag == ImpoundedFlag) ? PyBool.True : PyBool.False;
    }

    public async ValueTask<PyValue> GetImpoundReleasePrice(ServiceCall call)
    {
        var corporationId = SessionCorporationId(call.Session);
        if (corporationId <= 0) return PyNone.Instance;
        var corporation = await corporations.GetAsync(corporationId, call.Cancellation);
        if (corporation is null || corporation.IsNpc) return PyNone.Instance;
        var station = CurrentStation(call.Session);
        if (await store.WriteAsync(async (connection, transaction) =>
        {
            var office = await store.GetAsync(connection, transaction, corporationId, station.Id,
                cancellationToken: call.Cancellation);
            return office is not null;
        }, call.Cancellation))
            return PyNone.Instance;
        var contents = await items.InLocationAsync(station.Id, corporationId, call.Cancellation);
        if (!contents.Any(item => item.Flag == ImpoundedFlag)) return PyNone.Instance;
        var rent = await QuoteAsync(station, call.Cancellation);
        return new PyReal((double)decimal.Round(rent / 2m, 2, MidpointRounding.AwayFromZero));
    }

    public async ValueTask<PyValue> TrashImpoundedOffice(ServiceCall call)
    {
        var actor = RequireActor(call.Session);
        var corporationId = SessionCorporationId(call.Session);
        var station = CurrentStation(call.Session);
        var authority = await RequireAuthorityAsync(actor, corporationId, station.Id, call.Cancellation);
        if (!IsCeoOrDirector(actor, authority)) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var impounded = (await items.InLocationAsync(station.Id, corporationId, call.Cancellation))
            .Where(item => item.Flag == ImpoundedFlag).ToArray();
        await store.WriteAsync(async (connection, transaction) =>
        {
            await store.LockStationAsync(connection, transaction, station.Id, call.Cancellation);
            foreach (var item in impounded)
            {
                var current = await items.LockAndGetAsync(connection, transaction, item.Id, call.Cancellation);
                if (current is { OwnerId: var owner, LocationId: var location, Flag: ImpoundedFlag } &&
                    owner == corporationId && location == station.Id)
                    await items.DeleteAsync(connection, transaction, item.Id, call.Cancellation);
            }
            return 0;
        }, call.Cancellation);
        return PyNone.Instance;
    }

    public async ValueTask<PyValue> PrimeOfficeItem(ServiceCall call)
    {
        var corporationId = SessionCorporationId(call.Session);
        if (corporationId <= 0) return PyNone.Instance;
        var station = CurrentStation(call.Session);
        var office = await store.WriteAsync((connection, transaction) =>
            store.GetAsync(connection, transaction, corporationId, station.Id,
                cancellationToken: call.Cancellation), call.Cancellation);
        return office is null ? PyNone.Instance : OfficeRow(office);
    }

    private async Task<decimal> QuoteAsync(CorporationOfficeStation station, CancellationToken token) =>
        await store.WriteAsync(async (connection, transaction) =>
        {
            await store.LockStationAsync(connection, transaction, station.Id, token);
            var occupied = await store.CountAtStationAsync(connection, transaction, station.Id, token);
            return await store.PriceQuoteAsync(connection, transaction, station, occupied, now(), token);
        }, token);

    private async Task<(Corporation Corporation, SessionRoles Roles)> RequireAuthorityAsync(long actor,
        long corporationId, long stationId, CancellationToken token)
    {
        if (corporationId <= 0) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var character = await characters.GetAsync(actor, token) ??
            throw new CorporationError("CHARACTER_NOT_FOUND");
        var corporation = await corporations.GetAsync(corporationId, token) ??
            throw new CorporationError("CORPORATION_NOT_FOUND");
        var member = await corporations.MemberAsync(actor, token);
        if (corporation.IsNpc || character.CorporationId != corporationId ||
            member is null || member.CorporationId != corporationId)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var titles = await corporations.TitlesAsync(corporationId, token);
        return (corporation, CorporationRoles.Session(corporation, member, stationId, titles));
    }

    private static bool IsCeoOrDirector(long actor, (Corporation Corporation, SessionRoles Roles) authority) =>
        (!authority.Corporation.CeoRolesSuspended && authority.Corporation.CeoId == actor) ||
        (authority.Roles.AtAll & CorporationRoles.Director) != 0;

    private async Task MoveIfCurrentAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction,
        Eve.Inventory.Item snapshot, long corporationId, long stationId, int expectedFlag, int destinationFlag,
        CancellationToken token)
    {
        var current = await items.LockAndGetAsync(connection, transaction, snapshot.Id, token);
        if (current is { OwnerId: var owner, LocationId: var location, Flag: var flag } &&
            owner == corporationId && location == stationId && flag == expectedFlag)
            await items.MoveAsync(connection, transaction, current.Id, stationId, destinationFlag, token);
    }

    private CorporationOfficeStation CurrentStation(ClientSession session)
    {
        var stationId = session.GetInt("stationid") ?? session.GetInt("structureid") ?? 0;
        if (stationId <= 0) throw new CorporationError("OFFICE_STATION_REQUIRED");
        return readStation(stationId) ?? throw new CorporationError("OFFICE_STATION_NOT_FOUND");
    }

    private static long RequireActor(ClientSession session) =>
        session.CharacterId is > 0 ? session.CharacterId.Value : throw new CorporationError("CHARACTER_NOT_FOUND");

    private static long SessionCorporationId(ClientSession session)
    {
        var id = session.GetInt("corporationID") ?? 0;
        return id > 0 ? id : session.GetInt("corpid") ?? 0;
    }

    private static long? Integral(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    private static decimal? Money(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number when double.IsFinite(number.Value) => (decimal)number.Value,
        _ => null,
    };

    private static PyValue OfficeRow(CorporationOffice office) => Py.KeyVal(
        ("officeID", new PyLong(office.OfficeId)),
        ("stationID", new PyLong(office.StationId)),
        ("solarSystemID", new PyLong(office.SolarSystemId)),
        ("corporationID", new PyLong(office.CorporationId)),
        ("ownerID", new PyLong(office.CorporationId)),
        ("itemID", new PyLong(office.OfficeFolderId)),
        ("officeFolderID", new PyLong(office.OfficeFolderId)),
        ("typeID", new PyInt(CorporationOfficeStore.OfficeFolderTypeId)),
        ("flagID", new PyInt(Eve.Inventory.ItemFlags.Hangar)),
        ("locationID", new PyLong(office.StationId)),
        ("rent", new PyReal((double)office.RentIsk)),
        ("billDate", new PyLong(office.ExpiresAtMs)));
}

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


internal static class CorporationNotificationAdapter
{
    public static void MemberChanged(CharacterSessions? sessions,ServiceCall call,long corporationId,long characterId,
        Member? before,Member? after)
    {
        if (sessions is null) return;
        var payload = Py.Tuple(CorporationWire.Id(corporationId),CorporationWire.Id(characterId),MemberChange(before,after));
        call.Session.AfterResponse(() =>
        {
            var sent = new HashSet<ClientSession>();
            foreach (var session in sessions.OfCorporation(corporationId))
            {
                sent.Add(session);
                Send(session,"OnCorporationMemberChanged","corpid",payload);
            }
            if (sessions.Of(characterId) is { } member && sent.Add(member))
                Send(member,"OnCorporationMemberChanged","clientID",payload);
        });
    }

    public static void TitleChanged(CharacterSessions? sessions,ServiceCall call,long corporationId,
        CorporationTitle? before,CorporationTitle? after)
    {
        if (sessions is null) return;
        var titleId = after?.Id ?? before?.Id ?? 0;
        var payload = Py.Tuple(CorporationWire.Id(corporationId),new PyInt(titleId),TitleChange(before,after));
        call.Session.AfterResponse(() =>
        {
            foreach (var session in sessions.OfCorporation(corporationId))
                Send(session,"OnTitleChanged","corpid",payload);
        });
    }

public static void ApplicationChanged(CharacterSessions? sessions,ServiceCall call,long corporationId,
        long applicantId,long applicationId,PyValue applicationRow,IEnumerable<long> recipientIds)
    {
        if (sessions is null) return;
        var ids = recipientIds.Distinct().ToArray();
        var payload = Py.Tuple(CorporationWire.Id(corporationId),CorporationWire.Id(applicantId),
            CorporationWire.Id(applicationId),applicationRow);
        call.Session.AfterResponse(() =>
        {
            var targets = new HashSet<ClientSession>();
            foreach (var id in ids)
                if (sessions.Of(id) is { } recipient) targets.Add(recipient);
            if (call.Session.CharacterId is { } actor && ids.Contains(actor)) targets.Add(call.Session);
            foreach (var recipient in targets)
                Send(recipient,"OnCorporationApplicationChanged","clientID",payload);
        });
    }

    // eve.rs extension: the local EveJS medal service emits no push event on award.
    public static void MedalsReceived(CharacterSessions? sessions,ServiceCall call,long medalId,
        IEnumerable<long> recipients)
    {
        if (sessions is null) return;
        var uniqueRecipients = recipients.Distinct().ToArray();
        call.Session.AfterResponse(() =>
        {
            foreach (var id in uniqueRecipients)
                if (sessions.Of(id) is { } member)
                    Send(member,"OnMedalsReceived","clientID",Py.Tuple(CorporationWire.Id(medalId)));
        });
    }

    private static PyDict MemberChange(Member? before,Member? after)
    {
        var changes = new List<KeyValuePair<PyValue,PyValue>>();
        void Add(string name,PyValue oldValue,PyValue newValue)
        {
            if (oldValue.Equals(newValue)) return;
            changes.Add(new(new PyStr(name),new PyList([oldValue,newValue])));
        }
        Add("characterID",CorporationWire.Id(before?.CharacterId),CorporationWire.Id(after?.CharacterId));
        Add("corporationID",CorporationWire.Id(before?.CorporationId),CorporationWire.Id(after?.CorporationId));
        Add("startDate",before is null ? PyNone.Instance : CorporationWire.Time(before.StartedMs),
            after is null ? PyNone.Instance : CorporationWire.Time(after.StartedMs));
        Add("title",before is null ? PyNone.Instance : new PyWString(before.Title),
            after is null ? PyNone.Instance : new PyWString(after.Title));
        Add("divisionID",before is null ? PyNone.Instance : new PyInt(before.DivisionId),
            after is null ? PyNone.Instance : new PyInt(after.DivisionId));
        Add("squadronID",before is null ? PyNone.Instance : new PyInt(before.SquadronId),
            after is null ? PyNone.Instance : new PyInt(after.SquadronId));
        Add("roles",before is null ? PyNone.Instance : new PyLong(before.Masks.Roles),
            after is null ? PyNone.Instance : new PyLong(after.Masks.Roles));
        Add("grantableRoles",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableRoles),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableRoles));
        Add("rolesAtHQ",before is null ? PyNone.Instance : new PyLong(before.Masks.AtHq),
            after is null ? PyNone.Instance : new PyLong(after.Masks.AtHq));
        Add("grantableRolesAtHQ",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableHq),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableHq));
        Add("rolesAtBase",before is null ? PyNone.Instance : new PyLong(before.Masks.AtBase),
            after is null ? PyNone.Instance : new PyLong(after.Masks.AtBase));
        Add("grantableRolesAtBase",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableBase),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableBase));
        Add("rolesAtOther",before is null ? PyNone.Instance : new PyLong(before.Masks.AtOther),
            after is null ? PyNone.Instance : new PyLong(after.Masks.AtOther));
        Add("grantableRolesAtOther",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableOther),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableOther));
        Add("baseID",CorporationWire.Id(before?.BaseId),CorporationWire.Id(after?.BaseId));
        Add("titleMask",before is null ? PyNone.Instance : new PyInt(before.TitleMask),
            after is null ? PyNone.Instance : new PyInt(after.TitleMask));
        Add("blockRoles",before is null || !before.BlockRoles ? PyNone.Instance : new PyLong(1),
            after is null || !after.BlockRoles ? PyNone.Instance : new PyLong(1));
        Add("accountKey",before is null ? PyNone.Instance : new PyInt(before.AccountKey),
            after is null ? PyNone.Instance : new PyInt(after.AccountKey));
        return new PyDict(changes);
    }

    private static PyDict TitleChange(CorporationTitle? before,CorporationTitle? after)
    {
        var changes = new List<KeyValuePair<PyValue,PyValue>>();
        void Add(string name,PyValue oldValue,PyValue newValue)
        {
            if (oldValue.Equals(newValue)) return;
            changes.Add(new(new PyStr(name),new PyList([oldValue,newValue])));
        }
        Add("titleID",before is null ? PyNone.Instance : new PyInt(before.Id),
            after is null ? PyNone.Instance : new PyInt(after.Id));
        Add("titleName",before is null ? PyNone.Instance : new PyWString(before.Name),
            after is null ? PyNone.Instance : new PyWString(after.Name));
        Add("roles",before is null ? PyNone.Instance : new PyLong(before.Masks.Roles),
            after is null ? PyNone.Instance : new PyLong(after.Masks.Roles));
        Add("grantableRoles",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableRoles),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableRoles));
        Add("rolesAtHQ",before is null ? PyNone.Instance : new PyLong(before.Masks.AtHq),
            after is null ? PyNone.Instance : new PyLong(after.Masks.AtHq));
        Add("grantableRolesAtHQ",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableHq),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableHq));
        Add("rolesAtBase",before is null ? PyNone.Instance : new PyLong(before.Masks.AtBase),
            after is null ? PyNone.Instance : new PyLong(after.Masks.AtBase));
        Add("grantableRolesAtBase",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableBase),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableBase));
        Add("rolesAtOther",before is null ? PyNone.Instance : new PyLong(before.Masks.AtOther),
            after is null ? PyNone.Instance : new PyLong(after.Masks.AtOther));
        Add("grantableRolesAtOther",before is null ? PyNone.Instance : new PyLong(before.Masks.GrantableOther),
            after is null ? PyNone.Instance : new PyLong(after.Masks.GrantableOther));
        return new PyDict(changes);
    }

    private static void Send(ClientSession session,string name,string idType,PyValue payload)
    {
        try { session.SendNotification(name,idType,payload); }
        catch (Exception) { }
    }
}

// Wire behavior follows EveJS v0.12.9 standingMgrService.js / standingRuntime.js (828823a75f211fdecb945b087a26c225f111491f)
// and eve-logic V24.01 standingsvc.py (1dbb5711a072a146e1d228365668ac05655eefe1).
/// <summary>The client's standing manager, backed by shared affiliation and standing relations.</summary>
/// <summary>Read-only projection of war relations into the warsInfoMgr wire contract.</summary>
[Eve.Services.Service("warsInfoMgr")]
public sealed partial class WarsInfoMgrService
{
    private const string WarKind = "war";
    private const string WarAllyKind = "war.ally";
    private const string RowsetName = "carbon.common.script.sys.crowset.CRowset";
    private const long MaxSafeInteger = 9_007_199_254_740_991L;
    private const long FileTimeEpochThreshold = 100_000_000_000_000_000L;
    private static readonly (string Name, int Type)[] WarColumns =
    [
        ("warID", 0x03), ("declaredByID", 0x03), ("againstID", 0x03),
        ("timeDeclared", 0x40), ("timeFinished", 0x40), ("retracted", 0x40),
        ("retractedBy", 0x03), ("timeStarted", 0x40), ("billID", 0x03),
        ("mutual", 0x0b), ("createdFromWarID", 0x03), ("openForAllies", 0x0b),
        ("canBeRetracted", 0x0b), ("reasonEnded", 0x11), ("warHQ", 0x14),
        ("noOfAllies", 0x03), ("reasonStarted", 0x11)
    ];
    private static readonly System.Text.RegularExpressions.Regex PickledInt =
        new(@"(?:^|\n)I(-?\d+)\na", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex PickledLong =
        new(@"(?:^|\n)L(-?\d+)L\na", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly Eve.Relations.RelationStore _relations;
    private readonly Func<long> _nowUnixMs;

    public WarsInfoMgrService(Eve.Relations.RelationStore relations, Func<long> nowUnixMs)
    {
        _relations = relations;
        _nowUnixMs = nowUnixMs;
    }

    [Eve.Services.Method("GetWarsByOwnerID")]
    public async ValueTask<Eve.Marshal.PyValue> GetWarsByOwnerID(Eve.Services.ServiceCall call)
    {
        var ownerId = PositiveId(call.Arg(0)) ?? 0;
        var wars = await WarsAsync(call.Cancellation);
        WarRecord[] rows = ownerId == 0 ? [] : wars.Where(war => Owns(war, ownerId)).OrderBy(war => war.WarId).ToArray();
        return Eve.Services.ObjectCaching.MethodResult(Rowset(rows), _nowUnixMs(), "15 minutes");
    }

    [Eve.Services.Method("GetWarsByOwners")]
    public async ValueTask<Eve.Marshal.PyValue> GetWarsByOwners(Eve.Services.ServiceCall call)
    {
        var ownerIds = OwnerIds(call.Arg(0));
        var wars = await WarsAsync(call.Cancellation);
        return new Eve.Marshal.PyDict([.. ownerIds.Select(ownerId =>
            new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(
                Number(ownerId),
                new Eve.Marshal.PyDict([.. wars.Where(war => Owns(war, ownerId)).OrderBy(war => war.WarId)
                    .Select(war => new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(Number(war.WarId), Payload(war)))])))]);
    }

    [Eve.Services.Method("GetWarsRequiringAssistance")]
    public async ValueTask<Eve.Marshal.PyValue> GetWarsRequiringAssistance(Eve.Services.ServiceCall call)
    {
        var ownerId = IntegerValue(call.Arg(0));
        if (ownerId == 0)
        {
            ownerId = SessionOwnerId(call.Session);
        }
        if (ownerId == 0)
        {
            return new Eve.Marshal.PyList([]);
        }

        var wars = await WarsAsync(call.Cancellation);
        return new Eve.Marshal.PyList([.. wars
            .Where(war => war.AgainstId == ownerId && war.OpenForAllies)
            .OrderBy(war => war.WarId)
            .Select(Payload)]);
    }

    [Eve.Services.Method("GetTop50")]
    public async ValueTask<Eve.Marshal.PyValue> GetTop50(Eve.Services.ServiceCall call)
    {
        var maximum = IntegerValue(call.Arg(0));
        if (maximum == 0)
        {
            maximum = MaxSafeInteger;
        }
        var wars = await WarsAsync(call.Cancellation);
        var rows = wars.Where(war => war.WarId < maximum).OrderByDescending(war => war.WarId).Take(50).ToArray();
        return Eve.Services.ObjectCaching.MethodResult(Rowset(rows), _nowUnixMs(), "15 minutes");
    }

    [Eve.Services.Method("GetPublicWarInfo")]
    public async ValueTask<Eve.Marshal.PyValue> GetPublicWarInfo(Eve.Services.ServiceCall call)
    {
        var warId = PositiveId(call.Arg(0)) ?? 0;
        if (warId == 0)
        {
            return Eve.Marshal.PyNone.Instance;
        }

        var wars = await WarsAsync(call.Cancellation);
        var war = wars.FirstOrDefault(candidate => candidate.WarId == warId);
        return war is null ? Eve.Marshal.PyNone.Instance : Payload(war);
    }

    [Eve.Services.Method("GetWarsForStructure")]
    public async ValueTask<Eve.Marshal.PyValue> GetWarsForStructure(Eve.Services.ServiceCall call)
    {
        var structureId = PositiveId(call.Arg(0)) ?? 0;
        if (structureId == 0)
        {
            return new Eve.Marshal.PyList([]);
        }

        var wars = await WarsAsync(call.Cancellation);
        return new Eve.Marshal.PyList([.. wars.Where(war => war.WarHqId == structureId)
            .OrderByDescending(war => war.TimeDeclared)
            .ThenBy(war => war.WarId)
            .Select(Payload)]);
    }

    private async Task<IReadOnlyList<WarRecord>> WarsAsync(CancellationToken token)
    {
        var warsTask = _relations.ListAsync(WarKind, token);
        var alliesTask = _relations.ListAsync(WarAllyKind, token);
        await Task.WhenAll(warsTask, alliesTask);
        var allies = alliesTask.Result.GroupBy(row => row.Object)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Subject).ToArray());
        return [.. warsTask.Result.Select(war =>
        {
            allies.TryGetValue(war.Id, out var warAllies);
            return Record(war, warAllies ?? []);
        })];
    }

    private static WarRecord Record(Eve.Relations.Relation relation, IReadOnlyList<Eve.Relations.Relation> allies)
    {
        using var document = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(relation.AttrsJson) ? "{}" : relation.AttrsJson);
        var attrs = document.RootElement.Clone();
        return new WarRecord(
            relation.Id, relation.Subject, relation.Object,
            FileTime(attrs, "timeDeclared", relation.SinceMs),
            FileTime(attrs, "timeStarted", relation.SinceMs),
            OptionalFileTime(attrs, "timeFinished", relation.UntilMs),
            OptionalFileTime(attrs, "retracted", null),
            OptionalLong(attrs, "retractedBy"),
            OptionalLong(attrs, "billID"),
            Flag(attrs, "mutual"), Flag(attrs, "openForAllies"),
            OptionalLong(attrs, "createdFromWarID"),
            OptionalLong(attrs, "warHQID"),
            OptionalLong(attrs, "warHQ"),
            Integer(attrs, "reasonEnded"), Integer(attrs, "reasonStarted"),
            Number(attrs, "reward"),
            [.. allies.Select(ReadAlly)]);
    }

    private static AllyInfo ReadAlly(Eve.Relations.Relation relation)
    {
        using var document = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(relation.AttrsJson) ? "{}" : relation.AttrsJson);
        var attrs = document.RootElement.Clone();
        return new AllyInfo(relation.Subject, FileTime(attrs, "timeStarted", relation.SinceMs),
            OptionalFileTime(attrs, "timeFinished", relation.UntilMs));
    }

    private static bool Owns(WarRecord war, long id) =>
        war.DeclaredById == id || war.AgainstId == id || war.Allies.Any(ally => ally.AllyId == id);

    private static Eve.Marshal.PyValue Payload(WarRecord war)
    {
        var allies = new Eve.Marshal.PyDict([.. war.Allies.Select(ally =>
            new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(
                Number(ally.AllyId),
                Eve.Services.Py.KeyVal(
                    ("allyID", Number(ally.AllyId)),
                    ("timeStarted", Eve.Services.Py.FileTimeLong(ally.TimeStarted)),
                    ("timeFinished", Time(ally.TimeFinished)))))]);

        return Eve.Services.Py.KeyVal(
            ("warID", Number(war.WarId)),
            ("declaredByID", Number(war.DeclaredById)),
            ("againstID", Number(war.AgainstId)),
            ("warHQID", OptionalNumber(war.WarHqId)),
            ("warHQ", OptionalNumber(war.WarHqId)),
            ("timeDeclared", Eve.Services.Py.FileTimeLong(war.TimeDeclared)),
            ("timeStarted", Eve.Services.Py.FileTimeLong(war.TimeStarted)),
            ("timeFinished", Time(war.TimeFinished)),
            ("retracted", Time(war.Retracted)),
            ("retractedBy", OptionalNumber(war.RetractedBy)),
            ("billID", OptionalNumber(war.BillId)),
            ("mutual", new Eve.Marshal.PyInt(war.Mutual ? 1 : 0)),
            ("openForAllies", new Eve.Marshal.PyInt(war.OpenForAllies ? 1 : 0)),
            ("createdFromWarID", OptionalNumber(war.CreatedFromWarId)),
            ("reward", Eve.Services.Py.Number(war.Reward)),
            ("allies", allies));
    }

    private static Eve.Marshal.PyValue Rowset(IReadOnlyList<WarRecord> wars)
    {
        var descriptor = Eve.Marshal.Descriptor.FromHeader(new Eve.Marshal.PyObjectEx(
            Eve.Marshal.ObjectExKind.Ex1,
            Eve.Services.Py.Tuple(new Eve.Marshal.PyToken("blue.DBRowDescriptor"),
                Eve.Services.Py.Tuple(Eve.Services.Py.Tuple([.. WarColumns.Select(column =>
                    (Eve.Marshal.PyValue)Eve.Services.Py.Tuple(new Eve.Marshal.PyStr(column.Name), new Eve.Marshal.PyInt(column.Type)))]))),
            [], []));
        var header = Eve.Services.Py.Tuple(
            Eve.Services.Py.Tuple(new Eve.Marshal.PyToken(RowsetName)),
            Eve.Services.Py.Dict(("header", descriptor.Header)));
        var rows = wars.Select(war => (Eve.Marshal.PyValue)new Eve.Marshal.PyPackedRow(descriptor,
        [
            Number(war.WarId), Number(war.DeclaredById), Number(war.AgainstId),
            Eve.Services.Py.FileTimeLong(war.TimeDeclared), Time(war.TimeFinished), Time(war.Retracted),
            OptionalNumber(war.RetractedBy), Eve.Services.Py.FileTimeLong(war.TimeStarted), OptionalNumber(war.BillId),
            new Eve.Marshal.PyInt(war.Mutual ? 1 : 0), OptionalNumber(war.CreatedFromWarId),
            new Eve.Marshal.PyInt(war.OpenForAllies ? 1 : 0),
            new Eve.Marshal.PyInt(war.TimeFinished is null && war.Retracted is null ? 1 : 0),
            new Eve.Marshal.PyInt(war.ReasonEnded), OptionalNumber(war.WarHqId ?? war.WarHq),
            new Eve.Marshal.PyInt(war.Allies.Count), new Eve.Marshal.PyInt(war.ReasonStarted)
        ]));
        return new Eve.Marshal.PyObjectEx(Eve.Marshal.ObjectExKind.Ex2, header, [.. rows], []);
    }

    private static long SessionOwnerId(Eve.Services.ClientSession session) =>
        PositiveId(session.Get("allianceID")) ?? PositiveId(session.Get("allianceid")) ??
        PositiveId(session.Get("corporationID")) ?? PositiveId(session.Get("corpid")) ?? 0;

    private static long? PositiveId(Eve.Marshal.PyValue? value)
    {
        var id = IntegerValue(value);
        return id > 0 ? id : null;
    }

    private static long IntegerValue(Eve.Marshal.PyValue? value) => value switch
    {
        Eve.Marshal.PyInt integer => integer.Value,
        Eve.Marshal.PyLong integer => integer.Value,
        Eve.Marshal.PyBool boolean => boolean.Value ? 1 : 0,
        Eve.Marshal.PyReal number when double.IsFinite(number.Value) && number.Value >= long.MinValue && number.Value <= long.MaxValue => (long)number.Value,
        Eve.Marshal.PyStr text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        Eve.Marshal.PyWString text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        Eve.Marshal.PyToken text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0
    };

    private static long[] OwnerIds(Eve.Marshal.PyValue raw)
    {
        IEnumerable<Eve.Marshal.PyValue> values = raw switch
        {
            Eve.Marshal.PyList list => list.Items,
            Eve.Marshal.PyTuple tuple => tuple.Items,
            Eve.Marshal.PyObjectEx { Kind: Eve.Marshal.ObjectExKind.Ex1, Header: Eve.Marshal.PyTuple header }
                when header.Items.Count > 1 && IsSetName(header.Items[0]) =>
                    header.Items[1] is Eve.Marshal.PyTuple args && args.Items.Count > 0 ? Sequence(args.Items[0]) : [],
            Eve.Marshal.PyCPickled pickle => PickledOwnerIds(pickle),
            _ => []
        };
        return [.. values.Select(IntegerValue).Where(id => id != 0).Distinct()];
    }

    private static bool IsSetName(Eve.Marshal.PyValue value) =>
        value is Eve.Marshal.PyToken token && token.Value is "__builtin__.set" or "builtins.set";

    private static IEnumerable<Eve.Marshal.PyValue> Sequence(Eve.Marshal.PyValue value) => value switch
    {
        Eve.Marshal.PyList list => list.Items,
        Eve.Marshal.PyTuple tuple => tuple.Items,
        _ => []
    };

    private static IEnumerable<Eve.Marshal.PyValue> PickledOwnerIds(Eve.Marshal.PyCPickled pickle)
    {
        var text = System.Text.Encoding.ASCII.GetString(pickle.Value.Span);
        if (!text.Contains("set\n", StringComparison.Ordinal) ||
            (!text.Contains("c__builtin__\nset\n", StringComparison.Ordinal) &&
             !text.Contains("cbuiltins\nset\n", StringComparison.Ordinal)))
        {
            return [];
        }

        var values = new List<Eve.Marshal.PyValue>();
        foreach (System.Text.RegularExpressions.Match match in PickledInt.Matches(text))
        {
            if (long.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                values.Add(new Eve.Marshal.PyLong(value));
            }
        }
        foreach (System.Text.RegularExpressions.Match match in PickledLong.Matches(text))
        {
            if (long.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                values.Add(new Eve.Marshal.PyLong(value));
            }
        }
        return values;
    }

    private static Eve.Marshal.PyValue Number(long value) => Eve.Services.Py.Number(value);
    private static Eve.Marshal.PyValue OptionalNumber(long? value) => value is { } id ? Number(id) : Eve.Marshal.PyNone.Instance;
    private static Eve.Marshal.PyValue Time(long? value) => value is { } filetime ? Eve.Services.Py.FileTimeLong(filetime) : Eve.Marshal.PyNone.Instance;

    private static long FileTime(System.Text.Json.JsonElement attrs, string key, long fallbackUnixMs) =>
        TryLong(attrs, key, out var value) ? NormalizeTime(value) : Eve.Services.Py.FileTimeFromUnixMs(fallbackUnixMs);

    private static long? OptionalFileTime(System.Text.Json.JsonElement attrs, string key, long? fallbackUnixMs)
    {
        if (TryLong(attrs, key, out var value))
        {
            return value == 0 ? null : NormalizeTime(value);
        }
        return fallbackUnixMs is { } unixMs ? Eve.Services.Py.FileTimeFromUnixMs(unixMs) : null;
    }

    private static long NormalizeTime(long value) =>
        value == 0 || value >= FileTimeEpochThreshold ? value : Eve.Services.Py.FileTimeFromUnixMs(value);

    private static long? OptionalLong(System.Text.Json.JsonElement attrs, string key) =>
        TryLong(attrs, key, out var value) && value != 0 ? value : null;

    private static int Integer(System.Text.Json.JsonElement attrs, string key) =>
        TryLong(attrs, key, out var value) && value is >= int.MinValue and <= int.MaxValue ? (int)value : 0;

    private static double Number(System.Text.Json.JsonElement attrs, string key)
    {
        if (TryProperty(attrs, key, out var value))
        {
            if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
            {
                return number;
            }
            if (value.ValueKind == System.Text.Json.JsonValueKind.String &&
                double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) &&
                double.IsFinite(number))
            {
                return number;
            }
        }
        return 0;
    }

    private static bool Flag(System.Text.Json.JsonElement attrs, string key)
    {
        if (!TryProperty(attrs, key, out var value))
        {
            return false;
        }
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.Null => false,
            System.Text.Json.JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0,
            System.Text.Json.JsonValueKind.String => bool.TryParse(value.GetString(), out var boolean)
                ? boolean
                : long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var integer) && integer != 0,
            _ => false
        };
    }

    private static bool TryLong(System.Text.Json.JsonElement attrs, string key, out long value)
    {
        if (TryProperty(attrs, key, out var element))
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetInt64(out value))
            {
                return true;
            }
            if (element.ValueKind == System.Text.Json.JsonValueKind.String &&
                long.TryParse(element.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static bool TryProperty(System.Text.Json.JsonElement attrs, string key, out System.Text.Json.JsonElement value)
    {
        if (attrs.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in attrs.EnumerateObject())
            {
                if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private sealed record WarRecord(long WarId, long DeclaredById, long AgainstId, long TimeDeclared,
        long TimeStarted, long? TimeFinished, long? Retracted, long? RetractedBy, long? BillId,
        bool Mutual, bool OpenForAllies, long? CreatedFromWarId, long? WarHqId, long? WarHq,
        int ReasonEnded, int ReasonStarted, double Reward, IReadOnlyList<AllyInfo> Allies);

    private sealed record AllyInfo(long AllyId, long TimeStarted, long? TimeFinished);
}

[Service("standingMgr")]
public sealed partial class StandingMgrService
{
    private const string NpcNpcQuery = "standing.npcNpc";
    private const string CharacterQuery = "standing.character";
    private const string CorporationQuery = "standing.corporation";
    private const string TransactionQuery = "standing.transactions";
    private const string CompositionQuery = "standing.compositions";
    private readonly Eve.ReadSide.ReadSide _reads;
    private readonly Eve.Corporations.CorporationStore _corporations;
    private readonly Func<long> _nowUnixMs;

    public StandingMgrService(Eve.ReadSide.ReadSide reads, Eve.Corporations.CorporationStore corporations, Func<long> nowUnixMs)
    {
        _reads = reads;
        _corporations = corporations;
        _nowUnixMs = nowUnixMs;

        reads.Register(new Eve.ReadSide.RowsetQuery(
            NpcNpcQuery,
            """
            (
                WITH npc AS (
                    SELECT key::bigint AS id,
                           NULLIF(doc->>'factionID', '')::bigint AS faction_id,
                           NULLIF(doc->>'enemyID', '')::bigint AS enemy_id,
                           NULLIF(doc->>'friendID', '')::bigint AS friend_id
                    FROM {sde}."npcCorporations"
                ),
                factions AS (
                    SELECT key::bigint AS id,
                           NULLIF(doc->>'corporationID', '')::bigint AS corporation_id
                    FROM {sde}.factions
                ),
                relations AS (
                    SELECT faction_id AS from_id, id AS to_id, 2::double precision AS standing
                    FROM npc WHERE faction_id IS NOT NULL
                    UNION ALL
                    SELECT id, faction_id, 2::double precision
                    FROM npc WHERE faction_id IS NOT NULL
                    UNION ALL
                    SELECT id, enemy_id, -1::double precision
                    FROM npc WHERE enemy_id IS NOT NULL
                    UNION ALL
                    SELECT id, friend_id, 1::double precision
                    FROM npc WHERE friend_id IS NOT NULL
                    UNION ALL
                    SELECT faction.id, enemy.faction_id, -1::double precision
                    FROM factions faction
                    JOIN npc executive ON executive.id = faction.corporation_id
                    JOIN npc enemy ON enemy.id = executive.enemy_id
                    WHERE enemy.faction_id IS NOT NULL AND enemy.faction_id <> faction.id
                    UNION ALL
                    SELECT faction.id, friend.faction_id, 1::double precision
                    FROM factions faction
                    JOIN npc executive ON executive.id = faction.corporation_id
                    JOIN npc friend ON friend.id = executive.friend_id
                    WHERE friend.faction_id IS NOT NULL AND friend.faction_id <> faction.id
                )
                SELECT from_id, to_id, max(standing) AS standing
                FROM relations
                WHERE from_id <> to_id
                GROUP BY from_id, to_id
            ) npc_standings
            """,
            [
                Eve.ReadSide.Column.Of("fromID", Eve.ReadSide.ColumnType.Int, "from_id"),
                Eve.ReadSide.Column.Of("toID", Eve.ReadSide.ColumnType.Int, "to_id"),
                Eve.ReadSide.Column.Of("standing", Eve.ReadSide.ColumnType.Real, "standing"),
            ])
        {
            OrderBy = "fromID, toID",
            Cache = true,
        });
        reads.Register(new Eve.ReadSide.RowsetQuery(
            CharacterQuery,
            "{relations}.relations",
            [
                Eve.ReadSide.Column.Of("fromID", Eve.ReadSide.ColumnType.Int, "subject"),
                Eve.ReadSide.Column.Of("standing", Eve.ReadSide.ColumnType.Real, "value"),
            ])
        {
            Where = "kind = 'standing' AND object = $1",
            OrderBy = "fromID",
        });
        reads.Register(new Eve.ReadSide.RowsetQuery(
            CorporationQuery,
            """
            (
                WITH direct AS (
                    SELECT subject AS from_id, floor(value * 1000 + 0.5) / 1000 AS standing
                    FROM {relations}.relations
                    WHERE kind = 'standing' AND object = $1
                ),
                member_average AS (
                    SELECT standing.subject AS from_id,
                           floor(avg(standing.value) * 1000 + 0.5) / 1000 AS standing
                    FROM {relations}.relations member
                    JOIN {relations}.relations standing
                      ON standing.kind = 'standing' AND standing.object = member.subject
                    WHERE member.kind = 'affiliation.corporation' AND member.object = $1
                    GROUP BY standing.subject
                )
                SELECT from_id, standing FROM direct
                UNION ALL
                SELECT from_id, standing FROM member_average
                WHERE NOT EXISTS (SELECT 1 FROM direct)
            ) standings
            """,
            [
                Eve.ReadSide.Column.Of("fromID", Eve.ReadSide.ColumnType.Int, "from_id"),
                Eve.ReadSide.Column.Of("standing", Eve.ReadSide.ColumnType.Real, "standing"),
            ])
        {
            OrderBy = "fromID",
        });
        reads.Register(new Eve.ReadSide.RowsetQuery(
            TransactionQuery,
            "{relations}.relation_history",
            [
                Eve.ReadSide.Column.Of("atMs", Eve.ReadSide.ColumnType.Long, "at_ms"),
                Eve.ReadSide.Column.Of("oldValue", Eve.ReadSide.ColumnType.Real, "old_value"),
                Eve.ReadSide.Column.Of("newValue", Eve.ReadSide.ColumnType.Real, "new_value"),
                Eve.ReadSide.Column.Of("reason", Eve.ReadSide.ColumnType.Text, "reason"),
            ])
        {
            Where = "kind = 'standing' AND subject = $1 AND object = $2",
            OrderBy = "at_ms DESC, id DESC",
            Limit = 512,
        });
        reads.Register(new Eve.ReadSide.RowsetQuery(
            CompositionQuery,
            """
            {relations}.relations member
            JOIN {relations}.relations standing
              ON standing.kind = 'standing'
             AND standing.subject = $1
             AND standing.object = member.subject
            """,
            [
                Eve.ReadSide.Column.Of("standing", Eve.ReadSide.ColumnType.Real,
                    "floor(standing.value * 1000 + 0.5) / 1000"),
                Eve.ReadSide.Column.Of("ownerID", Eve.ReadSide.ColumnType.Int, "member.subject"),
            ])
        {
            Where = "member.kind = 'affiliation.corporation' AND member.object = $2",
            OrderBy = "standing DESC, ownerID",
        });
    }

    [Method("GetNPCNPCStandings")]
    public async ValueTask<PyValue> GetNPCNPCStandings(ServiceCall call)
    {
        var rows = await _reads.RowsAsync(NpcNpcQuery, cancellationToken: call.Cancellation);
        return Rowset(rows);
    }

    [Method("GetCharStandings")]
    public async ValueTask<PyValue> GetCharStandings(ServiceCall call)
    {
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        if (characterId == 0)
        {
            characterId = call.Session.GetInt("characterID") ?? 0;
        }
        var rows = await _reads.RowsAsync(CharacterQuery, [characterId], call.Cancellation);
        return Rowset(rows);
    }

    [Method("GetCorpStandings")]
    public async ValueTask<PyValue> GetCorpStandings(ServiceCall call)
    {
        var corporationId = call.Session.GetInt("corporationID") ?? call.Session.GetInt("corpid") ?? 0;
        var rows = await _reads.RowsAsync(CorporationQuery, [corporationId], call.Cancellation);
        return Rowset(rows);
    }

    [Method("GetStandingTransactions")]
    public async ValueTask<PyValue> GetStandingTransactions(ServiceCall call)
    {
        var fromId = PositiveId(call.Arg(0));
        var toId = PositiveId(call.Arg(1));
        if (fromId == 0 || toId == 0)
        {
            return new PyList([]);
        }
        var rows = await _reads.RowsAsync(TransactionQuery, [fromId, toId], call.Cancellation);
        var transactions = new List<PyValue>(rows.Count);
        foreach (var row in rows.Rows)
        {
            if (row[3] is not string reason ||
                !TryStandingTransaction(reason, Convert.ToInt64(row[0]), row[1], row[2], fromId, toId, out var transaction))
            {
                continue;
            }
            transactions.Add(transaction);
        }
        return new PyList(transactions);
    }

    [Method("GetStandingCompositions")]
    public async ValueTask<PyValue> GetStandingCompositions(ServiceCall call)
    {
        var fromId = PositiveId(call.Arg(0));
        var corporationId = PositiveId(call.Arg(1));
        if (fromId == 0 || corporationId == 0 ||
            await _corporations.GetAsync(corporationId, call.Cancellation) is not { IsNpc: false })
        {
            return EmptyCompositionRowset();
        }
        var rows = await _reads.RowsAsync(CompositionQuery, [fromId, corporationId], call.Cancellation);
        return ObjectCaching.MethodResult(rows.ToCRowset(), _nowUnixMs(), "5 minutes", "corpid");
    }

    private static PyValue Rowset(Eve.ReadSide.RowSet rows) => new PyObject(
        new PyStr("eve.common.script.sys.rowset.Rowset"),
        Py.Dict(
            ("header", new PyList([.. rows.Columns.Select(column => (PyValue)new PyStr(column.Name))])),
            ("RowClass", new PyToken("util.Row")),
            ("lines", new PyList([.. rows.Rows.Select(row => (PyValue)new PyList(rows.Values(row)))]))));

    private static PyValue EmptyCompositionRowset() => new PyObject(
        new PyStr("carbon.common.script.sys.crowset.CRowset"),
        Py.Dict(
            ("header", Eve.Marshal.Descriptor.FromHeader(new PyObjectEx(
                ObjectExKind.Ex1,
                Py.Tuple(new PyToken("blue.DBRowDescriptor"), Py.Tuple(
                    Py.Tuple(Py.Tuple(new PyStr("standing"), new PyInt(5))),
                    Py.Tuple(Py.Tuple(new PyStr("ownerID"), new PyInt(3))))),
                [],
                [])).Header),
            ("RowClass", new PyToken("util.Row")),
            ("lines", new PyList([]))));

    private static long PositiveId(PyValue value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value > 0 && number.Value <= long.MaxValue => (long)number.Value,
        _ => 0,
    };

    private static bool TryStandingTransaction(
        string reason,
        long atMs,
        object? oldValue,
        object? newValue,
        long fromId,
        long toId,
        out PyValue transaction)
    {
        transaction = PyNone.Instance;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(reason);
            var root = document.RootElement;
            if (!root.TryGetProperty("eventTypeID", out var eventTypeValue) ||
                !eventTypeValue.TryGetInt32(out var eventTypeId) ||
                eventTypeId <= 0)
            {
                return false;
            }
            var oldNumber = oldValue is null ? 0d : Convert.ToDouble(oldValue);
            var newNumber = newValue is null ? 0d : Convert.ToDouble(newValue);
            var modification = JsonNumber(root, "modification") ?? newNumber - oldNumber;
            var eventDateTime = JsonLong(root, "eventDateTime") ?? Py.FileTimeFromUnixMs(atMs);
            var message = root.TryGetProperty("msg", out var msg) && msg.ValueKind == System.Text.Json.JsonValueKind.String
                ? msg.GetString() ?? ""
                : "";
            transaction = Py.KeyVal(
                ("eventTypeID", new PyInt(eventTypeId)),
                ("eventDateTime", new PyLong(eventDateTime)),
                ("modification", Py.Number(modification)),
                ("fromID", new PyInt(fromId)),
                ("toID", new PyInt(toId)),
                ("msg", new PyWString(message)),
                ("int_1", JsonLong(root, "int_1") is { } first ? new PyInt(first) : PyNone.Instance),
                ("int_2", JsonLong(root, "int_2") is { } second ? new PyInt(second) : PyNone.Instance),
                ("int_3", JsonLong(root, "int_3") is { } third ? new PyInt(third) : PyNone.Instance));
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static double? JsonNumber(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.Number &&
        value.TryGetDouble(out var number) &&
        double.IsFinite(number)
            ? number
            : null;

    private static long? JsonLong(System.Text.Json.JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }
        return value.ValueKind == System.Text.Json.JsonValueKind.String &&
               long.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }
}
