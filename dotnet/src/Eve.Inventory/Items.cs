using Eve.Data;
using Npgsql;

namespace Eve.Inventory;

/// <summary>The client's inventory flags that S2 needs.</summary>
public static class ItemFlags
{
    /// <summary>The hangar of a station.</summary>
    public const int Hangar = 4;

    /// <summary>The first of the eight low slots of a ship (11 to 18).</summary>
    public const int LowSlot0 = 11;

    /// <summary>The first of the eight medium slots of a ship (19 to 26).</summary>
    public const int MedSlot0 = 19;

    /// <summary>The first of the eight high slots of a ship (27 to 34).</summary>
    public const int HiSlot0 = 27;
}

public sealed record Item(long Id, int TypeId, long OwnerId, long LocationId, int Flag, long Quantity, bool Singleton, string? CustomName);

public sealed record NewItem(int TypeId, long OwnerId, long LocationId, int Flag, long Quantity = 1, bool Singleton = false, string? CustomName = null);

/// <summary>Items in PostgreSQL, in the module's own schema (<c>inventory</c> unless told otherwise).</summary>
public sealed class PgItemStore
{
    public const string DefaultSchema = "inventory";

    private const string Columns = "id, type_id, owner_id, location_id, flag, quantity, singleton, custom_name";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgItemStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(PgItemStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private static Item Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4), r.GetInt64(5), r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetString(7));

    /// <summary>Creates an item inside the caller's transaction and returns its id.</summary>
    public async Task<long> CreateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, NewItem item, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_schema}.items (type_id, owner_id, location_id, flag, quantity, singleton, custom_name) VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id",
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = item.TypeId },
                new() { Value = item.OwnerId },
                new() { Value = item.LocationId },
                new() { Value = item.Flag },
                new() { Value = item.Quantity },
                new() { Value = item.Singleton },
                new() { Value = (object?)item.CustomName ?? DBNull.Value },
            },
        };
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Puts an item somewhere else (another location, another flag), inside the caller's transaction.</summary>
    public async Task MoveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long id, long locationId, int flag, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"UPDATE {_schema}.items SET location_id = $2, flag = $3 WHERE id = $1", connection, transaction)
        {
            Parameters = { new() { Value = id }, new() { Value = locationId }, new() { Value = flag } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Item?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.items WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>The items an owner has at a location (a station), oldest first.</summary>
    public async Task<IReadOnlyList<Item>> InLocationAsync(long locationId, long ownerId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.items WHERE location_id = $1 AND owner_id = $2 ORDER BY id");
        command.Parameters.Add(new NpgsqlParameter { Value = locationId });
        command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
        var list = new List<Item>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    /// <summary>Everything in a place (a ship, a container, a wreck), whoever owns it, oldest first.</summary>
    public async Task<IReadOnlyList<Item>> InContainerAsync(long locationId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.items WHERE location_id = $1 ORDER BY id");
        command.Parameters.Add(new NpgsqlParameter { Value = locationId });
        var list = new List<Item>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    /// <summary>Takes an item out of the world, inside the caller's transaction.</summary>
    public async Task DeleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long id, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"DELETE FROM {_schema}.items WHERE id = $1", connection, transaction) { Parameters = { new() { Value = id } } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Item>> OwnedByAsync(long ownerId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.items WHERE owner_id = $1 ORDER BY id");
        command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
        var list = new List<Item>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }
}
