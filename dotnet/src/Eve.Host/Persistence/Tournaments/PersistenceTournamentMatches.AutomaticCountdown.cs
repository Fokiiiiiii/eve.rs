using Eve.Services;
using Eve.Services.Core;
using Eve.Space;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    // The live simulator currently cannot certify all pending work at the cutoff.
    // An implementation is installed only after that capability is authoritative.
    internal Func<SimRegisterTournamentCutoff,CancellationToken,Task<SimRegisterTournamentCutoffReply>>? CutoffRegistrar { get; set; }

    private async Task<CutoffIntent?> PrepareAutomaticCutoffAsync(TourneyMatchActionRequest request, CountdownPreparation? preparation, CancellationToken ct)
    {
        if (scheduler is null) throw new InvalidOperationException("Tournament scheduler is unavailable.");
        CutoffIntent intent;
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"),c,tx)) await gate.ExecuteNonQueryAsync(ct);
            int state, length; string policy;
            await using (var match = new NpgsqlCommand(Q("""
                SELECT m.state,m.match_length,COALESCE(p.policy,'manual') FROM {schema}.matches m
                JOIN {schema}.match_referees r ON r.match_id=m.id
                LEFT JOIN {schema}.tournament_completion_policies p ON p.tournament_id=m.tournament_id
                WHERE m.id=$1 AND m.system_id=$2 AND r.oid=$3 AND r.session_id=$4 AND r.character_id=$5 FOR UPDATE OF m,r
                """),c,tx))
            {
                match.Parameters.AddWithValue(request.MatchId); match.Parameters.AddWithValue(request.SystemId); match.Parameters.AddWithValue(request.BoundObjectId);
                match.Parameters.AddWithValue(request.Session.SessionId); match.Parameters.AddWithValue(request.Session.CharacterId ?? 0);
                await using var row=await match.ExecuteReaderAsync(ct);
                if (!await row.ReadAsync(ct) || request.Session.DockedLocationId is not null || request.Session.GetInt("solarsystemid") != request.SystemId)
                    throw new InvalidOperationException("Referee is not authorized for this match.");
                state=row.GetInt32(0); length=row.GetInt32(1); policy=row.GetString(2);
            }
            if (policy != "alliance_tournament" || state != 2) { await tx.CommitAsync(ct); return null; }
            var registrar=CutoffRegistrar ?? (simulation is not null ? simulation.RegisterTournamentCutoffAsync : throw new InvalidOperationException("Automatic settlement requires an authoritative simulation cutoff provider."));
            await ValidateCountdownAsync(c,tx,request,preparation,ct);
            var roster=new List<SimTournamentCutoffPilot>();
            await using (var members=new NpgsqlCommand(Q("SELECT character_id,ship_id FROM {schema}.match_roster_details WHERE match_id=$1 ORDER BY character_id"),c,tx))
            {
                members.Parameters.AddWithValue(request.MatchId); await using var row=await members.ExecuteReaderAsync(ct);
                while (await row.ReadAsync(ct)) roster.Add(new(row.GetInt64(0),row.GetInt64(1)));
            }
            var start=checked(scheduler.Now+30000);
            intent=await PrepareCutoffIntentAsync(c,tx,request.MatchId,checked((int)request.SystemId),start,checked(start+length*1000L),roster,ct);
            await tx.CommitAsync(ct); scheduler.Wake();
            // The RPC is deliberately outside the database transaction; the durable intent owns recovery.
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var message=new SimRegisterTournamentCutoff(SimClient.NextRequestId(),SimSceneKey.SolarSystem(intent.SystemId),intent.MatchId,intent.Generation,
                Py.FileTimeFromUnixMs(intent.StartMs),Py.FileTimeFromUnixMs(intent.DeadlineMs),roster);
            var response=await registrar(message,timeout.Token);
            if (response.RequestId != message.RequestId || response.Scene != message.Scene || response.MatchId != message.MatchId ||
                response.Generation != message.Generation || response.Start != message.Start || response.Deadline != message.Deadline ||
                response.Status != SimTournamentCutoffStatus.Pending || response.Error.Length != 0)
                throw new InvalidOperationException("Simulation did not confirm the tournament cutoff identity.");
        }
        await using (var c=await source.OpenConnectionAsync(ct))
        await using (var tx=await c.BeginTransactionAsync(ct))
        {
            if (!await TransitionCutoffIntentAsync(c,tx,intent,"prepared","registered",ct))
                throw new InvalidOperationException("Tournament cutoff registration was superseded.");
            await tx.CommitAsync(ct);
        }
        return intent;
    }

    private async Task ReleaseCommittedCutoffAsync(NpgsqlConnection c, NpgsqlTransaction tx, long matchId, CancellationToken ct)
    {
        CutoffIntent? intent=null;
        await using (var find=new NpgsqlCommand(Q("""
            SELECT generation,system_id,start_ms,deadline_ms,characters,ships FROM {schema}.tournament_cutoff_intents
            WHERE match_id=$1 AND state='committed' FOR UPDATE
            """),c,tx))
        {
            find.Parameters.AddWithValue(matchId); await using var row=await find.ExecuteReaderAsync(ct);
            if (await row.ReadAsync(ct)) intent=new CutoffIntent(matchId,row.GetInt64(0),checked((int)row.GetInt64(1)),row.GetInt64(2),row.GetInt64(3),row.GetFieldValue<long[]>(4),row.GetFieldValue<long[]>(5));
        }
        if (intent is not null) await ScheduleCutoffReleaseAsync(c,tx,intent,"committed",ct);
    }
}
