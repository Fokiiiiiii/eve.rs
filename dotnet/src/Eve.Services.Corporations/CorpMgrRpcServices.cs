using System.Text.Json;
using Eve.Corporations;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Corporations;

public sealed record CorpMgrAggressionSettings(long? EnableAfterFiletime, long? DisableAfterFiletime);

public sealed record CorpMgrAuditEvent(
    long EventId, long EventDateTimeFiletime, int EventTypeId, long CharacterId, long CorporationId);

public sealed partial class CorpMgrService
{
    private const long DirectorRole = 1;
    private const long AuditorRole = 4096;
    private const long FiletimeUnixEpoch = 116444736000000000;
    private const int AssetSafetyFlag = 36;
    private const int CorporationDeliveryFlag = 62;
    private const int CapsuleerDeliveryFlag = 187;
    private const string CRowsetName = "carbon.common.script.sys.crowset.CRowset";

    private static readonly string[] AssetBucketNames =
    [
        "offices", "impounded", "property", "deliveries", "capsuleerdeliveries", "assetwraps"
    ];

    private static readonly (string Name, int Type)[] AuditEventColumns =
    [
        ("eventID", 0x14), ("eventDateTime", 0x40), ("eventTypeID", 0x02),
        ("characterID", 0x03), ("corporationID", 0x03)
    ];

    private static readonly (string Name, int Type)[] RoleHistoryColumns =
    [
        ("charID", 0x03), ("corporationID", 0x03), ("changeTime", 0x40),
        ("grantable", 0x0b), ("oldRoles", 0x14), ("newRoles", 0x14), ("issuerID", 0x03)
    ];

    private static readonly (string Name, int Type)[] AssetLocationColumns =
    [
        ("locationID", 0x14), ("solarsystemID", 0x14), ("typeID", 0x03)
    ];

    private static readonly (string Name, int Type)[] AssetDeliveryLocationColumns =
    [
        ("locationID", 0x14), ("solarsystemID", 0x14), ("typeID", 0x03), ("itemCount", 0x03)
    ];

    private static readonly (string Name, int Type)[] AssetItemColumns =
    [
        ("itemID", 0x14), ("typeID", 0x03), ("ownerID", 0x03), ("locationID", 0x14),
        ("flagID", 0x02), ("quantity", 0x03), ("groupID", 0x03), ("categoryID", 0x03),
        ("customInfo", 0x81), ("stacksize", 0x03), ("singleton", 0x03)
    ];

    private static readonly (string Name, int Type)[] AssetSearchColumns =
    [
        ("locationID", 0x14)
    ];

    [Method("GetAggressionSettings")]
    public async ValueTask<PyValue> GetAggressionSettings(ServiceCall call) =>
        AggressionPayload(await ReadAggressionSettingsAsync(CorpMgrLong(call.Arg(0)), call.Cancellation));

