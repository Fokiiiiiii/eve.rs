using Eve.Data;
using Npgsql;

namespace Eve.Ships;

/// <summary>A can or a wreck that lies in space: where, how, whose and until when. The item itself, and what is in it, are the inventory's.</summary>
public sealed record SpaceItem(
    long ItemId, int SolarSystemId, IReadOnlyList<double> Position, IReadOnlyList<double> Direction, double Radius, int GroupId, long LauncherId,
    long CharacterId, long CorporationId, long AllianceId, long WarFactionId, long ExpiresAtMs,
    long MoonExtractionId = 0, long MoonId = 0, long MoonStructureId = 0, long MoonOwnerCorporationId = 0, int MoonRockOrdinal = 0, int MoonGraphicId = 0);

/// <summary>What lies in space as items, in PostgreSQL, in the module's own schema (<c>space</c> unless told otherwise).</summary>
public sealed class PgSpaceItemStore
{
    public const string DefaultSchema = "space";

    private const string Columns = "item_id, solar_system_id, x, y, z, dx, dy, dz, radius, group_id, launcher_id, character_id, corporation_id, alliance_id, war_faction_id, expires_at_ms, moon_extraction_id, moon_id, moon_structure_id, moon_owner_corporation_id, moon_rock_ordinal, moon_graphic_id";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgSpaceItemStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(PgSpaceItemStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private static SpaceItem Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt32(1), [r.GetDouble(2), r.GetDouble(3), r.GetDouble(4)], [r.GetDouble(5), r.GetDouble(6), r.GetDouble(7)], r.GetDouble(8), r.GetInt32(9), r.GetInt64(10),
        r.GetInt64(11), r.GetInt64(12), r.GetInt64(13), r.GetInt64(14), r.GetInt64(15), r.GetInt64(16), r.GetInt64(17), r.GetInt64(18), r.GetInt64(19),
        r.GetInt32(20), r.GetInt32(21));

    /// <summary>Records where an item lies (a second record of the same item replaces the first).</summary>
    public async Task SaveAsync(SpaceItem item, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SaveAsync(connection, transaction, item, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SpaceItem item, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.space_items ({Columns}) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21, $22)
            ON CONFLICT (item_id) DO UPDATE SET
                solar_system_id = EXCLUDED.solar_system_id, x = EXCLUDED.x, y = EXCLUDED.y, z = EXCLUDED.z, dx = EXCLUDED.dx, dy = EXCLUDED.dy, dz = EXCLUDED.dz,
                radius = EXCLUDED.radius, group_id = EXCLUDED.group_id, launcher_id = EXCLUDED.launcher_id, character_id = EXCLUDED.character_id,
                corporation_id = EXCLUDED.corporation_id, alliance_id = EXCLUDED.alliance_id, war_faction_id = EXCLUDED.war_faction_id, expires_at_ms = EXCLUDED.expires_at_ms,
                moon_extraction_id = EXCLUDED.moon_extraction_id, moon_id = EXCLUDED.moon_id, moon_structure_id = EXCLUDED.moon_structure_id,
                moon_owner_corporation_id = EXCLUDED.moon_owner_corporation_id, moon_rock_ordinal = EXCLUDED.moon_rock_ordinal, moon_graphic_id = EXCLUDED.moon_graphic_id
            """,
            connection, transaction);
        foreach (var value in new object[]
        {
            item.ItemId, item.SolarSystemId, item.Position[0], item.Position[1], item.Position[2], item.Direction[0], item.Direction[1], item.Direction[2], item.Radius, item.GroupId,
            item.LauncherId, item.CharacterId, item.CorporationId, item.AllianceId, item.WarFactionId, item.ExpiresAtMs,
            item.MoonExtractionId, item.MoonId, item.MoonStructureId, item.MoonOwnerCorporationId, item.MoonRockOrdinal, item.MoonGraphicId,
        })
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Forgets where an item lies (it is gone). Returns whether it was recorded.</summary>
    public async Task<bool> RemoveAsync(long itemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var removed = await RemoveAsync(connection, transaction, itemId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    public async Task<bool> RemoveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long itemId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"DELETE FROM {_schema}.space_items WHERE item_id = $1", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = itemId });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<SpaceItem?> GetAsync(long itemId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.space_items WHERE item_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = itemId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }


    public async Task<SpaceItem?> GetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long itemId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM {_schema}.space_items WHERE item_id = $1", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = itemId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>What lies in a solar system, oldest first.</summary>
    public async Task<IReadOnlyList<SpaceItem>> InSystemAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.space_items WHERE solar_system_id = $1 ORDER BY item_id");
        command.Parameters.Add(new NpgsqlParameter { Value = solarSystemId });
        return await ReadAllAsync(command, cancellationToken);
    }


    public async Task<IReadOnlyList<SpaceItem>> MoonFieldAsync(long extractionId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.space_items WHERE moon_extraction_id = $1 ORDER BY moon_rock_ordinal, item_id");
        command.Parameters.Add(new NpgsqlParameter { Value = extractionId });
        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<SpaceItem>> MoonFieldAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long extractionId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.space_items WHERE moon_extraction_id = $1 ORDER BY moon_rock_ordinal, item_id",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = extractionId });
        return await ReadAllAsync(command, cancellationToken);
    }

    /// <summary>What is due to go by <paramref name="nowMs"/>.</summary>
    public async Task<IReadOnlyList<SpaceItem>> DueAsync(long nowMs, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.space_items WHERE expires_at_ms <= $1 ORDER BY expires_at_ms, item_id");
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        return await ReadAllAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<SpaceItem>> ReadAllAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var list = new List<SpaceItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }
}
