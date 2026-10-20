using Npgsql;

namespace Eve.Agents;

/// <summary>
/// The storyline offer counter: per character, faction and level, the completed regular missions (<see cref="IncrementAsync"/>, in the completion's
/// transaction) and the storyline offers made (<see cref="MarkOfferedAsync"/>). The rule is <see cref="Interval"/> completions for one offer.
/// </summary>
public sealed class StorylineCounters(NpgsqlDataSource source, string schema = AgentMissionStore.DefaultSchema)
{

    public const long Interval = 16;

    /// <summary>The storyline offers that are due: the completions in whole intervals, less the offers already made (never negative).</summary>
    public static long PendingOffers(long completed, long offered) => Math.Max(0, completed / Interval - offered);

    /// <summary>Counts one completed regular mission of the faction and level, in the caller's transaction (the completion's).</summary>
    public async Task IncrementAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long factionId, long level,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"INSERT INTO {schema}.storyline_counters (character_id, faction_id, level, completed) VALUES ($1, $2, $3, 1) " +
            "ON CONFLICT (character_id, faction_id, level) DO UPDATE SET completed = storyline_counters.completed + 1", connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = factionId }, new() { Value = level } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The completed and offered counts of the character for the faction and level (zero when none is counted).</summary>
    public async Task<(long Completed, long Offered)> ReadAsync(long characterId, long factionId, long level, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT completed, offered FROM {schema}.storyline_counters WHERE character_id = $1 AND faction_id = $2 AND level = $3", connection)
        {
            Parameters = { new() { Value = characterId }, new() { Value = factionId }, new() { Value = level } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetInt64(0), reader.GetInt64(1)) : (0L, 0L);
    }

    /// <summary>Records one storyline offer made for the faction and level.</summary>
    public async Task MarkOfferedAsync(long characterId, long factionId, long level, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"UPDATE {schema}.storyline_counters SET offered = offered + 1 WHERE character_id = $1 AND faction_id = $2 AND level = $3", connection)
        {
            Parameters = { new() { Value = characterId }, new() { Value = factionId }, new() { Value = level } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
