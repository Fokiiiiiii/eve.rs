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

    /// <summary>The completed and offered counts in a caller's transaction.</summary>
    public async Task<(long Completed, long Offered)> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, long factionId, long level, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT completed, offered FROM {schema}.storyline_counters WHERE character_id = $1 AND faction_id = $2 AND level = $3 FOR UPDATE",
            connection, transaction)
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

    /// <summary>Records one offer in the transaction that advances the accepted-event subscriber cursor.</summary>
    public async Task MarkOfferedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, long factionId, long level, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE {schema}.storyline_counters SET offered = offered + 1 WHERE character_id = $1 AND faction_id = $2 AND level = $3",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = factionId }, new() { Value = level } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Consumes one due offer once, for a committed acceptance outside a caller-owned transaction.</summary>
    public async Task<bool> MarkOneDueOfferAsync(
        long characterId, long factionId, string acceptanceReference, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var applied = await MarkOneDueOfferAsync(connection, transaction, characterId, factionId, acceptanceReference, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return applied;
    }

    /// <summary>Consumes one due offer in the transaction that advances an accepted-event subscriber cursor.</summary>
    public async Task<bool> MarkOneDueOfferAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, long factionId, string acceptanceReference, CancellationToken cancellationToken = default)
    {
        if (await HasAcceptanceReceiptAsync(connection, transaction, characterId, acceptanceReference, cancellationToken)) return false;
        for (long level = 1; level <= 5; level++)
        {
            var (completed, offered) = await ReadAsync(connection, transaction, characterId, factionId, level, cancellationToken);
            if (PendingOffers(completed, offered) <= 0) continue;
            return await MarkOfferedOnceAsync(connection, transaction, characterId, factionId, level,
                acceptanceReference, cancellationToken);
        }
        return false;
    }

    /// <summary>Whether an acceptance reference has already consumed a storyline offer.</summary>
    public async Task<bool> HasAcceptanceReceiptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, string acceptanceReference, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT 1 FROM {schema}.storyline_offer_receipts WHERE character_id = $1 AND acceptance_reference = $2",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = acceptanceReference } },
        };
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>Consumes a pending offer once per accepted mission, within the event subscriber's transaction.</summary>
    public async Task<bool> MarkOfferedOnceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, long factionId, long level, string acceptanceReference,
        CancellationToken cancellationToken = default)
    {
        await using (var receipt = new NpgsqlCommand($"""
            INSERT INTO {schema}.storyline_offer_receipts (character_id, acceptance_reference, faction_id, level)
            VALUES ($1, $2, $3, $4) ON CONFLICT (character_id, acceptance_reference) DO NOTHING
            RETURNING 1
            """, connection, transaction)
        {
            Parameters =
            {
                new() { Value = characterId }, new() { Value = acceptanceReference },
                new() { Value = factionId }, new() { Value = level },
            },
        })
        {
            if (await receipt.ExecuteScalarAsync(cancellationToken) is null) return false;
        }

        await using var update = new NpgsqlCommand(
            $"UPDATE {schema}.storyline_counters SET offered = offered + 1 WHERE character_id = $1 AND faction_id = $2 AND level = $3",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = factionId }, new() { Value = level } },
        };
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("a due storyline offer has no counter row");
        return true;
    }

}