    [Method("GetAggressionSettingsForCorps")]
    public async ValueTask<PyValue> GetAggressionSettingsForCorps(ServiceCall call)
    {
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var corporationId in CorpMgrSequence(call.Arg(0))
                     .Select(value => CorpMgrLong(value))
                     .Where(id => id > 0)
                     .Distinct())
        {
            entries.Add(new KeyValuePair<PyValue, PyValue>(
                new PyLong(corporationId),
                AggressionPayload(await ReadAggressionSettingsAsync(corporationId, call.Cancellation))));
        }
        return new PyDict(entries);
    }

    [Method("AuditMember")]
    public async ValueTask<PyValue> AuditMember(ServiceCall call)
    {
        var memberId = CorpMgrLong(call.Arg(0));
        var corporationId = call.Session.GetInt("corporationID") ?? call.Session.GetInt("corpid") ?? 0;
        var fromFiletime = CorpMgrLong(call.Arg(1));
        var toFiletime = CorpMgrLong(call.Arg(2));
        var pageSize = CorpMgrPageSize(call.Arg(3));
        var emptyEvents = BuildCRowset(AuditEventColumns, []);
        var emptyRoles = BuildCRowset(RoleHistoryColumns, []);

        if (memberId <= 0 || corporationId <= 0 ||
            !await CanAuditMembersAsync(call, corporationId, call.Cancellation))
            return Py.Tuple(emptyEvents, emptyRoles);

        IReadOnlyList<CorpMgrAuditEvent> auditEvents = readAuditEvents is null
            ? Array.Empty<CorpMgrAuditEvent>()
            : await readAuditEvents(corporationId, memberId, fromFiletime, toFiletime, pageSize, call.Cancellation);

        var eventRows = auditEvents
            .Where(entry => entry.CharacterId == memberId &&
                IsInFiletimeRange(entry.EventDateTimeFiletime, fromFiletime, toFiletime))
            .OrderByDescending(entry => entry.EventDateTimeFiletime)
            .Take(pageSize)
            .Select(entry => new PyValue[]
            {
                new PyLong(entry.EventId),
                new PyLong(entry.EventDateTimeFiletime),
                new PyInt(entry.EventTypeId),
                new PyInt(entry.CharacterId),
                new PyInt(entry.CorporationId)
            });

        var history = await store.RoleHistoryAsync(
            corporationId, memberId, UnixMs(fromFiletime), UnixMs(toFiletime), 5000, call.Cancellation);
        var roleChanges = new List<RoleHistoryRow>();
        foreach (var entry in history)
        {
            var oldRoles = RoleMask(entry.OldRolesJson, "Roles");
            var newRoles = RoleMask(entry.NewRolesJson, "Roles");
            if (oldRoles != newRoles)
                roleChanges.Add(new RoleHistoryRow(entry.ChangedMs, false, oldRoles, newRoles, entry.ActorId));

            var oldGrantable = RoleMask(entry.OldRolesJson, "GrantableRoles");
            var newGrantable = RoleMask(entry.NewRolesJson, "GrantableRoles");
            if (oldGrantable != newGrantable)
                roleChanges.Add(new RoleHistoryRow(entry.ChangedMs, true, oldGrantable, newGrantable, entry.ActorId));
        }

        var roleRows = roleChanges
            .OrderByDescending(entry => entry.ChangedMs)
            .Take(pageSize)
            .Select(entry => new PyValue[]
            {
                new PyInt(memberId),
                new PyInt(corporationId),
                new PyLong(Py.FileTimeFromUnixMs(entry.ChangedMs)),
                entry.Grantable ? PyBool.True : PyBool.False,
                new PyLong(entry.OldRoles),
                new PyLong(entry.NewRoles),
                new PyInt(entry.ActorId)
            });

        return Py.Tuple(BuildCRowset(AuditEventColumns, eventRows), BuildCRowset(RoleHistoryColumns, roleRows));
    }

    [Method("GetAssetInventory")]
    public async ValueTask<PyValue> GetAssetInventory(ServiceCall call)
    {
        var corporationId = CorpMgrLong(call.Arg(0));
        await RequireAssetAuthorityAsync(call, corporationId, call.Cancellation);
        var bucket = AssetBucket(call.Arg(1));
        var snapshot = await BuildAssetSnapshotAsync(corporationId, call.Cancellation);
        return CachedAssets(AssetLocationsRowset(bucket, snapshot.Locations[bucket]));
    }

    [Method("GetAssetInventoryForLocation")]
    public async ValueTask<PyValue> GetAssetInventoryForLocation(ServiceCall call)
    {
        var corporationId = CorpMgrLong(call.Arg(0));
        var locationId = CorpMgrLong(call.Arg(1));
        await RequireAssetAuthorityAsync(call, corporationId, call.Cancellation);
        var bucket = AssetBucket(call.Arg(2));
        var snapshot = await BuildAssetSnapshotAsync(corporationId, call.Cancellation);
        var entries = snapshot.Items[bucket].Where(entry => entry.RootLocationId == locationId);
        return CachedAssets(AssetItemsRowset(entries));
    }

    [Method("SearchAssets")]
    public async ValueTask<PyValue> SearchAssets(ServiceCall call)
    {
        var corporationId = call.Session.GetInt("corporationID") ?? call.Session.GetInt("corpid") ?? 0;
        await RequireAssetAuthorityAsync(call, corporationId, call.Cancellation);
        var bucket = AssetBucket(call.Arg(0));
        var categoryId = CorpMgrLong(call.Arg(1));
        var groupId = CorpMgrLong(call.Arg(2));
        var typeId = CorpMgrLong(call.Arg(3));
        var minimumQuantity = CorpMgrLong(call.Arg(4));
        var snapshot = await BuildAssetSnapshotAsync(corporationId, call.Cancellation);
        var hasFilter = categoryId > 0 || groupId > 0 || typeId > 0 || minimumQuantity > 0;
        var entries = hasFilter
            ? snapshot.Locations[bucket].Where(location =>
                snapshot.Items[bucket].Any(item => item.RootLocationId == location.LocationId &&
                    (categoryId <= 0 || item.CategoryId == categoryId) &&
                    (groupId <= 0 || item.GroupId == groupId) &&
                    (typeId <= 0 || item.Item.TypeId == typeId) &&
                    (minimumQuantity <= 0 || item.Item.Quantity >= minimumQuantity)))
            : snapshot.Locations[bucket];
        return CachedAssets(BuildCRowset(AssetSearchColumns,
            entries.Select(location => new PyValue[] { new PyLong(location.LocationId) })));
    }

    private async ValueTask<CorpMgrAggressionSettings> ReadAggressionSettingsAsync(
        long corporationId, CancellationToken cancellationToken)
    {
        var corporation = corporationId > 0
            ? await store.GetAsync(corporationId, cancellationToken)
            : null;
        var defaults = corporation?.IsNpc == true
            ? new CorpMgrAggressionSettings(null, 0)
            : new CorpMgrAggressionSettings(0, null);
        var stored = StoredAggressionSettings(corporation, defaults);
        var callback = readAggressionSettings is null
            ? null
            : await readAggressionSettings(corporationId, cancellationToken);
        return callback ?? stored;
    }

    private static CorpMgrAggressionSettings StoredAggressionSettings(
        Corporation? corporation, CorpMgrAggressionSettings defaults)
    {
        if (corporation is not { Settings.ValueKind: JsonValueKind.Object } ||
            !TryProperty(corporation.Settings, "aggressionSettings", out var settings) ||
            settings.ValueKind != JsonValueKind.Object)
            return defaults;

        return new CorpMgrAggressionSettings(
            OptionalFiletime(settings, "enableAfter", defaults.EnableAfterFiletime),
            OptionalFiletime(settings, "disableAfter", defaults.DisableAfterFiletime));
    }

    private async ValueTask<bool> CanAuditMembersAsync(
        ServiceCall call, long corporationId, CancellationToken cancellationToken)
    {
        var actorId = call.Session.CharacterId;
        if (actorId is not > 0 ||
            await store.GetAsync(corporationId, cancellationToken) is not { IsNpc: false, Deleted: false } corporation ||
            await characters.GetAsync(actorId.Value, cancellationToken) is not { } character ||
            character.CorporationId != corporationId ||
            await store.MemberAsync(actorId.Value, cancellationToken) is not { } member ||
            member.CorporationId != corporationId)
            return false;

        var sessionRoles = call.Session.GetInt("corprole") ?? 0;
        if (sessionRoles == 0)
            sessionRoles = call.Session.GetInt("rolesAtAll") ?? call.Session.GetInt("corpRole") ?? 0;
        if (HasAuditRole(sessionRoles) || HasAuditRole(member.Masks.Roles))
            return true;

        var titles = await store.TitlesAsync(corporationId, cancellationToken);
        return HasAuditRole(CorporationRoles.Session(corporation, member, character.StationId, titles).CorpRole);
    }

    private async ValueTask RequireAssetAuthorityAsync(
        ServiceCall call, long corporationId, CancellationToken cancellationToken)
    {
        var characterId = call.Session.CharacterId;
        var sessionCorporationId = call.Session.GetInt("corporationID") ?? call.Session.GetInt("corpid") ?? 0;
        if (corporationId <= 0 || sessionCorporationId != corporationId || characterId is not > 0 ||
            await store.GetAsync(corporationId, cancellationToken) is not { IsNpc: false, Deleted: false } corporation ||
            await characters.GetAsync(characterId.Value, cancellationToken) is not { } character ||
            character.CorporationId != corporationId ||
            await store.MemberAsync(characterId.Value, cancellationToken) is not { CorporationId: var memberCorporation } member ||
            memberCorporation != corporationId)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");

        var roles = CorporationRoles.Session(
            corporation, member, character.StationId,
            await store.TitlesAsync(corporationId, cancellationToken)).CorpRole;
        if ((roles & (CorporationRoles.Accountant | CorporationRoles.JuniorAccountant)) == 0)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
    }

    private async ValueTask<AssetSnapshot> BuildAssetSnapshotAsync(
        long corporationId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Item> ownedItems = items is null
            ? Array.Empty<Item>()
            : await items.OwnedByAsync(corporationId, cancellationToken);
        IReadOnlyList<Item> wraps = readAssetWraps is null
            ? Array.Empty<Item>()
            : await readAssetWraps(corporationId, cancellationToken);
        IReadOnlyList<CorporationOffice> rentedOffices = offices is null
            ? Array.Empty<CorporationOffice>()
            : await offices.ForCorporationAsync(corporationId, cancellationToken);
        var itemTypesById = itemTypes is null
            ? new Dictionary<int, ItemType>()
            : await itemTypes.GetAsync(ownedItems.Concat(wraps).Select(item => item.TypeId), cancellationToken);

        var officeByKnownLocation = new Dictionary<long, CorporationOffice>();
        var officeStations = new HashSet<long>();
        foreach (var office in rentedOffices)
        {
            officeByKnownLocation[office.OfficeId] = office;
            officeByKnownLocation[office.OfficeFolderId] = office;
            officeStations.Add(office.StationId);
        }

        var locations = AssetBucketNames.ToDictionary(
            bucket => bucket,
            _ => new Dictionary<long, MutableAssetLocation>(),
            StringComparer.Ordinal);
        var assetItems = AssetBucketNames.ToDictionary(
            bucket => bucket,
            _ => new List<AssetItemRow>(),
            StringComparer.Ordinal);

        void AddLocation(string bucket, long locationId, long? systemId, int? typeId, bool hasItem)
        {
            if (!locations[bucket].TryGetValue(locationId, out var location))
            {
                location = new MutableAssetLocation(locationId, systemId, typeId);
                locations[bucket].Add(locationId, location);
            }
            else
            {
                location.SolarSystemId ??= systemId;
                location.TypeId ??= typeId;
            }
            if (hasItem)
                location.ItemCount++;
        }

        foreach (var office in rentedOffices)
        {
            var station = resolveAssetLocation?.Invoke(office.StationId);
            AddLocation("offices", office.StationId,
                office.SolarSystemId > 0 ? office.SolarSystemId : station?.SolarSystemId,
                station?.TypeId, false);
        }

        var itemById = ownedItems.ToDictionary(item => item.Id);
        foreach (var item in ownedItems)
        {
            if (item.Flag == AssetSafetyFlag)
                continue;
            var root = ResolveAssetRoot(item, itemById, officeByKnownLocation);
            if (root is null)
                continue;

            var bucket = AssetBucketFor(item, root, officeStations);
            var metadata = itemTypesById.GetValueOrDefault(item.TypeId);
            AddLocation(bucket, root.LocationId, root.SolarSystemId, root.TypeId, true);
            assetItems[bucket].Add(new AssetItemRow(item, root.LocationId, metadata?.GroupId ?? 0, metadata?.CategoryId ?? 0));
        }

        foreach (var wrap in wraps)
        {
            var location = resolveAssetLocation?.Invoke(wrap.LocationId);
            if (wrap.LocationId <= 0 || location is null)
                continue;
            var item = wrap with { OwnerId = corporationId, Flag = AssetSafetyFlag, Quantity = -1, Singleton = true };
            var metadata = itemTypesById.GetValueOrDefault(item.TypeId);
            AddLocation("assetwraps", item.LocationId, location.Value.SolarSystemId, location.Value.TypeId, true);
            assetItems["assetwraps"].Add(new AssetItemRow(item, item.LocationId, metadata?.GroupId ?? 0, metadata?.CategoryId ?? 0));
        }

        return new AssetSnapshot(
            locations.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<AssetLocation>)pair.Value.Values
                    .OrderBy(location => location.SolarSystemId ?? 0)
                    .ThenBy(location => location.LocationId)
                    .Select(location => new AssetLocation(
                        location.LocationId, location.SolarSystemId, location.TypeId, location.ItemCount))
                    .ToArray(),
                StringComparer.Ordinal),
            assetItems.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<AssetItemRow>)pair.Value
                    .OrderBy(item => item.Item.Flag)
                    .ThenBy(item => item.Item.TypeId)
                    .ThenBy(item => item.Item.Id)
                    .ToArray(),
                StringComparer.Ordinal));
    }

    private AssetRoot? ResolveAssetRoot(
        Item item, IReadOnlyDictionary<long, Item> itemById,
        IReadOnlyDictionary<long, CorporationOffice> officeByKnownLocation)
    {
        var current = item;
        var visited = new HashSet<long>();
        while (current is not null)
        {
            var locationId = current.LocationId;
            if (locationId <= 0 || !visited.Add(locationId))
                return null;
            if (officeByKnownLocation.TryGetValue(locationId, out var office))
            {
                var station = resolveAssetLocation?.Invoke(office.StationId);
                return new AssetRoot(
                    office.StationId,
                    office.SolarSystemId > 0 ? office.SolarSystemId : station?.SolarSystemId,
                    station?.TypeId,
                    office);
            }
            if (resolveAssetLocation?.Invoke(locationId) is { } location)
                return new AssetRoot(locationId, location.SolarSystemId, location.TypeId, null);
            if (!itemById.TryGetValue(locationId, out current!))
                return null;
        }
        return null;
    }

    private static string AssetBucketFor(
        Item item, AssetRoot root, IReadOnlySet<long> officeStations)
    {
        if (root.Office is not null)
            return "offices";
        if (item.Flag == CorporationOfficeStore.ImpoundedFlag)
            return "impounded";
        if (item.Flag == CorporationDeliveryFlag)
            return "deliveries";
        if (item.Flag == CapsuleerDeliveryFlag)
            return "capsuleerdeliveries";
        if (officeStations.Contains(root.LocationId) && ItemFlags.IsCorporationHangar(item.Flag))
            return "offices";
        return "property";
    }

    private static PyValue AssetLocationsRowset(string bucket, IReadOnlyList<AssetLocation> locations)
    {
        var deliveryRows = bucket is "deliveries" or "capsuleerdeliveries";
        var columns = deliveryRows ? AssetDeliveryLocationColumns : AssetLocationColumns;
        var rows = locations.Select(location => deliveryRows
            ? new PyValue[]
            {
                new PyLong(location.LocationId),
                OptionalLongValue(location.SolarSystemId),
                OptionalIntValue(location.TypeId),
                new PyInt(location.ItemCount)
            }
            : new PyValue[]
            {
                new PyLong(location.LocationId),
                OptionalLongValue(location.SolarSystemId),
                OptionalIntValue(location.TypeId)
            });
        return BuildCRowset(columns, rows);
    }

    private static PyValue OptionalLongValue(long? value) =>
        value is { } number ? new PyLong(number) : PyNone.Instance;

    private static PyValue OptionalIntValue(int? value) =>
        value is { } number ? new PyInt(number) : PyNone.Instance;

    private static PyValue AssetItemsRowset(IEnumerable<AssetItemRow> entries) =>
        BuildCRowset(AssetItemColumns, entries.Select(entry =>
        {
            var singleton = entry.Item.BlueprintCopy ? 2 : entry.Item.Singleton ? 1 : 0;
            var quantity = singleton == 2 ? -2 : singleton == 1 ? -1 : entry.Item.Quantity;
            return new PyValue[]
            {
                new PyLong(entry.Item.Id),
                new PyInt(entry.Item.TypeId),
                new PyInt(entry.Item.OwnerId),
                new PyLong(entry.Item.LocationId),
                new PyInt(entry.Item.Flag),
                new PyInt(quantity),
                new PyInt(entry.GroupId),
                new PyInt(entry.CategoryId),
                new PyWString(entry.Item.CustomName ?? string.Empty),
                new PyInt(entry.Item.Quantity),
                new PyInt(singleton)
            };
        }));

    private PyValue CachedAssets(PyValue result) =>
        ObjectCaching.MethodResult(result, store.ClockMs, "5 minutes", "corpid");

    private static PyValue AggressionPayload(CorpMgrAggressionSettings settings) =>
        new PyObject(new PyStr("crimewatch.corp_aggression.settings.AggressionSettings"), Py.Dict(
            ("_enableAfter", settings.EnableAfterFiletime is { } enable ? new PyLong(enable) : PyNone.Instance),
            ("_disableAfter", settings.DisableAfterFiletime is { } disable ? new PyLong(disable) : PyNone.Instance)));

    private static PyValue BuildCRowset(
        IReadOnlyList<(string Name, int Type)> columns, IEnumerable<PyValue[]> rows)
    {
        var header = new PyObjectEx(ObjectExKind.Ex1,
            Py.Tuple(new PyToken("blue.DBRowDescriptor"),
                Py.Tuple(Py.Tuple([.. columns.Select(column =>
                    (PyValue)Py.Tuple(new PyStr(column.Name), new PyInt(column.Type)))]))),
            [], []);
        var descriptor = Descriptor.FromHeader(header);
        return new PyObject(new PyStr(CRowsetName), Py.Dict(
            ("header", descriptor.Header),
            ("RowClass", new PyToken("util.Row")),
            ("lines", new PyList([.. rows.Select(row =>
                (PyValue)new PyPackedRow(descriptor, row))]))));
    }

    private static IEnumerable<PyValue> CorpMgrSequence(PyValue? value) => value switch
    {
        PyList list => list.Items,
        PyTuple tuple => tuple.Items,
        PyDict dictionary => dictionary.Entries.Select(entry => entry.Key),
        _ => Array.Empty<PyValue>()
    };

    private static long CorpMgrLong(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number when double.IsFinite(number.Value) &&
            number.Value >= long.MinValue && number.Value < (double)long.MaxValue => (long)Math.Truncate(number.Value),
        PyStr text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
        PyWString text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
        _ => 0
    };

    private static int CorpMgrPageSize(PyValue? value)
    {
        var requested = CorpMgrLong(value);
        return requested <= 0 ? 10 : (int)Math.Min(requested, 100);
    }

    private static string AssetBucket(PyValue? value)
    {
        var requested = value switch
        {
            var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
            PyToken text => text.Value,
            _ => "offices"
        };
        var bucket = requested.Trim().ToLowerInvariant();
        return AssetBucketNames.Contains(bucket, StringComparer.Ordinal) ? bucket : "offices";
    }

    private static bool HasAuditRole(long roles) =>
        (roles & (DirectorRole | AuditorRole)) != 0;

    private static bool IsInFiletimeRange(long filetime, long fromFiletime, long toFiletime) =>
        (fromFiletime <= 0 || filetime >= fromFiletime) &&
        (toFiletime <= 0 || filetime < toFiletime);

    private static long? UnixMs(long filetime) =>
        filetime <= 0 ? null : (filetime - FiletimeUnixEpoch) / 10000;

    private static long? OptionalFiletime(JsonElement source, string name, long? fallback)
    {
        if (!TryProperty(source, name, out var value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out number))
            return number;
        return fallback;
    }

    private static bool TryProperty(JsonElement source, string name, out JsonElement value)
    {
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static long RoleMask(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        var source = TryProperty(document.RootElement, "Masks", out var masks) ? masks : document.RootElement;
        return TryProperty(source, name, out var value) ? JsonLong(value) : 0;
    }

    private static long JsonLong(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var number) => number,
        JsonValueKind.String when long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
        _ => 0
    };

    private sealed record AssetRoot(
        long LocationId, long? SolarSystemId, int? TypeId, CorporationOffice? Office);

    private sealed record AssetLocation(
        long LocationId, long? SolarSystemId, int? TypeId, int ItemCount);

    private sealed record AssetItemRow(Item Item, long RootLocationId, int GroupId, int CategoryId);

    private sealed record AssetSnapshot(
        IReadOnlyDictionary<string, IReadOnlyList<AssetLocation>> Locations,
        IReadOnlyDictionary<string, IReadOnlyList<AssetItemRow>> Items);

    private sealed record RoleHistoryRow(long ChangedMs, bool Grantable, long OldRoles, long NewRoles, long ActorId);

    private sealed class MutableAssetLocation(long locationId, long? solarSystemId, int? typeId)
    {
        public long LocationId { get; } = locationId;
        public long? SolarSystemId { get; set; } = solarSystemId;
        public int? TypeId { get; set; } = typeId;
        public int ItemCount { get; set; }
    }
}

