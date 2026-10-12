using Eve.Data;
using Npgsql;

namespace Eve.Services.Space.Beacons;

/// <summary>One CRAB's own record (the host's, in the space schema): its activation, its link and its scan, and how it ended (null while it runs).</summary>
public sealed record BeaconRow(
    long DeployableId, int TypeId, int SolarSystemId, long OwnerId, long ShipId, long DeployedMs, long ActivateAtMs, bool Active, int LinkState,
    long LinkCompleteMs, long LinkedShipId, long ScanMs, long ScanUpdatedMs, string? Outcome, bool Rewarded, int WavesMade);

/// <summary>
/// The tables of the CRAB runtime (<see cref="AnalysisBeaconDirector"/>): one row per beacon, and one row per Rogue Drone it has made, so a kill is a delete and the
/// count of the drones alive is a query. The beacon's item and its position, lifetime and ball are the deployed-record store's (ADR 0012).
/// </summary>
public sealed class AnalysisBeaconStore(NpgsqlDataSource source, string schema)
{
    public const string MigrationName = "001_analysis_beacons";

    private const string MigrationText = """
        CREATE TABLE analysis_beacons (
            deployable_id bigint PRIMARY KEY,
            type_id integer NOT NULL,
            solar_system_id integer NOT NULL,
            owner_id bigint NOT NULL,
            ship_id bigint NOT NULL,
            deployed_ms bigint NOT NULL,
            activate_at_ms bigint NOT NULL,
            active boolean NOT NULL DEFAULT false,
            link_state integer NOT NULL DEFAULT 1,
            link_complete_ms bigint NOT NULL DEFAULT 0,
            linked_ship_id bigint NOT NULL DEFAULT 0,
            scan_ms bigint NOT NULL DEFAULT 0,
            scan_updated_ms bigint NOT NULL DEFAULT 0,
            outcome text NULL,
            outcome_reason text NULL,
            rewarded boolean NOT NULL DEFAULT false
        );
        CREATE INDEX analysis_beacons_running ON analysis_beacons (outcome) WHERE outcome IS NULL;
        CREATE TABLE analysis_beacon_drones (
            ship_id bigint PRIMARY KEY,
            deployable_id bigint NOT NULL
        );
        CREATE INDEX analysis_beacon_drones_beacon ON analysis_beacon_drones (deployable_id);
        """;

    /// <summary>How many of a beacon's waves were made (the first at activation, the next when the scan reaches its progress).</summary>
    private const string WavesText = "ALTER TABLE analysis_beacons ADD COLUMN waves_made integer NOT NULL DEFAULT 0;";

    private const string Columns =
        "deployable_id, type_id, solar_system_id, owner_id, ship_id, deployed_ms, activate_at_ms, active, link_state, link_complete_ms, linked_ship_id, scan_ms, scan_updated_ms, outcome, rewarded, waves_made";

    private readonly NpgsqlDataSource _source = source;
    private readonly string _schema = Sql.CheckSchema(schema);

    private string Q(string text) => Sql.In(_schema, text);

