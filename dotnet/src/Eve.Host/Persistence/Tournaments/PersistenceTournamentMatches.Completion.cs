using Eve.Scheduler;
using Npgsql;

namespace Eve.Host.Persistence;

/// <summary>Optional event-specific outcome policy; missing entries retain manual referee completion.</summary>
public sealed record TournamentCompletionPolicyImport(long TournamentId, string Policy);

public sealed partial class PersistenceTournamentMatches
{
    private const string MatchDeadlineJob = "tournament.match.deadline";
    private const string SettlementPendingTopic = "tournament.match.settlement_pending";
    private sealed record MatchDeadline(long MatchId, long StartMs);

    public static Task MigrateCompletionPolicyAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema), [new Eve.Data.Migration("0024_tournament_completion_policy", """
            CREATE TABLE tournament_completion_policies (
                tournament_id bigint PRIMARY KEY REFERENCES tournaments(id) ON DELETE CASCADE,
                policy text NOT NULL CHECK (policy IN ('manual','alliance_tournament'))
            );
            ALTER TABLE matches ADD COLUMN settlement_pending boolean NOT NULL DEFAULT false,
                ADD COLUMN winner_team integer NULL CHECK (winner_team IN (0,1)),
                ADD COLUMN completion_reason text NULL CHECK (completion_reason IN ('manual','score','elimination','tiebreak'));
            """)], ct);

    public async Task ImportCompletionPolicyAsync(TournamentCompletionPolicyImport input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.TournamentId <= 0 || input.Policy is not ("manual" or "alliance_tournament"))
            throw new ArgumentException("Tournament completion policy must be manual or alliance_tournament.");
        await using var c = await source.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await using (var configured = new NpgsqlCommand(Q("SELECT id FROM {schema}.tournaments WHERE id=$1 FOR UPDATE"), c, tx))
        {
            configured.Parameters.AddWithValue(input.TournamentId);
            if (await configured.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Tournament configuration does not exist.");
        }
        await using (var prior = new NpgsqlCommand(Q("SELECT policy FROM {schema}.tournament_completion_policies WHERE tournament_id=$1"), c, tx))
        {
            prior.Parameters.AddWithValue(input.TournamentId);
            if (await prior.ExecuteScalarAsync(ct) is string existing)
            {
                if (existing != input.Policy) throw new InvalidOperationException("Tournament completion policy already exists with a different value.");
                await tx.CommitAsync(ct); return;
            }
        }
        await using (var active = new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.matches WHERE tournament_id=$1)"), c, tx))
        {
            active.Parameters.AddWithValue(input.TournamentId);
            if ((bool)(await active.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("Tournament completion policy cannot be added while a match is active.");
        }
        await using (var insert = new NpgsqlCommand(Q("INSERT INTO {schema}.tournament_completion_policies(tournament_id,policy) VALUES ($1,$2)"), c, tx))
        { insert.Parameters.AddWithValue(input.TournamentId); insert.Parameters.AddWithValue(input.Policy); await insert.ExecuteNonQueryAsync(ct); }
        await events.PublishAsync(c, tx, "tournament.completion_policy.configured", new { input.TournamentId, input.Policy }, cancellationToken: ct);
        await tx.CommitAsync(ct); events.Wake();
    }

    private async Task ScheduleMatchDeadlineAsync(NpgsqlConnection c, NpgsqlTransaction tx, long matchId, long startMs, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(Q("""
            SELECT m.match_length,COALESCE(p.policy,'manual') FROM {schema}.matches m
            LEFT JOIN {schema}.tournament_completion_policies p ON p.tournament_id=m.tournament_id WHERE m.id=$1
            """), c, tx);
        cmd.Parameters.AddWithValue(matchId); await using var row = await cmd.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) return;
        var length = row.GetInt32(0); var policy = row.GetString(1); await row.CloseAsync();
        if (policy == "alliance_tournament")
            await scheduler!.ScheduleAsync(c, tx, new JobSpec(MatchDeadlineJob, checked(startMs + length * 1000L))
            { Key = $"match:{matchId}", Payload = new MatchDeadline(matchId, startMs) }, ct);
    }

    private async Task MatchDeadlineDueAsync(JobContext job, CancellationToken ct)
    {
        var deadline = job.Payload<MatchDeadline>() ?? throw new InvalidOperationException("Missing tournament match deadline.");
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), job.Connection, job.Transaction))
            await gate.ExecuteNonQueryAsync(ct);
        await using var pending = new NpgsqlCommand(Q("""
            UPDATE {schema}.matches SET settlement_pending=true
            WHERE id=$1 AND state=5 AND start_ms=$2 AND NOT settlement_pending
            RETURNING system_id
            """), job.Connection, job.Transaction);
        pending.Parameters.AddWithValue(deadline.MatchId); pending.Parameters.AddWithValue(deadline.StartMs);
        if (await pending.ExecuteScalarAsync(ct) is long systemId)
        {
            await events.PublishAsync(job.Connection, job.Transaction, SettlementPendingTopic,
                new { deadline.MatchId, deadline.StartMs, SystemId = systemId }, cancellationToken: ct);
            await scheduler!.ScheduleAsync(job.Connection,job.Transaction,new JobSpec(SettlementJob,scheduler.Now)
            { Key=$"match:{deadline.MatchId}", Payload=deadline, OnDuplicate=OnDuplicate.Keep },ct);
        }
        // An authoritative scene cutoff snapshot/barrier must settle any delayed deaths before a winner is chosen.
    }

    private async Task CompleteMatchAsync(NpgsqlConnection c, NpgsqlTransaction tx, long matchId, long? startMs,
        int? winnerTeam, string reason, long completedMs, CancellationToken ct)
    {
        if (winnerTeam is not (null or 0 or 1) || reason is not ("manual" or "score" or "elimination" or "tiebreak"))
            throw new ArgumentException("Invalid tournament outcome.");
        await using (var finish = new NpgsqlCommand(Q("""
            UPDATE {schema}.matches SET state=6,start_ms=$2,settlement_pending=false,winner_team=$3,completion_reason=$4,
                cleanup_required=true,postgame_pending=true WHERE id=$1 AND state=5
            """), c, tx))
        {
            finish.Parameters.AddWithValue(matchId);
            finish.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = (object?)startMs ?? DBNull.Value });
            finish.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = (object?)winnerTeam ?? DBNull.Value });
            finish.Parameters.AddWithValue(reason);
            if (await finish.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("The match is no longer in progress.");
        }
        await using (var snapshot = new NpgsqlCommand(Q("""
            INSERT INTO {schema}.postgame_referees(match_id,character_id,system_id)
            SELECT DISTINCT m.id,r.character_id,m.system_id FROM {schema}.matches m
            JOIN {schema}.match_referees r ON r.match_id=m.id
            JOIN {schema}.referee_travel t ON t.character_id=r.character_id AND t.target_system_id=m.system_id
            WHERE m.id=$1 ON CONFLICT DO NOTHING
            """), c, tx))
        { snapshot.Parameters.AddWithValue(matchId); await snapshot.ExecuteNonQueryAsync(ct); }
        await ReleaseCommittedCutoffAsync(c,tx,matchId,ct);
        await scheduler!.ScheduleAsync(c, tx, new JobSpec(PostgameJob, checked(completedMs + 60000))
        { Key=$"match:{matchId}", Payload=new PostgameStep(matchId,completedMs,0) }, ct);
        await scheduler.CancelAsync(c, tx, MatchDeadlineJob, $"match:{matchId}", ct);
    }
}
