using Eve.Scheduler;
using Eve.Services;
using Eve.Space;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private const string SettlementJob = "tournament.match.settle";
    private sealed record CutoffRosterMember(long Character, long Ship, int Team, int Points);
    private sealed record SettlementSnapshot(CutoffIntent Intent, int Maximum, CutoffRosterMember[] Roster);
    internal Func<SimReadTournamentCutoff,CancellationToken,Task<SimReadTournamentCutoffReply>>? CutoffReader { get; set; }

    private async Task<SettlementSnapshot?> ReadSettlementSnapshotAsync(NpgsqlConnection c, NpgsqlTransaction? tx, MatchDeadline deadline, bool locked, CancellationToken ct)
    {
        CutoffIntent intent; int maximum;
        var suffix=locked ? " FOR UPDATE OF m,i" : "";
        await using (var cmd=new NpgsqlCommand(Q("""
            SELECT m.system_id,m.maximum_points,i.generation,i.start_ms,i.deadline_ms,i.characters,i.ships
            FROM {schema}.matches m JOIN {schema}.tournament_completion_policies p ON p.tournament_id=m.tournament_id
            JOIN {schema}.tournament_cutoff_intents i ON i.match_id=m.id AND i.state='committed'
            WHERE m.id=$1 AND m.state=5 AND m.start_ms=$2 AND m.settlement_pending AND p.policy='alliance_tournament'
            """+suffix),c,tx))
        {
            cmd.Parameters.AddWithValue(deadline.MatchId); cmd.Parameters.AddWithValue(deadline.StartMs);
            await using var row=await cmd.ExecuteReaderAsync(ct); if (!await row.ReadAsync(ct)) return null;
            maximum=row.GetInt32(1); intent=new CutoffIntent(deadline.MatchId,row.GetInt64(2),checked((int)row.GetInt64(0)),row.GetInt64(3),row.GetInt64(4),row.GetFieldValue<long[]>(5),row.GetFieldValue<long[]>(6));
        }
        if (intent.StartMs != deadline.StartMs || intent.Characters.Length == 0 || intent.Characters.Length != intent.Ships.Length) return null;
        var roster=new List<CutoffRosterMember>();
        await using (var cmd=new NpgsqlCommand(Q("""
            SELECT m.character_id,d.ship_id,m.team,d.points FROM {schema}.match_members m
            JOIN {schema}.match_roster_details d ON d.match_id=m.match_id AND d.team=m.team AND d.character_id=m.character_id
            WHERE m.match_id=$1 AND m.locked ORDER BY m.character_id
            """+(locked ? " FOR UPDATE OF m,d" : "")),c,tx))
        {
            cmd.Parameters.AddWithValue(deadline.MatchId); await using var row=await cmd.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) roster.Add(new(row.GetInt64(0),row.GetInt64(1),row.GetInt32(2),row.GetInt32(3)));
        }
        if (roster.Count != intent.Characters.Length || !roster.Select(x=>x.Character).SequenceEqual(intent.Characters) ||
            !roster.Select(x=>x.Ship).SequenceEqual(intent.Ships) || !roster.Any(x=>x.Team==0) || !roster.Any(x=>x.Team==1) ||
            roster.Any(x=>x.Team is not (0 or 1) || x.Points <= 0) || roster.GroupBy(x=>x.Team).Any(g=>g.Sum(x=>(long)x.Points)>maximum)) return null;
        return new(intent,maximum,roster.ToArray());
    }

    private async Task SettleMatchDueAsync(JobContext job, CancellationToken ct)
    {
        if (scheduler is null) throw new InvalidOperationException("Tournament scheduler is unavailable.");
        var deadline=job.Payload<MatchDeadline>() ?? throw new InvalidOperationException("Missing tournament settlement identity.");
        SettlementSnapshot? before;
        await using (var c=await source.OpenConnectionAsync(ct)) before=await ReadSettlementSnapshotAsync(c,null,deadline,false,ct);
        if (before is null)
        {
            await using var c=await source.OpenConnectionAsync(ct);
            await using var check=new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.matches WHERE id=$1 AND state=5 AND start_ms=$2 AND settlement_pending)"),c);
            check.Parameters.AddWithValue(deadline.MatchId); check.Parameters.AddWithValue(deadline.StartMs);
            if ((bool)(await check.ExecuteScalarAsync(ct))!) job.RunAgainAt(scheduler.Now+5000,deadline);
            return;
        }
        var read=CutoffReader ?? (simulation is null ? null : simulation.ReadTournamentCutoffAsync);
        if (read is null) { job.RunAgainAt(scheduler.Now+5000,deadline); return; }
        var intent=before.Intent;
        var request=new SimReadTournamentCutoff(SimClient.NextRequestId(),SimSceneKey.SolarSystem(intent.SystemId),intent.MatchId,intent.Generation,
            Py.FileTimeFromUnixMs(intent.StartMs),Py.FileTimeFromUnixMs(intent.DeadlineMs));
        SimReadTournamentCutoffReply reply;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            reply=await read(request,timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { job.RunAgainAt(scheduler.Now+5000,deadline); return; }
        if (reply.RequestId != request.RequestId || reply.Scene != request.Scene || reply.MatchId != request.MatchId ||
            reply.Generation != request.Generation || reply.Start != request.Start || reply.Deadline != request.Deadline ||
            reply.Status != SimTournamentCutoffStatus.Ready || reply.Error.Length != 0 || reply.Evidence.Count != before.Roster.Length ||
            reply.Evidence.Any(x=>x.Evidence is not (SimTournamentCutoffEvidence.Alive or SimTournamentCutoffEvidence.Destroyed)))
        { job.RunAgainAt(scheduler.Now+5000,deadline); return; }
        var evidence=reply.Evidence.OrderBy(x=>x.CharacterId).ToArray();
        if (!evidence.Select(x=>x.CharacterId).SequenceEqual(intent.Characters) || !evidence.Select(x=>x.ShipId).SequenceEqual(intent.Ships))
        { job.RunAgainAt(scheduler.Now+5000,deadline); return; }
        var alive=new long[2];
        for (var i=0;i<evidence.Length;i++) if (evidence[i].Evidence == SimTournamentCutoffEvidence.Alive) alive[before.Roster[i].Team]+=before.Roster[i].Points;
        var red=before.Maximum-alive[1]; var blue=before.Maximum-alive[0];
        if (red == blue) { job.RunAgainAt(scheduler.Now+5000,deadline); return; }
        await using (var gate=new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"),job.Connection,job.Transaction)) await gate.ExecuteNonQueryAsync(ct);
        var current=await ReadSettlementSnapshotAsync(job.Connection,job.Transaction,deadline,true,ct);
        if (current is null || current.Maximum != before.Maximum || current.Intent.Generation != intent.Generation ||
            current.Intent.SystemId != intent.SystemId || current.Intent.StartMs != intent.StartMs || current.Intent.DeadlineMs != intent.DeadlineMs ||
            !current.Intent.Characters.SequenceEqual(intent.Characters) || !current.Intent.Ships.SequenceEqual(intent.Ships) || !current.Roster.SequenceEqual(before.Roster))
        { job.RunAgainAt(scheduler.Now+5000,deadline); return; }
        for (var i=0;i<evidence.Length;i++)
        {
            await using var update=new NpgsqlCommand(Q("UPDATE {schema}.match_members SET eliminated=$3 WHERE match_id=$1 AND character_id=$2 AND locked"),job.Connection,job.Transaction);
            update.Parameters.AddWithValue(deadline.MatchId); update.Parameters.AddWithValue(evidence[i].CharacterId); update.Parameters.AddWithValue(evidence[i].Evidence == SimTournamentCutoffEvidence.Destroyed);
            if (await update.ExecuteNonQueryAsync(ct)!=1) throw new InvalidOperationException("Tournament roster changed during settlement.");
        }
        await CompleteMatchAsync(job.Connection,job.Transaction,deadline.MatchId,deadline.StartMs,red>blue ? 0 : 1,"score",scheduler.Now,ct);
        await PublishUpdateAsync(job.Connection,job.Transaction,deadline.MatchId,ct);
        await PublishPlayerStatusAsync(job.Connection,job.Transaction,deadline.MatchId,ct);
    }
}
