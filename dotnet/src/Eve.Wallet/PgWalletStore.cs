using Eve.Data;
using Npgsql;

namespace Eve.Wallet;

public sealed record Balance(long OwnerId, decimal Isk, long Plex);

/// <summary>Wallets in PostgreSQL, in the module's own schema (<c>wallet</c> unless told otherwise).</summary>
public sealed class PgWalletStore
{
    public const string DefaultSchema = "wallet";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgWalletStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(PgWalletStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    public async Task OpenAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long ownerId, decimal isk, long plex, CancellationToken cancellationToken = default)
    {
        if (isk > WalletLimits.MaximumIsk) throw new MoneyLimitException();
        await using var command = new NpgsqlCommand($"INSERT INTO {_schema}.balances (owner_id, isk, plex) VALUES ($1, $2, $3)", connection, transaction)
        {
            Parameters = { new() { Value = ownerId }, new() { Value = isk }, new() { Value = plex } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> AdjustPlexAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long ownerId,
        long delta,
        CancellationToken cancellationToken = default)
    {
        if (ownerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(ownerId));
        if (delta == 0)
            throw new ArgumentOutOfRangeException(nameof(delta));
        await using var read = new NpgsqlCommand(
            $"SELECT plex FROM {_schema}.balances WHERE owner_id = $1 AND account_key = 1000 FOR UPDATE",
            connection, transaction)
        {
            Parameters = { new() { Value = ownerId } },
        };
        var balanceValue = await read.ExecuteScalarAsync(cancellationToken);
        if (balanceValue is not long balance)
            throw new WalletNotFoundException(ownerId);

        long next;
        try { next = checked(balance + delta); }
        catch (OverflowException) { throw new MoneyLimitException(); }
        if (next < 0)
            throw new InsufficientFundsException(ownerId);

        await using var update = new NpgsqlCommand(
            $"UPDATE {_schema}.balances SET plex = $2 WHERE owner_id = $1 AND account_key = 1000",
            connection, transaction)
        {
            Parameters = { new() { Value = ownerId }, new() { Value = next } },
        };
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new WalletNotFoundException(ownerId);
        return next;
    }

    public async Task<IReadOnlyDictionary<long, long>> ReadPlexBalancesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyCollection<long> ownerIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        return await ReadPlexBalancesCoreAsync(connection, transaction, ownerIds, lockRows: true, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<long, long>> ReadPlexBalancesAsync(
        IReadOnlyCollection<long> ownerIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerIds);
        var ids = ownerIds.Distinct().Order().ToArray();
        if (ids.Length == 0)
            return new Dictionary<long, long>();

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        return await ReadPlexBalancesCoreAsync(connection, null, ids, lockRows: false, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<long, long>> ReadPlexBalancesCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<long> ownerIds,
        bool lockRows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownerIds);
        if (ownerIds.Any(ownerId => ownerId <= 0))
            throw new ArgumentOutOfRangeException(nameof(ownerIds));
        var ids = ownerIds.Distinct().Order().ToArray();
        if (ids.Length == 0)
            return new Dictionary<long, long>();

        var suffix = lockRows ? " FOR UPDATE" : string.Empty;
        await using var command = new NpgsqlCommand(
            $"SELECT owner_id, plex FROM {_schema}.balances WHERE account_key = 1000 AND owner_id = ANY($1) ORDER BY owner_id{suffix}",
            connection, transaction)
        {
            Parameters = { new() { Value = ids } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var balances = new Dictionary<long, long>(ids.Length);
        while (await reader.ReadAsync(cancellationToken))
            balances.Add(reader.GetInt64(0), reader.GetInt64(1));
        return balances;
    }

    public async Task<decimal> LastIskChangeAsync(long ownerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT amount FROM {_schema}.journal WHERE owner_id = $1 AND account_key = 1000 ORDER BY id DESC LIMIT 1", connection)
        {
            Parameters = { new() { Value = ownerId } },
        };
        return await command.ExecuteScalarAsync(cancellationToken) is decimal amount ? amount : 0m;
    }

    public async Task<Balance?> GetAsync(long ownerId, CancellationToken cancellationToken = default) =>
        (await ForOwnersAsync([ownerId], cancellationToken)).GetValueOrDefault(ownerId);

    public async Task<IReadOnlyDictionary<long, Balance>> ForOwnersAsync(IEnumerable<long> ownerIds, CancellationToken cancellationToken = default)
    {
        var balances = new Dictionary<long, Balance>();
        var ids = ownerIds.ToArray();
        if (ids.Length == 0)
        {
            return balances;
        }
        await using var command = _source.CreateCommand($"SELECT owner_id, isk, plex FROM {_schema}.balances WHERE owner_id = ANY($1) AND account_key = 1000");
        command.Parameters.Add(new NpgsqlParameter { Value = ids });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            balances[reader.GetInt64(0)] = new Balance(reader.GetInt64(0), reader.GetDecimal(1), reader.GetInt64(2));
        }
        return balances;
    }
}
