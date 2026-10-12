using Eve.Data;
using Npgsql;

namespace Eve.Wormholes;

/// <summary>A stored connection (one row of <c>wormhole_connections</c>).</summary>
public sealed record StoredConnection(long Id, int SourceSystemId, int DestinationSystemId, int TypeId, string Code, bool IsStatic, double MassKg,
    double MaxStableMassKg, long CreatedMs, long ExpiresMs, bool Active, ulong Seed);

/// <summary>
/// The wormhole connections in PostgreSQL (schema <c>wormhole</c>, migration 0001). Every method takes the caller's connection and
/// transaction, so a transit, a collapse and the replacement's expiry job commit together.
/// </summary>
public sealed class WormholeStore
{
    public const string DefaultSchema = "wormhole";

    private const string Columns =
        "connection_id, source_system_id, destination_system_id, type_id, code, is_static, mass_kg, max_stable_mass_kg, created_ms, expires_ms, active, seed";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public WormholeStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        _source = source;
        _schema = Sql.CheckSchema(schema);
    }

    public string Schema => _schema;

    public NpgsqlDataSource Source => _source;

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(WormholeStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private string Q(string text) => Sql.In(_schema, text);

    public async Task<long> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, StoredConnection stored, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q(
            "INSERT INTO {schema}.wormhole_connections (source_system_id, destination_system_id, type_id, code, is_static, mass_kg, max_stable_mass_kg, " +
            "created_ms, expires_ms, active, seed) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) RETURNING connection_id"),
            stored.SourceSystemId, stored.DestinationSystemId, stored.TypeId, stored.Code, stored.IsStatic, stored.MassKg, stored.MaxStableMassKg,
            stored.CreatedMs, stored.ExpiresMs, stored.Active, unchecked((long)stored.Seed));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>The connection, or null. With <paramref name="forUpdate"/> its row is locked until the transaction ends.</summary>
    public async Task<StoredConnection?> GetAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long id, bool forUpdate, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction,
            Q($"SELECT {Columns} FROM {{schema}}.wormhole_connections WHERE connection_id = $1{(forUpdate ? " FOR UPDATE" : "")}"), id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>The active connections leaving a system, by ID.</summary>
    public async Task<IReadOnlyList<StoredConnection>> ActiveFromAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, int sourceSystemId, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction,
            Q($"SELECT {Columns} FROM {{schema}}.wormhole_connections WHERE source_system_id = $1 AND active ORDER BY connection_id"), sourceSystemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<StoredConnection>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(Read(reader));
        return rows;
    }

    /// <summary>Writes the mass and the active flag of a connection.</summary>
    public async Task UpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, StoredConnection stored, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q(
            "UPDATE {schema}.wormhole_connections SET mass_kg = $2, active = $3 WHERE connection_id = $1"),
            stored.Id, stored.MassKg, stored.Active);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"wormhole connection {stored.Id} is not stored");
    }

    private static StoredConnection Read(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4), reader.GetBoolean(5), reader.GetDouble(6),
        reader.GetDouble(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetBoolean(10), unchecked((ulong)reader.GetInt64(11)));
}
