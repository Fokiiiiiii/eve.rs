using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

public sealed partial class PersistenceStructureAssetSafetyReadModel : Eve.Services.Core.IStructureAssetSafetyReadCallbacks
{
    public const string DefaultSchema = "asset_safety";
    public const string DueJobKind = "structure_asset_safety.process_due";
    private const int AssetSafetyRecoveryTaxRefTypeId = 123; // ESI wallet ref type asset_safety_recovery_tax.
    private const long DayMs = 86_400_000L;
    private const int Stored = 0;
    private const int Delivered = 1;
    private const int Ejected = 2;
    private const short Personal = 1;
    private const short Corporation = 2;
    private const long DirectorRole = Eve.Corporations.CorporationRoles.Director;
    private const double HighSecurityMinimum = 0.45;
    private static readonly System.Collections.Generic.HashSet<string> AssetSafetyGroups = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "Citadel",
        "Engineering Complex",
        "Refinery",
        "Observatory Array",
    };

    private readonly Npgsql.NpgsqlDataSource _source;
    private readonly Eve.Inventory.PgItemStore _items;
    private readonly Eve.Ships.PgSpaceItemStore _spaceItems;
    private readonly Eve.Wallet.WalletTransactions _wallets;
    private readonly Eve.Sde.SdeReader _sde;
    private readonly System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<System.Text.Json.Nodes.JsonObject>> _readTable;
    private readonly System.Func<long, System.Threading.CancellationToken, System.Threading.Tasks.Task<Eve.Relations.Affiliation>> _affiliationOf;
    private readonly System.Func<long> _now;
    private readonly string _inventorySchema;
    private readonly string _corporationSchema;
    private readonly string _schema;
    private readonly Eve.Services.Core.IStructureDirectoryReadCallbacks _structures;
    private readonly System.Action<string>? _log;
    private readonly System.Threading.SemaphoreSlim _catalogGate = new(1, 1);
    private StaticData? _catalog;

    public PersistenceStructureAssetSafetyReadModel(
        Npgsql.NpgsqlDataSource source,
        Eve.Inventory.PgItemStore items,
        Eve.Ships.PgSpaceItemStore spaceItems,
        Eve.Wallet.WalletTransactions wallets,
        Eve.Sde.SdeReader sde,
        System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<System.Text.Json.Nodes.JsonObject>> readTable,
        System.Func<long, System.Threading.CancellationToken, System.Threading.Tasks.Task<Eve.Relations.Affiliation>> affiliationOf,
        System.Func<long> now,
        string inventorySchema,
        string corporationSchema,
        string schema,
        Eve.Services.Core.IStructureDirectoryReadCallbacks structures,
        System.Action<string>? log = null)
    {
        _source = source ?? throw new System.ArgumentNullException(nameof(source));
        _items = items ?? throw new System.ArgumentNullException(nameof(items));
        _spaceItems = spaceItems ?? throw new System.ArgumentNullException(nameof(spaceItems));
        _wallets = wallets ?? throw new System.ArgumentNullException(nameof(wallets));
        _sde = sde ?? throw new System.ArgumentNullException(nameof(sde));
        _readTable = readTable ?? throw new System.ArgumentNullException(nameof(readTable));
        _affiliationOf = affiliationOf ?? throw new System.ArgumentNullException(nameof(affiliationOf));
        _now = now ?? throw new System.ArgumentNullException(nameof(now));
        _inventorySchema = SchemaName(inventorySchema);
        _corporationSchema = SchemaName(corporationSchema);
        _schema = SchemaName(schema);
        _structures = structures ?? throw new System.ArgumentNullException(nameof(structures));
        _log = log;
    }

    private async System.Threading.Tasks.Task DeliverAutomaticallyAsync(
        long wrapId,
        StaticData data,
        System.Threading.CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var operationKey = $"asset-safety:auto:{wrapId}";
        var replay = await BeginReceiptAsync(connection, transaction, operationKey, "auto-delivery", cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var wrap = await ReadWrapForUpdateAsync(connection, transaction, wrapId, cancellationToken);
        if (wrap is null || wrap.State != Stored || _now() - wrap.CreatedAtMs < 20 * DayMs)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        var station = NearestAutomaticStation(wrap.SourceSystemId, data);
        if (station is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var contents = await LockItemTreeAsync(connection, transaction, wrapId, wrap.OwnerId, cancellationToken);
        var fee = await FeeAsync(contents, wrap.SourceSystemId == station.SolarSystemId, cancellationToken);
        if (fee > 0)
        {
            await _wallets.AdjustAsync(
                connection, transaction, wrap.OwnerId, -fee, station.Id, AssetSafetyRecoveryTaxRefTypeId,
                "Asset Safety automatic recovery", _now(), $"asset-safety-wrap:{wrapId}", cancellationToken, wrapId);
        }
        foreach (var item in contents.Where(i => i.LocationId == wrapId && i.OwnerId == wrap.OwnerId))
            await _items.MoveAsync(connection, transaction, item.Id, station.Id, item.Flag, cancellationToken);

        await _items.DeleteAsync(connection, transaction, wrapId, cancellationToken);
        await SetDeliveredAsync(connection, transaction, wrapId, station.Id, _now(), cancellationToken);
        await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.WrapDelivered, wrapId, cancellationToken);
    }

    private async System.Threading.Tasks.Task<DestinationCheck> ValidateDestinationAsync(
        long characterId,
        long corporationId,
        long destinationId,
        long sourceSystemId,
        StaticData data,
        System.Threading.CancellationToken cancellationToken)
    {
        var station = data.Stations.Get(destinationId);
        if (station is not null)
        {
            return station.SolarSystemId == sourceSystemId
                ? new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.WrapDelivered, station.SolarSystemId)
                : new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.InvalidDestination, station.SolarSystemId);
        }

        var structure = await ReadPersistedStructureAsync(destinationId, data, cancellationToken);
        if (structure is null || !IsStandardUpwell(structure.TypeId, data))
            return new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.InvalidDestination, 0);
        if (structure.SystemId != sourceSystemId)
            return new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.InvalidDestination, structure.SystemId);

        if (corporationId <= 0)
            return new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.DestinationAccessDenied, structure.SystemId);
        var affiliation = await _affiliationOf(characterId, cancellationToken);
        var snapshots = await _structures.GetDockingAccessSnapshotsAsync(
            characterId, corporationId, affiliation.Alliance ?? 0, [destinationId], cancellationToken);
        var access = snapshots.FirstOrDefault(s => s.StructureId == destinationId);
        return access is not null && AllowsDocking(access, characterId, corporationId, affiliation.Alliance ?? 0)
            ? new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.WrapDelivered, structure.SystemId)
            : new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.DestinationAccessDenied, structure.SystemId);
    }

    private async System.Threading.Tasks.Task<StructureRow?> ReadPersistedStructureAsync(
        long structureId,
        StaticData data,
        System.Threading.CancellationToken cancellationToken)
    {
        if (structureId <= 0 || await _structures.GetStructureInfoAsync(structureId, cancellationToken) is not { PersistentlyDeployed: true })
            return null;

        var table = await _readTable("structures", cancellationToken);
        var key = structureId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (table[key] is not System.Text.Json.Nodes.JsonObject row || !IsPersistedStructureRow(row, structureId))
            return null;
        var typeId = checked((int)Positive(Integer(row["typeID"])));
        var systemId = checked((int)Positive(Integer(row["solarSystemID"])));
        var ownerCorporationId = Positive(Integer(row["ownerCorpID"] ?? row["corporationID"]));
        if (!data.Systems.ContainsKey(systemId))
            return null;
        return new StructureRow(
            structureId, typeId, systemId, ownerCorporationId, Math.Max(0, Integer(row["allianceID"])),
            Text(row["name"] ?? row["structureName"] ?? row["itemName"]) is { Length: > 0 } name ? name : $"Structure {structureId}",
            ReadPosition(row));
    }

    private async System.Threading.Tasks.Task<System.Collections.Generic.List<StructureRow>> ReadPersistedStructuresAsync(
        long systemId,
        StaticData data,
        System.Threading.CancellationToken cancellationToken)
    {
        var table = await _readTable("structures", cancellationToken);
        var rows = new System.Collections.Generic.List<StructureRow>();
        foreach (var (key, value) in table)
        {
            if (!long.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                || value is not System.Text.Json.Nodes.JsonObject row
                || !IsPersistedStructureRow(row, id)
                || Integer(row["solarSystemID"]) != systemId)
                continue;
            var typeId = checked((int)Positive(Integer(row["typeID"])));

            if (!IsStandardUpwell(typeId, data))
                continue;
            rows.Add(new StructureRow(
                id, typeId, checked((int)systemId), Positive(Integer(row["ownerCorpID"] ?? row["corporationID"])),
                Math.Max(0, Integer(row["allianceID"])),
                Text(row["name"] ?? row["structureName"] ?? row["itemName"]) is { Length: > 0 } name ? name : $"Structure {id}",
                ReadPosition(row)));
        }
        return rows;
    }

    private static bool IsPersistedStructureRow(System.Text.Json.Nodes.JsonObject row, long structureId) =>
        structureId > 0
        && Positive(Integer(row["structureID"] ?? row["itemID"], structureId)) == structureId
        && Positive(Integer(row["typeID"])) > 0
        && Positive(Integer(row["solarSystemID"])) > 0
        && Positive(Integer(row["ownerCorpID"] ?? row["corporationID"])) > 0
        && !Truthy(row["destroyedAt"]);

    private static bool IsStandardUpwell(int typeId, StaticData data)
    {
        if (!data.Types.TryGetValue(typeId, out var type) || !type.Published
            || !data.Groups.TryGetValue(type.GroupId, out var group)
            || !data.Categories.TryGetValue(group.CategoryId, out var category)
            || !category.Equals("Structure", System.StringComparison.OrdinalIgnoreCase))
            return false;
        return AssetSafetyGroups.Contains(group.Name);
    }

    private static bool AllowsDocking(
        Eve.Services.Core.StructureDockingAccessSnapshot structure,
        long characterId,
        long corporationId,
        long allianceId)
    {
        if (!structure.PersistentlyDeployed || structure.OwnerCorporationId <= 0
            || structure.OwnerAllianceId < 0 || !structure.ProfileResolved || !structure.DockingSettingResolved
            || structure.AccessLists is not { Count: > 0 } lists
            || lists.Any(list => list is null || !list.IsLoaded || list.Members is null))
            return false;

        var seen = new System.Collections.Generic.HashSet<long>();
        foreach (var list in lists)
        {
            if (list is null || !seen.Add(list.AccessListId))
                return false;
        }
        foreach (var list in lists)
        {
            var specificity = new System.Collections.Generic.List<long>(4);
            if (characterId > 0) specificity.Add(characterId);
            if (corporationId > 0) specificity.Add(corporationId);
            if (allianceId > 0) specificity.Add(allianceId);
            specificity.Add(0);
            foreach (var memberId in specificity)
            {
                var matches = list!.Members!.Where(m => m.MemberId == memberId).ToArray();
                if (matches.Length > 1)
                    return false;
                if (matches.Length == 1)
                {
                    if (matches[0].IsAllowed)
                        return true;
                    break;
                }
            }
        }
        return false;
    }

    private async System.Threading.Tasks.Task<bool> IsCorporationExecutiveAsync(
        long characterId,
        long corporationId,
        System.Threading.CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var allowed = await IsCorporationExecutiveAsync(connection, transaction, characterId, corporationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return allowed;
    }

    private async System.Threading.Tasks.Task<bool> IsCorporationExecutiveAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long characterId,
        long corporationId,
        System.Threading.CancellationToken cancellationToken)
    {
        if (characterId <= 0 || corporationId <= 0)
            return false;

        long ceoId;
        bool ceoRolesSuspended;
        bool isNpc;
        bool deleted;
        await using (var corporation = new Npgsql.NpgsqlCommand(
            $"SELECT ceo_id, ceo_roles_suspended, is_npc, deleted FROM {_corporationSchema}.corporations WHERE id = $1 FOR SHARE",
            connection, transaction))
        {
            corporation.Parameters.Add(new Npgsql.NpgsqlParameter { Value = corporationId });
            await using var reader = await corporation.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return false;
            ceoId = reader.GetInt64(0);
            ceoRolesSuspended = reader.GetBoolean(1);
            isNpc = reader.GetBoolean(2);
            deleted = reader.GetBoolean(3);
        }
        if (isNpc || deleted)
            return false;

        long roles;
        await using (var member = new Npgsql.NpgsqlCommand(
            $"SELECT roles FROM {_corporationSchema}.members WHERE corporation_id = $1 AND character_id = $2 FOR SHARE",
            connection, transaction))
        {
            member.Parameters.Add(new Npgsql.NpgsqlParameter { Value = corporationId });
            member.Parameters.Add(new Npgsql.NpgsqlParameter { Value = characterId });
            var value = await member.ExecuteScalarAsync(cancellationToken);
            if (value is not long roleMask)
                return false;
            roles = roleMask;
        }
        return (!ceoRolesSuspended && ceoId == characterId) || (roles & DirectorRole) != 0;
    }

    private async System.Threading.Tasks.Task<System.Collections.Generic.List<Eve.Inventory.Item>> LockItemTreeAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long locationId,
        long ownerId,
        System.Threading.CancellationToken cancellationToken)
    {
        var ids = new System.Collections.Generic.List<long>();
        await using (var command = new Npgsql.NpgsqlCommand(
            $"""
            WITH RECURSIVE contents(id, path) AS (
                SELECT id, ARRAY[id]::bigint[]
                FROM {_inventorySchema}.items
                WHERE location_id = $1 AND owner_id = $2
                UNION ALL
                SELECT child.id, contents.path || child.id
                FROM {_inventorySchema}.items child
                JOIN contents ON child.location_id = contents.id
                WHERE NOT child.id = ANY(contents.path)
            )
            SELECT DISTINCT id FROM contents ORDER BY id
            """, connection, transaction))
        {
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = locationId });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = ownerId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetInt64(0));
        }

        var items = new System.Collections.Generic.List<Eve.Inventory.Item>(ids.Count);
        foreach (var id in ids)
        {
            var item = await _items.LockAndGetAsync(connection, transaction, id, cancellationToken);
            if (item is not null)
                items.Add(item);
        }
        return items;
    }

    private async System.Threading.Tasks.Task<decimal> FeeAsync(
        System.Collections.Generic.IReadOnlyList<Eve.Inventory.Item> items,
        bool sameSystem,
        System.Threading.CancellationToken cancellationToken)
    {
        var value = await EstimateValueAsync(items, cancellationToken);
        var rate = sameSystem ? 0.005m : 0.15m;
        return decimal.Round(value * rate, 2, System.MidpointRounding.AwayFromZero);
    }

    private async System.Threading.Tasks.Task<decimal> EstimateValueAsync(
        System.Collections.Generic.IReadOnlyList<Eve.Inventory.Item> items,
        System.Threading.CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return 0m;

        var typeIds = items.Select(i => (long)i.TypeId).Distinct().ToArray();
        var prices = new System.Collections.Generic.Dictionary<int, decimal>();
        try
        {
            foreach (var row in await _sde.ReadAsync("marketPrices", typeIds, cancellationToken))
            {
                var typeId = checked((int)row.Key);
                var average = DecimalProperty(row.Doc, "averagePrice");
                var adjusted = DecimalProperty(row.Doc, "adjustedPrice");
                var price = average > 0 ? average : adjusted;
                if (price > 0)
                    prices[typeId] = price;
            }
        }
        catch (Npgsql.PostgresException error) when (error.SqlState == "42P01")
        {
            // The current SDE build may not publish marketPrices; the type basePrice below remains usable.
        }

        var missing = typeIds.Where(id => !prices.ContainsKey(checked((int)id))).ToArray();
        if (missing.Length > 0)
        {
            foreach (var row in await _sde.ReadAsync("types", missing, cancellationToken))
            {
                var price = DecimalProperty(row.Doc, "basePrice");
                if (price > 0)
                    prices[checked((int)row.Key)] = price;
            }
        }

        decimal total = 0;
        foreach (var item in items)
        {
            var quantity = item.Singleton || item.Quantity <= 0 ? 1m : item.Quantity;
            var price = prices.GetValueOrDefault(item.TypeId);
            if (price <= 0)
            {
                // ponytail: Unknown-price items are valued at 1 ISK per unit so rare items do not block delivery or create an overcharge.
                price = 1m;
            }
            total += price * quantity;
        }
        return decimal.Round(total, 2, System.MidpointRounding.AwayFromZero);
    }

    private async System.Threading.Tasks.Task<StaticData> CatalogAsync(System.Threading.CancellationToken cancellationToken)
    {
        if (_catalog is not null)
            return _catalog;
        await _catalogGate.WaitAsync(cancellationToken);
        try
        {
            if (_catalog is not null)
                return _catalog;

            var summaries = await _sde.ReadTypeSummariesAsync(cancellationToken);
            var types = summaries.ToDictionary(t => t.TypeId);
            var groups = (await _sde.ReadAsync("groups", cancellationToken: cancellationToken))
                .ToDictionary(r => checked((int)r.Key), r => new GroupInfo(
                    checked((int)Integer(r.Doc, "categoryID")),
                    Eve.Sde.SdeReader.English(r.Doc.GetProperty("name"))));
            var categories = (await _sde.ReadAsync("categories", cancellationToken: cancellationToken))
                .ToDictionary(r => checked((int)r.Key), r => Eve.Sde.SdeReader.English(r.Doc.GetProperty("name")));
            var regions = (await _sde.ReadAsync("mapRegions", cancellationToken: cancellationToken))
                .ToDictionary(r => checked((int)r.Key), r => new RegionInfo(
                    Eve.Sde.SdeReader.English(r.Doc.GetProperty("name")),
                    Positive(Integer(r.Doc, "wormholeClassID")) > 0));
            var systems = new System.Collections.Generic.Dictionary<int, SystemInfo>();
            foreach (var row in await _sde.ReadAsync("mapSolarSystems", cancellationToken: cancellationToken))
            {
                var id = checked((int)row.Key);
                var regionId = checked((int)Integer(row.Doc, "regionID"));
                var region = regions.GetValueOrDefault(regionId);
                systems[id] = new SystemInfo(
                    id,
                    Eve.Sde.SdeReader.English(row.Doc.GetProperty("name")),
                    regionId,
                    row.Doc.TryGetProperty("securityStatus", out var security) && security.ValueKind == System.Text.Json.JsonValueKind.Number ? security.GetDouble() : 0,
                    ReadVector(row.Doc, "center"),
                    region?.Name.Equals("Pochven", System.StringComparison.OrdinalIgnoreCase) == true,
                    region?.Wormhole == true);
            }
            var stations = await Eve.Universe.StationCatalog.LoadAsync(_sde, cancellationToken);
            var cargoType = summaries.FirstOrDefault(t => t.Name.Equals("Cargo Container", System.StringComparison.OrdinalIgnoreCase));
            if (cargoType is null)
                throw new System.InvalidOperationException("The current SDE has no published Cargo Container type.");
            var wrapType = summaries.FirstOrDefault(t => t.Name.Equals("Asset Safety Wrap", System.StringComparison.OrdinalIgnoreCase)) ?? cargoType;
            var containerDetails = await _sde.ReadAsync("types", [cargoType.TypeId, wrapType.TypeId], cancellationToken);
            var details = containerDetails.ToDictionary(
                row => checked((int)row.Key),
                row => new TypeDetails(
                    DecimalProperty(row.Doc, "basePrice"),
                    DoubleProperty(row.Doc, "radius")));

            _catalog = new StaticData(types, groups, categories, systems, stations, cargoType, wrapType, details);
            return _catalog;
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private static Eve.Universe.Station? NearestAutomaticStation(int sourceSystemId, StaticData data)
    {
        if (!data.Systems.TryGetValue(sourceSystemId, out var source))
            return null;
        var high = source.Security >= HighSecurityMinimum;
        var bestDistance = double.PositiveInfinity;
        Eve.Universe.Station? best = null;
        foreach (var station in data.Stations.All)
        {
            if (!data.Systems.TryGetValue(station.SolarSystemId, out var system))
                continue;
            var eligibleSecurity = high
                ? system.Security >= HighSecurityMinimum
                : system.Security > 0 && system.Security < HighSecurityMinimum;
            if (!eligibleSecurity)
                continue;
            var distance = SquaredDistance(source.Position, system.Position);
            if (distance < bestDistance || (distance == bestDistance && station.Id < best?.Id))
            {
                bestDistance = distance;
                best = station;
            }
        }
        return best;
    }

    private static bool IsWormholeOrPochven(long systemId, StaticData data) =>
        data.Systems.TryGetValue(checked((int)systemId), out var system) && (system.Wormhole || system.Pochven);

    private static double SquaredDistance(double[] left, double[] right)
    {
        var x = left[0] - right[0];
        var y = left[1] - right[1];
        var z = left[2] - right[2];
        return x * x + y * y + z * z;
    }

    private static Eve.Marshal.PyValue WrapPayload(
        long wrapId,
        long solarSystemId,
        string name,
        long createdAtMs,
        Eve.Universe.Station? nearest)
    {
        return Eve.Services.Py.KeyVal(
            ("assetWrapID", new Eve.Marshal.PyLong(wrapId)),
            ("solarSystemID", new Eve.Marshal.PyLong(solarSystemId)),
            ("wrapName", new Eve.Marshal.PyStr(name)),
            ("ejectTime", new Eve.Marshal.PyLong(ToFileTime(createdAtMs))),
            ("daysUntilCanDeliverConst", new Eve.Marshal.PyInt(5)),
            ("daysUntilAutoMoveConst", new Eve.Marshal.PyInt(20)),
            ("nearestNPCStationInfo", NearestStationPayload(nearest)));
    }

    private static Eve.Marshal.PyValue NearestStationPayload(Eve.Universe.Station? station) =>
        station is null
            ? Eve.Marshal.PyNone.Instance
            : Eve.Services.Py.KeyVal(
                ("itemID", new Eve.Marshal.PyLong(station.Id)),
                ("typeID", new Eve.Marshal.PyInt(station.TypeId)));

    private static Eve.Marshal.PyValue DestinationPayload(long itemId, int typeId, string name) =>
        Eve.Services.Py.KeyVal(
            ("itemID", new Eve.Marshal.PyLong(itemId)),
            ("typeID", new Eve.Marshal.PyInt(typeId)),
            ("name", new Eve.Marshal.PyStr(name)));

    private static (int TypeId, int GroupId, double Radius) GetWrapMarkerType(StaticData data)
    {
        var type = data.WrapItem;
        var radius = data.TypeDetails.TryGetValue(type.TypeId, out var details) ? details.Radius : 0;
        return (type.TypeId, type.GroupId, radius);
    }

    private async System.Threading.Tasks.Task<WrapRecord?> ReadWrapForUpdateAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long wrapId,
        System.Threading.CancellationToken cancellationToken)
    {
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            SELECT wrap_id, scope, owner_id, source_structure_id, source_system_id, source_type_id,
                   source_name, system_name, wrap_name, created_at_ms, state
            FROM {_schema}.asset_safety_wraps
            WHERE wrap_id = $1
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = wrapId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new WrapRecord(
                reader.GetInt64(0), reader.GetInt16(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetInt64(9), reader.GetInt16(10))
            : null;
    }

    private async System.Threading.Tasks.Task<ReceiptReplay?> BeginReceiptAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        string operationKey,
        string action,
        System.Threading.CancellationToken cancellationToken)
    {
        await using var insert = new Npgsql.NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.asset_safety_operation_receipts (operation_key, action, result_state, created_at_ms)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (operation_key) DO NOTHING
            """, connection, transaction);
        insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = operationKey });
        insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = action });
        insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = (int)Eve.Services.Core.AssetSafetyActionState.NotCompleted });
        insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = _now() });
        if (await insert.ExecuteNonQueryAsync(cancellationToken) > 0)
            return null;

        await using var read = new Npgsql.NpgsqlCommand(
            $"SELECT action, result_state FROM {_schema}.asset_safety_operation_receipts WHERE operation_key = $1",
            connection, transaction);
        read.Parameters.Add(new Npgsql.NpgsqlParameter { Value = operationKey });
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        if (!reader.GetString(0).Equals(action, System.StringComparison.Ordinal))
            throw new System.InvalidOperationException("ASSET_SAFETY_RECEIPT_CONFLICT");
        return new ReceiptReplay((Eve.Services.Core.AssetSafetyActionState)reader.GetInt32(1));
    }

    private async System.Threading.Tasks.Task<Eve.Services.Core.AssetSafetyActionState> FinishReceiptAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        string operationKey,
        Eve.Services.Core.AssetSafetyActionState state,
        long wrapId,
        System.Threading.CancellationToken cancellationToken)
    {
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            UPDATE {_schema}.asset_safety_operation_receipts
            SET result_state = $2, wrap_id = $3
            WHERE operation_key = $1
            """, connection, transaction);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = operationKey });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = (int)state });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = wrapId });
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return state;
    }

    private async System.Threading.Tasks.Task SetDeliveredAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long wrapId,
        long destinationId,
        long deliveredAt,
        System.Threading.CancellationToken cancellationToken)
    {
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            UPDATE {_schema}.asset_safety_wraps
            SET state = $2, destination_id = $3, delivered_at_ms = $4
            WHERE wrap_id = $1 AND state = $5
            """, connection, transaction);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = wrapId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Delivered });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = destinationId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = deliveredAt });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Stored });
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new System.InvalidOperationException("ASSET_SAFETY_WRAP_STATE_CHANGED");
    }

    private string RequestKey(Eve.Services.ServiceCall call, string action, params long[] values)
    {
        var callId = call.CallId switch
        {
            Eve.Marshal.PyInt integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Eve.Marshal.PyLong integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Eve.Marshal.PyReal real => real.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _ => Eve.Macho.JsSemantics.StringValue(call.CallId) ?? call.CallId.ToString(),
        };
        return $"asset-safety:{action}:{call.Session.SessionId}:{call.Session.CharacterId ?? 0}:{callId}:{System.String.Join(':', values)}";
    }

    private static long ToFileTime(long unixMilliseconds) =>
        checked(unixMilliseconds * 10_000L + 116_444_736_000_000_000L);

    private static string SchemaName(string schema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new System.ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        return schema;
    }

    private static long Positive(long value) => value > 0 ? value : 0;

    private static long Integer(System.Text.Json.Nodes.JsonNode? node, long fallback = 0) =>
        long.TryParse(node?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value : fallback;

    private static double Number(System.Text.Json.Nodes.JsonNode? node, double fallback = 0) =>
        double.TryParse(node?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value : fallback;

    private static string Text(System.Text.Json.Nodes.JsonNode? node) => node?.ToString() ?? "";

    private static bool Truthy(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null)
            return false;
        var text = node.ToString();
        return text.Length > 0 && text != "0" && !text.Equals("false", System.StringComparison.OrdinalIgnoreCase);
    }

    private static double[]? ReadPosition(System.Text.Json.Nodes.JsonObject row)
    {
        if (row["position"] is System.Text.Json.Nodes.JsonArray array && array.Count >= 3)
        {
            var values = new[] { Number(array[0], double.NaN), Number(array[1], double.NaN), Number(array[2], double.NaN) };
            return values.All(double.IsFinite) ? values : null;
        }
        if (row["position"] is System.Text.Json.Nodes.JsonObject position)
        {
            var values = new[] { Number(position["x"], double.NaN), Number(position["y"], double.NaN), Number(position["z"], double.NaN) };
            return values.All(double.IsFinite) ? values : null;
        }
        var topLevel = new[] { Number(row["x"], double.NaN), Number(row["y"], double.NaN), Number(row["z"], double.NaN) };
        return topLevel.All(double.IsFinite) ? topLevel : null;
    }

    private static double[] ReadVector(System.Text.Json.JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var vector))
            return [0, 0, 0];
        if (vector.ValueKind == System.Text.Json.JsonValueKind.Array && vector.GetArrayLength() >= 3)
            return [vector[0].GetDouble(), vector[1].GetDouble(), vector[2].GetDouble()];
        if (vector.ValueKind == System.Text.Json.JsonValueKind.Object
            && vector.TryGetProperty("x", out var x) && vector.TryGetProperty("y", out var y) && vector.TryGetProperty("z", out var z))
            return [x.GetDouble(), y.GetDouble(), z.GetDouble()];
        return [0, 0, 0];
    }

    private static long Integer(System.Text.Json.JsonElement parent, string name, long fallback = 0) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? parsed : fallback;

    private static decimal DecimalProperty(System.Text.Json.JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetDecimal(out var parsed)
            ? parsed : 0m;

    private static double DoubleProperty(System.Text.Json.JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
            ? value.GetDouble() : 0d;

    private sealed record GroupInfo(int CategoryId, string Name);
    private sealed record RegionInfo(string Name, bool Wormhole);
    private sealed record SystemInfo(int Id, string Name, int RegionId, double Security, double[] Position, bool Pochven, bool Wormhole);
    private sealed record StructureRow(long Id, int TypeId, int SystemId, long OwnerCorporationId, long OwnerAllianceId, string Name, double[]? Position);
    private sealed record TypeDetails(decimal BasePrice, double Radius);
    private sealed record WrapRecord(long Id, short Scope, long OwnerId, long SourceStructureId, int SourceSystemId, int SourceTypeId, string SourceName, string SystemName, string WrapName, long CreatedAtMs, short State);
    private sealed record ReceiptReplay(Eve.Services.Core.AssetSafetyActionState State);
    private sealed record DestinationCheck(Eve.Services.Core.AssetSafetyActionState State, long SystemId);
    private sealed record StaticData(
        System.Collections.Generic.Dictionary<int, Eve.Sde.SdeTypeSummary> Types,
        System.Collections.Generic.Dictionary<int, GroupInfo> Groups,
        System.Collections.Generic.Dictionary<int, string> Categories,
        System.Collections.Generic.Dictionary<int, SystemInfo> Systems,
        Eve.Universe.StationCatalog Stations,
        Eve.Sde.SdeTypeSummary CargoContainer,
        Eve.Sde.SdeTypeSummary WrapItem,
        System.Collections.Generic.Dictionary<int, TypeDetails> TypeDetails)
    {
        public bool IsStandardUpwell(int typeId) => PersistenceStructureAssetSafetyReadModel.IsStandardUpwell(typeId, this);
        public string GroupNameOf(int typeId) =>
            Types.TryGetValue(typeId, out var type) && Groups.TryGetValue(type.GroupId, out var group) ? group.Name : "";
    }
}
