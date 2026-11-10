using System.Text.Json.Nodes;
using System.Globalization;
using System.Text.RegularExpressions;
using Eve.Data;
using Eve.GameStore;
using Npgsql;

namespace Eve.Host;

internal sealed record SkinLicenseActivationIntent(Guid Id, string OverridePath, string OverrideJson, long CreatedAtMs);

/// <summary>
/// A committed inventory consumption carries its skin grant here until the asynchronous GameStore write is durable.
/// Startup replays pending rows before the Host accepts character RPCs.
/// </summary>
internal static class SkinLicenseActivationIntents
{
    private static string Schema(string schema) => Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$")
        ? schema
        : throw new ArgumentException("not a usable schema name", nameof(schema));

    public static IReadOnlyList<Migration> MigrationSet() =>
        Migrations.Embedded(typeof(SkinLicenseActivationIntents).Assembly, "SkinActivationMigrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(
        NpgsqlDataSource source, string schema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, Schema(schema), MigrationSet(), cancellationToken);

    public static async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        Guid id,
        long characterId,
        long skinId,
        string overridePath,
        string overrideJson,
        long createdAtMs,
        CancellationToken cancellationToken)
    {
        var table = $"{Schema(schema)}.skin_license_activation_intents";
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {table} (intent_id, character_id, skin_id, override_path, override_json, created_at_ms)
            VALUES ($1, $2, $3, $4, $5::jsonb, $6)
            """, connection, transaction);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(skinId);
        command.Parameters.AddWithValue(overridePath);
        command.Parameters.AddWithValue(overrideJson);
        command.Parameters.AddWithValue(createdAtMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task RecoverAsync(
        NpgsqlDataSource source,
        string schema,
        GameStoreCache cache,
        CancellationToken cancellationToken = default)
    {
        var table = $"{Schema(schema)}.skin_license_activation_intents";
        var pending = new List<SkinLicenseActivationIntent>();
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand($"""
            SELECT intent_id, override_path, override_json::text, created_at_ms
            FROM {table}
            ORDER BY created_at_ms, intent_id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                pending.Add(new SkinLicenseActivationIntent(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        }

        if (pending.Count == 0) return;
        foreach (var intent in pending)
        {
            var current = cache.Read("shipCosmetics", intent.OverridePath).Data as JsonObject;
            if (current?["activationIntentId"]?.ToString() == intent.Id.ToString("D") || IsNewer(current, intent.CreatedAtMs))
                continue;
            var write = cache.Write("shipCosmetics", intent.OverridePath, JsonNode.Parse(intent.OverrideJson));
            if (!write.Success)
                throw new InvalidOperationException($"Could not recover a SKIN license activation: {write.ErrorMsg}");
        }

        await cache.FlushTableAsync("shipCosmetics", cancellationToken);
        await using var markConnection = await source.OpenConnectionAsync(cancellationToken);
        await using var markTransaction = await markConnection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand($"""
            DELETE FROM {table}
            WHERE intent_id = ANY($1)
            """, markConnection, markTransaction))
        {
            command.Parameters.AddWithValue(pending.Select(intent => intent.Id).ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await markTransaction.CommitAsync(cancellationToken);
    }

    private static bool IsNewer(JsonObject? current, long createdAtMs) =>
        DateTimeOffset.TryParse(current?["updatedAt"]?.ToString(), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var updatedAt) && updatedAt.ToUnixTimeMilliseconds() > createdAtMs;

    public static async Task CompleteAsync(
        NpgsqlDataSource source,
        string schema,
        IReadOnlyCollection<Guid> intentIds,
        CancellationToken cancellationToken)
    {
        if (intentIds.Count == 0) return;
        var table = $"{Schema(schema)}.skin_license_activation_intents";
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand($"""
            DELETE FROM {table}
            WHERE intent_id = ANY($1)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(intentIds.ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
