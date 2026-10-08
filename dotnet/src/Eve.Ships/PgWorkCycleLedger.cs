using Eve.Data;
using Npgsql;

namespace Eve.Ships;

/// <summary>The answer the host gave to one work cycle of an owner (a drone or a module).</summary>
public sealed record WorkCycleRecord(long OwnerId, long CycleId, bool Accepted, string Reason, long UnitsRemaining, long DeliveredUnits, bool Complete);

/// <summary>
/// The last work cycle the host settled for each owner, in PostgreSQL in the space schema (<c>space_work_cycles</c>). A repeated cycle id of an owner is answered from
/// this record, so a cycle that is re-sent after a host restart is not granted twice (ADR 0013, rule 4).
/// </summary>
public sealed class PgWorkCycleLedger
{
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgWorkCycleLedger(NpgsqlDataSource source, string schema = PgSpaceItemStore.DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    /// <summary>The table is made with the space items (the same migration set, the same schema).</summary>
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = PgSpaceItemStore.DefaultSchema, CancellationToken cancellationToken = default) =>
        PgSpaceItemStore.MigrateAsync(source, schema, cancellationToken);

    /// <summary>The last cycle settled for the owner, or null when none was.</summary>
    public async Task<WorkCycleRecord?> LastAsync(long ownerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT owner_id, cycle_id, accepted, reason, units_remaining, delivered_units, complete FROM {_schema}.space_work_cycles WHERE owner_id = $1",
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new WorkCycleRecord(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetBoolean(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetBoolean(6));
    }

    /// <summary>Records the answer to the owner's cycle, in place of the one before it.</summary>
    public async Task RecordAsync(WorkCycleRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.space_work_cycles (owner_id, cycle_id, accepted, reason, units_remaining, delivered_units, complete)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (owner_id) DO UPDATE SET
                cycle_id = EXCLUDED.cycle_id, accepted = EXCLUDED.accepted, reason = EXCLUDED.reason, units_remaining = EXCLUDED.units_remaining,
                delivered_units = EXCLUDED.delivered_units, complete = EXCLUDED.complete
            """,
            connection);
        foreach (var value in new object[]
        {
            record.OwnerId, record.CycleId, record.Accepted, record.Reason, record.UnitsRemaining, record.DeliveredUnits, record.Complete,
        })
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
