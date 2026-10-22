using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Corporations;

/// <summary>Client contract for station corporation offices and their impounded assets.</summary>
[Service("officeManager")]
public sealed partial class OfficeManagerService(
    CorporationOfficeStore store,
    CorporationStore corporations,
    PgCharacterStore characters,
    Eve.Inventory.PgItemStore items,
    Eve.Wallet.WalletTransactions wallets,
    Func<long,CorporationOfficeStation?> readStation,
    Func<long> now,
    IStructureOffices? structures = null)
{
    private const long RentOfficeRole = CorporationRoles.RentOffice;
    // The SDE inventory flag for the station's Impounded location.
    private const int ImpoundedFlag = CorporationOfficeStore.ImpoundedFlag;
    private const int OfficeRentReferenceType = 13;
    private const int ImpoundReleaseReferenceType = 41;
    private const long EveDayMs = 24L * 60 * 60 * 1000;

    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var stationId = StationParameter(call.Arg(0));
        if (stationId is not > 0) throw new CorporationError("OFFICE_STATION_REQUIRED");
        if (readStation(stationId.Value) is null && await StructureAtAsync(stationId.Value, call.Cancellation) is null)
            throw new CorporationError("OFFICE_STATION_NOT_FOUND");

        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        var bound = new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(now())))));
        PyValue result = PyNone.Instance;
        var nestedItems = call.Arg(1) switch
        {
            PyTuple nested => nested.Items,
            PyList nested => nested.Items,
            _ => Array.Empty<PyValue>(),
        };
        if (nestedItems.Count > 0 && JsSemantics.StringValue(nestedItems[0]) is { } method)
        {
            IReadOnlyList<PyValue> nestedArgs = Array.Empty<PyValue>();
            if (nestedItems.Count > 1)
            {
                nestedArgs = nestedItems[1] switch
                {
                    PyTuple args => args.Items,
                    PyList args => args.Items,
                    PyNone => Array.Empty<PyValue>(),
                    var argument => new[] { argument },
                };
            }
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = nestedArgs,
                Kwargs = nestedItems.Count > 2 ? nestedItems[2] : PyNone.Instance,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                BoundObjectParameters = call.Arg(0),
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(bound, result);
    }

    [Method("GetMyCorporationsOffices")]
    public async ValueTask<PyValue> GetMyCorporationsOffices(ServiceCall call)
    {
        var corporationId = await CurrentCorporationIdAsync(call);
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
        if (await StructureSiteAsync(call) is { } site)
            return new PyReal((double)await structures!.RentForAsync(call.Session.CharacterId ?? 0, site.StructureId, call.Cancellation));
        var station = LocalStation(call);
        var price = await QuoteAsync(station, call.Cancellation);
        return new PyReal((double)price);
    }

    [Method("RentOffice")]
    public async ValueTask<PyValue> RentOffice(ServiceCall call)
    {
        var actor = RequireActor(call.Session);
        var corporationId = SessionCorporationId(call.Session);
        if (await StructureSiteAsync(call) is { } structure) return await RentStructureOfficeAsync(call, actor, corporationId, structure);
        var station = LocalStation(call);
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
                call.Cancellation, creditorId: station.OwnerId);
            await wallets.AdjustAsync(connection, transaction, new Eve.Wallet.WalletAccount(corporationId), -price,
                station.OwnerId, OfficeRentReferenceType, "Corporation office rental", atStation,
                $"office-rent:{office.OfficeId}:charge", "office_rental_fee", call.Cancellation);

            await wallets.EnsureAsync(connection, transaction, station.OwnerId, call.Cancellation);
            await wallets.AdjustAsync(connection, transaction, new Eve.Wallet.WalletAccount(station.OwnerId), price,
                corporationId, OfficeRentReferenceType, "Corporation office rental", atStation,
                $"office-rent:{office.OfficeId}:credit", "office_rental_fee", call.Cancellation);
            if (structures is not null)
                await structures.ScheduleRentDueAsync(connection, transaction, office.OfficeId, office.ExpiresAtMs, call.Cancellation);

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
        if (await StructureSiteAsync(call) is { } structure) return await UnrentStructureOfficeAsync(call, actor, corporationId, structure);
        var station = BoundStation(call);
        var authority = await RequireAuthorityAsync(actor, corporationId, station.Id, call.Cancellation);
        if (!IsCeoOrDirector(actor, authority))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var atStation = now();
        await store.WriteAsync(async (connection, transaction) =>
        {
            await store.LockStationAsync(connection, transaction, station.Id, call.Cancellation);
            var office = await store.GetAsync(connection, transaction, corporationId, station.Id,
                forUpdate: true, cancellationToken: call.Cancellation);
            if (office is null) return 0;

            await StationOfficeImpound.ImpoundAsync(items, store, connection, transaction, office, atStation, call.Cancellation);
            return 0;
        }, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetItemsFromImpound")]
    public async ValueTask<PyValue> GetItemsFromImpound(ServiceCall call)
    {
        if (await StructureSiteAsync(call) is not null) return PyNone.Instance;
        var actor = RequireActor(call.Session);
        var corporationId = SessionCorporationId(call.Session);
        var station = LocalStation(call);
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
            await wallets.EnsureAsync(connection, transaction, station.OwnerId, call.Cancellation);
            await wallets.AdjustAsync(connection, transaction, new Eve.Wallet.WalletAccount(station.OwnerId), fee,
                corporationId, ImpoundReleaseReferenceType, "Corporation impound release", atStation,
                $"office-impound-release:{corporationId}:{station.Id}:{atStation}:credit",
                "office_impound_release", call.Cancellation);
            foreach (var item in impounded)
                await MoveIfCurrentAsync(connection, transaction, item, corporationId, station.Id,
                    ImpoundedFlag, Eve.Inventory.ItemFlags.CorpSAG1, call.Cancellation);
            return 0;
        }, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetCorporationsWithOffices")]
    public async ValueTask<PyValue> GetCorporationsWithOffices(ServiceCall call)
    {
        var stationId = call.BoundObjectId is null
            ? call.Session.GetInt("stationid") ?? call.Session.GetInt("structureid")
            : StationParameter(call.BoundObjectParameters) ??
              throw new CorporationError("OFFICE_STATION_REQUIRED");
        if (stationId is not > 0) return PyValue.PythonSet([]);
        var ids = await store.CorporationsAtStationAsync(stationId.Value, call.Cancellation);
        return PyValue.PythonSet(ids.Select(id => (PyValue)new PyLong(id)));
    }

    [Method("GetEmptyOfficeCount")]
    public async ValueTask<PyValue> GetEmptyOfficeCount(ServiceCall call)
    {
        if (call.Session.GetInt("structureid") is > 0) return PyNone.Instance;
        var station = BoundStation(call);
        var occupied = await store.WriteAsync((connection, transaction) =>
            store.CountAtStationAsync(connection, transaction, station.Id, call.Cancellation), call.Cancellation);
        return new PyInt(Math.Max(0, station.OfficeCapacity - occupied));
    }

    [Method("HasCorpImpoundedItems")]
    public async ValueTask<PyValue> HasCorpImpoundedItems(ServiceCall call)
    {
        if (await StructureSiteAsync(call) is not null) return PyBool.False; // a structure office is never impounded (EVE.js)
        var corporationId = await CurrentCorporationIdAsync(call);
        if (corporationId <= 0) return PyBool.False;
        var corporation = await corporations.GetAsync(corporationId, call.Cancellation);
        if (corporation is null || corporation.IsNpc) return PyBool.False;
        var station = BoundStation(call);
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

    [Method("GetImpoundReleasePrice")]
    public async ValueTask<PyValue> GetImpoundReleasePrice(ServiceCall call)
    {
        if (await StructureSiteAsync(call) is not null) return new PyInt(0);
        var corporationId = await CurrentCorporationIdAsync(call);
        if (corporationId <= 0) return new PyInt(0);
        var corporation = await corporations.GetAsync(corporationId, call.Cancellation);
        if (corporation is null || corporation.IsNpc) return new PyInt(0);
        var station = BoundStation(call);
        if (await store.WriteAsync(async (connection, transaction) =>
        {
            var office = await store.GetAsync(connection, transaction, corporationId, station.Id,
                cancellationToken: call.Cancellation);
            return office is not null;
        }, call.Cancellation))
            return new PyInt(0);
        var contents = await items.InLocationAsync(station.Id, corporationId, call.Cancellation);
        if (!contents.Any(item => item.Flag == ImpoundedFlag)) return new PyInt(0);
        var rent = await QuoteAsync(station, call.Cancellation);
        return new PyReal((double)decimal.Round(rent / 2m, 2, MidpointRounding.AwayFromZero));
    }

    [Method("TrashImpoundedOffice")]
    public async ValueTask<PyValue> TrashImpoundedOffice(ServiceCall call)
    {
        if (await StructureSiteAsync(call) is not null) return PyNone.Instance;
        var actor = RequireActor(call.Session);
        var corporationId = SessionCorporationId(call.Session);
        var station = BoundStation(call);
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

    [Method("PrimeOfficeItem")]
    public ValueTask<PyValue> PrimeOfficeItem(ServiceCall call) =>
        ValueTask.FromResult<PyValue>(PyNone.Instance);

    private async Task<long> CurrentCorporationIdAsync(ServiceCall call)
    {
        var corporationId = SessionCorporationId(call.Session);
        if (corporationId <= 0) return 0;
        var corporation = await corporations.GetAsync(corporationId, call.Cancellation);
        if (corporation is null) return 0;
        if (corporation.IsNpc) return corporationId;
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        if (characterId <= 0 || await corporations.MemberAsync(characterId, call.Cancellation) is not { } member ||
            member.CorporationId != corporationId)
            return 0;
        return corporationId;
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
        await StationOfficeImpound.MoveIfCurrentAsync(items, connection, transaction, snapshot, corporationId, stationId, expectedFlag, destinationFlag, token);
    }

    private CorporationOfficeStation BoundStation(ServiceCall call)
    {
        if (call.BoundObjectId is null) return CurrentStation(call.Session);
        var stationId = StationParameter(call.BoundObjectParameters);
        if (stationId is not > 0) throw new CorporationError("OFFICE_STATION_REQUIRED");
        return readStation(stationId.Value) ?? throw new CorporationError("OFFICE_STATION_NOT_FOUND");
    }

    private CorporationOfficeStation LocalStation(ServiceCall call)
    {
        var station = CurrentStation(call.Session);
        if (call.BoundObjectId is not null &&
            StationParameter(call.BoundObjectParameters) != station.Id)
            throw new CorporationError("OFFICE_STATION_REQUIRED");
        return station;
    }

    private static long? StationParameter(PyValue? parameter)
    {
        var value = parameter switch
        {
            PyTuple { Items.Count: > 0 } tuple => tuple.Items[0],
            PyList { Items.Count: > 0 } list => list.Items[0],
            _ => parameter,
        };
        return value is null ? null : Integral(value);
    }

    /// <summary>
    /// A corporation office at a structure (EVE.js RentOffice, structure branch): the Corporation Offices service and its access, the profile's rent charged to the
    /// </summary>
    private async Task<PyValue> RentStructureOfficeAsync(ServiceCall call, long actor, long corporationId, StructureOfficeSite site)
    {
        var authority = await RequireAuthorityAsync(actor, corporationId, site.StructureId, call.Cancellation);
        if ((authority.Roles.AtAll & RentOfficeRole) == 0)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (!await structures!.MayRentAsync(actor, site.StructureId, call.Cancellation))
            throw new CorporationError("StructureCorpOfficesDenied");
        var rent = await structures.RentForAsync(actor, site.StructureId, call.Cancellation);
        var atStructure = now();
        var structureStation = new CorporationOfficeStation(site.StructureId, site.TypeId, site.OwnerCorporationId, site.SolarSystemId, 0);
        await store.WriteAsync(async (connection, transaction) =>
        {
            await store.LockStationAsync(connection, transaction, site.StructureId, call.Cancellation);
            if (await store.GetAsync(connection, transaction, corporationId, site.StructureId, forUpdate: true,
                cancellationToken: call.Cancellation) is not null)
                return 0;

            var officeFolderId = await items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(CorporationOfficeStore.OfficeFolderTypeId, corporationId, site.StructureId,
                    Eve.Inventory.ItemFlags.Hangar, Singleton: true), call.Cancellation);
            var office = await store.RentAsync(connection, transaction, corporationId, officeFolderId, structureStation, rent, atStructure,
                call.Cancellation, creditorId: site.OwnerCorporationId);
            if (rent > 0)
            {
                await wallets.AdjustAsync(connection, transaction, new Eve.Wallet.WalletAccount(corporationId), -rent,
                    site.OwnerCorporationId, OfficeRentReferenceType, "Corporation office rental", atStructure,
                    $"office-rent:{office.OfficeId}:charge", "office_rental_fee", call.Cancellation);
                await wallets.AdjustAsync(connection, transaction, new Eve.Wallet.WalletAccount(site.OwnerCorporationId), rent,
                    corporationId, OfficeRentReferenceType, "Corporation office rental", atStructure,
                    $"office-rent:{office.OfficeId}:credit", "office_rental_fee", call.Cancellation);
            }
            await structures.ScheduleRentDueAsync(connection, transaction, office.OfficeId, office.ExpiresAtMs, call.Cancellation);
            return 0;
        }, call.Cancellation);
        return PyNone.Instance;
    }

    /// <summary>
    /// A corporation office at a structure given up (EVE.js UnrentOffice, structure branch): the corporation's items there go to asset safety and the office ends, in one
    /// transaction. A structure office is not impounded, and only a Director or the CEO may give it up.
    /// </summary>
    private async Task<PyValue> UnrentStructureOfficeAsync(ServiceCall call, long actor, long corporationId, StructureOfficeSite site)
    {
        var authority = await RequireAuthorityAsync(actor, corporationId, site.StructureId, call.Cancellation);
        if (!IsCeoOrDirector(actor, authority))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var atStructure = now();
        await store.WriteAsync(async (connection, transaction) =>
        {
            await store.LockStationAsync(connection, transaction, site.StructureId, call.Cancellation);
            var office = await store.GetAsync(connection, transaction, corporationId, site.StructureId,
                forUpdate: true, cancellationToken: call.Cancellation);
            if (office is null) return 0;
            if (!await structures!.MoveOfficeToSafetyAsync(connection, transaction, site, corporationId, call.Cancellation))
                throw new CorporationError("STRUCTURE_ASSET_SAFETY_UNAVAILABLE");
            await store.UnrentAsync(connection, transaction, office, atStructure, call.Cancellation);
            return 0;
        }, call.Cancellation);
        return PyNone.Instance;
    }

    /// <summary>The structure this call is for (its bound id, else the session's structure), or null for a station call.</summary>
    private async Task<StructureOfficeSite?> StructureSiteAsync(ServiceCall call)
    {
        var structureId = call.BoundObjectId is null
            ? call.Session.GetInt("structureid") ?? 0
            : StationParameter(call.BoundObjectParameters) ?? 0;
        return structureId > 0 ? await StructureAtAsync(structureId, call.Cancellation) : null;
    }

    private async Task<StructureOfficeSite?> StructureAtAsync(long structureId, CancellationToken token) =>
        structures is null ? null : (await structures.ReadAsync(structureId, token)) is { Gone: false } site ? site : null;

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
