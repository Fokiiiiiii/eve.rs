using System.Text.RegularExpressions;
using Npgsql;

namespace Eve.Ships;

/// <summary>
/// The per-ship dirt timestamp (an EVE FILETIME) that <c>ship.ResetDirtTimestamp</c> writes and <c>ship.GetDirtTimestamp</c> reads. A ship has no row
/// until it is first cleaned; the table is created by the ship migrations (<c>Migrations/0027_ship_dirt_timestamps.sql</c>), applied with
/// <see cref="PgShipConfigurationStore.MigrateAsync"/>.
/// </summary>
public sealed class PgShipDirtStore
{
    public const string DefaultSchema = "space";
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgShipDirtStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant))
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        _source = source;
        _schema = schema;
    }

    /// <summary>The stored dirt timestamp of the ship, or null when the ship has never been cleaned.</summary>
    public async Task<long?> GetAsync(long shipId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand(
            $"SELECT dirt_filetime FROM {_schema}.ship_dirt_timestamps WHERE ship_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = shipId });
        return await command.ExecuteScalarAsync(cancellationToken) is long filetime ? filetime : null;
    }

    public async Task SetAsync(long shipId, long dirtFileTime, CancellationToken cancellationToken = default)
    {
        if (shipId <= 0)
            throw new ArgumentOutOfRangeException(nameof(shipId));
        await using var command = _source.CreateCommand(
            $"INSERT INTO {_schema}.ship_dirt_timestamps (ship_id, dirt_filetime) VALUES ($1, $2) ON CONFLICT (ship_id) DO UPDATE SET dirt_filetime = EXCLUDED.dirt_filetime");
        command.Parameters.Add(new NpgsqlParameter { Value = shipId });
        command.Parameters.Add(new NpgsqlParameter { Value = dirtFileTime });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
