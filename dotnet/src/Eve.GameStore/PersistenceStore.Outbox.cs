using Npgsql;
using NpgsqlTypes;

namespace Eve.GameStore;

/// <summary>
/// A flush journals its exact batch before applying it, so recovery preserves
/// deletes as well as upserts.
/// </summary>
public sealed partial class PersistenceStore
{
    private const string OperationColumns =
        "operation_id, table_name, owner_role, owner_epoch, owner_instance_id, upserts::text, deletes::text, state, created_at, applied_at";

    private static PersistenceOperation ReadOperation(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        TableNames.RequirePersistence(reader.GetString(1), $"persistence operation {reader.GetInt64(0)} table"),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.GetString(4),
        ParseUpserts(reader.GetString(5)),
        ParseDeletes(reader.GetString(6)),
        reader.GetString(7) == "applied" ? OperationState.Applied : OperationState.Pending,
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));

    private async Task<PersistenceOperation?> ReadOperationAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long operationId, CancellationToken ct)
    {
        await using var command = Command(
            connection, tx,
            $"SELECT {OperationColumns} FROM {Qualified(TableNames.Outbox)} WHERE operation_id = @id FOR UPDATE",
            ("id", NpgsqlDbType.Bigint, operationId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadOperation(reader) : null;
    }

    private static PersistenceOperation RequireOwnedBy(PersistenceOperation operation, OwnerFence fence)
    {
        if (operation.OwnerRole != fence.OwnerRole)
        {
            throw new PersistenceException(
                $"persistence operation {operation.OperationId} belongs to {operation.OwnerRole}, not {fence.OwnerRole}",
                PersistenceCodes.OperationOwnerMismatch,
                new Dictionary<string, object?>
                {
                    ["operationId"] = operation.OperationId,
                    ["ownerRole"] = operation.OwnerRole,
                    ["expectedOwnerRole"] = fence.OwnerRole,
                });
        }
        return operation;
    }

    private static void RequireTable(PersistenceOperation operation, string expectedTable)
    {
        var table = TableNames.RequirePersistence(expectedTable, "expected persistence table");
        if (operation.Table != table)
        {
            throw new InvalidOperationException($"persistence operation {operation.OperationId} targets {operation.Table}, not {table}");
        }
    }

    /// <summary>Journals a batch for <paramref name="table"/>. Fails with <c>PERSISTENCE_OPERATION_PENDING</c> while an earlier batch of that table is still open.</summary>
    public async Task<PersistenceOperation> EnqueueAsync(
        string table,
        IEnumerable<RowUpsert>? upserts,
        IEnumerable<string>? deletes,
        OwnerFence fence,
        CancellationToken ct = default)
    {
        var name = TableNames.RequirePersistence(table);
        var validatedUpserts = ValidateUpserts(upserts);
        var validatedDeletes = ValidateDeletes(deletes);

        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var active = await AssertActiveAsync(connection, tx, fence, ct);
        // Publish table creation in the fenced transaction.
        await CreateTableAsync(connection, tx, name, ct);
        try
        {
            await using var command = Command(
                connection, tx,
                $"""
                INSERT INTO {Qualified(TableNames.Outbox)} (table_name, owner_role, owner_epoch, owner_instance_id, upserts, deletes)
                VALUES (@table, @role, @epoch, @instance, @upserts, @deletes)
                RETURNING {OperationColumns}
                """,
                ("table", NpgsqlDbType.Text, name), ("role", NpgsqlDbType.Text, active.OwnerRole),
                ("epoch", NpgsqlDbType.Bigint, active.Epoch), ("instance", NpgsqlDbType.Text, active.InstanceId),
                ("upserts", NpgsqlDbType.Jsonb, SerializeUpserts(validatedUpserts)),
                ("deletes", NpgsqlDbType.Jsonb, SerializeDeletes(validatedDeletes)));
            PersistenceOperation operation;
            await using (var reader = await RunAsync(() => command.ExecuteReaderAsync(ct)))
            {
                await reader.ReadAsync(ct);
                operation = ReadOperation(reader);
            }
            await tx.CommitAsync(ct);
            _ensuredTables.TryAdd(name, 0);
            return operation;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new PersistenceException(
                $"a persistence operation for {name} is already pending",
                PersistenceCodes.OperationPending,
                new Dictionary<string, object?> { ["table"] = name },
                ex);
        }
    }

    public async Task<PersistenceOperation?> GetOperationAsync(long operationId, OwnerFence fence, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var active = await AssertActiveAsync(connection, tx, fence, ct);
        var operation = await ReadOperationAsync(connection, tx, operationId, ct);
        return operation is null ? null : RequireOwnedBy(operation, active);
    }

    public async Task<IReadOnlyList<PersistenceOperation>> ListOperationsAsync(OwnerFence fence, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var active = await AssertActiveAsync(connection, tx, fence, ct);
        return await ListOperationsCoreAsync(connection, tx, active, ct);
    }

    private async Task<List<PersistenceOperation>> ListOperationsCoreAsync(NpgsqlConnection connection, NpgsqlTransaction tx, OwnerFence active, CancellationToken ct)
    {
        await using var command = Command(
            connection, tx,
            $"SELECT {OperationColumns} FROM {Qualified(TableNames.Outbox)} WHERE owner_role = @role ORDER BY operation_id",
            ("role", NpgsqlDbType.Text, active.OwnerRole));
        var operations = new List<PersistenceOperation>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            operations.Add(ReadOperation(reader));
        }
        return operations;
    }

    public async Task<IReadOnlyList<PersistenceOperation>> ListAllOperationsAsync(CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var command = Command(connection, null, $"SELECT {OperationColumns} FROM {Qualified(TableNames.Outbox)} ORDER BY operation_id");
        var operations = new List<PersistenceOperation>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            operations.Add(ReadOperation(reader));
        }
        return operations;
    }

    public async Task AssertNoForeignOperationsAsync(OwnerFence fence, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var active = await AssertActiveAsync(connection, tx, fence, ct);
        var stored = await ReadOwnerAsync(connection, tx, active.OwnerRole, RowLock.None, ct);
        if (active.OwnerRole != PersistenceRoles.Maintenance || stored is not { Exclusive: true })
        {
            throw new PersistenceException(
                "foreign persistence operation inspection requires the active exclusive maintenance owner",
                PersistenceCodes.MaintenanceFenceRequired,
                new Dictionary<string, object?> { ["ownerRole"] = active.OwnerRole, ["ownerEpoch"] = active.Epoch });
        }
        var foreign = new List<string>();
        await using var command = Command(
            connection, tx,
            $"SELECT owner_role, table_name, operation_id FROM {Qualified(TableNames.Outbox)} WHERE owner_role <> @role ORDER BY operation_id",
            ("role", NpgsqlDbType.Text, active.OwnerRole));
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                foreign.Add($"{reader.GetString(0)}:{reader.GetString(1)}#{reader.GetInt64(2)}");
            }
        }
        if (foreign.Count > 0)
        {
            throw new PersistenceException(
                $"exclusive maintenance is blocked by {foreign.Count} foreign persistence operation{(foreign.Count == 1 ? "" : "s")}: {string.Join(", ", foreign)}",
                PersistenceCodes.ForeignOperationsPending,
                new Dictionary<string, object?> { ["ownerRole"] = active.OwnerRole, ["foreignOperations"] = foreign.ToArray() });
        }
    }

    /// <summary>Applies a journaled batch and marks it applied. A batch already removed by reconciliation is a harmless no-op (null).</summary>
    public async Task<PersistenceOperation?> ApplyOperationAsync(long operationId, string? expectedTable, OwnerFence fence, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var active = await AssertActiveAsync(connection, tx, fence, ct);
        var operation = await ReadOperationAsync(connection, tx, operationId, ct);
        if (operation is null)
        {
            return null;
        }
        RequireOwnedBy(operation, active);
        if (expectedTable is not null)
        {
            RequireTable(operation, expectedTable);
        }
        await ApplyChangesCoreAsync(connection, tx, operation.Table, operation.Upserts, operation.Deletes, ct);
        var appliedAt = await MarkAppliedAsync(connection, tx, operation, ct);
        await tx.CommitAsync(ct);
        return operation with { State = OperationState.Applied, AppliedAt = appliedAt };
    }

    private async Task<DateTimeOffset> MarkAppliedAsync(NpgsqlConnection connection, NpgsqlTransaction tx, PersistenceOperation operation, CancellationToken ct)
    {
        await using var command = Command(
            connection, tx,
            $"""
            UPDATE {Qualified(TableNames.Outbox)}
            SET state = 'applied', applied_at = COALESCE(applied_at, clock_timestamp())
            WHERE operation_id = @id AND table_name = @table
            RETURNING applied_at
            """,
            ("id", NpgsqlDbType.Bigint, operation.OperationId), ("table", NpgsqlDbType.Text, operation.Table));
        var appliedAt = await command.ExecuteScalarAsync(ct);
        return appliedAt is DateTime value
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : appliedAt is DateTimeOffset offset
                ? offset
                : throw new InvalidOperationException($"failed to mark persistence operation {operation.OperationId} applied");
    }

    /// <summary>Removes an applied batch from the journal.</summary>
    public async Task<PersistenceOperation> AcknowledgeOperationAsync(long operationId, string expectedTable, OwnerFence fence, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var active = await AssertActiveAsync(connection, tx, fence, ct);
        var operation = await ReadOperationAsync(connection, tx, operationId, ct)
            ?? throw new PersistenceException($"persistence operation {operationId} does not exist", PersistenceCodes.OperationNotFound);
        RequireOwnedBy(operation, active);
        RequireTable(operation, expectedTable);
        if (operation.State != OperationState.Applied)
        {
            throw new InvalidOperationException($"persistence operation {operationId} cannot be acknowledged from state {operation.State.ToString().ToLowerInvariant()}");
        }
        var removed = await ExecuteAsync(
            connection, tx,
            $"DELETE FROM {Qualified(TableNames.Outbox)} WHERE operation_id = @id AND table_name = @table AND state = 'applied'",
            ct, ("id", NpgsqlDbType.Bigint, operation.OperationId), ("table", NpgsqlDbType.Text, operation.Table));
        if (removed != 1)
        {
            throw new InvalidOperationException($"failed to acknowledge persistence operation {operation.OperationId}");
        }
        await tx.CommitAsync(ct);
        return operation;
    }

    /// <summary>Applies a journaled batch and removes it in one transaction. Null when it is already gone.</summary>
    public async Task<PersistenceOperation?> ReconcileOperationAsync(long operationId, string expectedTable, OwnerFence fence, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var active = await AssertActiveAsync(connection, tx, fence, ct);
        var operation = await ReadOperationAsync(connection, tx, operationId, ct);
        if (operation is null)
        {
            return null;
        }
        RequireOwnedBy(operation, active);
        RequireTable(operation, expectedTable);
        await ApplyChangesCoreAsync(connection, tx, operation.Table, operation.Upserts, operation.Deletes, ct);
        var removed = await ExecuteAsync(
            connection, tx,
            $"DELETE FROM {Qualified(TableNames.Outbox)} WHERE operation_id = @id AND table_name = @table",
            ct, ("id", NpgsqlDbType.Bigint, operation.OperationId), ("table", NpgsqlDbType.Text, operation.Table));
        if (removed != 1)
        {
            throw new InvalidOperationException($"failed to reconcile persistence operation {operation.OperationId}");
        }
        await tx.CommitAsync(ct);
        return operation with { State = OperationState.Applied, AppliedAt = operation.AppliedAt ?? DateTimeOffset.UtcNow };
    }

    public async Task<IReadOnlyList<PersistenceOperation>> RecoverOperationsAsync(OwnerFence fence, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var active = await AssertActiveAsync(connection, tx, fence, ct);
        var recovered = new List<PersistenceOperation>();
        foreach (var operation in await ListOperationsCoreAsync(connection, tx, active, ct))
        {
            await ApplyChangesCoreAsync(connection, tx, operation.Table, operation.Upserts, operation.Deletes, ct);
            var appliedAt = await MarkAppliedAsync(connection, tx, operation, ct);
            recovered.Add(operation with { State = OperationState.Applied, AppliedAt = appliedAt });
        }
        await tx.CommitAsync(ct);
        return recovered;
    }

    public async Task<IReadOnlyList<PersistenceOperation>> AcknowledgeRecoveredAsync(IEnumerable<long> operationIds, OwnerFence fence, CancellationToken ct = default)
    {
        var ids = operationIds.Distinct().ToList();
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var active = await AssertActiveAsync(connection, tx, fence, ct);
        var acknowledged = new List<PersistenceOperation>();
        foreach (var id in ids)
        {
            var operation = await ReadOperationAsync(connection, tx, id, ct)
                ?? throw new PersistenceException($"recovered persistence operation {id} does not exist", PersistenceCodes.OperationNotFound, new Dictionary<string, object?> { ["operationId"] = id });
            RequireOwnedBy(operation, active);
            if (operation.State != OperationState.Applied)
            {
                throw new InvalidOperationException($"recovered persistence operation {id} cannot be acknowledged from state {operation.State.ToString().ToLowerInvariant()}");
            }
            acknowledged.Add(operation);
        }
        foreach (var operation in acknowledged)
        {
            var removed = await ExecuteAsync(
                connection, tx,
                $"DELETE FROM {Qualified(TableNames.Outbox)} WHERE operation_id = @id AND table_name = @table AND owner_role = @role AND state = 'applied'",
                ct,
                ("id", NpgsqlDbType.Bigint, operation.OperationId), ("table", NpgsqlDbType.Text, operation.Table),
                ("role", NpgsqlDbType.Text, active.OwnerRole));
            if (removed != 1)
            {
                throw new InvalidOperationException($"failed to acknowledge recovered persistence operation {operation.OperationId}");
            }
        }
        await tx.CommitAsync(ct);
        return acknowledged;
    }
}
