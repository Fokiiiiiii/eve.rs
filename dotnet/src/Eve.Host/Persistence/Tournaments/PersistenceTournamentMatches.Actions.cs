using Eve.Services;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private const string CountdownJob = "tournament.match.countdown";
    private const string UpdateTopic = "tournament.match.updated";
    private const string PreMatchTopic = "tournament.match.camera_start";
    private sealed record PreMatch(long MatchId, long SystemId, string Red, string Blue, int NebulaId);
    private sealed record Referee(long CharacterId, long SessionId);
    private sealed record Update(long MatchId, long SystemId, int State, long? StartMs, int[] RedBans, int[] BlueBans, Referee[] Referees, RosterRow[]? RedTeam, RosterRow[]? BlueTeam, bool RedLocked, bool BlueLocked);
    private sealed record Deadline(long MatchId, long StartMs);

    public void RegisterActionHandlers()
    {
        if (scheduler is null || sessions is null) throw new InvalidOperationException("Tournament actions require scheduler and sessions.");
        scheduler.Register(CountdownJob, CountdownDueAsync);
        scheduler.Register(MatchDeadlineJob, MatchDeadlineDueAsync);
        scheduler.Register(SettlementJob, SettleMatchDueAsync);
        scheduler.Register(CutoffReleaseJob, ReleaseCutoffDueAsync);
        scheduler.Register(CutoffSweepJob, SweepCutoffIntentsAsync);
        RegisterMetricsHandlers();
        RegisterAutobanHandlers();
        RegisterPostgameHandlers();
        RegisterPlayerStatusHandler();
        events.Subscribe("tournament.match.camera_notifications", [PreMatchTopic], (context, _) =>
        {
            var start = context.Event.Payload<PreMatch>() ?? throw new InvalidOperationException("Missing camera start.");
            context.AfterCommit(_ =>
            {
                if (space is not null)
                    foreach (var session in sessions.InSpace(start.SystemId))
                        if (space.IsInPublicSystem(session, start.SystemId))
                            session.SendNotification("OnPreMatchStart", "clientID", new PyLong(start.MatchId),
                                new PyStr(start.Red), new PyStr(start.Blue), new PyInt(start.NebulaId));
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });
        events.Subscribe("tournament.match.referee_notifications", [UpdateTopic], (context, _) =>
        {
            var update = context.Event.Payload<Update>() ?? throw new InvalidOperationException("Missing tournament update.");
            context.AfterCommit(_ =>
            {
                static PyValue Team(RosterRow[]? roster) => new PyList([.. (roster ?? []).Select(row => (PyValue)Py.Tuple(
                    new PyLong(row.CharacterId), new PyInt(row.ShipTypeId), new PyLong(row.ShipId), new PyLong(row.LocationId),
                    new PyInt(row.Points), new PyList([.. row.Errors.Select(error => (PyValue)new PyStr(error))])))]);
                var fields = Py.Dict(("redTeamDetails", Team(update.RedTeam)), ("blueTeamDetails", Team(update.BlueTeam)),
                    ("redTeamLocked", PyValue.Of(update.RedLocked)), ("blueTeamLocked", PyValue.Of(update.BlueLocked)),
                    ("matchID", new PyLong(update.MatchId)), ("state", new PyInt(update.State)),
                    ("solarsystemID", new PyLong(update.SystemId)),
                    ("redTeamBans", new PyList([.. update.RedBans.Select(x => (PyValue)new PyInt(x))])),
                    ("blueTeamBans", new PyList([.. update.BlueBans.Select(x => (PyValue)new PyInt(x))])),
                    ("matchStartTime", update.StartMs is { } start ? new PyLong(Py.FileTimeFromUnixMs(start)) : PyNone.Instance));
                foreach (var referee in update.Referees)
                    if (sessions.Of(referee.CharacterId) is { } session && session.SessionId == referee.SessionId)
                        session.SendNotification("ProcessTournamentMatchUpdate", "clientID", fields);
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });
    }

    private async Task PublishUpdateAsync(NpgsqlConnection c, NpgsqlTransaction tx, long matchId, CancellationToken ct)
    {
        int state; long? start; long systemId;
        await using (var cmd = new NpgsqlCommand(Q("SELECT state,start_ms,system_id FROM {schema}.matches WHERE id=$1"), c, tx))
        {
            cmd.Parameters.AddWithValue(matchId); await using var row = await cmd.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) return;
            state = row.GetInt32(0); start = row.IsDBNull(1) ? null : row.GetInt64(1); systemId = row.GetInt64(2);
        }
        var red = new List<int>(); var blue = new List<int>(); var refs = new List<Referee>();
        await using (var cmd = new NpgsqlCommand(Q("SELECT team,type_id FROM {schema}.match_bans WHERE match_id=$1 ORDER BY type_id"), c, tx))
        {
            cmd.Parameters.AddWithValue(matchId); await using var row = await cmd.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) (row.GetInt32(0) == 0 ? red : blue).Add(row.GetInt32(1));
        }
        await using (var cmd = new NpgsqlCommand(Q("SELECT character_id,session_id FROM {schema}.match_referees WHERE match_id=$1 ORDER BY oid"), c, tx))
        {
            cmd.Parameters.AddWithValue(matchId); await using var row = await cmd.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) refs.Add(new Referee(row.GetInt64(0), row.GetInt64(1)));
        }
        var redTeam = new List<RosterRow>(); var blueTeam = new List<RosterRow>();
        await using (var cmd = new NpgsqlCommand(Q("""
            SELECT team,character_id,ship_type_id,ship_id,location_id,points,errors FROM {schema}.match_roster_details
            WHERE match_id=$1 ORDER BY team,character_id
            """), c, tx))
        {
            cmd.Parameters.AddWithValue(matchId); await using var row = await cmd.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct))
                (row.GetInt32(0) == 0 ? redTeam : blueTeam).Add(new RosterRow(row.GetInt64(1), row.GetInt32(2), row.GetInt64(3), row.GetInt64(4), row.GetInt32(5), row.GetFieldValue<string[]>(6)));
        }
        var redLocked = false; var blueLocked = false;
        await using (var cmd = new NpgsqlCommand(Q("SELECT team,is_locked FROM {schema}.match_teams WHERE match_id=$1"), c, tx))
        {
            cmd.Parameters.AddWithValue(matchId); await using var row = await cmd.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) { if (row.GetInt32(0) == 0) redLocked = row.GetBoolean(1); else blueLocked = row.GetBoolean(1); }
        }
        await events.PublishAsync(c, tx, UpdateTopic, new Update(matchId, systemId, state, start, red.ToArray(), blue.ToArray(), refs.ToArray(), redTeam.ToArray(), blueTeam.ToArray(), redLocked, blueLocked), cancellationToken: ct);
    }

    private async Task CountdownDueAsync(JobContext job, CancellationToken ct)
    {
        var due = job.Payload<Deadline>() ?? throw new InvalidOperationException("Missing tournament deadline.");
        await using var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), job.Connection, job.Transaction);
        await gate.ExecuteNonQueryAsync(ct);
        await using var cmd = new NpgsqlCommand(Q("UPDATE {schema}.matches SET state=5 WHERE id=$1 AND state=3 AND start_ms=$2"), job.Connection, job.Transaction);
        cmd.Parameters.AddWithValue(due.MatchId); cmd.Parameters.AddWithValue(due.StartMs);
        if (await cmd.ExecuteNonQueryAsync(ct) != 0)
        {
            await PublishUpdateAsync(job.Connection, job.Transaction, due.MatchId, ct);
            await PublishPlayerStatusAsync(job.Connection, job.Transaction, due.MatchId, ct);
            await ScheduleMatchDeadlineAsync(job.Connection, job.Transaction, due.MatchId, due.StartMs, ct);
        }
    }

    public async ValueTask<PyValue> ActionAsync(TourneyMatchActionRequest request, CancellationToken ct)
    {
        if (scheduler is null) throw new InvalidOperationException("Tournament scheduler is not configured.");
        var cameraNebula = request.Operation == TourneyMatchAction.StartCountdown
            ? await CameraNebulaAsync(request.SystemId, ct) : (int?)null;
        await using var countdownSnapshots = request.Operation == TourneyMatchAction.StartCountdown
            ? await PrepareCountdownAsync(request, ct) : null;
        var cutoff = request.Operation == TourneyMatchAction.StartCountdown
            ? await PrepareAutomaticCutoffAsync(request,countdownSnapshots,ct) : null;
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), c, tx)) await gate.ExecuteNonQueryAsync(ct);
        int state; long? start;
        await using (var cmd = new NpgsqlCommand(Q("""
            SELECT m.state,m.start_ms FROM {schema}.matches m JOIN {schema}.match_referees r ON r.match_id=m.id
            WHERE m.id=$1 AND m.system_id=$2 AND r.oid=$3 AND r.session_id=$4 AND r.character_id=$5 FOR UPDATE OF m,r
            """), c, tx))
        {
            cmd.Parameters.AddWithValue(request.MatchId); cmd.Parameters.AddWithValue(request.SystemId); cmd.Parameters.AddWithValue(request.BoundObjectId);
            cmd.Parameters.AddWithValue(request.Session.SessionId); cmd.Parameters.AddWithValue(request.Session.CharacterId ?? 0);
            await using var row = await cmd.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct) || request.Session.DockedLocationId is not null || request.Session.GetInt("solarsystemid") != request.SystemId)
                throw new InvalidOperationException("Referee is not authorized for this match.");
            state = row.GetInt32(0); start = row.IsDBNull(1) ? null : row.GetInt64(1);
        }
        if (request.Operation == TourneyMatchAction.ResetMatch)
        {
            await EnsureNoParticipantsAsync(c, tx, request.MatchId, ct);
            await using var pendingCutoff = new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.tournament_cutoff_intents WHERE match_id=$1 AND state <> 'released')"), c, tx);
            pendingCutoff.Parameters.AddWithValue(request.MatchId);
            if ((bool)(await pendingCutoff.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("Tournament cutoff release is still pending.");
        }
        if (request.Operation == TourneyMatchAction.StartAutobanning)
        {
            await BeginAutobanAsync(c, tx, request, state, ct);
            await tx.CommitAsync(ct); events.Wake(); scheduler.Wake(); return PyNone.Instance;
        }
        if (request.Operation == TourneyMatchAction.FinalizeBans)
        {
            await using var open = new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.autoban_rounds WHERE match_id=$1 AND NOT finished)"), c, tx);
            open.Parameters.AddWithValue(request.MatchId);
            if ((bool)(await open.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("Autobans are still in progress.");
        }
        var changed = false; PyValue result = PyNone.Instance;
        if (request.Operation is TourneyMatchAction.BanShipType or TourneyMatchAction.UnBanShipType)
        {
            if (state != 0 || request.Arguments.Count != 3) throw new InvalidOperationException("Bans can only be changed while banning.");
            static long Number(PyValue x) => x switch { PyInt i => i.Value, PyLong l => l.Value, _ => -1 };
            var type = Number(request.Arguments[1]); var team = Number(request.Arguments[2]);
            if (type is <= 0 or > int.MaxValue || team is < 0 or > 1) throw new ArgumentException("Invalid ship or team.");
            await using var cmd = new NpgsqlCommand(Q(request.Operation == TourneyMatchAction.BanShipType
                ? "INSERT INTO {schema}.match_bans(match_id,team,type_id) SELECT m.id,$2,$3 FROM {schema}.matches m JOIN {schema}.tournaments t ON t.id=m.tournament_id WHERE m.id=$1 AND $3=ANY(t.ship_types) ON CONFLICT DO NOTHING"
                : "DELETE FROM {schema}.match_bans WHERE match_id=$1 AND team=$2 AND type_id=$3"), c, tx);
            cmd.Parameters.AddWithValue(request.MatchId); cmd.Parameters.AddWithValue((int)team); cmd.Parameters.AddWithValue((int)type);
            changed = await cmd.ExecuteNonQueryAsync(ct) != 0;
            if (!changed && request.Operation == TourneyMatchAction.BanShipType)
            {
                await using var check = new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.match_bans WHERE match_id=$1 AND team=$2 AND type_id=$3)"), c, tx);
                check.Parameters.AddWithValue(request.MatchId); check.Parameters.AddWithValue((int)team); check.Parameters.AddWithValue((int)type);
                if (!(bool)(await check.ExecuteScalarAsync(ct))!) throw new ArgumentException("Ship is not allowed in this tournament.");
            }
        }
        else
        {
            int? next = request.Operation switch
            {
                TourneyMatchAction.FinalizeBans when state == 0 => 1,
                TourneyMatchAction.CompletePregameChecks when state == 1 => 2,
                TourneyMatchAction.StartCountdown when state == 2 => 3,
                TourneyMatchAction.AbortCountdown when state == 3 && start - scheduler.Now > 2000 => 2,
                TourneyMatchAction.MatchCompleted when state == 5 => 6,
                TourneyMatchAction.ResetMatch when state is 1 or 2 => 7,
                _ => null
            };
            if (request.Operation == TourneyMatchAction.AbortCountdown) result = next is not null ? PyBool.True : PyBool.False;
            else if (next is null && !(request.Operation == TourneyMatchAction.StartCountdown && state == 3))
                throw new InvalidOperationException("Action is invalid in the current match state.");
            if (next is { } target)
            {
                if (target == 3)
                {
                    await ValidateCountdownAsync(c, tx, request, countdownSnapshots, ct);
                    await using var policy = new NpgsqlCommand(Q("SELECT COALESCE(p.policy,'manual') FROM {schema}.matches m LEFT JOIN {schema}.tournament_completion_policies p ON p.tournament_id=m.tournament_id WHERE m.id=$1"),c,tx);
                    policy.Parameters.AddWithValue(request.MatchId);
                    if ((string?)(await policy.ExecuteScalarAsync(ct)) == "alliance_tournament")
                    {
                        if (cutoff is null || cutoff.StartMs <= scheduler.Now)
                            throw new InvalidOperationException("The tournament cutoff registration expired; retry the countdown.");
                        await using var roster = new NpgsqlCommand(Q("""
                            SELECT ARRAY(SELECT character_id FROM {schema}.match_roster_details WHERE match_id=$1 ORDER BY character_id),
                                   ARRAY(SELECT ship_id FROM {schema}.match_roster_details WHERE match_id=$1 ORDER BY character_id)
                            """),c,tx);
                        roster.Parameters.AddWithValue(request.MatchId); await using var row=await roster.ExecuteReaderAsync(ct);
                        if (!await row.ReadAsync(ct) || !row.GetFieldValue<long[]>(0).SequenceEqual(cutoff.Characters) || !row.GetFieldValue<long[]>(1).SequenceEqual(cutoff.Ships))
                            throw new InvalidOperationException("Tournament roster changed after cutoff registration.");
                        await row.CloseAsync();
                        if (!await TransitionCutoffIntentAsync(c,tx,cutoff,"registered","committed",ct))
                            throw new InvalidOperationException("Tournament cutoff registration changed before countdown.");
                    }
                }
                start = target == 3 ? cutoff?.StartMs ?? scheduler.Now + 30000 : target == 6 ? start : null;
                if (request.Operation == TourneyMatchAction.AbortCountdown)
                    await ReleaseCommittedCutoffAsync(c,tx,request.MatchId,ct);
                if (target == 6)
                    await CompleteMatchAsync(c, tx, request.MatchId, start, null, "manual", scheduler.Now, ct);
                else
                {
                    await using var cmd = new NpgsqlCommand(Q("UPDATE {schema}.matches SET state=$2,start_ms=$3 WHERE id=$1"), c, tx);
                    cmd.Parameters.AddWithValue(request.MatchId); cmd.Parameters.AddWithValue(target);
                    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = (object?)start ?? DBNull.Value });
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                changed = true;
                if (target == 3)
                {
                    await scheduler.ScheduleAsync(c, tx, new JobSpec(CountdownJob, start!.Value) { Key = $"match:{request.MatchId}", Payload = new Deadline(request.MatchId, start.Value) }, ct);
                    await using var names = new NpgsqlCommand(Q("SELECT s.red_team,s.blue_team FROM {schema}.matches m JOIN {schema}.series s ON s.tournament_id=m.tournament_id AND s.id=m.series_id WHERE m.id=$1"), c, tx);
                    names.Parameters.AddWithValue(request.MatchId);
                    string red, blue;
                    await using (var row = await names.ExecuteReaderAsync(ct))
                    {
                        if (!await row.ReadAsync(ct)) throw new InvalidOperationException("Tournament series is unavailable.");
                        red = row.GetString(0); blue = row.GetString(1);
                    }
                    await events.PublishAsync(c, tx, PreMatchTopic, new PreMatch(request.MatchId, request.SystemId, red, blue, cameraNebula!.Value), cancellationToken: ct);
                }
                else if (request.Operation == TourneyMatchAction.AbortCountdown)
                    await scheduler.CancelAsync(c, tx, CountdownJob, $"match:{request.MatchId}", ct);
            }
        }
        if (changed)
        {
            await PublishUpdateAsync(c, tx, request.MatchId, ct);
            await PublishPlayerStatusAsync(c, tx, request.MatchId, ct);
        }
        if (changed && request.Operation == TourneyMatchAction.ResetMatch)
        {
            await scheduler.CancelAsync(c, tx, CountdownJob, $"match:{request.MatchId}", ct);
            await using var delete = new NpgsqlCommand(Q("DELETE FROM {schema}.matches WHERE id=$1"), c, tx);
            delete.Parameters.AddWithValue(request.MatchId);
            await delete.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct); if (changed) { events.Wake(); scheduler.Wake(); }
        return result;
    }
}
