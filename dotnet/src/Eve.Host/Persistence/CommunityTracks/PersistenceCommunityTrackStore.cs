using Eve.Data;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Persistence;

/// <summary>Durable state owned by the community track producer and read by the V24 RPC.</summary>
public sealed class PersistenceCommunityTrackStore(NpgsqlDataSource source, string schema = PersistenceCommunityTrackStore.DefaultSchema)
{
    public const string DefaultSchema = "community_tracks";

    private string Q(string sql) => Sql.In(Sql.CheckSchema(schema), sql);

    public static Task MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema,
        [new Migration("0001_community_track_instances", """
            CREATE TABLE IF NOT EXISTS community_track_instances (
                instance_id bigint PRIMARY KEY CHECK (instance_id > 0),
                definition_id bigint NOT NULL CHECK (definition_id > 0),
                progress bigint NOT NULL CHECK (progress >= 0),
                updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
            );
            CREATE INDEX IF NOT EXISTS community_track_instances_updated_at_idx
                ON community_track_instances (updated_at DESC, instance_id);
            """)], cancellationToken);

    public async ValueTask<CommunityTrackInstanceAttributes?> ReadAsync(long instanceId, CancellationToken cancellationToken)
    {
        if (instanceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(instanceId));

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q("""
            SELECT definition_id, progress
            FROM {schema}.community_track_instances
            WHERE instance_id = $1
            """), instanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CommunityTrackInstanceAttributes(reader.GetInt64(0), reader.GetInt64(1))
            : null;
    }

    /// <summary>Creates or advances an instance in the same durable state read by the client RPC.</summary>
    public async Task SetInstanceAttributesAsync(long instanceId, long definitionId, long progress, CancellationToken cancellationToken = default)
    {
        if (instanceId <= 0) throw new ArgumentOutOfRangeException(nameof(instanceId));
        if (definitionId <= 0) throw new ArgumentOutOfRangeException(nameof(definitionId));
        if (progress < 0) throw new ArgumentOutOfRangeException(nameof(progress));

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q("""
            INSERT INTO {schema}.community_track_instances (instance_id, definition_id, progress)
            VALUES ($1, $2, $3)
            ON CONFLICT (instance_id) DO UPDATE
            SET definition_id = EXCLUDED.definition_id,
                progress = EXCLUDED.progress,
                updated_at = clock_timestamp()
            """), instanceId, definitionId, progress);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Atomically adds points for one completed community-track action.</summary>
    public async Task<long?> AddProgressAsync(long instanceId, long amount, CancellationToken cancellationToken = default)
    {
        if (instanceId <= 0) throw new ArgumentOutOfRangeException(nameof(instanceId));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q("""
            UPDATE {schema}.community_track_instances
            SET progress = progress + $1,
                updated_at = clock_timestamp()
            WHERE instance_id = $2
            RETURNING progress
            """), amount, instanceId);
        var progress = await command.ExecuteScalarAsync(cancellationToken);
        return progress is long value ? value : null;
    }

    public async Task<bool> RemoveAsync(long instanceId, CancellationToken cancellationToken = default)
    {
        if (instanceId <= 0) throw new ArgumentOutOfRangeException(nameof(instanceId));

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        return await Sql.ExecuteAsync(connection, null, Q("DELETE FROM {schema}.community_track_instances WHERE instance_id = $1"),
            cancellationToken, instanceId) == 1;
    }
}
