using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Space;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private const string MetricsSweepJob = "tournament.metrics.sweep";
    private const string MetricsReleaseJob = "tournament.metrics.release";
    private sealed record MetricsRelease(long Generation);
    internal sealed record MetricsIntent(long Generation, long MatchId, int SystemId, long StartMs, SimTournamentMetricsPilot[] Roster);

    public static Task MigrateMetricsIntentAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source,Eve.Data.Sql.CheckSchema(schema),[new Eve.Data.Migration("0026_tournament_metrics_intents", """
            CREATE TABLE tournament_metrics_intents (
                generation bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                match_id bigint NOT NULL, system_id bigint NOT NULL, start_ms bigint NOT NULL,
                characters bigint[] NOT NULL, ships bigint[] NOT NULL, teams integer[] NOT NULL,
                state text NOT NULL CHECK(state IN ('active','release_pending','released')),
                CHECK(cardinality(characters)>0 AND cardinality(characters)<=256 AND cardinality(characters)=cardinality(ships) AND cardinality(characters)=cardinality(teams))
            );
            CREATE UNIQUE INDEX tournament_metrics_active_match ON tournament_metrics_intents(match_id) WHERE state <> 'released';
            """)],ct);

    private void RegisterMetricsHandlers()
    {
        scheduler!.Register(MetricsSweepJob,SweepMetricsAsync);
        scheduler.Register(MetricsReleaseJob,ReleaseMetricsAsync);
    }

    public Task InitializeMetricsRecoveryAsync(CancellationToken ct = default) =>
        scheduler!.EnsureRecurringAsync(MetricsSweepJob,"metrics-intents",TimeSpan.FromSeconds(5),cancellationToken:ct);

    internal async Task<MetricsIntent?> GetMetricsIntentAsync(long matchId, int systemId, long start, SimTournamentMetricsPilot[] roster, CancellationToken ct)
    {
        if(scheduler is null || simulation is null || roster.Length is 0 or >256) return null;
        await using var c=await source.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"),c,tx)) await gate.ExecuteNonQueryAsync(ct);
        await using(var match=new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.matches WHERE id=$1 AND system_id=$2 AND start_ms=$3 AND state>=3 AND NOT closing)"),c,tx))
        { match.Parameters.AddWithValue(matchId); match.Parameters.AddWithValue((long)systemId); match.Parameters.AddWithValue(start); if(!(bool)(await match.ExecuteScalarAsync(ct))!) return null; }
        MetricsIntent? existing=null; string? state=null;
        await using(var find=new NpgsqlCommand(Q("SELECT generation,system_id,start_ms,characters,ships,teams,state FROM {schema}.tournament_metrics_intents WHERE match_id=$1 AND state<>'released' FOR UPDATE"),c,tx))
        {
            find.Parameters.AddWithValue(matchId); await using var row=await find.ExecuteReaderAsync(ct);
            if(await row.ReadAsync(ct)) { var chars=row.GetFieldValue<long[]>(3); var ships=row.GetFieldValue<long[]>(4); var teams=row.GetFieldValue<int[]>(5); existing=new(row.GetInt64(0),matchId,checked((int)row.GetInt64(1)),row.GetInt64(2),chars.Select((x,i)=>new SimTournamentMetricsPilot(x,ships[i],checked((byte)teams[i]))).ToArray()); state=row.GetString(6); }
        }
        if(existing is not null && (state!="active" || existing.SystemId!=systemId || existing.StartMs!=start || !existing.Roster.SequenceEqual(roster)))
        {
            await using(var release=new NpgsqlCommand(Q("UPDATE {schema}.tournament_metrics_intents SET state='release_pending' WHERE generation=$1 AND state='active'"),c,tx)) { release.Parameters.AddWithValue(existing.Generation); await release.ExecuteNonQueryAsync(ct); }
            await scheduler.ScheduleAsync(c,tx,new JobSpec(MetricsReleaseJob,scheduler.Now) { Key=$"metrics:{existing.Generation}",Payload=new MetricsRelease(existing.Generation) },ct);
            await tx.CommitAsync(ct); scheduler.Wake(); return null;
        }
        if(existing is null)
        {
            await using var insert=new NpgsqlCommand(Q("INSERT INTO {schema}.tournament_metrics_intents(match_id,system_id,start_ms,characters,ships,teams,state) VALUES($1,$2,$3,$4,$5,$6,'active') RETURNING generation"),c,tx);
            insert.Parameters.AddWithValue(matchId); insert.Parameters.AddWithValue((long)systemId); insert.Parameters.AddWithValue(start);
            insert.Parameters.AddWithValue(roster.Select(x=>x.CharacterId).ToArray()); insert.Parameters.AddWithValue(roster.Select(x=>x.ShipId).ToArray()); insert.Parameters.AddWithValue(roster.Select(x=>(int)x.Team).ToArray());
            existing=new((long)(await insert.ExecuteScalarAsync(ct))!,matchId,systemId,start,roster);
        }
        await tx.CommitAsync(ct); return existing;
    }

    private sealed record CameraMetricMember(SimTournamentMetricsPilot Pilot, bool Eliminated);

    private async Task<CameraMetricMember[]> CameraMetricRosterAsync(NpgsqlConnection c, long matchId, CancellationToken ct)
    {
        var members=new List<CameraMetricMember>();
        await using var query=new NpgsqlCommand(Q("""
            SELECT d.character_id,d.ship_id,d.team,m.eliminated FROM {schema}.match_roster_details d
            JOIN {schema}.match_members m ON m.match_id=d.match_id AND m.character_id=d.character_id AND m.team=d.team
            WHERE d.match_id=$1 AND m.locked ORDER BY d.character_id,d.ship_id,d.team
            """),c);
        query.Parameters.AddWithValue(matchId); await using var row=await query.ExecuteReaderAsync(ct);
        while(await row.ReadAsync(ct)) members.Add(new(new(row.GetInt64(0),row.GetInt64(1),checked((byte)row.GetInt32(2))),row.GetBoolean(3)));
        return members.ToArray();
    }

    private async Task<PyValue[]> CameraMetricsAsync(NpgsqlConnection c, long matchId, int systemId, long start, ClientSession session, CancellationToken ct)
    {
        PyValue[] unavailable=[PyNone.Instance,PyNone.Instance];
        if(simulation is null || space is null || scheduler is null) return unavailable;
        var members=await CameraMetricRosterAsync(c,matchId,ct);
        if(members.Length==0 || members.Length>256 || !members.Any(x=>x.Pilot.Team==0) || !members.Any(x=>x.Pilot.Team==1)) return unavailable;
        var roster=members.Select(x=>x.Pilot).ToArray(); var scene=SimSceneKey.SolarSystem(systemId);
        var intent=await GetMetricsIntentAsync(matchId,systemId,start,roster,ct); if(intent is null) return unavailable;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var captures=new Eve.Services.Space.TournamentPotentialCapture?[roster.Length];
            for(var i=0;i<roster.Length;i++) captures[i]=await space.CaptureTournamentPotentialAsync(roster[i].CharacterId,roster[i].ShipId,scene,timeout.Token);
            var identityStart=Py.FileTimeFromUnixMs(start);
            var register=await simulation.RegisterTournamentMetricsAsync(new(SimClient.NextRequestId(),scene,matchId,intent.Generation,identityStart,roster),timeout.Token);
            if(register.Status!=SimTournamentMetricsStatus.Success) return unavailable;
            var reply=await simulation.ReadTournamentMetricsV2Async(new(SimClient.NextRequestId(),scene,matchId,intent.Generation,identityStart,roster),timeout.Token);
            if(reply.Status!=SimTournamentMetricsStatus.Success || reply.Sample is not { AppliedDamage: { Count: 2 } damage } sample || sample.Pilots.Count!=roster.Length) return unavailable;
            var totals=new[] { new double[6],new double[6] }; totals[0][3]=damage[0]; totals[1][3]=damage[1];
            for(var i=0;i<roster.Length;i++)
            {
                var p=sample.Pilots[i]; var team=p.Pilot.Team;
                if(p.Pilot!=roster[i] || team>1) return unavailable;
                if(p.State==SimTournamentMetricsPilotState.Destroyed)
                { if(!members[i].Eliminated) return unavailable; continue; }
                if(members[i].Eliminated || captures[i] is null || p.EhpStatus!=SimTournamentMetricsEhp.Finite || p.Ehp is null) return unavailable;
                var friends=roster.Where(x=>x.Team==team).Select(x=>x.ShipId).ToHashSet();
                var values=await space.CompleteTournamentPotentialAsync(captures[i]!,scene,p,friends,timeout.Token);
                if(values.FittedDamagePerSecond is not {} maxDps || values.RunningRepairHpPerSecond is not {} reps || values.FittedControl is not {} maxControl || values.RunningControl is not {} control) return unavailable;
                totals[team][0]+=p.Ehp.Value; totals[team][1]+=reps; totals[team][2]+=maxDps; totals[team][4]+=maxControl; totals[team][5]+=control;
            }
            if(totals.SelectMany(x=>x).Any(x=>!double.IsFinite(x) || x<0)) return unavailable;
            var after=await CameraMetricRosterAsync(c,matchId,timeout.Token);
            if(!members.SequenceEqual(after) || !space.IsInPublicSystem(session,systemId)) return unavailable;
            await using(var match=new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.matches WHERE id=$1 AND system_id=$2 AND start_ms=$3 AND state>=3 AND NOT closing)"),c))
            { match.Parameters.AddWithValue(matchId); match.Parameters.AddWithValue((long)systemId); match.Parameters.AddWithValue(start); if(!(bool)(await match.ExecuteScalarAsync(timeout.Token))!) return unavailable; }
            return [Py.Tuple(totals[0].Select(x=>(PyValue)new Eve.Marshal.PyReal(x)).ToArray()),Py.Tuple(totals[1].Select(x=>(PyValue)new Eve.Marshal.PyReal(x)).ToArray())];
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) { return unavailable; }
        catch(SimLinkException) { return unavailable; }
        catch(SimUnavailableException) { return unavailable; }
    }

    private async Task SweepMetricsAsync(JobContext job, CancellationToken ct)
    {
        await using(var mark=new NpgsqlCommand(Q("""
            UPDATE {schema}.tournament_metrics_intents i SET state='release_pending'
            WHERE i.state='active' AND NOT EXISTS(SELECT 1 FROM {schema}.matches m WHERE m.id=i.match_id AND m.system_id=i.system_id AND m.start_ms=i.start_ms AND m.state>=3 AND NOT m.closing)
            """),job.Connection,job.Transaction)) await mark.ExecuteNonQueryAsync(ct);
        var ids=new List<long>();
        await using(var list=new NpgsqlCommand(Q("SELECT generation FROM {schema}.tournament_metrics_intents WHERE state='release_pending' ORDER BY generation LIMIT 128"),job.Connection,job.Transaction))
        await using(var rows=await list.ExecuteReaderAsync(ct)) while(await rows.ReadAsync(ct)) ids.Add(rows.GetInt64(0));
        foreach(var id in ids) await scheduler!.ScheduleAsync(job.Connection,job.Transaction,new JobSpec(MetricsReleaseJob,job.NowMs) { Key=$"metrics:{id}",Payload=new MetricsRelease(id),OnDuplicate=OnDuplicate.Keep },ct);
    }

    private async Task ReleaseMetricsAsync(JobContext job, CancellationToken ct)
    {
        if(simulation is null) { job.RunAgainAt(job.NowMs+5000,job.Payload<MetricsRelease>()!); return; }
        var payload=job.Payload<MetricsRelease>() ?? throw new InvalidOperationException("Missing metrics release.");
        MetricsIntent? intent=null;
        await using(var c=await source.OpenConnectionAsync(ct))
        await using(var find=new NpgsqlCommand(Q("SELECT match_id,system_id,start_ms,characters,ships,teams FROM {schema}.tournament_metrics_intents WHERE generation=$1 AND state='release_pending'"),c))
        {
            find.Parameters.AddWithValue(payload.Generation); await using var row=await find.ExecuteReaderAsync(ct);
            if(await row.ReadAsync(ct)) { var chars=row.GetFieldValue<long[]>(3); var ships=row.GetFieldValue<long[]>(4); var teams=row.GetFieldValue<int[]>(5); intent=new(payload.Generation,row.GetInt64(0),checked((int)row.GetInt64(1)),row.GetInt64(2),chars.Select((x,i)=>new SimTournamentMetricsPilot(x,ships[i],checked((byte)teams[i]))).ToArray()); }
        }
        if(intent is null) return;
        var reply=await simulation.ReleaseTournamentMetricsAsync(new(SimClient.NextRequestId(),SimSceneKey.SolarSystem(intent.SystemId),intent.MatchId,intent.Generation,Py.FileTimeFromUnixMs(intent.StartMs),intent.Roster),ct);
        if(reply.Status!=SimTournamentMetricsStatus.Success) { job.RunAgainAt(job.NowMs+5000,payload); return; }
        await using var done=new NpgsqlCommand(Q("UPDATE {schema}.tournament_metrics_intents SET state='released' WHERE generation=$1 AND state='release_pending'"),job.Connection,job.Transaction);
        done.Parameters.AddWithValue(payload.Generation); await done.ExecuteNonQueryAsync(ct);
    }
}
