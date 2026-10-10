using Npgsql;

namespace Eve.Ships;

/// <summary>A scanner probe the host keeps a record of (EVE.js probeRuntimeState): its id, whose and what it is, where it lies and when it goes.</summary>
public sealed record ScanProbeRecord(
    long ProbeId, long CharacterId, int SolarSystemId, int TypeId, int GroupId, long LauncherId, long ShipId, double[] Position, double[] Destination,
    double ScanRange, int RangeStep, int State, long ExpiresAtMs);

/// <summary>The scanner probes in PostgreSQL (the <c>scan_probes</c> table of the <c>space</c> schema, migration 0005).</summary>
public sealed class PgProbeStore
{
    /// <summary>The first id of a probe (EVE.js <c>PROBE_ID_BASE</c>); the probes are numbered after it.</summary>
    public const long ProbeIdBase = 990_000_000_000;

    public const string DefaultSchema = "space";

    private const string Columns =
        "probe_id, character_id, solar_system_id, type_id, group_id, launcher_id, ship_id, x, y, z, dest_x, dest_y, dest_z, scan_range, range_step, state, expires_at_ms";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgProbeStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    private static ScanProbeRecord Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt64(5), r.GetInt64(6),
        [r.GetDouble(7), r.GetDouble(8), r.GetDouble(9)], [r.GetDouble(10), r.GetDouble(11), r.GetDouble(12)], r.GetDouble(13), r.GetInt32(14), r.GetInt32(15),
        r.GetInt64(16));

    /// <summary>The id of the next probe: the base plus the next number of the sequence.</summary>
    public async Task<long> NextIdAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT nextval('{_schema}.scan_probe_seq')", connection);
        return ProbeIdBase + (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task SaveAsync(ScanProbeRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.scan_probes ({Columns}) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)
            ON CONFLICT (probe_id) DO UPDATE SET
                state = EXCLUDED.state, x = EXCLUDED.x, y = EXCLUDED.y, z = EXCLUDED.z, dest_x = EXCLUDED.dest_x, dest_y = EXCLUDED.dest_y,
                dest_z = EXCLUDED.dest_z, scan_range = EXCLUDED.scan_range, range_step = EXCLUDED.range_step, expires_at_ms = EXCLUDED.expires_at_ms
            """,
            connection);
        foreach (var value in new object[]
        {
            record.ProbeId, record.CharacterId, record.SolarSystemId, record.TypeId, record.GroupId, record.LauncherId, record.ShipId,
            record.Position[0], record.Position[1], record.Position[2], record.Destination[0], record.Destination[1], record.Destination[2],
            record.ScanRange, record.RangeStep, record.State, record.ExpiresAtMs,
        })
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ScanProbeRecord?> GetAsync(long probeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM {_schema}.scan_probes WHERE probe_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = probeId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>Forgets a probe (it is gone). Returns whether it was recorded.</summary>
    public async Task<bool> RemoveAsync(long probeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"DELETE FROM {_schema}.scan_probes WHERE probe_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = probeId });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>The probes a character has in a system that are active (a probe that is inactive does not count against the launch cap).</summary>
    public async Task<int> ActiveCountAsync(long characterId, int solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {_schema}.scan_probes WHERE character_id = $1 AND solar_system_id = $2 AND state > 0", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = solarSystemId });
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>The probes in a system that are still in their life at <paramref name="nowMs"/> (the balls to put back in space after a restart).</summary>
    public async Task<IReadOnlyList<ScanProbeRecord>> InSystemAsync(int solarSystemId, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.scan_probes WHERE solar_system_id = $1 AND expires_at_ms > $2 ORDER BY probe_id", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = solarSystemId });
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        var list = new List<ScanProbeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    /// <summary>What is due to go by <paramref name="nowMs"/>.</summary>
    public async Task<IReadOnlyList<ScanProbeRecord>> DueAsync(long nowMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.scan_probes WHERE expires_at_ms <= $1 ORDER BY expires_at_ms, probe_id", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        var list = new List<ScanProbeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }
}
