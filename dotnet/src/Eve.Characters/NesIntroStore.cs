using Npgsql;

namespace Eve.Characters;

/// <summary>The New Eden store introduction state of each character (table <c>nes_intro_states</c>, in the characters schema).</summary>
public sealed class NesIntroStore(NpgsqlDataSource source, string schema, Func<long> nowMs)
{
    public const int Unset = 0;
    public const int Active = 1;
    public const int Skipped = 3;

    /// <summary>The stored state; a character with no row is unset.</summary>
    public async Task<int> ReadAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand($"SELECT state FROM {schema}.nes_intro_states WHERE character_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        return await command.ExecuteScalarAsync(cancellationToken) is short state ? state : Unset;
    }

    /// <summary>Moves an active introduction to skipped. Answers whether it did (an introduction that is not active is left as it is).</summary>
    public async Task<bool> SkipAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand(
            $"UPDATE {schema}.nes_intro_states SET state = {Skipped}, updated_ms = $2 WHERE character_id = $1 AND state = {Active}");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs() });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
