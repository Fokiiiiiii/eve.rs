using Npgsql;

namespace Eve.Ships;

/// <summary>
/// A jump drive's spool in progress: the field it jumps to, the system, when the spool is over (Unix milliseconds), the fuel it took, and the fatigue state the
/// jump replaced (<see cref="PgJumpSpoolStore"/>).
/// </summary>
public sealed record JumpSpool(
    long CharacterId, long ShipId, long FieldId, int DestinationSystem, long EndsAtMs, int FuelTypeId, long FuelQuantity, JumpFatigueState Prior);

/// <summary>The jump spools in PostgreSQL (the <c>jump_spool</c> table of the <c>space</c> schema, migration 0010).</summary>
public sealed class PgJumpSpoolStore
{
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgJumpSpoolStore(NpgsqlDataSource source, string schema = PgJumpFatigueStore.DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    /// <summary>Writes a spool inside the caller's transaction.</summary>
    public async Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, JumpSpool spool, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.jump_spool (character_id, ship_id, field_id, destination_system, ends_at_ms, fuel_type_id, fuel_quantity,
                prior_fatigue_until_ms, prior_cooldown_until_ms)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """,
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = spool.CharacterId });
        command.Parameters.Add(new NpgsqlParameter { Value = spool.ShipId });
        command.Parameters.Add(new NpgsqlParameter { Value = spool.FieldId });
        command.Parameters.Add(new NpgsqlParameter { Value = spool.DestinationSystem });
        command.Parameters.Add(new NpgsqlParameter { Value = spool.EndsAtMs });
        command.Parameters.Add(new NpgsqlParameter { Value = spool.FuelTypeId });
        command.Parameters.Add(new NpgsqlParameter { Value = spool.FuelQuantity });
        command.Parameters.Add(new NpgsqlParameter { Value = spool.Prior.FatigueUntilMs });
        command.Parameters.Add(new NpgsqlParameter { Value = spool.Prior.CooldownUntilMs });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Removes the spool of a character that ends at <paramref name="endsAtMs"/>, inside the caller's transaction, and returns it (null when there is none).
    /// The end time keeps a sweep from taking a later spool of the same character.
    /// </summary>
    public async Task<JumpSpool?> TakeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long endsAtMs, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"""
            DELETE FROM {_schema}.jump_spool WHERE character_id = $1 AND ends_at_ms = $2
            RETURNING ship_id, field_id, destination_system, ends_at_ms, fuel_type_id, fuel_quantity, prior_fatigue_until_ms, prior_cooldown_until_ms
            """,
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = endsAtMs });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader, characterId) : null;
    }

    /// <summary>Removes a character's spool once the ship has jumped (nothing to remove when the spool was already cancelled).</summary>
    public async Task ForgetAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"DELETE FROM {_schema}.jump_spool WHERE character_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Every spool in progress, as the host last saw it.</summary>
    public async Task<IReadOnlyList<JumpSpool>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT character_id, ship_id, field_id, destination_system, ends_at_ms, fuel_type_id, fuel_quantity, prior_fatigue_until_ms, prior_cooldown_until_ms FROM {_schema}.jump_spool",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var spools = new List<JumpSpool>();
        while (await reader.ReadAsync(cancellationToken))
        {
            spools.Add(Read(reader, reader.GetInt64(0), offset: 1));
        }
        return spools;
    }

    private static JumpSpool Read(NpgsqlDataReader reader, long characterId, int offset = 0) => new(
        characterId,
        reader.GetInt64(offset),
        reader.GetInt64(offset + 1),
        reader.GetInt32(offset + 2),
        reader.GetInt64(offset + 3),
        reader.GetInt32(offset + 4),
        reader.GetInt64(offset + 5),
        new JumpFatigueState(reader.GetInt64(offset + 6), reader.GetInt64(offset + 7)));
}
