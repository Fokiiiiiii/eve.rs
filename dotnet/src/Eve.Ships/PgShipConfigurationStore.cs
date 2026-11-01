using System.Text.RegularExpressions;
using Eve.Data;
using Npgsql;

namespace Eve.Ships;

public sealed record ShipConfiguration(bool SmbFleetAccess, bool SmbCorpAccess, bool FleetHangarFleetAccess, bool FleetHangarCorpAccess)
{
    public static readonly ShipConfiguration Default = new(false, false, false, false);
}

public sealed class PgShipConfigurationStore
{
    public const string DefaultSchema = "space";
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgShipConfigurationStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant))
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        _source = source;
        _schema = schema;
    }

    public static IReadOnlyList<Migration> MigrationSet() =>
        Migrations.Embedded(typeof(PgShipConfigurationStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    public async Task<ShipConfiguration> GetAsync(long shipId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand(
            $"SELECT smb_fleet_access, smb_corp_access, fleet_hangar_fleet_access, fleet_hangar_corp_access FROM {_schema}.ship_configurations WHERE ship_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = shipId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ShipConfiguration(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3))
            : ShipConfiguration.Default;
    }

    public async Task PatchAsync(
        long shipId, bool? smbFleetAccess, bool? smbCorpAccess, bool? fleetHangarFleetAccess, bool? fleetHangarCorpAccess,
        CancellationToken cancellationToken = default)
    {
        if (shipId <= 0)
            throw new ArgumentOutOfRangeException(nameof(shipId));
        if (smbFleetAccess is null && smbCorpAccess is null && fleetHangarFleetAccess is null && fleetHangarCorpAccess is null)
            return;

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.ship_configurations (ship_id, smb_fleet_access, smb_corp_access, fleet_hangar_fleet_access, fleet_hangar_corp_access)
            VALUES ($1, COALESCE($2, FALSE), COALESCE($3, FALSE), COALESCE($4, FALSE), COALESCE($5, FALSE))
            ON CONFLICT (ship_id) DO UPDATE SET
                smb_fleet_access = COALESCE($2, ship_configurations.smb_fleet_access),
                smb_corp_access = COALESCE($3, ship_configurations.smb_corp_access),
                fleet_hangar_fleet_access = COALESCE($4, ship_configurations.fleet_hangar_fleet_access),
                fleet_hangar_corp_access = COALESCE($5, ship_configurations.fleet_hangar_corp_access)
            """,
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = shipId });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)smbFleetAccess ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)smbCorpAccess ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)fleetHangarFleetAccess ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)fleetHangarCorpAccess ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
