using Npgsql;

namespace Eve.Ships;

/// <summary>One buff input a ship carries as the host keeps it: its collection, the value it gives and when it runs out (milliseconds since 1970).</summary>
public sealed record ShipBuffRecord(int CollectionId, double Value, double ExpiresAtMs);

/// <summary>The ships' buff inputs in PostgreSQL (the <c>ship_buffs</c> table of the <c>space</c> schema, migration 0006).</summary>
public sealed class PgShipBuffStore
{
    public const string DefaultSchema = "space";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgShipBuffStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    /// <summary>Replaces the inputs a ship is kept with by <paramref name="records"/> (the ones it carries now), in one transaction.</summary>
    public async Task SaveAsync(long shipId, IReadOnlyList<ShipBuffRecord> records, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = new NpgsqlCommand($"DELETE FROM {_schema}.ship_buffs WHERE ship_id = $1", connection, transaction))
        {
            delete.Parameters.Add(new NpgsqlParameter { Value = shipId });
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var record in records)
        {
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO {_schema}.ship_buffs (ship_id, collection_id, value, expires_at_ms) VALUES ($1, $2, $3, $4)", connection, transaction);
            insert.Parameters.Add(new NpgsqlParameter { Value = shipId });
            insert.Parameters.Add(new NpgsqlParameter { Value = record.CollectionId });
            insert.Parameters.Add(new NpgsqlParameter { Value = record.Value });
            insert.Parameters.Add(new NpgsqlParameter { Value = record.ExpiresAtMs });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The inputs of a ship that are still running at <paramref name="nowMs"/> (an input at its expiry is still active).</summary>
    public async Task<IReadOnlyList<ShipBuffRecord>> LoadAsync(long shipId, double nowMs, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand(
            $"SELECT collection_id, value, expires_at_ms FROM {_schema}.ship_buffs WHERE ship_id = $1 AND expires_at_ms >= $2 ORDER BY collection_id, expires_at_ms");
        command.Parameters.Add(new NpgsqlParameter { Value = shipId });
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        var list = new List<ShipBuffRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ShipBuffRecord(reader.GetInt32(0), reader.GetDouble(1), reader.GetDouble(2)));
        }
        return list;
    }

    /// <summary>Takes out the inputs whose time is up before <paramref name="nowMs"/>. Returns how many were taken out.</summary>
    public async Task<int> PruneAsync(double nowMs, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"DELETE FROM {_schema}.ship_buffs WHERE expires_at_ms < $1");
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
