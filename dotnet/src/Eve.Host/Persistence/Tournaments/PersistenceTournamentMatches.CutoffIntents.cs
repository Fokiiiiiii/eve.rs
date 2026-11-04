using Eve.Scheduler;
using Eve.Space;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private const string CutoffReleaseJob = "tournament.cutoff.release";
    private const string CutoffSweepJob = "tournament.cutoff.sweep";
    internal sealed record CutoffIntent(long MatchId, long Generation, int SystemId, long StartMs, long DeadlineMs, long[] Characters, long[] Ships);
    private sealed record CutoffRelease(long Generation);

    public static Task MigrateCutoffIntentAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema), [new Eve.Data.Migration("0025_tournament_cutoff_intents", """
            CREATE TABLE tournament_cutoff_intents (
                generation bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                match_id bigint NOT NULL, system_id bigint NOT NULL,
                start_ms bigint NOT NULL, deadline_ms bigint NOT NULL CHECK (deadline_ms > start_ms),
                characters bigint[] NOT NULL, ships bigint[] NOT NULL,
                state text NOT NULL CHECK (state IN ('prepared','registered','committed','release_pending','released')),
                release_lease uuid, release_lease_until_ms bigint,
                CHECK (cardinality(characters) > 0 AND cardinality(characters) <= 256 AND cardinality(characters) = cardinality(ships))
            );
            CREATE UNIQUE INDEX tournament_cutoff_live_match ON tournament_cutoff_intents(match_id) WHERE state <> 'released';
            """)], ct);

    internal async Task<CutoffIntent> PrepareCutoffIntentAsync(NpgsqlConnection c, NpgsqlTransaction tx, long matchId, int systemId,
        long startMs, long deadlineMs, IReadOnlyList<SimTournamentCutoffPilot> roster, CancellationToken ct)
    {
        if (scheduler is null) throw new InvalidOperationException("Tournament cutoff scheduler is unavailable.");
        if (matchId <= 0 || systemId <= 0 || startMs <= scheduler.Now || deadlineMs <= startMs || roster.Count is 0 or > 256 ||
            roster.Any(x => x.CharacterId <= 0 || x.ShipId <= 0) || roster.Select(x => x.CharacterId).Distinct().Count() != roster.Count ||
            roster.Select(x => x.ShipId).Distinct().Count() != roster.Count)
            throw new ArgumentException("Invalid tournament cutoff intent.");
        var ordered = roster.OrderBy(x => x.CharacterId).ToArray();
        var characters = ordered.Select(x => x.CharacterId).ToArray(); var ships = ordered.Select(x => x.ShipId).ToArray();
        await using var insert = new NpgsqlCommand(Q("""
            INSERT INTO {schema}.tournament_cutoff_intents(match_id,system_id,start_ms,deadline_ms,characters,ships,state)
            VALUES($1,$2,$3,$4,$5,$6,'prepared') RETURNING generation
            """), c, tx);
        insert.Parameters.AddWithValue(matchId); insert.Parameters.AddWithValue((long)systemId); insert.Parameters.AddWithValue(startMs);
        insert.Parameters.AddWithValue(deadlineMs); insert.Parameters.AddWithValue(characters); insert.Parameters.AddWithValue(ships);
        var generation = (long)(await insert.ExecuteScalarAsync(ct))!;
        // If the process dies before registration/final commit, the intent still receives an idempotent release.
        await scheduler.ScheduleAsync(c, tx, new JobSpec(CutoffReleaseJob, startMs)
        { Key = $"cutoff:{generation}", Payload = new CutoffRelease(generation), OnDuplicate = OnDuplicate.Keep }, ct);
        return new CutoffIntent(matchId,generation,systemId,startMs,deadlineMs,characters,ships);
    }

    internal async Task<bool> TransitionCutoffIntentAsync(NpgsqlConnection c, NpgsqlTransaction tx, CutoffIntent intent, string from, string to, CancellationToken ct)
    {
        if ((from,to) is not (("prepared","registered") or ("registered","committed") or
            ("prepared","release_pending") or ("registered","release_pending") or ("committed","release_pending")))
            throw new ArgumentException("Invalid cutoff intent transition.");
        await using var cmd = new NpgsqlCommand(Q("""
            UPDATE {schema}.tournament_cutoff_intents SET state=$8
            WHERE generation=$1 AND match_id=$2 AND system_id=$3 AND start_ms=$4 AND deadline_ms=$5
              AND characters=$6 AND ships=$7 AND state=$9
            """), c, tx);
        cmd.Parameters.AddWithValue(intent.Generation); cmd.Parameters.AddWithValue(intent.MatchId); cmd.Parameters.AddWithValue((long)intent.SystemId);
        cmd.Parameters.AddWithValue(intent.StartMs); cmd.Parameters.AddWithValue(intent.DeadlineMs);
        cmd.Parameters.AddWithValue(intent.Characters); cmd.Parameters.AddWithValue(intent.Ships); cmd.Parameters.AddWithValue(to); cmd.Parameters.AddWithValue(from);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    internal async Task ScheduleCutoffReleaseAsync(NpgsqlConnection c, NpgsqlTransaction tx, CutoffIntent intent, string from, CancellationToken ct)
    {
        if (scheduler is null || !await TransitionCutoffIntentAsync(c,tx,intent,from,"release_pending",ct))
            throw new InvalidOperationException("Tournament cutoff intent changed before release.");
        await scheduler.ScheduleAsync(c,tx,new JobSpec(CutoffReleaseJob,scheduler.Now)
        { Key=$"cutoff:{intent.Generation}", Payload=new CutoffRelease(intent.Generation) },ct);
    }

    private async Task ReleaseOutstandingCutoffsAsync(NpgsqlConnection c, NpgsqlTransaction tx, long matchId, CancellationToken ct)
    {
        var pending = new List<(CutoffIntent Intent, string State)>();
        await using (var find = new NpgsqlCommand(Q("""
            SELECT generation,system_id,start_ms,deadline_ms,characters,ships,state
            FROM {schema}.tournament_cutoff_intents
            WHERE match_id=$1 AND state IN ('prepared','registered','committed') FOR UPDATE
            """), c, tx))
        {
            find.Parameters.AddWithValue(matchId);
            await using var row = await find.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct))
                pending.Add((new CutoffIntent(matchId,row.GetInt64(0),checked((int)row.GetInt64(1)),row.GetInt64(2),row.GetInt64(3),row.GetFieldValue<long[]>(4),row.GetFieldValue<long[]>(5)),row.GetString(6)));
        }
        foreach (var item in pending) await ScheduleCutoffReleaseAsync(c, tx, item.Intent, item.State, ct);
    }

    public async Task InitializeCutoffRecoveryAsync(CancellationToken ct = default)
    {
        if (scheduler is null) throw new InvalidOperationException("Tournament cutoff scheduler is unavailable.");
        await scheduler.EnsureRecurringAsync(CutoffSweepJob, "cutoff-intents", TimeSpan.FromSeconds(5), cancellationToken: ct);
    }

    private async Task SweepCutoffIntentsAsync(JobContext job, CancellationToken ct)
    {
        if (scheduler is null) throw new InvalidOperationException("Tournament cutoff scheduler is unavailable.");
        var generations = new List<long>();
        await using (var query = new NpgsqlCommand(Q("""
            SELECT generation FROM {schema}.tournament_cutoff_intents
            WHERE state='release_pending' OR (state IN ('prepared','registered') AND start_ms <= $1)
            ORDER BY generation LIMIT 128
            """),job.Connection,job.Transaction))
        {
            query.Parameters.AddWithValue(scheduler.Now); await using var row=await query.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) generations.Add(row.GetInt64(0));
        }
        foreach (var generation in generations)
            await scheduler.ScheduleAsync(job.Connection,job.Transaction,new JobSpec(CutoffReleaseJob,scheduler.Now)
            { Key=$"cutoff:{generation}", Payload=new CutoffRelease(generation), OnDuplicate=OnDuplicate.Keep },ct);
    }

    private async Task ReleaseCutoffDueAsync(JobContext job, CancellationToken ct)
    {
        if (scheduler is null || simulation is null) throw new InvalidOperationException("Tournament cutoff release is unavailable.");
        var release = job.Payload<CutoffRelease>() ?? throw new InvalidOperationException("Missing cutoff release.");
        var lease = Guid.NewGuid(); CutoffIntent? intent = null;
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            await using var claim = new NpgsqlCommand(Q("""
                UPDATE {schema}.tournament_cutoff_intents SET state='release_pending',release_lease=$2,release_lease_until_ms=$3
                WHERE generation=$1 AND (state='release_pending' OR (state IN ('prepared','registered') AND start_ms <= $4))
                  AND (release_lease_until_ms IS NULL OR release_lease_until_ms <= $4)
                RETURNING match_id,system_id,start_ms,deadline_ms,characters,ships
                """),c,tx);
            claim.Parameters.AddWithValue(release.Generation); claim.Parameters.AddWithValue(lease);
            claim.Parameters.AddWithValue(scheduler.Now + 30000); claim.Parameters.AddWithValue(scheduler.Now);
            await using (var row = await claim.ExecuteReaderAsync(ct))
                if (await row.ReadAsync(ct)) intent = new CutoffIntent(row.GetInt64(0),release.Generation,checked((int)row.GetInt64(1)),row.GetInt64(2),row.GetInt64(3),row.GetFieldValue<long[]>(4),row.GetFieldValue<long[]>(5));
            await tx.CommitAsync(ct);
        }
        if (intent is null)
        {
            await using var check = source.CreateCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.tournament_cutoff_intents WHERE generation=$1 AND state IN ('prepared','registered','release_pending'))"));
            check.Parameters.AddWithValue(release.Generation);
            if ((bool)(await check.ExecuteScalarAsync(ct))!) job.RunAgainAt(scheduler.Now + 5000,release);
            return;
        }
        var response = await simulation.ReleaseTournamentCutoffAsync(new SimReleaseTournamentCutoff(SimClient.NextRequestId(),
            SimSceneKey.SolarSystem(intent.SystemId),intent.MatchId,intent.Generation,
            Eve.Services.Py.FileTimeFromUnixMs(intent.StartMs),Eve.Services.Py.FileTimeFromUnixMs(intent.DeadlineMs)),ct);
        if (response.Status != SimTournamentCutoffStatus.Pending) throw new InvalidOperationException($"Tournament cutoff release failed: {response.Error}");
        await using var finishConnection = await source.OpenConnectionAsync(ct);
        await using var finishTx = await finishConnection.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), finishConnection, finishTx))
            await gate.ExecuteNonQueryAsync(ct);
        await using var finish = new NpgsqlCommand(Q("""
            UPDATE {schema}.tournament_cutoff_intents SET state='released',release_lease=NULL,release_lease_until_ms=NULL
            WHERE generation=$1 AND state='release_pending' AND release_lease=$2
            """), finishConnection, finishTx);
        finish.Parameters.AddWithValue(intent.Generation); finish.Parameters.AddWithValue(lease);
        if (await finish.ExecuteNonQueryAsync(ct) == 1)
        {
            await using var delete = new NpgsqlCommand(Q("""
                DELETE FROM {schema}.matches m WHERE id=$1 AND closing AND NOT cleanup_required AND NOT postgame_pending AND NOT settlement_pending
                AND NOT EXISTS (SELECT 1 FROM {schema}.match_cleanup_targets t WHERE t.match_id=m.id)
                AND NOT EXISTS (SELECT 1 FROM {schema}.match_participants p WHERE p.match_id=m.id)
                AND NOT EXISTS (SELECT 1 FROM {schema}.match_referees r WHERE r.match_id=m.id)
                AND NOT EXISTS (SELECT 1 FROM {schema}.tournament_cutoff_intents i WHERE i.match_id=m.id AND i.state <> 'released')
                RETURNING system_id
                """), finishConnection, finishTx);
            delete.Parameters.AddWithValue(intent.MatchId);
            if (await delete.ExecuteScalarAsync(ct) is long systemId)
                await events.PublishAsync(finishConnection, finishTx, "tournament.match.reset", new { matchId = intent.MatchId, systemId }, cancellationToken: ct);
        }
        await finishTx.CommitAsync(ct); events.Wake();
    }
}
