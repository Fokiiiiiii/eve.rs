using Eve.Wallet;
using Npgsql;

namespace Eve.Characters;

/// <summary>
/// The login milestone of each account (table <c>login_milestones</c>, in the characters schema): the online time counted so far, whether the
/// milestone was reached and whether its reward was claimed. The flags only go up, so a late write cannot undo a claim.
/// </summary>
public sealed class LoginMilestoneStore(NpgsqlDataSource source, string schema, WalletTransactions wallets)
{
    /// <summary>The online time a milestone takes (EveJS <c>MILESTONE_DURATION_MS</c>: two hours).</summary>
    public const long DurationMs = 2 * 60 * 60 * 1000;

    public readonly record struct Progress(long OnlineMs, bool Achieved, bool Claimed);

    /// <summary>The stored progress; an account with no row has none.</summary>
    public async Task<Progress> ReadAsync(long accountId, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand($"SELECT online_ms, achieved, claimed FROM {schema}.login_milestones WHERE account_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new Progress(reader.GetInt64(0), reader.GetBoolean(1), reader.GetBoolean(2))
            : new Progress(0, false, false);
    }

    /// <summary>Stores progress. Reached and claimed are kept once set.</summary>
    public async Task WriteAsync(long accountId, Progress progress, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand($"""
            INSERT INTO {schema}.login_milestones (account_id, online_ms, achieved, claimed, updated_ms)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (account_id) DO UPDATE SET
                online_ms = EXCLUDED.online_ms,
                achieved = login_milestones.achieved OR EXCLUDED.achieved,
                claimed = login_milestones.claimed OR EXCLUDED.claimed,
                updated_ms = EXCLUDED.updated_ms
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = Math.Clamp(progress.OnlineMs, 0, DurationMs) });
        command.Parameters.Add(new NpgsqlParameter { Value = progress.Achieved });
        command.Parameters.Add(new NpgsqlParameter { Value = progress.Claimed });
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Pays the reward of a reached milestone to the character and marks it claimed, in one transaction. Answers false (and pays nothing) when the
    /// milestone is not reached or was claimed already.
    /// </summary>
    public async Task<bool> ClaimAsync(long accountId, long characterId, int milestoneId, decimal reward, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var select = new NpgsqlCommand($"SELECT achieved, claimed FROM {schema}.login_milestones WHERE account_id = $1 FOR UPDATE", connection, transaction))
        {
            select.Parameters.Add(new NpgsqlParameter { Value = accountId });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(0) || reader.GetBoolean(1))
                return false;
        }
        await wallets.AdjustAsync(connection, transaction, characterId, reward, 0, 0, "Two-hour login milestone", nowMs,
            $"login-milestone:{accountId}:{milestoneId}", cancellationToken, referenceId: milestoneId);
        await using (var mark = new NpgsqlCommand($"UPDATE {schema}.login_milestones SET claimed = TRUE, updated_ms = $2 WHERE account_id = $1", connection, transaction))
        {
            mark.Parameters.Add(new NpgsqlParameter { Value = accountId });
            mark.Parameters.Add(new NpgsqlParameter { Value = nowMs });
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
