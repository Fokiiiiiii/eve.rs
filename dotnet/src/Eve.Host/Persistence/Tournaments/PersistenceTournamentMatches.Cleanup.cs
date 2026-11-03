using Eve.Scheduler;
using Eve.Space;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private sealed record CleanupTarget(long ItemId, long OwnerId, long LauncherId, int TypeId,
        double[] Center, double Radius, bool Seen, bool Removed);

    /// <summary>Processes one journaled match wreck. False means further confirmed work remains.</summary>
    private enum CleanupProgress { Completed, Progressed, Blocked }

    public async Task<bool> CleanupCompletedGridAsync(long matchId, CancellationToken ct) =>
        await CleanupCompletedGridStepAsync(matchId, ct) == CleanupProgress.Completed;

    private async Task<CleanupProgress> CleanupCompletedGridStepAsync(long matchId, CancellationToken ct)
    {
        if (simulation is null || tournamentContainers is null || scheduler is null)
            throw new InvalidOperationException("Tournament grid cleanup is unavailable.");
        int systemId;
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var match = new NpgsqlCommand(Q("SELECT system_id FROM {schema}.matches WHERE id=$1 AND state=6"), c))
        {
            match.Parameters.AddWithValue(matchId);
            var system = await match.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("The tournament match is not complete.");
            systemId = checked((int)(long)system);
        }
        var region = await ReadPhysicalRegionAsync(systemId, ct);
        // All participants must have returned before the immutable target set is
        // captured. Candidate reads and the scan marker share this transaction.
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), c, tx)) await gate.ExecuteNonQueryAsync(ct);
            bool required, scanned;
            await using (var match = new NpgsqlCommand(Q("SELECT cleanup_required,cleanup_scan_complete FROM {schema}.matches WHERE id=$1 AND state=6 AND system_id=$2 FOR UPDATE"), c, tx))
            {
                match.Parameters.AddWithValue(matchId); match.Parameters.AddWithValue((long)systemId);
                await using var row = await match.ExecuteReaderAsync(ct);
                if (!await row.ReadAsync(ct)) throw new InvalidOperationException("The tournament match is not complete.");
                required = row.GetBoolean(0); scanned = row.GetBoolean(1);
            }
            if (!required) { await tx.CommitAsync(ct); return CleanupProgress.Completed; }
            await EnsureNoParticipantsAsync(c, tx, matchId, ct);
            if (!scanned)
            {
                var members = new HashSet<long>();
                await using (var roster = new NpgsqlCommand(Q("SELECT character_id FROM {schema}.match_members WHERE match_id=$1 AND locked ORDER BY character_id"), c, tx))
                {
                    roster.Parameters.AddWithValue(matchId); await using var rows = await roster.ExecuteReaderAsync(ct);
                    while (await rows.ReadAsync(ct)) members.Add(rows.GetInt64(0));
                }
                var candidates = await tournamentContainers.TournamentWreckCandidatesAsync(c, tx, systemId, members, ct);
                foreach (var candidate in candidates)
                {
                    var item = await items.LockAndGetAsync(c, tx, candidate.ItemId, ct)
                        ?? throw new InvalidOperationException("A candidate wreck has no persistent item.");
                    if (candidate.CharacterId <= 0 || candidate.LauncherId <= 0 || item.TypeId <= 0)
                        throw new InvalidOperationException("A candidate wreck has invalid identity.");
                    await using var insert = new NpgsqlCommand(Q("""
                        INSERT INTO {schema}.match_cleanup_targets(match_id,item_id,owner_id,launcher_id,type_id,center_x,center_y,center_z,radius_meters)
                        VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT DO NOTHING
                        """), c, tx);
                    insert.Parameters.AddWithValue(matchId); insert.Parameters.AddWithValue(candidate.ItemId); insert.Parameters.AddWithValue(candidate.CharacterId);
                    insert.Parameters.AddWithValue(candidate.LauncherId); insert.Parameters.AddWithValue(item.TypeId);
                    insert.Parameters.AddWithValue(region.Center[0]); insert.Parameters.AddWithValue(region.Center[1]); insert.Parameters.AddWithValue(region.Center[2]);
                    insert.Parameters.AddWithValue(region.RadiusMeters); await insert.ExecuteNonQueryAsync(ct);
                }
                await using var done = new NpgsqlCommand(Q("UPDATE {schema}.matches SET cleanup_scan_complete=true WHERE id=$1"), c, tx);
                done.Parameters.AddWithValue(matchId); await done.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        var token = Guid.NewGuid(); CleanupTarget? target = null;
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            await using var claim = new NpgsqlCommand(Q("""
                UPDATE {schema}.match_cleanup_targets t SET cleanup_lease=$2,cleanup_lease_until_ms=$3
                WHERE (t.match_id,t.item_id) = (SELECT x.match_id,x.item_id FROM {schema}.match_cleanup_targets x
                    WHERE x.match_id=$1 AND x.status='pending' AND (x.cleanup_lease_until_ms IS NULL OR x.cleanup_lease_until_ms <= $4)
                    ORDER BY x.item_id FOR UPDATE SKIP LOCKED LIMIT 1)
                RETURNING item_id,owner_id,launcher_id,type_id,center_x,center_y,center_z,radius_meters,sim_seen,sim_removed
                """), c, tx);
            claim.Parameters.AddWithValue(matchId); claim.Parameters.AddWithValue(token);
            claim.Parameters.AddWithValue(scheduler.Now + 30000); claim.Parameters.AddWithValue(scheduler.Now);
            await using (var row = await claim.ExecuteReaderAsync(ct))
                if (await row.ReadAsync(ct)) target = new CleanupTarget(row.GetInt64(0), row.GetInt64(1), row.GetInt64(2), row.GetInt32(3),
                    [row.GetDouble(4), row.GetDouble(5), row.GetDouble(6)], row.GetDouble(7), row.GetBoolean(8), row.GetBoolean(9));
            await tx.CommitAsync(ct);
        }
        if (target is not null)
        {
            var scene = SimSceneKey.SolarSystem(systemId);
            if (!target.Removed)
            {
                var query = await simulation.QueryTournamentWreckAsync(new SimQueryTournamentWreck(SimClient.NextRequestId(), scene, target.ItemId), ct);
                if (query.Error is not null) throw new InvalidOperationException($"Wreck query failed: {query.Error}");
                if (!query.Present)
                {
                    // Presence before a crash does not prove that the live guard
                    // ever accepted a removal. Keep persistent items pending.
                    if (await items.GetAsync(target.ItemId, ct) is not null)
                        throw new InvalidOperationException("The cleanup target is absent without a durable guarded-removal acknowledgment.");
                }
                else if (query.Present)
                {
                    await UpdateCleanupTargetAsync(matchId, target.ItemId, token, "sim_seen=true", ct);
                    var removal = await simulation.RemoveTournamentWreckAsync(new SimRemoveTournamentWreck(SimClient.NextRequestId(), scene, target.ItemId,
                        target.OwnerId, target.LauncherId, target.TypeId, target.Center, target.Radius), ct);
                    if (removal.Error == "TOURNAMENT_WRECK_GUARD_MISMATCH")
                    {
                        await UpdateCleanupTargetAsync(matchId, target.ItemId, token, "status='skipped',skip_reason='live guard mismatch',cleanup_lease=NULL,cleanup_lease_until_ms=NULL", ct);
                        return CleanupProgress.Progressed;
                    }
                    if (removal.Error is not null) throw new InvalidOperationException($"Wreck removal failed: {removal.Error}");
                }
                await UpdateCleanupTargetAsync(matchId, target.ItemId, token, "sim_removed=true", ct);
            }
            await tournamentContainers.FinalizeTournamentWreckAsync(systemId, target.ItemId, target.OwnerId, target.LauncherId, target.TypeId, ct);
            await using (var c = await source.OpenConnectionAsync(ct))
            await using (var clear = new NpgsqlCommand(Q("DELETE FROM {schema}.match_cleanup_targets WHERE match_id=$1 AND item_id=$2 AND cleanup_lease=$3"), c))
            {
                clear.Parameters.AddWithValue(matchId); clear.Parameters.AddWithValue(target.ItemId); clear.Parameters.AddWithValue(token);
                if (await clear.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Cleanup lease was lost.");
            }
            return CleanupProgress.Progressed;
        }
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), c, tx)) await gate.ExecuteNonQueryAsync(ct);
            await using (var pending = new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.match_cleanup_targets WHERE match_id=$1 AND status='pending')"), c, tx))
            {
                pending.Parameters.AddWithValue(matchId);
                if ((bool)(await pending.ExecuteScalarAsync(ct))!) { await tx.CommitAsync(ct); return CleanupProgress.Blocked; }
            }
            await EnsureNoParticipantsAsync(c, tx, matchId, ct);
            await using (var clear = new NpgsqlCommand(Q("DELETE FROM {schema}.match_cleanup_targets WHERE match_id=$1 AND status='skipped'"), c, tx))
            { clear.Parameters.AddWithValue(matchId); await clear.ExecuteNonQueryAsync(ct); }
            bool closing;
            await using (var complete = new NpgsqlCommand(Q("UPDATE {schema}.matches SET cleanup_required=false WHERE id=$1 AND state=6 AND cleanup_scan_complete RETURNING closing"), c, tx))
            {
                complete.Parameters.AddWithValue(matchId);
                closing = (bool?)(await complete.ExecuteScalarAsync(ct)) ?? throw new InvalidOperationException("Cleanup state changed.");
            }
            if (closing) await ScheduleParticipantRecoveryAsync(c, tx, matchId, ct);
            await tx.CommitAsync(ct);
            if (closing) scheduler.Wake();
            return CleanupProgress.Completed;
        }
    }

    private async Task UpdateCleanupTargetAsync(long matchId, long itemId, Guid token, string update, CancellationToken ct)
    {
        // update is selected only from constant assignments above, never client input.
        await using var c = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(Q($"UPDATE {{schema}}.match_cleanup_targets SET {update} WHERE match_id=$1 AND item_id=$2 AND cleanup_lease=$3"), c);
        command.Parameters.AddWithValue(matchId); command.Parameters.AddWithValue(itemId); command.Parameters.AddWithValue(token);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Cleanup lease was lost.");
    }
}
