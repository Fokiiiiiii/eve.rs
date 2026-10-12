using Eve.Data;
using Npgsql;

namespace Eve.Insurgency;

/// <summary>
/// The campaigns and the ambition of each warzone in PostgreSQL (schema <c>insurgency</c>, migration 0001). Every method takes the caller's connection and
/// transaction, so a campaign change, its events and its deadline job commit together.
/// </summary>
public sealed class InsurgencyStore
{
    public const string DefaultSchema = "insurgency";

    private const string CampaignColumns =
        "campaign_id, pirate_faction_id, warzone_id, origin_solar_system_id, covered_solar_system_ids, structure_id, state, started_ms, max_ends_ms, " +
        "state_expires_ms, pirate_points_required, antipirate_points_required, pirate_points_scored, antipirate_points_scored, ended_ms";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public InsurgencyStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        _source = source;
        _schema = Sql.CheckSchema(schema);
    }

    public string Schema => _schema;

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(InsurgencyStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    public NpgsqlDataSource Source => _source;

    private string Q(string text) => Sql.In(_schema, text);

    /// <summary>The campaign, or null. With <paramref name="forUpdate"/> its row is locked until the transaction ends.</summary>
    public async Task<Campaign?> GetAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long campaignId, bool forUpdate, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction,
            Q($"SELECT {CampaignColumns} FROM {{schema}}.insurgency_campaigns WHERE campaign_id = $1{(forUpdate ? " FOR UPDATE" : "")}"), campaignId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>The campaigns the client may be shown (forecasting, active, or ended and in the wind-down), ordered by id.</summary>
    public Task<IReadOnlyList<Campaign>> VisibleAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken = default) =>
        ListAsync(connection, transaction, "state IN (2, 3, 4, 5, 6)", cancellationToken);

    /// <summary>Every campaign that has not finished: the ones whose deadline job must exist.</summary>
    public Task<IReadOnlyList<Campaign>> OpenAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken = default) =>
        ListAsync(connection, transaction, "state NOT IN (1, 99)", cancellationToken);

    private async Task<IReadOnlyList<Campaign>> ListAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string filter, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(connection, transaction, Q($"SELECT {CampaignColumns} FROM {{schema}}.insurgency_campaigns WHERE {filter} ORDER BY campaign_id"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<Campaign>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(Read(reader));
        return rows;
    }

    /// <summary>Stores a new campaign and returns it with its id.</summary>
    public async Task<Campaign> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Campaign campaign, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q(
            "INSERT INTO {schema}.insurgency_campaigns (pirate_faction_id, warzone_id, origin_solar_system_id, covered_solar_system_ids, structure_id, state, " +
            "started_ms, max_ends_ms, state_expires_ms, pirate_points_required, antipirate_points_required, pirate_points_scored, antipirate_points_scored, ended_ms) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14) RETURNING campaign_id"),
            (long)campaign.PirateFactionId, campaign.WarzoneId, campaign.OriginSolarSystemId, campaign.CoveredSolarSystemIds.ToArray(), campaign.StructureId,
            campaign.State, campaign.StartedMs, campaign.MaxEndsMs, campaign.StateExpiresMs, campaign.PiratePointsRequired, campaign.AntipiratePointsRequired,
            campaign.PiratePointsScored, campaign.AntipiratePointsScored, campaign.EndedMs);
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        return campaign with { CampaignId = id };
    }

    /// <summary>Writes the campaign's state, deadlines and points.</summary>
    public async Task UpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Campaign campaign, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q(
            "UPDATE {schema}.insurgency_campaigns SET state = $2, max_ends_ms = $3, state_expires_ms = $4, pirate_points_scored = $5, " +
            "antipirate_points_scored = $6, ended_ms = $7 WHERE campaign_id = $1"),
            campaign.CampaignId, campaign.State, campaign.MaxEndsMs, campaign.StateExpiresMs, campaign.PiratePointsScored, campaign.AntipiratePointsScored, campaign.EndedMs);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"insurgency campaign {campaign.CampaignId} is not stored");
    }

    /// <summary>The ambition modifiers of the two sides of a warzone (0 when none is stored).</summary>
    public async Task<(int Pirate, int Antipirate)> AmbitionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long warzoneId, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction,
            Q("SELECT pirate_modifier, antipirate_modifier FROM {schema}.insurgency_ambition WHERE warzone_id = $1"), warzoneId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
    }

    public async Task SetAmbitionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long warzoneId, int pirate, int antipirate, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q(
            "INSERT INTO {schema}.insurgency_ambition (warzone_id, pirate_modifier, antipirate_modifier) VALUES ($1, $2, $3) " +
            "ON CONFLICT (warzone_id) DO UPDATE SET pirate_modifier = EXCLUDED.pirate_modifier, antipirate_modifier = EXCLUDED.antipirate_modifier"),
            warzoneId, pirate, antipirate);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Records a side's point in a system once (the system's first point for that side). False when the system already scored for it.</summary>
    public async Task<bool> TryScorePointAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long campaignId, long solarSystemId, Side side, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q(
            "INSERT INTO {schema}.insurgency_points (campaign_id, solar_system_id, side, scored_ms) VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING"),
            campaignId, solarSystemId, side == Side.Pirate ? "pirate" : "antipirate", nowMs);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Makes a character a contributor of the campaign (once).</summary>
    public async Task AddContributorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long campaignId, long characterId, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q(
            "INSERT INTO {schema}.insurgency_contributors (campaign_id, character_id, first_ms) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING"),
            campaignId, characterId, nowMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The characters that contributed to a campaign, in the order they first did.</summary>
    public async Task<IReadOnlyList<long>> ContributorsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long campaignId, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q("SELECT character_id FROM {schema}.insurgency_contributors WHERE campaign_id = $1 ORDER BY first_ms, character_id"), campaignId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(reader.GetInt64(0));
        return rows;
    }

    /// <summary>The active campaigns that cover a system (a kill there is a contribution to each).</summary>
    public async Task<IReadOnlyList<long>> ActiveCoveringAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q(
            "SELECT campaign_id FROM {schema}.insurgency_campaigns WHERE state = 3 AND covered_solar_system_ids @> ARRAY[$1]::bigint[] ORDER BY campaign_id"), solarSystemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(reader.GetInt64(0));
        return rows;
    }

    /// <summary>Records a site a campaign started in one of its systems.</summary>
    public async Task RecordSiteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long campaignId, long solarSystemId, long dungeonId, long instanceId, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q(
            "INSERT INTO {schema}.insurgency_sites (campaign_id, solar_system_id, dungeon_id, instance_id) VALUES ($1, $2, $3, $4)"),
            campaignId, solarSystemId, dungeonId, instanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Marks a campaign site completed and returns its campaign and system, or null when the dungeon is not one of the campaigns' sites (or was already completed).</summary>
    public async Task<(long CampaignId, long SolarSystemId)?> CompleteSiteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q(
            "UPDATE {schema}.insurgency_sites SET completed_ms = $2 WHERE instance_id = $1 AND completed_ms IS NULL RETURNING campaign_id, solar_system_id"),
            instanceId, nowMs);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>The instances of a campaign's sites that have not completed (they end with the campaign).</summary>
    public async Task<IReadOnlyList<long>> OpenSiteInstancesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long campaignId, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q("SELECT instance_id FROM {schema}.insurgency_sites WHERE campaign_id = $1 AND completed_ms IS NULL ORDER BY instance_id"), campaignId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(reader.GetInt64(0));
        return rows;
    }

    /// <summary>How many campaigns a warzone has had (the next hub of the warzone is chosen from it).</summary>
    public async Task<long> CountInWarzoneAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long warzoneId, CancellationToken cancellationToken = default)
    {
        await using var command = Sql.Command(connection, transaction, Q("SELECT count(*) FROM {schema}.insurgency_campaigns WHERE warzone_id = $1"), warzoneId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static Campaign Read(NpgsqlDataReader reader) => new(
        CampaignId: reader.GetInt64(0),
        PirateFactionId: (int)reader.GetInt64(1),
        WarzoneId: reader.GetInt64(2),
        OriginSolarSystemId: reader.GetInt64(3),
        CoveredSolarSystemIds: reader.GetFieldValue<long[]>(4),
        StructureId: reader.GetInt64(5),
        State: reader.GetInt32(6),
        StartedMs: reader.GetInt64(7),
        MaxEndsMs: reader.GetInt64(8),
        StateExpiresMs: reader.GetInt64(9),
        PiratePointsRequired: reader.GetInt32(10),
        AntipiratePointsRequired: reader.GetInt32(11),
        PiratePointsScored: reader.GetInt32(12),
        AntipiratePointsScored: reader.GetInt32(13),
        EndedMs: reader.GetInt64(14));
}
