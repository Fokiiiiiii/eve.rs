using Eve.Events;
using Eve.Host.Persistence;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Host.PublicGateway;

/// <summary>Completes persisted SKINR sequence jobs through the host's durable scheduler.</summary>
internal static class ShipSkinrSequenceJobs
{
    public const string Kind = "ship-skinr.sequence.complete";
    public const string CompletedTopic = "ship.skinr.sequence.completed";

    public static void Register(DurableScheduler scheduler, PgShipSkinrStore store, EventBus events)
    {
        scheduler.Register(Kind, (context, cancellationToken) =>
            CompleteAsync(context, store, events, cancellationToken));
    }

    private static async Task CompleteAsync(
        JobContext context,
        PgShipSkinrStore store,
        EventBus events,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Key, out var jobId))
            throw new InvalidOperationException("the SKINR sequence scheduler key is not a UUID");

        await using var select = new NpgsqlCommand(store.Q("""
            UPDATE {schema}.skinr_sequencing_jobs
            SET state = 'completed', completed_ms = $2
            WHERE job_id = $1 AND state = 'pending' AND due_ms <= $2
            RETURNING character_id, skin_hex, ship_type_id, layout, editor_configurations,
                      name, line_name, licenses
            """), context.Connection, context.Transaction);
        select.Parameters.Add(new NpgsqlParameter { Value = jobId });
        select.Parameters.Add(new NpgsqlParameter { Value = context.NowMs });
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return;

        var characterId = reader.GetInt64(0);
        var skinHex = reader.GetString(1);
        var shipTypeId = reader.GetInt32(2);
        var layout = reader.GetFieldValue<byte[]>(3);
        var editorConfigurations = reader.GetFieldValue<byte[]>(4);
        var name = reader.GetString(5);
        var lineName = reader.GetString(6);
        var licenses = reader.GetInt32(7);
        await reader.DisposeAsync();

        await using var grant = new NpgsqlCommand(store.Q("""
            INSERT INTO {schema}.skinr_ship_licenses
                (character_id, skin_hex, ship_type_id, layout, editor_configurations,
                 name, line_name, activated, unactivated, escrowed, first_acquired_ms, updated_ms)
            VALUES ($1, $2, $3, $4, $5, $6, $7, false, $8, 0, $9, $9)
            ON CONFLICT (character_id, skin_hex) DO UPDATE SET
                unactivated = skinr_ship_licenses.unactivated + EXCLUDED.unactivated,
                updated_ms = EXCLUDED.updated_ms
            RETURNING activated, unactivated, escrowed, first_acquired_ms
            """), context.Connection, context.Transaction);
        grant.Parameters.Add(new NpgsqlParameter { Value = characterId });
        grant.Parameters.Add(new NpgsqlParameter { Value = skinHex });
        grant.Parameters.Add(new NpgsqlParameter { Value = shipTypeId });
        grant.Parameters.Add(new NpgsqlParameter { Value = layout });
        grant.Parameters.Add(new NpgsqlParameter { Value = editorConfigurations });
        grant.Parameters.Add(new NpgsqlParameter { Value = name });
        grant.Parameters.Add(new NpgsqlParameter { Value = lineName });
        grant.Parameters.Add(new NpgsqlParameter { Value = licenses });
        grant.Parameters.Add(new NpgsqlParameter { Value = context.NowMs });
        await using var license = await grant.ExecuteReaderAsync(cancellationToken);
        if (!await license.ReadAsync(cancellationToken))
            throw new InvalidOperationException("the SKINR sequence did not grant its license");
        var activated = license.GetBoolean(0);
        var unactivated = license.GetInt64(1);
        var escrowed = license.GetInt64(2);
        var firstAcquiredMs = license.GetInt64(3);
        await license.DisposeAsync();

        await events.PublishAsync(context.Connection, context.Transaction, CompletedTopic,
            new SequenceCompleted(jobId, characterId, skinHex, licenses, activated, unactivated,
                escrowed, firstAcquiredMs, context.NowMs),
            jobId.ToString("N"), cancellationToken);
    }

    internal sealed record SequenceCompleted(
        Guid JobId,
        long CharacterId,
        string SkinHex,
        int Quantity,
        bool Activated,
        long Unactivated,
        long Escrowed,
        long FirstAcquiredMs,
        long CompletedMs);
}
