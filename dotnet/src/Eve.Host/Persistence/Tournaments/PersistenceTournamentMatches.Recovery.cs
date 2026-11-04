using Eve.Scheduler;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private const string ParticipantRecoveryJob = "tournament.participants.return";
    private sealed record ParticipantRecovery(long MatchId);

    public static Task MigrateRecoveryAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0009_tournament_return_lease", """
            ALTER TABLE match_participants ADD COLUMN return_lease uuid, ADD COLUMN return_lease_until_ms bigint;
            """)], ct);

    /// <summary>Ensures completed participants have really returned; false means durable recovery is still pending.</summary>
    public async Task<bool> ReturnCompletedParticipantsAsync(long matchId, CancellationToken ct)
    {
        if (scheduler is null || sessions is null || space is null)
            throw new InvalidOperationException("Tournament participant recovery is unavailable.");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), c, tx))
            await gate.ExecuteNonQueryAsync(ct);
        await using (var match = new NpgsqlCommand(Q("SELECT 1 FROM {schema}.matches WHERE id=$1 AND state=6 FOR UPDATE"), c, tx))
        {
            match.Parameters.AddWithValue(matchId);
            if (await match.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("The tournament match is not complete.");
        }
        bool pending;
        await using (var query = new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.match_participants WHERE match_id=$1)"), c, tx))
        {
            query.Parameters.AddWithValue(matchId);
            pending = (bool)(await query.ExecuteScalarAsync(ct))!;
        }
        if (pending)
            await scheduler.ScheduleAsync(c, tx, new JobSpec(ParticipantRecoveryJob, scheduler.Now)
            { Key = $"match:{matchId}", Payload = new ParticipantRecovery(matchId), OnDuplicate = OnDuplicate.Keep }, ct);
        await tx.CommitAsync(ct);
        if (pending) scheduler.Wake();
        return !pending;
    }

    public void RegisterSpaceHandlers()
    {
        if (scheduler is null || sessions is null || space is null)
            throw new InvalidOperationException("Tournament participant recovery requires the scheduler and space sessions.");
        RegisterParticipantDeathHandler();
        scheduler.Register(ParticipantRecoveryJob, RecoverParticipantsAsync);
        scheduler.Register(RestrictionRepairJob, ReconcileTournamentRestrictionsAsync);
    }

    private async Task ScheduleParticipantRecoveryAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long matchId, CancellationToken ct)
    {
        if (scheduler is null) throw new InvalidOperationException("Tournament participant recovery requires the scheduler.");
        await using (var closing = new NpgsqlCommand(Q("UPDATE {schema}.matches SET closing=true WHERE id=$1"), connection, tx))
        { closing.Parameters.AddWithValue(matchId); await closing.ExecuteNonQueryAsync(ct); }
        await scheduler.ScheduleAsync(connection, tx, new JobSpec(ParticipantRecoveryJob, scheduler.Now)
        { Key = $"match:{matchId}", Payload = new ParticipantRecovery(matchId) }, ct);
    }

    private async Task RecoverParticipantsAsync(JobContext job, CancellationToken ct)
    {
        if (scheduler is null || sessions is null || space is null)
            throw new InvalidOperationException("Tournament participant recovery is unavailable.");
        var recovery = job.Payload<ParticipantRecovery>() ?? throw new InvalidOperationException("Missing tournament recovery payload.");
        var token = Guid.NewGuid();
        TournamentReturn? record = null;
        // Commit the lease before touching the simulation. A crashed worker leaves
        // the original destination intact and another attempt can claim it later.
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            await using var claim = new NpgsqlCommand(Q("""
                UPDATE {schema}.match_participants p SET return_lease=$2,return_lease_until_ms=$3
                WHERE (p.match_id,p.character_id) = (
                    SELECT x.match_id,x.character_id FROM {schema}.match_participants x
                    WHERE x.match_id=$1 AND (x.return_lease_until_ms IS NULL OR x.return_lease_until_ms <= $4)
                    ORDER BY x.character_id FOR UPDATE SKIP LOCKED LIMIT 1)
                RETURNING character_id,ship_id,original_system_id,COALESCE(original_station_id,original_structure_id),return_operation_id,COALESCE(return_ship_id,ship_id),death_home_id
                """), c, tx);
            claim.Parameters.AddWithValue(recovery.MatchId); claim.Parameters.AddWithValue(token);
            claim.Parameters.AddWithValue(scheduler.Now + 30000); claim.Parameters.AddWithValue(scheduler.Now);
            await using (var row = await claim.ExecuteReaderAsync(ct))
                if (await row.ReadAsync(ct))
                {
                    if (row.IsDBNull(3)) throw new InvalidOperationException("Tournament return has no dock destination.");
                    record = new TournamentReturn(row.GetInt64(0), row.GetInt64(1), checked((int)row.GetInt64(2)), row.GetInt64(3), row.GetGuid(4), row.GetInt64(5), row.IsDBNull(6) ? null : row.GetInt64(6));
                }
            await tx.CommitAsync(ct);
        }
        if (record is not null)
        {
            var pilot = sessions.Of(record.CharacterId);
            var returned = record.DeathHomeId is not null
                ? await IsProvenDeathHomeAsync(record, ct)
                : pilot is not null && await space.ReturnTournamentPilotToDockAsync(pilot, record.SystemId, record.DockId, record.CurrentShipId, ct, record.OperationId.ToString("D")) is null;
            await using var c = await source.OpenConnectionAsync(ct);
            await using var finish = new NpgsqlCommand(Q(returned
                ? "DELETE FROM {schema}.match_participants WHERE match_id=$1 AND character_id=$2 AND ship_id=$3 AND return_lease=$4 AND COALESCE(return_ship_id,ship_id)=$5 AND return_operation_id=$6"
                : "UPDATE {schema}.match_participants SET return_lease=NULL,return_lease_until_ms=NULL WHERE match_id=$1 AND character_id=$2 AND ship_id=$3 AND return_lease=$4 AND COALESCE(return_ship_id,ship_id)=$5 AND return_operation_id=$6"), c);
            finish.Parameters.AddWithValue(recovery.MatchId); finish.Parameters.AddWithValue(record.CharacterId);
            finish.Parameters.AddWithValue(record.ShipId); finish.Parameters.AddWithValue(token);
            finish.Parameters.AddWithValue(record.CurrentShipId); finish.Parameters.AddWithValue(record.OperationId);
            RestrictionFailClosed([record.CharacterId]);
            try
            {
                await finish.ExecuteNonQueryAsync(ct);
                await RefreshTournamentRestrictionsAsync(ct, [record.CharacterId]);
            }
            catch
            {
                RestrictionNeedsRepair([record.CharacterId]);
                throw;
            }
            job.RunAgainAt(scheduler.Now + (returned ? 1000 : 5000), recovery);
            return;
        }
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), c, tx)) await gate.ExecuteNonQueryAsync(ct);
            await using var delete = new NpgsqlCommand(Q("""
                DELETE FROM {schema}.matches m WHERE id=$1 AND closing AND NOT cleanup_required AND NOT postgame_pending AND NOT settlement_pending
                AND NOT EXISTS (SELECT 1 FROM {schema}.match_cleanup_targets t WHERE t.match_id=m.id)
                AND NOT EXISTS (SELECT 1 FROM {schema}.match_participants p WHERE p.match_id=m.id)
                AND NOT EXISTS (SELECT 1 FROM {schema}.match_referees r WHERE r.match_id=m.id)
                AND NOT EXISTS (SELECT 1 FROM {schema}.tournament_cutoff_intents i WHERE i.match_id=m.id AND i.state <> 'released')
                RETURNING system_id
                """), c, tx);
            delete.Parameters.AddWithValue(recovery.MatchId);
            if (await delete.ExecuteScalarAsync(ct) is long systemId)
                await events.PublishAsync(c, tx, "tournament.match.reset", new { matchId = recovery.MatchId, systemId }, cancellationToken: ct);
            await using var pending = new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.match_participants WHERE match_id=$1)"), c, tx);
            pending.Parameters.AddWithValue(recovery.MatchId);
            var remaining = (bool)(await pending.ExecuteScalarAsync(ct))!;
            await tx.CommitAsync(ct);
            if (remaining) job.RunAgainAt(scheduler.Now + 5000, recovery);
        }
    }
}
