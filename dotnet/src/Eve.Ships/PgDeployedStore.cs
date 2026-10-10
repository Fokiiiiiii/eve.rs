using Npgsql;

namespace Eve.Ships;

/// <summary>
/// A deployable the host keeps a record of (ADR 0012): its id, what it is (<see cref="Kind"/> names the expiry entry the sweep runs for it), where it lies, whose it
/// is and when it goes. <see cref="Position"/> is x, y, z. A mobile warp disruptor also has when it is anchored (<see cref="ActivateAtMs"/>) and its range.
/// </summary>
public sealed record DeployedRecord(
    long DeployableId, string Kind, int SolarSystemId, string Name, int TypeId, int GroupId, double[] Position, double Radius, long OwnerId, int CorporationId,
    int AllianceId, long ShipId, long ExpiresAtMs, long ActivateAtMs = 0, double RangeMeters = 0);

/// <summary>The deployed records in PostgreSQL (the <c>deployed</c> table of the <c>space</c> schema, migration 0004).</summary>
public sealed class PgDeployedStore
{
    public const string DefaultSchema = "space";

    private const string Columns =
        "deployable_id, kind, solar_system_id, name, type_id, group_id, x, y, z, radius, owner_id, corporation_id, alliance_id, ship_id, expires_at_ms, activate_at_ms, range_m";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgDeployedStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    private static DeployedRecord Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), [r.GetDouble(6), r.GetDouble(7), r.GetDouble(8)], r.GetDouble(9),
        r.GetInt64(10), r.GetInt32(11), r.GetInt32(12), r.GetInt64(13), r.GetInt64(14), r.GetInt64(15), r.GetDouble(16));

    /// <summary>The record of a deployable, or null when it is not recorded.</summary>
    public async Task<DeployedRecord?> GetAsync(long deployableId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.deployed WHERE deployable_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = deployableId });
        return (await ReadAllAsync(command, cancellationToken)).SingleOrDefault();
    }

    /// <summary>Records a deployable (a second record of the same id replaces the first).</summary>
    public async Task SaveAsync(DeployedRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.deployed ({Columns}) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)
            ON CONFLICT (deployable_id) DO UPDATE SET
                kind = EXCLUDED.kind, solar_system_id = EXCLUDED.solar_system_id, name = EXCLUDED.name, type_id = EXCLUDED.type_id, group_id = EXCLUDED.group_id,
                x = EXCLUDED.x, y = EXCLUDED.y, z = EXCLUDED.z, radius = EXCLUDED.radius, owner_id = EXCLUDED.owner_id, corporation_id = EXCLUDED.corporation_id,
                alliance_id = EXCLUDED.alliance_id, ship_id = EXCLUDED.ship_id, expires_at_ms = EXCLUDED.expires_at_ms,
                activate_at_ms = EXCLUDED.activate_at_ms, range_m = EXCLUDED.range_m
            """,
            connection);
        foreach (var value in new object[]
        {
            record.DeployableId, record.Kind, record.SolarSystemId, record.Name, record.TypeId, record.GroupId, record.Position[0], record.Position[1], record.Position[2],
            record.Radius, record.OwnerId, record.CorporationId, record.AllianceId, record.ShipId, record.ExpiresAtMs, record.ActivateAtMs, record.RangeMeters,
        })
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Forgets a deployable (it is gone). Returns whether it was recorded.</summary>
    public async Task<bool> RemoveAsync(long deployableId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"DELETE FROM {_schema}.deployed WHERE deployable_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = deployableId });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>What lies in a solar system, oldest first.</summary>
    public async Task<IReadOnlyList<DeployedRecord>> InSystemAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.deployed WHERE solar_system_id = $1 ORDER BY deployable_id");
        command.Parameters.Add(new NpgsqlParameter { Value = solarSystemId });
        return await ReadAllAsync(command, cancellationToken);
    }

    /// <summary>What is due to go by <paramref name="nowMs"/>.</summary>
    public async Task<IReadOnlyList<DeployedRecord>> DueAsync(long nowMs, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.deployed WHERE expires_at_ms <= $1 ORDER BY expires_at_ms, deployable_id");
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        return await ReadAllAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<DeployedRecord>> ReadAllAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var list = new List<DeployedRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }
}
