using System.Text.RegularExpressions;
using Eve.Data;
using Npgsql;

namespace Eve.Host.Persistence;

internal sealed record ShipSkinrDraft(
    long CharacterId,
    Guid Id,
    string Name,
    string LineName,
    int ShipTypeId,
    byte[] Layout,
    byte[] EditorConfigurations,
    string SkinHex,
    long CreatedMs,
    long UpdatedMs);

/// <summary>Persistent third-party SKINR drafts, sequencing jobs and licenses.</summary>
internal sealed partial class PgShipSkinrStore(NpgsqlDataSource source, string schema = "skinr")
{
    public const string DefaultSchema = "skinr";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();

    private readonly string _schema = SchemaName().IsMatch(schema)
        ? schema
        : throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));

    public static IReadOnlyList<Migration> MigrationSet() =>
        Migrations.Embedded(typeof(PgShipSkinrStore).Assembly, "SkinrMigrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(
        NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    internal string Q(string sql) => Sql.In(_schema, sql);

    public async Task<IReadOnlyList<ShipSkinrDraft>> SavedDesignsAsync(long characterId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) return Array.Empty<ShipSkinrDraft>();
        await using var command = source.CreateCommand(Q("""
            SELECT character_id, draft_id, name, line_name, ship_type_id, layout, editor_configurations,
                   skin_hex, created_ms, updated_ms
            FROM {schema}.skinr_design_drafts
            WHERE character_id = $1
            ORDER BY created_ms, draft_id
            """));
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var drafts = new List<ShipSkinrDraft>();
        while (await reader.ReadAsync(cancellationToken))
            drafts.Add(ReadDraft(reader));
        return drafts;
    }

    public async Task<ShipSkinrDraft?> SavedDesignAsync(long characterId, Guid draftId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || draftId == Guid.Empty) return null;
        await using var command = source.CreateCommand(Q("""
            SELECT character_id, draft_id, name, line_name, ship_type_id, layout, editor_configurations,
                   skin_hex, created_ms, updated_ms
            FROM {schema}.skinr_design_drafts
            WHERE character_id = $1 AND draft_id = $2
            """));
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = draftId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDraft(reader) : null;
    }

    public async Task<ShipSkinrDraft?> SaveDesignAsync(
        long characterId,
        Guid draftId,
        string name,
        string lineName,
        int shipTypeId,
        byte[] layout,
        byte[] editorConfigurations,
        string skinHex,
        int capacity,
        long nowMs,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || draftId == Guid.Empty || shipTypeId <= 0 || capacity < 0)
            return null;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
        await using (var existing = new NpgsqlCommand(Q("""
            SELECT character_id, draft_id, name, line_name, ship_type_id, layout, editor_configurations,
                   skin_hex, created_ms, updated_ms
            FROM {schema}.skinr_design_drafts
            WHERE character_id = $1 AND draft_id = $2
            FOR UPDATE
            """), connection, transaction))
        {
            existing.Parameters.Add(new NpgsqlParameter { Value = characterId });
            existing.Parameters.Add(new NpgsqlParameter { Value = draftId });
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var saved = ReadDraft(reader);
                await reader.DisposeAsync();
                var sameRequest = saved.Name == name && saved.LineName == lineName &&
                    saved.ShipTypeId == shipTypeId && saved.Layout.AsSpan().SequenceEqual(layout) &&
                    saved.EditorConfigurations.AsSpan().SequenceEqual(editorConfigurations) && saved.SkinHex == skinHex;
                if (sameRequest)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return saved;
                }
                return null;
            }
        }
        await using (var count = new NpgsqlCommand(Q("""
            SELECT count(*) FROM {schema}.skinr_design_drafts WHERE character_id = $1
            """), connection, transaction))
        {
            count.Parameters.Add(new NpgsqlParameter { Value = characterId });
            if ((long)(await count.ExecuteScalarAsync(cancellationToken) ?? 0L) >= capacity)
                return null;
        }
        await using (var insert = new NpgsqlCommand(Q("""
            INSERT INTO {schema}.skinr_design_drafts
                (character_id, draft_id, name, line_name, ship_type_id, layout, editor_configurations,
                 skin_hex, created_ms, updated_ms)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $9)
            """), connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = characterId });
            insert.Parameters.Add(new NpgsqlParameter { Value = draftId });
            insert.Parameters.Add(new NpgsqlParameter { Value = name });
            insert.Parameters.Add(new NpgsqlParameter { Value = lineName });
            insert.Parameters.Add(new NpgsqlParameter { Value = shipTypeId });
            insert.Parameters.Add(new NpgsqlParameter { Value = layout });
            insert.Parameters.Add(new NpgsqlParameter { Value = editorConfigurations });
            insert.Parameters.Add(new NpgsqlParameter { Value = skinHex });
            insert.Parameters.Add(new NpgsqlParameter { Value = nowMs });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new ShipSkinrDraft(characterId, draftId, name, lineName, shipTypeId, layout,
            editorConfigurations, skinHex, nowMs, nowMs);
    }

    public async Task<ShipSkinrDraft?> UpdateDesignAsync(
        long characterId,
        Guid draftId,
        string name,
        string lineName,
        int shipTypeId,
        byte[] layout,
        byte[] editorConfigurations,
        string skinHex,
        long nowMs,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || draftId == Guid.Empty || shipTypeId <= 0) return null;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
        await using var command = new NpgsqlCommand(Q("""
            UPDATE {schema}.skinr_design_drafts
            SET name = $3, line_name = $4, ship_type_id = $5, layout = $6,
                editor_configurations = $7, skin_hex = $8, updated_ms = $9
            WHERE character_id = $1 AND draft_id = $2
            RETURNING character_id, draft_id, name, line_name, ship_type_id, layout, editor_configurations,
                      skin_hex, created_ms, updated_ms
            """), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = draftId });
        command.Parameters.Add(new NpgsqlParameter { Value = name });
        command.Parameters.Add(new NpgsqlParameter { Value = lineName });
        command.Parameters.Add(new NpgsqlParameter { Value = shipTypeId });
        command.Parameters.Add(new NpgsqlParameter { Value = layout });
        command.Parameters.Add(new NpgsqlParameter { Value = editorConfigurations });
        command.Parameters.Add(new NpgsqlParameter { Value = skinHex });
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var result = ReadDraft(reader);
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<bool> DeleteDesignAsync(long characterId, Guid draftId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || draftId == Guid.Empty) return false;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
        await using var command = new NpgsqlCommand(Q("""
            DELETE FROM {schema}.skinr_design_drafts WHERE character_id = $1 AND draft_id = $2
            """), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = draftId });
        var removed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    internal static async Task LockCharacterAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('ship-skinr:' || $1::text, 0))", connection, transaction)
        {
            Parameters = { new NpgsqlParameter { Value = characterId } },
        };
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static ShipSkinrDraft ReadDraft(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
        reader.GetFieldValue<byte[]>(5), reader.GetFieldValue<byte[]>(6), reader.GetString(7),
        reader.GetInt64(8), reader.GetInt64(9));
}
