using Npgsql;

namespace Eve.Wallet;

public sealed class InsufficientFundsException(long ownerId) : Exception($"wallet {ownerId} has insufficient funds");
public sealed class WalletNotFoundException(long ownerId) : Exception($"wallet {ownerId} does not exist");
public sealed class WalletOperationConflictException(string key) : Exception($"wallet operation {key} has different terms");

public sealed record JournalEntry(long Id, long OwnerId, long CounterpartyId, int RefTypeId, decimal Amount,
    decimal Balance, string Reason, long TimeMs, string OperationKey);

/// <summary>Settles ISK and its append-only journal in the caller's transaction. PLEX is never changed.</summary>
public sealed class WalletTransactions
{
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public WalletTransactions(NpgsqlDataSource source, string schema = PgWalletStore.DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    /// <summary>Locks in owner order, validates both wallets and the retry key, then journals both sides.</summary>
    public async Task TransferAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long from, long to,
        decimal amount, int refTypeId, string reason, long timeMs, string operationKey, CancellationToken cancellationToken = default)
    {
        if (amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "an ISK amount must be positive whole cents");
        }
        if (from == to)
        {
            throw new ArgumentException("a transfer needs two different wallets", nameof(to));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        ArgumentNullException.ThrowIfNull(reason);
        var balances = new Dictionary<long, decimal>();
        await using (var command = new NpgsqlCommand(
            $"SELECT owner_id, isk FROM {_schema}.balances WHERE owner_id = ANY($1) ORDER BY owner_id FOR UPDATE", connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = new[] { from, to } });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                balances[reader.GetInt64(0)] = reader.GetDecimal(1);
            }
        }
        foreach (var owner in new[] { from, to })
        {
            if (!balances.ContainsKey(owner))
            {
                throw new WalletNotFoundException(owner);
            }
        }
        var debit = await FindAsync(connection, transaction, from, operationKey, cancellationToken);
        var credit = await FindAsync(connection, transaction, to, operationKey, cancellationToken);
        if (debit is not null || credit is not null)
        {
            if (debit is null || credit is null || debit.CounterpartyId != to || credit.CounterpartyId != from
                || debit.Amount != -amount || credit.Amount != amount || debit.RefTypeId != refTypeId || credit.RefTypeId != refTypeId
                || debit.Reason != reason || credit.Reason != reason)
            {
                throw new WalletOperationConflictException(operationKey);
            }
            return;
        }
        if (balances[from] < amount)
        {
            throw new InsufficientFundsException(from);
        }
        if (amount > WalletLimits.MaximumIsk - balances[to]) throw new MoneyLimitException();
        await ApplyAsync(connection, transaction, from, to, -amount, refTypeId, reason, timeMs, operationKey, cancellationToken);
        await ApplyAsync(connection, transaction, to, from, amount, refTypeId, reason, timeMs, operationKey, cancellationToken);
    }

    /// <summary>Journal a mint, burn, escrow reservation or refund in the same transaction as its owning operation.</summary>
    public async Task<decimal> AdjustAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long owner,
        decimal delta, long counterparty, int refTypeId, string reason, long timeMs, string operationKey, CancellationToken cancellationToken = default)
    {
        if (delta == 0 || decimal.Round(delta, 2) != delta)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "an ISK change must be nonzero whole cents");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        ArgumentNullException.ThrowIfNull(reason);
        await using var command = new NpgsqlCommand($"SELECT isk FROM {_schema}.balances WHERE owner_id = $1 FOR UPDATE", connection, transaction)
        {
            Parameters = { new() { Value = owner } },
        };
        var balance = await command.ExecuteScalarAsync(cancellationToken);
        if (balance is not decimal isk)
        {
            throw new WalletNotFoundException(owner);
        }
        var previous = await FindAsync(connection, transaction, owner, operationKey, cancellationToken);
        if (previous is not null)
        {
            if (previous.Amount != delta || previous.CounterpartyId != counterparty || previous.RefTypeId != refTypeId || previous.Reason != reason)
            {
                throw new WalletOperationConflictException(operationKey);
            }
            return previous.Balance;
        }
        if (isk + delta < 0)
        {
            throw new InsufficientFundsException(owner);
        }
        if (delta > WalletLimits.MaximumIsk - isk) throw new MoneyLimitException();
        return await ApplyAsync(connection, transaction, owner, counterparty, delta, refTypeId, reason, timeMs, operationKey, cancellationToken);
    }

    /// <summary>Prelocks all wallets involved in a multi-party settlement in the same global owner order.</summary>
    public async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<long> owners,
        CancellationToken cancellationToken = default)
    {
        var ids = owners.Distinct().Order().ToArray();
        await using var command = new NpgsqlCommand($"SELECT owner_id FROM {_schema}.balances WHERE owner_id = ANY($1) ORDER BY owner_id FOR UPDATE", connection, transaction)
        {
            Parameters = { new() { Value = ids } },
        };
        var found = new HashSet<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(reader.GetInt64(0));
        }
        foreach (var owner in ids)
        {
            if (!found.Contains(owner))
            {
                throw new WalletNotFoundException(owner);
            }
        }
    }

    private async Task<decimal> ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long owner, long other,
        decimal amount, int refTypeId, string reason, long timeMs, string key, CancellationToken cancellationToken)
    {
        // The caller holds the target wallet row. Each balance update and its journal row are one SQL statement.
        await using var command = new NpgsqlCommand($"""
            WITH changed AS (
                UPDATE {_schema}.balances SET isk = isk + $2
                WHERE owner_id = $1
                RETURNING isk
            )
            INSERT INTO {_schema}.journal (owner_id, counterparty_id, ref_type_id, amount, balance, reason, time_ms, operation_key)
            SELECT $1, $3, $4, $2, isk, $5, $6, $7 FROM changed RETURNING balance
            """, connection, transaction)
        {
            Parameters =
            {
                new() { Value = owner }, new() { Value = amount }, new() { Value = other }, new() { Value = refTypeId },
                new() { Value = reason }, new() { Value = timeMs }, new() { Value = key },
            },
        };
        return (decimal)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<JournalEntry?> FindAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long owner, string key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT id, owner_id, counterparty_id, ref_type_id, amount, balance, reason, time_ms, operation_key FROM {_schema}.journal WHERE owner_id = $1 AND operation_key = $2",
            connection, transaction)
        {
            Parameters = { new() { Value = owner }, new() { Value = key } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static JournalEntry Read(NpgsqlDataReader reader) => new(reader.GetInt64(0), reader.GetInt64(1),
        reader.GetInt64(2), reader.GetInt32(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetString(6),
        reader.GetInt64(7), reader.GetString(8));

    public async Task<IReadOnlyList<JournalEntry>> JournalAsync(long ownerId, long? beforeId = null,
        int limit = 1000, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        await using var command = _source.CreateCommand(
            $"SELECT id, owner_id, counterparty_id, ref_type_id, amount, balance, reason, time_ms, operation_key FROM {_schema}.journal WHERE owner_id = $1 AND id < $2 ORDER BY id DESC LIMIT $3");
        command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
        command.Parameters.Add(new NpgsqlParameter { Value = beforeId ?? long.MaxValue });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        var entries = new List<JournalEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(Read(reader));
        }
        return entries;
    }
}
