using Eve.Events;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private const string NominationTopic = "tournament.fleet.nominated";
    private sealed record NominationNotice(long MatchId, int Team, long CandidateId, long CandidateSessionId, long RefereeId, long FleetId, long CallId, string RefereeOid);
    private string F(string sql) => Eve.Data.Sql.In(Eve.Data.Sql.CheckSchema(fleetSchema), sql);

    public static Task MigrateFleetAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0006_tournament_fleet_consent", """
            ALTER TABLE matches ADD COLUMN red_fleet_id bigint, ADD COLUMN blue_fleet_id bigint;
            CREATE TABLE fleet_nominations (
                match_id bigint NOT NULL REFERENCES matches(id) ON DELETE CASCADE,
                team integer NOT NULL CHECK (team IN (0,1)), candidate_id bigint NOT NULL,
                candidate_session_id bigint NOT NULL, referee_id bigint NOT NULL, fleet_id bigint NOT NULL, last_call_id bigint NOT NULL, referee_oid text NOT NULL REFERENCES match_referees(oid) ON DELETE CASCADE,
                PRIMARY KEY (match_id,team), UNIQUE (candidate_session_id)
            );
            """)], ct);

    public void RegisterFleetHandlers()
    {
        if (sessions is null) throw new InvalidOperationException("Tournament fleet consent requires sessions.");
        events.Subscribe("tournament.fleet.consent_notifications", [NominationTopic], async (context, ct) =>
        {
            var notice = context.Event.Payload<NominationNotice>() ?? throw new InvalidOperationException("Missing fleet nomination.");
            await using var current = new NpgsqlCommand(Q("""
                SELECT 1 FROM {schema}.fleet_nominations WHERE match_id=$1 AND team=$2 AND candidate_id=$3
                    AND candidate_session_id=$4 AND referee_id=$5 AND fleet_id=$6 AND last_call_id=$7 AND referee_oid=$8
                """), context.Connection, context.Transaction);
            current.Parameters.AddWithValue(notice.MatchId); current.Parameters.AddWithValue(notice.Team); current.Parameters.AddWithValue(notice.CandidateId);
            current.Parameters.AddWithValue(notice.CandidateSessionId); current.Parameters.AddWithValue(notice.RefereeId); current.Parameters.AddWithValue(notice.FleetId); current.Parameters.AddWithValue(notice.CallId); current.Parameters.AddWithValue(notice.RefereeOid);
            if (await current.ExecuteScalarAsync(ct) is null) return;
            context.AfterCommit(_ =>
            {
                if (sessions.Of(notice.CandidateId) is { } target && target.SessionId == notice.CandidateSessionId)
                    target.SendNotification("OnChangeFleetBoss", "clientID", new PyLong(notice.RefereeId), new PyInt(MachoMessage.ProxyNodeId));
                return Task.CompletedTask;
            });
        });
    }

    private async Task<long> LockCurrentFleetMemberAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long candidateId, CancellationToken ct)
    {
        // Fleet mutation paths lock the fleet then its member; follow the same order.
        long fleetId;
        await using (var find = new NpgsqlCommand(F("SELECT fleet_id FROM {schema}.fleet_members WHERE character_id=$1"), connection, tx))
        {
            find.Parameters.AddWithValue(candidateId);
            fleetId = (long?)await find.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Candidate has no fleet.");
        }
        await using (var fleet = new NpgsqlCommand(F("SELECT fleet_id FROM {schema}.fleets WHERE fleet_id=$1 FOR UPDATE"), connection, tx))
        {
            fleet.Parameters.AddWithValue(fleetId);
            if (await fleet.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Candidate fleet no longer exists.");
        }
        await using (var member = new NpgsqlCommand(F("SELECT 1 FROM {schema}.fleet_members WHERE fleet_id=$1 AND character_id=$2 FOR UPDATE"), connection, tx))
        {
            member.Parameters.AddWithValue(fleetId); member.Parameters.AddWithValue(candidateId);
            if (await member.ExecuteScalarAsync(ct) is null)
                throw new InvalidOperationException("Candidate is no longer a member of the fleet.");
        }
        return fleetId;
    }

    public async ValueTask NominateFleetAsync(TourneyFleetNominationRequest request, CancellationToken ct)
    {
        if (sessions?.Of(request.CandidateId) is not { } target || target.SessionId <= 0 ||
            request.Session.CharacterId is not { } refereeId || !GameMasterRoles.Has(request.Session, GameMasterRoles.Staff))
            throw new InvalidOperationException("Candidate or referee is unavailable.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), connection, tx)) await gate.ExecuteNonQueryAsync(ct);
        await using (var authorize = new NpgsqlCommand(Q("""
            SELECT 1 FROM {schema}.matches m JOIN {schema}.match_referees r ON r.match_id=m.id
            WHERE m.id=$1 AND m.system_id=$2 AND m.state=1 AND r.oid=$3 AND r.session_id=$4 AND r.character_id=$5 FOR UPDATE OF m,r
            """), connection, tx))
        {
            authorize.Parameters.AddWithValue(request.MatchId); authorize.Parameters.AddWithValue(request.SystemId); authorize.Parameters.AddWithValue(request.BoundObjectId);
            authorize.Parameters.AddWithValue(request.Session.SessionId); authorize.Parameters.AddWithValue(refereeId);
            if (await authorize.ExecuteScalarAsync(ct) is null || request.Session.DockedLocationId is not null ||
                request.Session.GetInt("solarsystemid") != request.SystemId)
                throw new InvalidOperationException("Referee is not authorized for this match.");
        }
        await EnsureTeamUnlockedForConsentAsync(connection, tx, request.MatchId, request.Team, ct);
        var fleetId = await LockCurrentFleetMemberAsync(connection, tx, request.CandidateId, ct);
        if (!ReferenceEquals(sessions.Of(request.CandidateId), target)) throw new InvalidOperationException("Candidate session changed.");
        await using (var existing = new NpgsqlCommand(Q("SELECT match_id,team,candidate_id,candidate_session_id,referee_id,fleet_id,last_call_id,referee_oid FROM {schema}.fleet_nominations WHERE (match_id=$1 AND team=$2) OR candidate_session_id=$3 FOR UPDATE"), connection, tx))
        {
            existing.Parameters.AddWithValue(request.MatchId); existing.Parameters.AddWithValue(request.Team); existing.Parameters.AddWithValue(target.SessionId);
            await using var row = await existing.ExecuteReaderAsync(ct);
            if (await row.ReadAsync(ct))
            {
                if (row.GetInt64(0) != request.MatchId || row.GetInt32(1) != request.Team || row.GetInt64(2) != request.CandidateId ||
                    row.GetInt64(3) != target.SessionId || row.GetInt64(4) != refereeId || row.GetInt64(5) != fleetId || row.GetString(7) != request.BoundObjectId)
                    throw new InvalidOperationException("A conflicting fleet nomination is already pending.");
                var previousCallId = row.GetInt64(6);
                await row.CloseAsync();
                if (previousCallId == request.CallId) { await tx.CommitAsync(ct); return; }
                await using var retry = new NpgsqlCommand(Q("UPDATE {schema}.fleet_nominations SET last_call_id=$3 WHERE match_id=$1 AND team=$2"), connection, tx);
                retry.Parameters.AddWithValue(request.MatchId); retry.Parameters.AddWithValue(request.Team); retry.Parameters.AddWithValue(request.CallId);
                await retry.ExecuteNonQueryAsync(ct);
                await events.PublishAsync(connection, tx, NominationTopic, new NominationNotice(request.MatchId, request.Team, request.CandidateId, target.SessionId, refereeId, fleetId, request.CallId, request.BoundObjectId), cancellationToken: ct);
                await tx.CommitAsync(ct); events.Wake();
                return;
            }
        }
        await using (var insert = new NpgsqlCommand(Q("INSERT INTO {schema}.fleet_nominations VALUES ($1,$2,$3,$4,$5,$6,$7,$8)"), connection, tx))
        {
            insert.Parameters.AddWithValue(request.MatchId); insert.Parameters.AddWithValue(request.Team); insert.Parameters.AddWithValue(request.CandidateId);
            insert.Parameters.AddWithValue(target.SessionId); insert.Parameters.AddWithValue(refereeId); insert.Parameters.AddWithValue(fleetId); insert.Parameters.AddWithValue(request.CallId); insert.Parameters.AddWithValue(request.BoundObjectId); await insert.ExecuteNonQueryAsync(ct);
        }
        await events.PublishAsync(connection, tx, NominationTopic, new NominationNotice(request.MatchId, request.Team, request.CandidateId, target.SessionId, refereeId, fleetId, request.CallId, request.BoundObjectId), cancellationToken: ct);
        await tx.CommitAsync(ct); events.Wake();
    }

    private async Task EnsureTeamUnlockedForConsentAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long matchId, int team, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(Q("SELECT COALESCE((SELECT is_locked FROM {schema}.match_teams WHERE match_id=$1 AND team=$2),false)"), connection, tx);
        command.Parameters.AddWithValue(matchId); command.Parameters.AddWithValue(team);
        if ((bool)(await command.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("Unlock the team before changing fleet consent.");
    }

    public async ValueTask AcceptFleetAsync(ClientSession session, CancellationToken ct)
    {
        if (session.CharacterId is not { } candidate || sessions?.Of(candidate) != session)
            throw new InvalidOperationException("Candidate session is unavailable.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), connection, tx)) await gate.ExecuteNonQueryAsync(ct);
        long matchId, fleetId; int team;
        await using (var pending = new NpgsqlCommand(Q("""
            SELECT n.match_id,n.team,n.fleet_id FROM {schema}.fleet_nominations n JOIN {schema}.matches m ON m.id=n.match_id
            WHERE n.candidate_id=$1 AND n.candidate_session_id=$2 AND m.state=1 FOR UPDATE OF n,m
            """), connection, tx))
        {
            pending.Parameters.AddWithValue(candidate); pending.Parameters.AddWithValue(session.SessionId);
            await using var row = await pending.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) throw new InvalidOperationException("No active fleet nomination for this session.");
            matchId = row.GetInt64(0); team = row.GetInt32(1); fleetId = row.GetInt64(2);
        }
        await EnsureTeamUnlockedForConsentAsync(connection, tx, matchId, team, ct);
        if (await LockCurrentFleetMemberAsync(connection, tx, candidate, ct) != fleetId || !ReferenceEquals(sessions.Of(candidate), session))
            throw new InvalidOperationException("Fleet or candidate session changed before consent.");
        var sql = team == 0
            ? "UPDATE {schema}.matches SET red_captain_id=$2,red_fleet_id=$3 WHERE id=$1 AND blue_fleet_id IS DISTINCT FROM $3"
            : "UPDATE {schema}.matches SET blue_captain_id=$2,blue_fleet_id=$3 WHERE id=$1 AND red_fleet_id IS DISTINCT FROM $3";
        await using (var update = new NpgsqlCommand(Q(sql), connection, tx))
        {
            update.Parameters.AddWithValue(matchId); update.Parameters.AddWithValue(candidate); update.Parameters.AddWithValue(fleetId);
            if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("The fleet already represents the other team.");
        }
        await using (var clear = new NpgsqlCommand(Q("DELETE FROM {schema}.fleet_nominations WHERE match_id=$1 AND team=$2"), connection, tx))
        { clear.Parameters.AddWithValue(matchId); clear.Parameters.AddWithValue(team); await clear.ExecuteNonQueryAsync(ct); }
        await PublishUpdateAsync(connection, tx, matchId, ct);
        await tx.CommitAsync(ct); events.Wake();
    }
}
