using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Eve.Host.Persistence;

/// <summary>Persistent owner-configured tax settings for player industry facilities such as POS assembly arrays.</summary>
public sealed class PgIndustryFacilityTaxStore(NpgsqlDataSource source, string schema)
{
    private readonly string _schema = System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$")
        ? schema
        : throw new ArgumentException("not a usable PostgreSQL schema name", nameof(schema));

    public async Task<JsonObject?> GetAsync(long facilityId, long ownerId, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand(
            $"SELECT settings::text FROM {_schema}.facility_taxes WHERE facility_id = $1 AND owner_id = $2");
        command.Parameters.Add(new NpgsqlParameter { Value = facilityId });
        command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
        var raw = await command.ExecuteScalarAsync(cancellationToken) as string;
        return raw is null ? null : JsonNode.Parse(raw) as JsonObject;
    }

    public async Task<bool> SetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long facilityId,
        long ownerId,
        JsonObject settings,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.facility_taxes (facility_id, owner_id, settings)
            VALUES ($1, $2, $3::jsonb)
            ON CONFLICT (facility_id) DO UPDATE
               SET settings = EXCLUDED.settings, updated_at = clock_timestamp()
             WHERE facility_taxes.owner_id = EXCLUDED.owner_id
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = facilityId });
        command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
        command.Parameters.Add(new NpgsqlParameter { Value = settings.ToJsonString(JsonSerializerOptions.Web) });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
