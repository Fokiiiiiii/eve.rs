using Npgsql;

namespace Eve.Ships;

/// <summary>A character's jump fatigue and activation cooldown, each as the Unix millisecond it ends at (0 when none is running).</summary>
public sealed record JumpFatigueState(long FatigueUntilMs, long CooldownUntilMs)
{
    /// <summary>No fatigue and no cooldown.</summary>
    public static readonly JumpFatigueState None = new(0, 0);
}

/// <summary>The jump fatigue in PostgreSQL (the <c>jump_fatigue</c> table of the <c>space</c> schema, migration 0008).</summary>
public sealed class PgJumpFatigueStore
{
    public const string DefaultSchema = "space";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgJumpFatigueStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    /// <summary>The fatigue state of a character (none when it has never jumped).</summary>
    public async Task<JumpFatigueState> GetAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT fatigue_until_ms, cooldown_until_ms FROM {_schema}.jump_fatigue WHERE character_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new JumpFatigueState(reader.GetInt64(0), reader.GetInt64(1))
            : JumpFatigueState.None;
    }

    /// <summary>Writes the fatigue state of a character inside the caller's transaction.</summary>
    public async Task SaveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, JumpFatigueState state, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.jump_fatigue (character_id, fatigue_until_ms, cooldown_until_ms) VALUES ($1, $2, $3)
            ON CONFLICT (character_id) DO UPDATE SET fatigue_until_ms = EXCLUDED.fatigue_until_ms, cooldown_until_ms = EXCLUDED.cooldown_until_ms
            """,
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = state.FatigueUntilMs });
        command.Parameters.Add(new NpgsqlParameter { Value = state.CooldownUntilMs });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
