using Eve.Data;
using Eve.Dogma;
using Npgsql;

namespace Eve.Fitting;

/// <summary>Whether the modules fitted to ships are online, in PostgreSQL, in the module's own schema (<c>fitting</c> unless told otherwise).</summary>
public sealed class PgModuleStateStore
{
    public const string DefaultSchema = "fitting";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgModuleStateStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(PgModuleStateStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    /// <summary>Sets how far a module is switched on, inside the caller's transaction. Offline is the absence of a row.</summary>
    public async Task SetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long itemId, ItemState state, CancellationToken cancellationToken = default)
    {
        await using var command = state == ItemState.Offline
            ? new NpgsqlCommand($"DELETE FROM {_schema}.module_state WHERE item_id = $1", connection, transaction) { Parameters = { new() { Value = itemId } } }
            : new NpgsqlCommand(
                $"INSERT INTO {_schema}.module_state (item_id, state) VALUES ($1, $2) ON CONFLICT (item_id) DO UPDATE SET state = EXCLUDED.state", connection, transaction)
            {
                Parameters = { new() { Value = itemId }, new() { Value = (short)state } },
            };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>How far each of these modules is switched on (a module with no row is offline).</summary>
    public async Task<IReadOnlyDictionary<long, ItemState>> StatesAsync(IEnumerable<long> itemIds, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        var states = ids.ToDictionary(id => id, _ => ItemState.Offline);
        if (ids.Length == 0)
        {
            return states;
        }
        await using var command = _source.CreateCommand($"SELECT item_id, state FROM {_schema}.module_state WHERE item_id = ANY($1)");
        command.Parameters.Add(new NpgsqlParameter { Value = ids });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            states[reader.GetInt64(0)] = (ItemState)reader.GetInt16(1);
        }
        return states;
    }
}
