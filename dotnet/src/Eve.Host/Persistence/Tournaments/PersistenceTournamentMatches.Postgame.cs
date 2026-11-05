using Eve.Scheduler;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private const string PostgameJob = "tournament.match.postgame";
    private sealed record PostgameStep(long MatchId, long CompletedMs, int Stage);

    public static Task MigratePostgameAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema), [new Eve.Data.Migration("0021_tournament_postgame", """
            ALTER TABLE matches ADD COLUMN postgame_pending boolean NOT NULL DEFAULT false;
            CREATE TABLE postgame_referees (
                match_id bigint NOT NULL REFERENCES matches(id) ON DELETE CASCADE,
                character_id bigint NOT NULL, system_id bigint NOT NULL,
                PRIMARY KEY(match_id,character_id)
            );
            """)], ct);

    private void RegisterPostgameHandlers() => scheduler!.Register(PostgameJob, RunPostgameAsync);

    private async Task RunPostgameAsync(JobContext job, CancellationToken ct)
    {
        var step = job.Payload<PostgameStep>() ?? throw new InvalidOperationException("Missing postgame state.");
        if (step.Stage == 0)
        {
            if (!await ReturnCompletedParticipantsAsync(step.MatchId,ct)) { job.RunAgainAt(job.NowMs+5000,step); return; }
            job.RunAgainAt(Math.Max(job.NowMs,step.CompletedMs+65000),step with { Stage=1 }); return;
        }
        if (step.Stage == 1)
        {
            if (!await CleanupCompletedGridAsync(step.MatchId,ct)) { job.RunAgainAt(job.NowMs+5000,step); return; }
            job.RunAgainAt(Math.Max(job.NowMs,step.CompletedMs+70000),step with { Stage=2 }); return;
        }
        if (step.Stage != 2) throw new InvalidOperationException("Unknown postgame stage.");
        if (!await ReturnCompletedRefereesAsync(step.MatchId,ct)) { job.RunAgainAt(job.NowMs+5000,step); return; }
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"),job.Connection,job.Transaction))
            await gate.ExecuteNonQueryAsync(ct);
        await using (var clear = new NpgsqlCommand(Q("UPDATE {schema}.matches SET postgame_pending=false WHERE id=$1 AND state=6 AND NOT cleanup_required"),job.Connection,job.Transaction))
        { clear.Parameters.AddWithValue(step.MatchId); await clear.ExecuteNonQueryAsync(ct); }
        await using var delete = new NpgsqlCommand(Q("""
            DELETE FROM {schema}.matches m WHERE id=$1 AND closing AND NOT postgame_pending AND NOT cleanup_required AND NOT settlement_pending
            AND NOT EXISTS (SELECT 1 FROM {schema}.match_referees r WHERE r.match_id=m.id)
            AND NOT EXISTS (SELECT 1 FROM {schema}.match_participants p WHERE p.match_id=m.id)
            AND NOT EXISTS (SELECT 1 FROM {schema}.tournament_cutoff_intents i WHERE i.match_id=m.id AND i.state <> 'released')
            RETURNING system_id
            """),job.Connection,job.Transaction);
        delete.Parameters.AddWithValue(step.MatchId); var system = await delete.ExecuteScalarAsync(ct);
        if (system is long systemId)
            await events.PublishAsync(job.Connection,job.Transaction,"tournament.match.reset",new { matchId=step.MatchId,systemId },cancellationToken:ct);
    }
}
