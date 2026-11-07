using Eve.Data;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>A container of a cosmic site: where it lies, what it is, its failed attempts and its state (<c>secure</c>, <c>cleared</c> or <c>destroyed</c>).</summary>
public sealed record SiteContainerRecord(
    long ContainerId, long SiteId, int SolarSystemId, string Role, bool Relic, int TypeId, int GroupId,
    double[] Position, double Radius, int Difficulty, int Failures, string State, long ExpiresAtMs);

public sealed record SiteContainerHackAttempt(Guid ReservationId, long ContainerId, long CharacterId, string State, IReadOnlyList<HackingLootItem> Contents);

/// <summary>The site containers in PostgreSQL (the <c>site_containers</c> table of <c>Migrations/0001_site_containers.sql</c>).</summary>
public sealed class SiteContainerStore
{
    public const string DefaultSchema = "sites";

    private const string Columns =
        "container_id, site_id, solar_system_id, role, relic, type_id, group_id, x, y, z, radius, difficulty, failures, state, expires_at_ms";

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public SiteContainerStore(NpgsqlDataSource source, string schema = DefaultSchema)
    {
        _source = source;
        _schema = Sql.CheckSchema(schema);
    }

    public static IReadOnlyList<Migration> MigrationSet() =>
        Migrations.Embedded(typeof(SiteContainerStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    /// <summary>Places containers in the caller's transaction (a container already recorded is left as it is).</summary>
    public async Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<SiteContainerRecord> records, CancellationToken cancellationToken = default)
    {
        foreach (var record in records)
        {
            await using var command = new NpgsqlCommand(
                $"INSERT INTO {_schema}.site_containers ({Columns}) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15) ON CONFLICT (container_id) DO NOTHING",
                connection, transaction);
            foreach (var value in new object[]
            {
                record.ContainerId, record.SiteId, record.SolarSystemId, record.Role, record.Relic, record.TypeId, record.GroupId,
                record.Position[0], record.Position[1], record.Position[2], record.Radius, record.Difficulty, record.Failures, record.State, record.ExpiresAtMs,
            })
            {
                command.Parameters.Add(new NpgsqlParameter { Value = value });
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<SiteContainerRecord?> GetAsync(long containerId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.site_containers WHERE container_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = containerId });
        return (await ReadAllAsync(command, cancellationToken)).SingleOrDefault();
    }

    public async Task<SiteContainerRecord?> GetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long containerId, bool forUpdate, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.site_containers WHERE container_id = $1{(forUpdate ? " FOR UPDATE" : "")}", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = containerId });
        return (await ReadAllAsync(command, cancellationToken)).SingleOrDefault();
    }

    /// <summary>Claims a secure container before waiting for the simulation's analyzer reply. The partial unique index arbitrates across Host instances.</summary>
    public async Task<bool> TryReserveHackAsync(long containerId, Guid reservationId, long characterId, long sessionId, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.site_container_hack_attempts
                (reservation_id, container_id, character_id, session_id, status, contents, started_at_ms)
            SELECT $1, container_id, $3, $4, 'active', '[]'::jsonb, $5
            FROM {_schema}.site_containers
            WHERE container_id = $2 AND state = 'secure' AND expires_at_ms > $5
            FOR UPDATE
            ON CONFLICT (container_id) WHERE status = 'active' DO NOTHING
            RETURNING reservation_id
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = reservationId });
        command.Parameters.Add(new NpgsqlParameter { Value = containerId });
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = sessionId });
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        var reserved = await command.ExecuteScalarAsync(cancellationToken) is Guid;
        await transaction.CommitAsync(cancellationToken);
        return reserved;
    }

    public async Task ReleaseHackReservationAsync(Guid reservationId, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"""
            UPDATE {_schema}.site_container_hack_attempts
            SET status = 'cancelled', completed_at_ms = $2
            WHERE reservation_id = $1 AND status = 'active'
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = reservationId });
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SiteContainerHackAttempt?> GetHackAttemptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid reservationId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT reservation_id, container_id, character_id, status, contents::text
            FROM {_schema}.site_container_hack_attempts
            WHERE reservation_id = $1
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = reservationId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var contents = System.Text.Json.JsonSerializer.Deserialize<HackingLootItem[]>(reader.GetString(4)) ?? [];
        return new SiteContainerHackAttempt(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), contents);
    }

    public async Task<bool> CompleteHackAttemptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid reservationId, string state, IReadOnlyList<HackingLootItem> contents,
        long nowMs, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {_schema}.site_container_hack_attempts
            SET status = $2, contents = $3::jsonb, completed_at_ms = $4
            WHERE reservation_id = $1 AND status = 'active'
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = reservationId });
        command.Parameters.Add(new NpgsqlParameter { Value = state });
        command.Parameters.Add(new NpgsqlParameter { Value = System.Text.Json.JsonSerializer.Serialize(contents) });
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Clears attempt reservations left by a previous Host process; its in-memory hacking windows no longer exist.</summary>
    public async Task ResetActiveHackAttemptsAsync(long nowMs, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"""
            UPDATE {_schema}.site_container_hack_attempts
            SET status = 'cancelled', completed_at_ms = $1
            WHERE status = 'active'
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The containers of a system that are still hackable.</summary>
    public async Task<IReadOnlyList<SiteContainerRecord>> InSystemAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.site_containers WHERE solar_system_id = $1 AND state IN ('secure', 'looted') ORDER BY container_id");
        command.Parameters.Add(new NpgsqlParameter { Value = solarSystemId });
        return await ReadAllAsync(command, cancellationToken);
    }

    /// <summary>Counts a lost attempt on a hackable container: the new count, or null when the container is not hackable.</summary>
    public async Task<int?> RecordFailureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long containerId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.site_containers SET failures = failures + 1 WHERE container_id = $1 AND state = 'secure' RETURNING failures", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = containerId });
        return await command.ExecuteScalarAsync(cancellationToken) is int failures ? failures : null;
    }

    public async Task SetStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long containerId, string state, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"UPDATE {_schema}.site_containers SET state = $2 WHERE container_id = $1", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = containerId });
        command.Parameters.Add(new NpgsqlParameter { Value = state });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> SetStateIfSecureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long containerId, string state, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.site_containers SET state = $2 WHERE container_id = $1 AND state = 'secure'", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = containerId });
        command.Parameters.Add(new NpgsqlParameter { Value = state });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static SiteContainerRecord Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.GetBoolean(4), r.GetInt32(5), r.GetInt32(6),
        [r.GetDouble(7), r.GetDouble(8), r.GetDouble(9)], r.GetDouble(10), r.GetInt32(11), r.GetInt32(12), r.GetString(13), r.GetInt64(14));

    private static async Task<IReadOnlyList<SiteContainerRecord>> ReadAllAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var list = new List<SiteContainerRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }
}
