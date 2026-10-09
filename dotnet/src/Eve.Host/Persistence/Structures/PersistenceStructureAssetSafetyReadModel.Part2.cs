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
    public static System.Collections.Generic.IReadOnlyList<Eve.Data.Migration> MigrationSet() =>
    [
        new Eve.Data.Migration("0001_asset_safety", """
            CREATE TABLE asset_safety_wraps (
                wrap_id bigint PRIMARY KEY,
                scope smallint NOT NULL CHECK (scope IN (1, 2)),
                owner_id bigint NOT NULL CHECK (owner_id > 0),
                corporation_id bigint NOT NULL DEFAULT 0,
                source_structure_id bigint NOT NULL CHECK (source_structure_id > 0),
                source_system_id integer NOT NULL CHECK (source_system_id > 0),
                source_type_id integer NOT NULL CHECK (source_type_id > 0),
                source_name text NOT NULL,
                system_name text NOT NULL,
                wrap_name text NOT NULL,
                created_at_ms bigint NOT NULL,
                state smallint NOT NULL CHECK (state IN (0, 1, 2)),
                destination_id bigint,
                delivered_at_ms bigint
            );
            CREATE INDEX asset_safety_wraps_owner_state
                ON asset_safety_wraps (scope, owner_id, state, created_at_ms);
            CREATE INDEX asset_safety_wraps_due
                ON asset_safety_wraps (created_at_ms, wrap_id)
                WHERE state = 0;
            CREATE TABLE asset_safety_operation_receipts (
                operation_key text PRIMARY KEY,
                action text NOT NULL,
                wrap_id bigint NOT NULL DEFAULT 0,
                result_state integer NOT NULL,
                created_at_ms bigint NOT NULL
            );
            """),
    ];

    public static Task MigrateAsync(Npgsql.NpgsqlDataSource source, string schema = DefaultSchema) =>
        Eve.Data.Migrations.ApplyAsync(source, SchemaName(schema), MigrationSet());

    public async System.Threading.Tasks.ValueTask<Eve.Marshal.PyList> GetItemsInSafetyForCharacterAsync(
        long characterId,
        System.Threading.CancellationToken cancellationToken)
    {
        if (characterId <= 0)
            return new Eve.Marshal.PyList([]);

        var data = await CatalogAsync(cancellationToken);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            SELECT wrap_id, source_system_id, wrap_name, created_at_ms
            FROM {_schema}.asset_safety_wraps
            WHERE scope = $1 AND owner_id = $2 AND state = $3
            ORDER BY created_at_ms, wrap_id
            """, connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Personal });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Stored });
        var rows = new System.Collections.Generic.List<Eve.Marshal.PyValue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var station = NearestAutomaticStation(reader.GetInt32(1), data);
            rows.Add(WrapPayload(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3), station));
        }
        return new Eve.Marshal.PyList(rows);
    }

    public async System.Threading.Tasks.ValueTask<Eve.Marshal.PyList> GetItemsInSafetyForCorpAsync(
        long characterId,
        long corporationId,
        System.Threading.CancellationToken cancellationToken)
    {
        if (characterId <= 0 || corporationId <= 0)
            return new Eve.Marshal.PyList([]);

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await IsCorporationExecutiveAsync(connection, transaction, characterId, corporationId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new Eve.Marshal.PyList([]);
        }

        var data = await CatalogAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            SELECT wrap_id, source_system_id, wrap_name, created_at_ms
            FROM {_schema}.asset_safety_wraps
            WHERE scope = $1 AND owner_id = $2 AND state = $3
            ORDER BY created_at_ms, wrap_id
            """, connection, transaction);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Corporation });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = corporationId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Stored });
        var rows = new System.Collections.Generic.List<Eve.Marshal.PyValue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var station = NearestAutomaticStation(reader.GetInt32(1), data);
            rows.Add(WrapPayload(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3), station));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return new Eve.Marshal.PyList(rows);
    }

    public async System.Threading.Tasks.ValueTask<Eve.Marshal.PyDict> GetWrapNamesAsync(
        long characterId,
        long corporationId,
        System.Collections.Generic.IReadOnlyList<long> wrapIds,
        System.Threading.CancellationToken cancellationToken)
    {
        if (characterId <= 0 || wrapIds.Count == 0)
            return new Eve.Marshal.PyDict([]);

        var mayReadCorp = corporationId > 0 && await IsCorporationExecutiveAsync(characterId, corporationId, cancellationToken);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            SELECT wrap_id, scope, owner_id, wrap_name
            FROM {_schema}.asset_safety_wraps
            WHERE wrap_id = ANY($1) AND state = $2
            ORDER BY wrap_id
            """, connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = wrapIds.Distinct().ToArray() });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Stored });
        var entries = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var scope = reader.GetInt16(1);
            var owner = reader.GetInt64(2);
            var allowed = scope == Personal
                ? owner == characterId
                : scope == Corporation && owner == corporationId && mayReadCorp;
            if (allowed)
            {
                entries.Add(new(
                    new Eve.Marshal.PyLong(reader.GetInt64(0)),
                    new Eve.Marshal.PyStr(reader.GetString(3))));
            }
        }
        return new Eve.Marshal.PyDict(entries);
    }

    public async System.Threading.Tasks.ValueTask<Eve.Marshal.PyTuple> GetStructuresICanDeliverToAsync(
        long characterId,
        long corporationId,
        long solarSystemId,
        System.Threading.CancellationToken cancellationToken)
    {
        if (characterId <= 0 || solarSystemId <= 0)
            return new Eve.Marshal.PyTuple([new Eve.Marshal.PyList([]), Eve.Marshal.PyNone.Instance]);

        var data = await CatalogAsync(cancellationToken);
        var destinations = new System.Collections.Generic.List<Eve.Marshal.PyValue>();
        foreach (var station in data.Stations.All.Where(s => s.SolarSystemId == solarSystemId).OrderBy(s => s.Id))
            destinations.Add(DestinationPayload(station.Id, station.TypeId, station.Name));

        var affiliation = await _affiliationOf(characterId, cancellationToken);
        if (corporationId > 0 && affiliation.Corporation == corporationId)
        {
            var rows = await ReadPersistedStructuresAsync(solarSystemId, data, cancellationToken);
            var snapshots = rows.Count == 0
                ? System.Array.Empty<Eve.Services.Core.StructureDockingAccessSnapshot>()
                : await _structures.GetDockingAccessSnapshotsAsync(
                    characterId, corporationId, affiliation.Alliance ?? 0, rows.Select(r => r.Id).ToArray(), cancellationToken);
            foreach (var row in rows)
            {
                var snapshot = snapshots.FirstOrDefault(s => s.StructureId == row.Id);
                if (snapshot is not null && AllowsDocking(snapshot, characterId, corporationId, affiliation.Alliance ?? 0))
                    destinations.Add(DestinationPayload(row.Id, row.TypeId, row.Name));
            }
        }

        return new Eve.Marshal.PyTuple([
            new Eve.Marshal.PyList(destinations),
            NearestStationPayload(NearestAutomaticStation((int)solarSystemId, data)),
        ]);
    }

    public async System.Threading.Tasks.ValueTask<bool> MoveEjectTimeGMAsync(
        Eve.Services.ServiceCall call,
        long assetWrapId,
        int days,
        System.Threading.CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const long roleGml = 1L << 54;
        if (assetWrapId <= 0 || days is not (5 or 30) ||
            ((call.Session?.GetInt("role") ?? 0) & roleGml) == 0)
            return false;

        var deltaMs = checked((long)days * DayMs);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            UPDATE {_schema}.asset_safety_wraps
            SET created_at_ms = created_at_ms + $2
            WHERE wrap_id = $1 AND state = $3
            RETURNING wrap_id
            """,
            connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = assetWrapId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = deltaMs });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Stored });
        var updated = await command.ExecuteScalarAsync(cancellationToken);
        return updated is not null && updated is not System.DBNull;
    }

    public System.Threading.Tasks.ValueTask<Eve.Services.Core.AssetSafetyActionState> MovePersonalAssetsToSafetyAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        long structureId,
        System.Threading.CancellationToken cancellationToken) =>
        TriggerAssetsAsync(call, solarSystemId, structureId, Personal, call.Session.CharacterId ?? 0, cancellationToken);

    public System.Threading.Tasks.ValueTask<Eve.Services.Core.AssetSafetyActionState> MoveCorpAssetsToSafetyAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        long structureId,
        System.Threading.CancellationToken cancellationToken)
    {
        var corporationId = call.Session.GetInt("corpid") ?? 0;
        if (corporationId <= 0)
            return System.Threading.Tasks.ValueTask.FromResult(Eve.Services.Core.AssetSafetyActionState.NotAuthorized);
        return TriggerAssetsAsync(call, solarSystemId, structureId, Corporation, corporationId, cancellationToken);
    }

    public async System.Threading.Tasks.ValueTask<Eve.Services.Core.AssetSafetyActionState> MoveSafetyWrapToStructureAsync(
        Eve.Services.ServiceCall call,
        long assetWrapId,
        long solarSystemId,
        long destinationId,
        System.Threading.CancellationToken cancellationToken)
    {
        if (assetWrapId <= 0 || solarSystemId <= 0 || destinationId <= 0 || call.Session.CharacterId is not { } characterId || characterId <= 0)
            return Eve.Services.Core.AssetSafetyActionState.InvalidDestination;

        var operationKey = RequestKey(call, "release", assetWrapId, solarSystemId, destinationId);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
            var replay = await BeginReceiptAsync(connection, transaction, operationKey, "release", cancellationToken);
            if (replay is { } prior)
            {
                await transaction.CommitAsync(cancellationToken);
                return prior.State;
            }

            var wrap = await ReadWrapForUpdateAsync(connection, transaction, assetWrapId, cancellationToken);
            if (wrap is null)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.InvalidDestination, assetWrapId, cancellationToken);

            if (wrap.State == Delivered)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.WrapDelivered, assetWrapId, cancellationToken);
            if (wrap.State != Stored)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.InvalidDestination, assetWrapId, cancellationToken);

            if (wrap.Scope == Personal)
            {
                if (wrap.OwnerId != characterId)
                    return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.NotAuthorized, assetWrapId, cancellationToken);
            }
            else if (wrap.Scope == Corporation)
            {
                if ((call.Session.GetInt("corpid") ?? 0) != wrap.OwnerId
                    || !await IsCorporationExecutiveAsync(connection, transaction, characterId, wrap.OwnerId, cancellationToken))
                    return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.NotAuthorized, assetWrapId, cancellationToken);
            }
            else
            {
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.InvalidDestination, assetWrapId, cancellationToken);
            }

            if (wrap.SourceSystemId != solarSystemId)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.SolarSystemMismatch, assetWrapId, cancellationToken);
            if (_now() - wrap.CreatedAtMs < 5 * DayMs)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.DeliveryNotReady, assetWrapId, cancellationToken);

            var data = await CatalogAsync(cancellationToken);
            var target = await ValidateDestinationAsync(
                characterId, wrap.Scope == Corporation ? wrap.OwnerId : (call.Session.GetInt("corpid") ?? 0),
                destinationId, solarSystemId, data, cancellationToken);
            if (target.State != Eve.Services.Core.AssetSafetyActionState.WrapDelivered)
                return await FinishReceiptAsync(connection, transaction, operationKey, target.State, assetWrapId, cancellationToken);

            var contents = await LockItemTreeAsync(connection, transaction, assetWrapId, wrap.OwnerId, cancellationToken);
            var fee = await FeeAsync(contents, wrap.SourceSystemId == target.SystemId, cancellationToken);
            if (fee > 0)
            {
                await _wallets.AdjustAsync(
                    connection, transaction, wrap.OwnerId, -fee, destinationId, AssetSafetyRecoveryTaxRefTypeId,
                    "Asset Safety recovery", _now(), $"asset-safety-wrap:{assetWrapId}", cancellationToken, assetWrapId);
            }

            foreach (var item in contents.Where(i => i.LocationId == assetWrapId && i.OwnerId == wrap.OwnerId))
                await _items.MoveAsync(connection, transaction, item.Id, destinationId, item.Flag, cancellationToken);

            await _items.DeleteAsync(connection, transaction, assetWrapId, cancellationToken);
            await SetDeliveredAsync(connection, transaction, assetWrapId, destinationId, _now(), cancellationToken);
            return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.WrapDelivered, assetWrapId, cancellationToken);
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _log?.Invoke($"asset safety delivery for wrap {assetWrapId} failed: {error.Message}");
            return Eve.Services.Core.AssetSafetyActionState.PersistenceFailure;
        }
    }

    public async System.Threading.Tasks.ValueTask ProcessDueAutomaticDeliveriesAsync(
        System.Threading.CancellationToken cancellationToken)
    {
        var data = await CatalogAsync(cancellationToken);
        var now = _now();
        var due = new System.Collections.Generic.List<long>();
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var command = new Npgsql.NpgsqlCommand(
            $"""
            SELECT wrap_id
            FROM {_schema}.asset_safety_wraps
            WHERE state = $1 AND created_at_ms <= $2
            ORDER BY created_at_ms, wrap_id
            """, connection))
        {
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Stored });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = now - 20 * DayMs });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                due.Add(reader.GetInt64(0));
        }

        foreach (var wrapId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DeliverAutomaticallyAsync(wrapId, data, cancellationToken);
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                _log?.Invoke($"automatic asset safety delivery for wrap {wrapId} failed: {error.Message}");
            }
        }
    }

    private async System.Threading.Tasks.ValueTask<Eve.Services.Core.AssetSafetyActionState> TriggerAssetsAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        long structureId,
        short scope,
        long ownerId,
        System.Threading.CancellationToken cancellationToken)
    {
        var characterId = call.Session.CharacterId ?? 0;
        var corporationId = scope == Corporation ? ownerId : call.Session.GetInt("corpid") ?? 0;
        if (characterId <= 0 || ownerId <= 0 || solarSystemId <= 0 || structureId <= 0)
            return Eve.Services.Core.AssetSafetyActionState.NotAuthorized;
        if (scope == Corporation && corporationId != (call.Session.GetInt("corpid") ?? 0))
            return Eve.Services.Core.AssetSafetyActionState.NotAuthorized;

        var operationKey = RequestKey(call, scope == Corporation ? "corp-trigger" : "personal-trigger", structureId, solarSystemId, ownerId);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
            var replay = await BeginReceiptAsync(connection, transaction, operationKey, scope == Corporation ? "corp-trigger" : "personal-trigger", cancellationToken);
            if (replay is { } prior)
            {
                await transaction.CommitAsync(cancellationToken);
                return prior.State;
            }

            if (scope == Corporation
                && !await IsCorporationExecutiveAsync(connection, transaction, characterId, corporationId, cancellationToken))
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.NotAuthorized, 0, cancellationToken);

            var data = await CatalogAsync(cancellationToken);
            var source = await ReadPersistedStructureAsync(structureId, data, cancellationToken);
            if (source is null)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.StructureUnavailable, 0, cancellationToken);
            if (source.SystemId != solarSystemId)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.SolarSystemMismatch, 0, cancellationToken);
            if (!IsStandardUpwell(source.TypeId, data))
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.AssetSafetyUnavailable, 0, cancellationToken);

            var ejected = IsWormholeOrPochven(source.SystemId, data);
            if (ejected && source.Position is null)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.StructureUnavailable, 0, cancellationToken);

            var contents = await LockItemTreeAsync(connection, transaction, structureId, ownerId, cancellationToken);
            var roots = contents.Where(i => i.LocationId == structureId && i.OwnerId == ownerId).ToArray();
            if (roots.Length == 0)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.NoItemsToMove, 0, cancellationToken);

            var wrapName = $"{data.Systems[source.SystemId].Name} - {source.Name} Asset Safety";
            var markerType = GetWrapMarkerType(data);
            var markerLocation = ejected ? solarSystemId : 0;
            var markerOwner = ejected ? 0 : ownerId;
            var markerId = await _items.CreateAsync(
                connection, transaction,
                new Eve.Inventory.NewItem(markerType.TypeId, markerOwner, markerLocation, 0, Singleton: true, CustomName: wrapName),
                cancellationToken);

            if (ejected)
            {
                var spaceItem = new Eve.Ships.SpaceItem(
                    markerId, checked((int)solarSystemId), source.Position!, [0, 0, 1],
                    markerType.Radius > 0 ? markerType.Radius : 1,
                    markerType.GroupId, 0, 0, 0, 0, 0, _now() + (long)System.TimeSpan.FromHours(2).TotalMilliseconds);
                await _spaceItems.SaveAsync(connection, transaction, spaceItem, cancellationToken);
                var rootIds = roots.Select(i => i.Id).ToHashSet();
                foreach (var item in contents)
                {
                    var location = rootIds.Contains(item.Id) ? markerId : item.LocationId;
                    await _items.MoveToOwnerAsync(connection, transaction, item.Id, 0, location, item.Flag, cancellationToken);
                }
            }
            else
            {
                foreach (var item in roots)
                    await _items.MoveAsync(connection, transaction, item.Id, markerId, item.Flag, cancellationToken);
            }

            await using (var insert = new Npgsql.NpgsqlCommand(
                $"""
                INSERT INTO {_schema}.asset_safety_wraps
                    (wrap_id, scope, owner_id, corporation_id, source_structure_id, source_system_id, source_type_id,
                     source_name, system_name, wrap_name, created_at_ms, state)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
                """, connection, transaction))
            {
                foreach (var value in new object[]
                {
                    markerId, scope, ownerId, corporationId, structureId, checked((int)solarSystemId), source.TypeId,
                    source.Name, data.Systems[source.SystemId].Name, wrapName, _now(), ejected ? Ejected : Stored,
                })
                    insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = value });
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return await FinishReceiptAsync(
                connection, transaction, operationKey,
                ejected ? Eve.Services.Core.AssetSafetyActionState.ItemsEjected
                        : scope == Corporation ? Eve.Services.Core.AssetSafetyActionState.CorporationWrapCreated
                                               : Eve.Services.Core.AssetSafetyActionState.PersonalWrapCreated,
                markerId, cancellationToken);
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _log?.Invoke($"asset safety trigger for structure {structureId} failed: {error.Message}");
            return Eve.Services.Core.AssetSafetyActionState.PersistenceFailure;
        }
    }
}
