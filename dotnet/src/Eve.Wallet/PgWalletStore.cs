using Eve.Data;
using Npgsql;

namespace Eve.Wallet;

/// <summary>What an owner has: ISK to the cent and whole PLEX.</summary>
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

    /// <summary>Opens a wallet with a starting balance inside the caller's transaction. An owner has one.</summary>
    public async Task OpenAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long ownerId, decimal isk, long plex, CancellationToken cancellationToken = default)
    {
        if (isk > WalletLimits.MaximumIsk) throw new MoneyLimitException();
        await using var command = new NpgsqlCommand($"INSERT INTO {_schema}.balances (owner_id, isk, plex) VALUES ($1, $2, $3)", connection, transaction)
        {
            Parameters = { new() { Value = ownerId }, new() { Value = isk }, new() { Value = plex } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Balance?> GetAsync(long ownerId, CancellationToken cancellationToken = default) =>
        (await ForOwnersAsync([ownerId], cancellationToken)).GetValueOrDefault(ownerId);

    /// <summary>The balances of the owners that have a wallet.</summary>
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