    /// <summary>Creates the tables in the schema (applied once; a second start changes nothing).</summary>
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, [new Migration(MigrationName, MigrationText), new Migration("002_analysis_beacon_waves", WavesText)], cancellationToken);

    /// <summary>The row of a beacon, or null when it has none.</summary>
    public async Task<BeaconRow?> GetAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long deployableId, bool forUpdate, CancellationToken cancellationToken)
    {
        var sql = Q($"SELECT {Columns} FROM {{schema}}.analysis_beacons WHERE deployable_id = $1" + (forUpdate ? " FOR UPDATE" : ""));
        await using var command = Sql.Command(connection, transaction, sql, deployableId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>The rows of the beacons that have not ended, locked for the scan tick.</summary>
    public async Task<IReadOnlyList<BeaconRow>> RunningAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<BeaconRow>();
        await using var command = Sql.Command(connection, transaction, Q($"SELECT {Columns} FROM {{schema}}.analysis_beacons WHERE outcome IS NULL ORDER BY deployable_id FOR UPDATE"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(Read(reader));
        }
        return rows;
    }

    /// <summary>Makes the row of a beacon put in space (given once: a row already there is left as it is).</summary>
    public Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, BeaconRow row, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(connection, transaction, Q(
            "INSERT INTO {schema}.analysis_beacons (deployable_id, type_id, solar_system_id, owner_id, ship_id, deployed_ms, activate_at_ms) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT (deployable_id) DO NOTHING"),
            cancellationToken, row.DeployableId, row.TypeId, row.SolarSystemId, row.OwnerId, row.ShipId, row.DeployedMs, row.ActivateAtMs);

    /// <summary>Records that a beacon is active (its scan runs from now).</summary>
    public Task ActivateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long deployableId, long nowMs, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.analysis_beacons SET active = true, scan_updated_ms = $2, waves_made = 1 WHERE deployable_id = $1"),
            cancellationToken, deployableId, nowMs);

    /// <summary>Records how many of the beacon's waves are made.</summary>
    public Task SetWavesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long deployableId, int waves, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.analysis_beacons SET waves_made = $2 WHERE deployable_id = $1"), cancellationToken, deployableId, waves);

    /// <summary>Records the scan's progress (in milliseconds) and when it was last advanced.</summary>
    public Task SetScanAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long deployableId, long scanMs, long nowMs, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.analysis_beacons SET scan_ms = $2, scan_updated_ms = $3 WHERE deployable_id = $1"),
            cancellationToken, deployableId, scanMs, nowMs);

    /// <summary>Ends a beacon's run with its outcome (completed, failed or expired) and why.</summary>
    public Task EndAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long deployableId, string outcome, string reason, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.analysis_beacons SET outcome = $2, outcome_reason = $3 WHERE deployable_id = $1 AND outcome IS NULL"),
            cancellationToken, deployableId, outcome, reason);

    /// <summary>Marks the reward of a completed scan as granted (once).</summary>
    public Task MarkRewardedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long deployableId, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.analysis_beacons SET rewarded = true WHERE deployable_id = $1"), cancellationToken, deployableId);

    /// <summary>A link that can start: the beacon is idle and has no ship. Answers whether the link was taken (a second request does not take it again).</summary>
    public async Task<bool> StartLinkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long deployableId, long shipId, long completeMs, CancellationToken cancellationToken)
    {
        var sql = Q("UPDATE {schema}.analysis_beacons SET link_state = 2, linked_ship_id = $2, link_complete_ms = $3 " +
                    "WHERE deployable_id = $1 AND link_state = 1 AND linked_ship_id = 0 AND outcome IS NULL");
        return await Sql.ExecuteAsync(connection, transaction, sql, cancellationToken, deployableId, shipId, completeMs) > 0;
    }

    /// <summary>The link is complete (it was running and its time is up).</summary>
    public Task<int> CompleteLinkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long deployableId, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.analysis_beacons SET link_state = 3 WHERE deployable_id = $1 AND link_state = 2"), cancellationToken, deployableId);

    /// <summary>Makes the drones of a beacon (the NPCs of its wave, which must die before the scan goes on).</summary>
    public async Task AddDronesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long deployableId, IEnumerable<long> shipIds, CancellationToken cancellationToken)
    {
        foreach (var ship in shipIds)
        {
            await Sql.ExecuteAsync(connection, transaction, Q("INSERT INTO {schema}.analysis_beacon_drones (ship_id, deployable_id) VALUES ($1, $2)"), cancellationToken, ship, deployableId);
        }
    }

    /// <summary>How many of a beacon's drones are alive.</summary>
    public async Task<long> DronesAliveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long deployableId, CancellationToken cancellationToken) =>
        Convert.ToInt64(await Sql.ScalarAsync(connection, transaction, Q("SELECT COUNT(*) FROM {schema}.analysis_beacon_drones WHERE deployable_id = $1"), cancellationToken, deployableId));

    /// <summary>Takes the drones of a beacon out of the rows: the ids of the ships (for the simulation to take out).</summary>
    public async Task<IReadOnlyList<long>> TakeDronesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long deployableId, CancellationToken cancellationToken)
    {
        var ships = new List<long>();
        await using var command = Sql.Command(connection, transaction, Q("DELETE FROM {schema}.analysis_beacon_drones WHERE deployable_id = $1 RETURNING ship_id"), deployableId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ships.Add(reader.GetInt64(0));
        }
        return ships;
    }

    /// <summary>A kill: the drone is no longer alive (a ship that is not a drone of a beacon is left alone).</summary>
    public Task<int> ForgetDroneAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long shipId, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(connection, transaction, Q("DELETE FROM {schema}.analysis_beacon_drones WHERE ship_id = $1"), cancellationToken, shipId);

    private static BeaconRow Read(System.Data.Common.DbDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
        reader.GetBoolean(7), reader.GetInt32(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12),
        reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetBoolean(14), reader.GetInt32(15));

    /// <summary>The store's source, for the director's own transactions.</summary>
    internal NpgsqlDataSource Source => _source;
}