public sealed record MutualWarInvite(long FromOwnerId, long ToOwnerId, long SentDateFiletime);

[Service("mutualWarInviteMgr")]
public sealed partial class MutualWarInviteMgrService(
    Func<long, CancellationToken, ValueTask<IReadOnlyList<MutualWarInvite>>>? readPendingInvites = null,
    Func<long, CancellationToken, ValueTask<bool>>? readInvitesBlocked = null,
    Func<ServiceCall, long, long, CancellationToken, ValueTask>? sendInvite = null,
    Func<ServiceCall, long, long, CancellationToken, ValueTask>? withdrawInvite = null,
    Func<ServiceCall, long, long, bool, CancellationToken, ValueTask>? respondToInvite = null,
    Func<ServiceCall, long, bool, CancellationToken, ValueTask>? setInvitesBlocked = null)
{
    [Method("GetPendingInvitesForSession")]
    public async ValueTask<PyValue> GetPendingInvitesForSession(ServiceCall call)
    {
        var ownerId = OwnerId(call.Session);
        IReadOnlyList<MutualWarInvite> invites = ownerId <= 0 || readPendingInvites is null
            ? Array.Empty<MutualWarInvite>()
            : await readPendingInvites(ownerId, call.Cancellation);
        return new PyList([.. invites
            .Where(invite => invite.FromOwnerId == ownerId || invite.ToOwnerId == ownerId)
            .OrderByDescending(invite => invite.SentDateFiletime)
            .Select(invite => (PyValue)Py.KeyVal(
                ("fromOwnerID", new PyInt(invite.FromOwnerId)),
                ("toOwnerID", new PyInt(invite.ToOwnerId)),
                ("sentDate", new PyLong(invite.SentDateFiletime))))]);
    }

    [Method("IsCorpInvitesBlockedPlayer")]
    public async ValueTask<PyValue> IsCorpInvitesBlockedPlayer(ServiceCall call)
    {
        var ownerId = OwnerId(call.Session);
        var blocked = ownerId > 0 && readInvitesBlocked is not null &&
            await readInvitesBlocked(ownerId, call.Cancellation);
        return new PyInt(blocked ? 1 : 0);
    }

    [Method("SendInviteByPlayer")]
    public async ValueTask<PyValue> SendInviteByPlayer(ServiceCall call)
    {
        if (sendInvite is null)
            throw NotAvailable();
        var fromOwnerId = OwnerId(call.Session);
        var toOwnerId = InviteInteger(call.Arg(0));
        if (fromOwnerId > 0 && toOwnerId > 0 &&
            (readInvitesBlocked is null || !await readInvitesBlocked(toOwnerId, call.Cancellation)))
            await sendInvite(call, fromOwnerId, toOwnerId, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("WithdrawInviteByPlayer")]
    public async ValueTask<PyValue> WithdrawInviteByPlayer(ServiceCall call)
    {
        if (withdrawInvite is null)
            throw NotAvailable();
        var fromOwnerId = OwnerId(call.Session);
        var toOwnerId = InviteInteger(call.Arg(0));
        if (fromOwnerId > 0 && toOwnerId > 0)
            await withdrawInvite(call, fromOwnerId, toOwnerId, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("RespondToInviteByPlayer")]
    public async ValueTask<PyValue> RespondToInviteByPlayer(ServiceCall call)
    {
        if (respondToInvite is null)
            throw NotAvailable();
        var fromOwnerId = InviteInteger(call.Arg(0));
        var toOwnerId = OwnerId(call.Session);
        var accepts = JsTruthy(call.Arg(1));
        if (fromOwnerId > 0 && toOwnerId > 0)
            await respondToInvite(call, fromOwnerId, toOwnerId, accepts, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("SetInvitesBlockedByPlayer")]
    public async ValueTask<PyValue> SetInvitesBlockedByPlayer(ServiceCall call)
    {
        if (setInvitesBlocked is null)
            throw NotAvailable();
        var ownerId = OwnerId(call.Session);
        if (ownerId > 0)
            await setInvitesBlocked(call, ownerId, JsTruthy(call.Arg(0)), call.Cancellation);
        return PyNone.Instance;
    }

    private static MachoWrappedException NotAvailable() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("Mutual war invitations are not available on this server.")));

    private static long OwnerId(ClientSession session) =>
        Positive(session.GetInt("allianceID")) ??
        Positive(session.GetInt("allianceid")) ??
        Positive(session.GetInt("corporationID")) ??
        Positive(session.GetInt("corpid")) ?? 0;

    private static long? Positive(long? value) => value is > 0 ? value : null;

    private static long InviteInteger(PyValue? value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => 0
    };

    private static bool JsTruthy(PyValue? value) => value switch
    {
        null or PyNone => false,
        PyBool boolean => boolean.Value,
        PyInt integer => integer.Value != 0,
        PyLong integer => integer.Value != 0,
        PyReal real => real.Value != 0 && !double.IsNaN(real.Value),
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!.Length != 0,
        _ => true
    };
}
