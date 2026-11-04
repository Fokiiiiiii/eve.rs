using Eve.Macho;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private const string BanTimeoutJob = "tournament.ban.timeout";
    private const string BanPromptTopic = "tournament.ban.prompted";
    private sealed record BanDeadline(long RoundId);
    private sealed record BanPrompt(long BanId, long CharacterId, long SessionId, int Quantity, int[] Own, int[] Opponent, long DeadlineMs, long TournamentId);

    public static Task MigrateAutobansAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema), [new Eve.Data.Migration("0008_tournament_autobans", """
            CREATE TABLE autoban_rounds (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, match_id bigint NOT NULL REFERENCES matches(id) ON DELETE CASCADE,
                deadline_ms bigint NOT NULL, quantity integer NOT NULL CHECK(quantity > 0), finished boolean NOT NULL DEFAULT false);
            CREATE UNIQUE INDEX autoban_open_match ON autoban_rounds(match_id) WHERE NOT finished;
            CREATE TABLE autoban_choices (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, round_id bigint NOT NULL REFERENCES autoban_rounds(id) ON DELETE CASCADE,
                team integer NOT NULL CHECK(team IN (0,1)), character_id bigint NOT NULL, session_id bigint NOT NULL,
                submitted boolean NOT NULL DEFAULT false, choices integer[] NOT NULL DEFAULT '{}', UNIQUE(round_id,team));
            """)], ct);

    private void RegisterAutobanHandlers()
    {
        scheduler!.Register(BanTimeoutJob, BanTimeoutAsync);
        events.Subscribe("tournament.ban.client_prompts", [BanPromptTopic], (ctx, _) =>
        {
            var prompt = ctx.Event.Payload<BanPrompt>() ?? throw new InvalidOperationException("Missing ban prompt.");
            ctx.AfterCommit(_ =>
            {
                if (sessions!.Of(prompt.CharacterId) is { } target && target.SessionId == prompt.SessionId)
                    target.SendNotification("OnTournamentPerformBan", "clientID", new PyLong(prompt.BanId), new PyInt(prompt.Quantity),
                        Py.Tuple(new PyList([.. prompt.Own.Select(x => (PyValue)new PyInt(x))]), new PyList([.. prompt.Opponent.Select(x => (PyValue)new PyInt(x))])),
                        new PyLong(Py.FileTimeFromUnixMs(prompt.DeadlineMs)), new PyInt(MachoMessage.ProxyNodeId), new PyLong(prompt.TournamentId));
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });
    }

    private static long BanNumber(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, _ => -1 };
    private static long BanKeyword(PyValue kwargs, string key) => kwargs is PyDict dict
        ? dict.Entries.Where(x => JsSemantics.StringValue(x.Key) == key).Select(x => BanNumber(x.Value)).FirstOrDefault(-1) : -1;

    private async Task BeginAutobanAsync(NpgsqlConnection c, NpgsqlTransaction tx, TourneyMatchActionRequest request, int state, CancellationToken ct)
    {
        if (state != 0 || request.Arguments.Count != 2) throw new InvalidOperationException("Autobans require the banning stage.");
        var tournamentId = BanNumber(request.Arguments[1]);
        var red = BanKeyword(request.KeywordArguments, "redCharID"); var blue = BanKeyword(request.KeywordArguments, "blueCharID");
        var quantity = BanKeyword(request.KeywordArguments, "totalNumBans");
        if (tournamentId <= 0 || red <= 0 || blue <= 0 || red == blue || quantity is <= 0 or > int.MaxValue || sessions is null)
            throw new ArgumentException("Invalid autoban parameters.");
        var redSession = sessions.Of(red); var blueSession = sessions.Of(blue);
        if (redSession is null || blueSession is null || redSession.SessionId <= 0 || blueSession.SessionId <= 0)
            throw new InvalidOperationException("Both ban participants must be online.");
        var prior = new List<(long Id,int Team,long Character,long Session,bool Submitted)>();
        long priorQuantity = 0, priorDeadline = 0, priorTournament = 0;
        await using (var existing = new NpgsqlCommand(Q("""
            SELECT r.quantity,r.deadline_ms,m.tournament_id,ch.id,ch.team,ch.character_id,ch.session_id,ch.submitted
            FROM {schema}.autoban_rounds r JOIN {schema}.matches m ON m.id=r.match_id
            JOIN {schema}.autoban_choices ch ON ch.round_id=r.id WHERE r.match_id=$1 AND NOT r.finished ORDER BY ch.team
            """), c, tx))
        {
            existing.Parameters.AddWithValue(request.MatchId); await using var rows = await existing.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
            {
                priorQuantity=rows.GetInt32(0); priorDeadline=rows.GetInt64(1); priorTournament=rows.GetInt64(2);
                prior.Add((rows.GetInt64(3),rows.GetInt32(4),rows.GetInt64(5),rows.GetInt64(6),rows.GetBoolean(7)));
            }
        }
        if (prior.Count != 0)
        {
            if (prior.Count != 2 || priorQuantity != quantity || priorTournament != tournamentId ||
                prior[0].Character != red || prior[1].Character != blue || scheduler!.Now >= priorDeadline)
                throw new InvalidOperationException("A different or expired autoban round is active.");
            foreach (var choice in prior)
            {
                var newSession = choice.Team == 0 ? redSession.SessionId : blueSession.SessionId;
                if (newSession == choice.Session) continue;
                await using (var replace = new NpgsqlCommand(Q("UPDATE {schema}.autoban_choices SET session_id=$2 WHERE id=$1"), c, tx))
                { replace.Parameters.AddWithValue(choice.Id); replace.Parameters.AddWithValue(newSession); await replace.ExecuteNonQueryAsync(ct); }
                if (!choice.Submitted)
                {
                    var (own,opponent) = await ReadBansAsync(c, tx, request.MatchId, choice.Team, ct);
                    await events.PublishAsync(c, tx, BanPromptTopic, new BanPrompt(choice.Id,choice.Character,newSession,(int)quantity,own,opponent,priorDeadline,tournamentId), cancellationToken: ct);
                }
            }
            return;
        }
        await using (var check = new NpgsqlCommand(Q("SELECT cardinality(t.ship_types) FROM {schema}.matches m JOIN {schema}.tournaments t ON t.id=m.tournament_id WHERE m.id=$1 AND t.id=$2"), c, tx))
        {
            check.Parameters.AddWithValue(request.MatchId); check.Parameters.AddWithValue(tournamentId);
            var available = await check.ExecuteScalarAsync(ct);
            if (available is not int count || quantity > count) throw new ArgumentException("Invalid tournament or ban count.");
        }
        var deadline = scheduler!.Now + 60000; long round;
        await using (var cmd = new NpgsqlCommand(Q("INSERT INTO {schema}.autoban_rounds(match_id,deadline_ms,quantity) VALUES ($1,$2,$3) RETURNING id"), c, tx))
        { cmd.Parameters.AddWithValue(request.MatchId); cmd.Parameters.AddWithValue(deadline); cmd.Parameters.AddWithValue((int)quantity); round = (long)(await cmd.ExecuteScalarAsync(ct))!; }
        for (var team = 0; team < 2; team++)
        {
            var character = team == 0 ? red : blue; var sid = team == 0 ? redSession.SessionId : blueSession.SessionId; long banId;
            await using (var cmd = new NpgsqlCommand(Q("INSERT INTO {schema}.autoban_choices(round_id,team,character_id,session_id) VALUES ($1,$2,$3,$4) RETURNING id"), c, tx))
            { cmd.Parameters.AddWithValue(round); cmd.Parameters.AddWithValue(team); cmd.Parameters.AddWithValue(character); cmd.Parameters.AddWithValue(sid); banId = (long)(await cmd.ExecuteScalarAsync(ct))!; }
            var (own, opponent) = await ReadBansAsync(c, tx, request.MatchId, team, ct);
            await events.PublishAsync(c, tx, BanPromptTopic, new BanPrompt(banId,character,sid,(int)quantity,own,opponent,deadline,tournamentId), cancellationToken: ct);
        }
        await scheduler.ScheduleAsync(c, tx, new JobSpec(BanTimeoutJob, deadline) { Key = $"round:{round}", Payload = new BanDeadline(round) }, ct);
    }

    private async Task<(int[] Own,int[] Opponent)> ReadBansAsync(NpgsqlConnection c, NpgsqlTransaction tx, long match, int team, CancellationToken ct)
    {
        var own = new List<int>(); var opponent = new List<int>();
        await using var cmd = new NpgsqlCommand(Q("SELECT team,type_id FROM {schema}.match_bans WHERE match_id=$1 ORDER BY type_id"), c, tx);
        cmd.Parameters.AddWithValue(match); await using var rows = await cmd.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct)) (rows.GetInt32(0) == team ? own : opponent).Add(rows.GetInt32(1));
        return (own.ToArray(),opponent.ToArray());
    }

    public async ValueTask<PyValue> SubmitBanAsync(TourneyBanSubmitRequest request, CancellationToken ct)
    {
        if (scheduler is null || sessions is null || request.Session.CharacterId is not > 0 || !ReferenceEquals(sessions.Of(request.Session.CharacterId.Value), request.Session))
            throw new InvalidOperationException("Ban participant is not connected.");
        var values = request.ShipTypes.Select(BanNumber).ToArray();
        if (values.Any(x => x is <= 0 or > int.MaxValue) || values.Distinct().Count() != values.Length) throw new ArgumentException("Invalid ship choices.");
        var types = values.Select(x => (int)x).Order().ToArray();
        await using var c = await source.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), c, tx)) await gate.ExecuteNonQueryAsync(ct);
        long round,match,deadline; int team,quantity; bool submitted,finished; int[] prior;
        await using (var cmd = new NpgsqlCommand(Q("""
            SELECT r.id,r.match_id,r.deadline_ms,r.quantity,r.finished,ch.team,ch.submitted,ch.choices
            FROM {schema}.autoban_choices ch JOIN {schema}.autoban_rounds r ON r.id=ch.round_id
            JOIN {schema}.matches m ON m.id=r.match_id WHERE ch.id=$1 AND ch.character_id=$2 AND ch.session_id=$3 AND m.state=0 FOR UPDATE OF r,ch,m
            """), c, tx))
        {
            cmd.Parameters.AddWithValue(request.BanId); cmd.Parameters.AddWithValue(request.Session.CharacterId.Value); cmd.Parameters.AddWithValue(request.Session.SessionId);
            await using var row = await cmd.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) throw new InvalidOperationException("Unknown or unauthorized ban.");
            round=row.GetInt64(0); match=row.GetInt64(1); deadline=row.GetInt64(2); quantity=row.GetInt32(3); finished=row.GetBoolean(4); team=row.GetInt32(5); submitted=row.GetBoolean(6); prior=row.GetFieldValue<int[]>(7);
        }
        // The result-only prompt has zero choices and its Okay button calls BanShip again.
        if (finished && types.Length == 0) { await tx.CommitAsync(ct); return PyNone.Instance; }
        if (submitted)
        {
            if (!prior.SequenceEqual(types)) throw new InvalidOperationException("Ban already submitted with different choices.");
            await tx.CommitAsync(ct); return PyNone.Instance;
        }
        if (finished || scheduler.Now >= deadline || types.Length > quantity) throw new InvalidOperationException("Ban round has expired or too many choices were submitted.");
        await using (var validate = new NpgsqlCommand(Q("SELECT $2::int[] <@ t.ship_types FROM {schema}.matches m JOIN {schema}.tournaments t ON t.id=m.tournament_id WHERE m.id=$1"), c, tx))
        { validate.Parameters.AddWithValue(match); validate.Parameters.AddWithValue(types); if (!(bool)(await validate.ExecuteScalarAsync(ct))!) throw new ArgumentException("Ship is not allowed."); }
        foreach (var type in types)
        {
            await using var add = new NpgsqlCommand(Q("INSERT INTO {schema}.match_bans VALUES ($1,$2,$3) ON CONFLICT DO NOTHING"), c, tx);
            add.Parameters.AddWithValue(match); add.Parameters.AddWithValue(team); add.Parameters.AddWithValue(type); await add.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand(Q("UPDATE {schema}.autoban_choices SET submitted=true,choices=$2 WHERE id=$1"), c, tx))
        { cmd.Parameters.AddWithValue(request.BanId); cmd.Parameters.AddWithValue(types); await cmd.ExecuteNonQueryAsync(ct); }
        await PublishUpdateAsync(c, tx, match, ct);
        await FinishAutobanIfReadyAsync(c, tx, round, false, ct);
        await tx.CommitAsync(ct); events.Wake(); return PyNone.Instance;
    }

    private async Task BanTimeoutAsync(JobContext job, CancellationToken ct)
    {
        var deadline = job.Payload<BanDeadline>() ?? throw new InvalidOperationException("Missing ban deadline.");
        await using var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), job.Connection, job.Transaction);
        await gate.ExecuteNonQueryAsync(ct);
        await FinishAutobanIfReadyAsync(job.Connection, job.Transaction, deadline.RoundId, true, ct);
    }

    private async Task FinishAutobanIfReadyAsync(NpgsqlConnection c, NpgsqlTransaction tx, long round, bool expired, CancellationToken ct)
    {
        long match,tournament,deadline;
        await using (var cmd = new NpgsqlCommand(Q("""
            UPDATE {schema}.autoban_rounds r SET finished=true FROM {schema}.matches m
            WHERE r.id=$1 AND m.id=r.match_id AND NOT r.finished AND ($2 OR NOT EXISTS(SELECT 1 FROM {schema}.autoban_choices ch WHERE ch.round_id=r.id AND NOT ch.submitted))
            RETURNING r.match_id,m.tournament_id,r.deadline_ms
            """), c, tx))
        {
            cmd.Parameters.AddWithValue(round); cmd.Parameters.AddWithValue(expired); await using var row = await cmd.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) return;
            match=row.GetInt64(0); tournament=row.GetInt64(1); deadline=row.GetInt64(2);
        }
        var recipients = new List<(long Id,int Team,long Character,long Session)>();
        await using (var cmd = new NpgsqlCommand(Q("SELECT id,team,character_id,session_id FROM {schema}.autoban_choices WHERE round_id=$1 ORDER BY team"), c, tx))
        {
            cmd.Parameters.AddWithValue(round); await using var rows = await cmd.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct)) recipients.Add((rows.GetInt64(0),rows.GetInt32(1),rows.GetInt64(2),rows.GetInt64(3)));
        }
        foreach (var recipient in recipients)
        {
            var (own,other) = await ReadBansAsync(c, tx, match, recipient.Team, ct);
            await events.PublishAsync(c, tx, BanPromptTopic, new BanPrompt(recipient.Id,recipient.Character,recipient.Session,0,own,other,deadline,tournament), cancellationToken: ct);
        }
        if (!expired) await scheduler!.CancelAsync(c, tx, BanTimeoutJob, $"round:{round}", ct);
    }
}
