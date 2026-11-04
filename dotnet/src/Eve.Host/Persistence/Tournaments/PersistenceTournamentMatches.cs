using Eve.Events;
using Eve.Inventory;
using Eve.Sde;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Core;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Host.Persistence;

/// <summary>Active matches and referee leases. All state changes and their events commit together.</summary>
public sealed partial class PersistenceTournamentMatches(NpgsqlDataSource source, PersistenceTournamentArenas arenas,
    PgItemStore items, PgDeployedStore deployed, EventBus events, SimClient? simulation, SdeReader sde,
    string schema = PersistenceTournamentStore.DefaultSchema, DurableScheduler? scheduler = null, CharacterSessions? sessions = null,
    string fleetSchema = Eve.Services.Space.FleetStore.DefaultSchema, Eve.Services.Space.SpaceSessions? space = null,
    CharacterShips? tournamentShips = null, Eve.Characters.PgCharacterStore? tournamentCharacters = null, Eve.Services.Space.SpaceContainers? tournamentContainers = null)
{
    private string Q(string sql) => Eve.Data.Sql.In(Eve.Data.Sql.CheckSchema(schema), sql);

    public static Task MigrateAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0004_tournament_matches", """
            CREATE TABLE matches (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                tournament_id bigint NOT NULL, series_id bigint NOT NULL, system_id bigint NOT NULL UNIQUE,
                maximum_points integer NOT NULL, match_length integer NOT NULL, overtime_length integer NOT NULL,
                external_ruleset_id bigint, red_captain_id bigint, blue_captain_id bigint,
                FOREIGN KEY (tournament_id, series_id) REFERENCES series(tournament_id, id),
                UNIQUE (tournament_id, series_id)
            );
            CREATE TABLE match_referees (
                oid text PRIMARY KEY, match_id bigint NOT NULL REFERENCES matches(id) ON DELETE CASCADE,
                session_id bigint NOT NULL, character_id bigint NOT NULL
            );
            CREATE INDEX match_referees_session ON match_referees(session_id);
            """), new Eve.Data.Migration("0005_tournament_match_actions", """
            ALTER TABLE matches ADD COLUMN state integer NOT NULL DEFAULT 0 CHECK (state BETWEEN 0 AND 7);
            ALTER TABLE matches ADD COLUMN start_ms bigint;
            CREATE TABLE match_bans (match_id bigint NOT NULL REFERENCES matches(id) ON DELETE CASCADE, team integer NOT NULL CHECK (team IN (0,1)), type_id integer NOT NULL CHECK (type_id > 0), PRIMARY KEY (match_id,team,type_id));
            """)], ct);

    public async ValueTask<TourneyCreateMatchResult> CreateAsync(TourneyCreateMatchRequest request, CancellationToken ct)
    {
        if (simulation is null) throw new InvalidOperationException("Tournament matches require a running simulation.");
        if (request.Session.SessionId <= 0 || request.Session.CharacterId is not > 0)
            throw new InvalidOperationException("Tournament referee session is invalid.");
        var systems = await arenas.ReadSystemsAsync(ct);
        var arena = systems.SingleOrDefault(x => x.SolarSystemId == request.SystemId)
            ?? throw new InvalidOperationException("The tournament system has no configured arena.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var snapshots = new List<DeployedRecord>();
        foreach (var id in arena.BeaconIds)
        {
            var beacon = await deployed.GetAsync(id, timeout.Token) ?? throw new InvalidOperationException("Arena beacon is missing.");
            var types = await sde.ReadAsync("types", [beacon.TypeId], timeout.Token);
            if (types.Count != 1 || !types[0].Doc.TryGetProperty("graphicID", out var graphic) ||
                !graphic.TryGetInt32(out var graphicId) || graphicId <= 0)
                throw new InvalidOperationException("Arena beacon has no valid SDE graphic.");
            var ensureError = await simulation.EnsureTournamentBeaconAsync(new SimEnsureTournamentBeacon(
                SimClient.NextRequestId(), SimSceneKey.SolarSystem((int)request.SystemId), id, beacon.TypeId,
                beacon.GroupId, 2, beacon.OwnerId, graphicId, beacon.Name, beacon.Position, beacon.Radius), timeout.Token);
            if (ensureError is not null) throw new InvalidOperationException($"Arena beacon placement failed: {ensureError}");
            var reply = await simulation.GetSceneBodyAsync(new SimGetSceneBody(
                SimClient.NextRequestId(), SimSceneKey.SolarSystem((int)request.SystemId), id), timeout.Token);
            if (reply.Error is not null || reply.TypeId != beacon.TypeId || reply.GroupId != 310 || reply.CategoryId != 2 ||
                !reply.Position.SequenceEqual(beacon.Position))
                throw new InvalidOperationException("Arena beacon does not match the live simulation.");
            snapshots.Add(beacon);
        }
        if (snapshots.Count == 0) throw new InvalidOperationException("The tournament arena has no beacons.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var releaseGate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), connection, tx))
            await releaseGate.ExecuteNonQueryAsync(ct);
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.match'), $1)"), connection, tx))
        { gate.Parameters.AddWithValue(checked((int)request.SystemId)); await gate.ExecuteNonQueryAsync(ct); }
        await using (var config = new NpgsqlCommand(Q("""
            SELECT t.maximum_points,t.external_ruleset_id FROM {schema}.tournaments t
            JOIN {schema}.series s ON s.tournament_id=t.id
            WHERE t.id=$1 AND s.id=$2 FOR UPDATE OF t,s
            """), connection, tx))
        {
            config.Parameters.AddWithValue(request.TournamentId); config.Parameters.AddWithValue(request.SeriesId);
            await using var row = await config.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct) || row.GetInt32(0) != request.MaximumPoints ||
                (row.IsDBNull(1) ? (long?)null : row.GetInt64(1)) != request.ExternalRulesetId ||
                request.MatchLength is < 120 or > 600 || request.OvertimeLength is < 0 or > 300)
                throw new InvalidOperationException("Tournament match configuration is invalid.");
        }
        await using (var arenaLock = new NpgsqlCommand(Q("SELECT item_id FROM {schema}.arena_beacons WHERE system_id=$1 ORDER BY ordinal FOR UPDATE"), connection, tx))
        {
            arenaLock.Parameters.AddWithValue(request.SystemId);
            var ids = new List<long>(); await using var row = await arenaLock.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) ids.Add(row.GetInt64(0));
            if (!ids.SequenceEqual(arena.BeaconIds)) throw new InvalidOperationException("Arena changed while creating the match.");
        }
        foreach (var snapshot in snapshots.OrderBy(x => x.DeployableId))
        {
            var item = await items.LockAndGetAsync(connection, tx, snapshot.DeployableId, ct);
            var current = await deployed.LockAndGetAsync(connection, tx, snapshot.DeployableId, ct);
            if (item is null || current is null || item.OwnerId != snapshot.OwnerId || item.LocationId != request.SystemId ||
                item.TypeId != snapshot.TypeId || !item.Singleton || item.Quantity != 1 || current.Kind != snapshot.Kind ||
                current.TypeId != snapshot.TypeId || current.GroupId != snapshot.GroupId || current.OwnerId != snapshot.OwnerId ||
                current.SolarSystemId != snapshot.SolarSystemId || current.Radius != snapshot.Radius ||
                !current.Position.SequenceEqual(snapshot.Position))
                throw new InvalidOperationException("Arena beacon changed while creating the match.");
        }
        if (request.Session.CharacterId is not > 0 || request.Session.DockedLocationId is not null ||
            request.Session.GetInt("solarsystemid") != request.SystemId)
            throw new InvalidOperationException("Referee left the tournament system.");
        long matchId; long? red; long? blue;
        await using (var existing = new NpgsqlCommand(Q("SELECT id,tournament_id,series_id,maximum_points,match_length,overtime_length,external_ruleset_id,red_captain_id,blue_captain_id,closing,settlement_pending FROM {schema}.matches WHERE system_id=$1 FOR UPDATE"), connection, tx))
        {
            existing.Parameters.AddWithValue(request.SystemId);
            await using var row = await existing.ExecuteReaderAsync(ct);
            if (await row.ReadAsync(ct))
            {
                if ((row.GetBoolean(9) && !row.GetBoolean(10)) || row.GetInt64(1) != request.TournamentId || row.GetInt64(2) != request.SeriesId ||
                    row.GetInt32(3) != request.MaximumPoints || row.GetInt32(4) != request.MatchLength ||
                    row.GetInt32(5) != request.OvertimeLength || (row.IsDBNull(6) ? (long?)null : row.GetInt64(6)) != request.ExternalRulesetId)
                    throw new InvalidOperationException("A different match is already active in this system.");
                matchId = row.GetInt64(0); red = row.IsDBNull(7) ? null : row.GetInt64(7); blue = row.IsDBNull(8) ? null : row.GetInt64(8);
            }
            else { matchId = 0; red = null; blue = null; }
        }
        if (matchId == 0)
        {
            await using var insert = new NpgsqlCommand(Q("""
                INSERT INTO {schema}.matches (tournament_id,series_id,system_id,maximum_points,match_length,overtime_length,external_ruleset_id)
                VALUES ($1,$2,$3,$4,$5,$6,$7) RETURNING id
                """), connection, tx);
            insert.Parameters.AddWithValue(request.TournamentId); insert.Parameters.AddWithValue(request.SeriesId); insert.Parameters.AddWithValue(request.SystemId);
            insert.Parameters.AddWithValue(request.MaximumPoints); insert.Parameters.AddWithValue(request.MatchLength); insert.Parameters.AddWithValue(request.OvertimeLength);
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = (object?)request.ExternalRulesetId ?? DBNull.Value });
            matchId = (long)(await insert.ExecuteScalarAsync(ct))!;
            await events.PublishAsync(connection, tx, "tournament.match.created", new { matchId, request.TournamentId, request.SeriesId, request.SystemId }, cancellationToken: ct);
        }
        var addedReferee = false;
        await using (var referee = new NpgsqlCommand(Q("INSERT INTO {schema}.match_referees(oid,match_id,session_id,character_id,lease_generation) VALUES ($1,$2,$3,$4,$5) ON CONFLICT (oid) DO NOTHING"), connection, tx))
        {
            referee.Parameters.AddWithValue(request.BoundObjectId); referee.Parameters.AddWithValue(matchId);
            referee.Parameters.AddWithValue(request.Session.SessionId); referee.Parameters.AddWithValue(request.Session.CharacterId!.Value);
            referee.Parameters.AddWithValue(request.LeaseGeneration);
            addedReferee = await referee.ExecuteNonQueryAsync(ct) != 0;
            if (!addedReferee)
            {
                await using var prior = new NpgsqlCommand(Q("SELECT match_id,session_id,character_id,lease_generation FROM {schema}.match_referees WHERE oid=$1 FOR UPDATE"), connection, tx);
                prior.Parameters.AddWithValue(request.BoundObjectId);
                await using var row = await prior.ExecuteReaderAsync(ct);
                if (!await row.ReadAsync(ct) || row.GetInt64(0) != matchId || row.GetInt64(1) != request.Session.SessionId ||
                    row.GetInt64(2) != request.Session.CharacterId.Value || row.GetGuid(3) != request.LeaseGeneration)
                    throw new InvalidOperationException("The bound referee object already belongs to another match or session.");
            }
        }
        if (addedReferee) await PublishUpdateAsync(connection, tx, matchId, ct);
        await tx.CommitAsync(ct); events.Wake();
        return new TourneyCreateMatchResult(matchId, arena.BeaconIds, red, blue);
    }

    public ValueTask ReleaseAsync(string oid, CancellationToken ct) => new(ReleaseWhereAsync("oid=$1", oid, ct));
    public ValueTask ReleaseStampedAsync(string oid, Guid generation, CancellationToken ct) => new(ReleaseWhereAsync("oid=$1 AND lease_generation=$2", oid, ct, generation));
    public ValueTask RemoveRefereeAsync(string oid, Guid generation, long matchId, CancellationToken ct) =>
        new(ReleaseWhereAsync("oid=$1 AND lease_generation=$2 AND match_id=$3", oid, ct, generation, matchId));
    public Task ReleaseSessionAsync(long sessionId, CancellationToken ct = default) => ReleaseWhereAsync("session_id=$1", sessionId, ct);
    public Task ResetAfterRestartAsync(CancellationToken ct = default) => ReleaseWhereAsync("TRUE", null, ct);

    private async Task ReleaseWhereAsync(string predicate, object? value, CancellationToken ct, Guid? generation = null, long? expectedMatchId = null)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        // One global transaction lock serializes simultaneous releases and restart reconciliation.
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), connection, tx))
            await gate.ExecuteNonQueryAsync(ct);
        if (predicate == "session_id=$1")
        {
            await using var nominations = new NpgsqlCommand(Q("DELETE FROM {schema}.fleet_nominations WHERE candidate_session_id=$1"), connection, tx);
            nominations.Parameters.AddWithValue(value!); await nominations.ExecuteNonQueryAsync(ct);
        }
        var matchIds = new List<long>();
        await using (var delete = new NpgsqlCommand(Q($"DELETE FROM {schema}.match_referees WHERE {predicate} RETURNING match_id"), connection, tx))
        {
            if (value is not null) delete.Parameters.AddWithValue(value);
            if (generation is { } stamp) delete.Parameters.AddWithValue(stamp);
            if (expectedMatchId is { } match) delete.Parameters.AddWithValue(match);
            await using var rows = await delete.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct)) matchIds.Add(rows.GetInt64(0));
        }
        if (value is null)
        {
            await using var orphan = new NpgsqlCommand(Q("SELECT id FROM {schema}.matches"), connection, tx);
            await using var rows = await orphan.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct)) matchIds.Add(rows.GetInt64(0));
        }
        foreach (var matchId in matchIds.Distinct())
        {
            await using var state = new NpgsqlCommand(Q("""
                SELECT EXISTS(SELECT 1 FROM {schema}.match_referees WHERE match_id=$1),
                       EXISTS(SELECT 1 FROM {schema}.match_participants WHERE match_id=$1),
                       EXISTS(SELECT 1 FROM {schema}.matches WHERE id=$1 AND (cleanup_required OR postgame_pending OR settlement_pending)) OR
                       EXISTS(SELECT 1 FROM {schema}.match_cleanup_targets WHERE match_id=$1) OR
                       EXISTS(SELECT 1 FROM {schema}.tournament_cutoff_intents WHERE match_id=$1 AND state <> 'released'),
                       EXISTS(SELECT 1 FROM {schema}.matches WHERE id=$1 AND settlement_pending)
                """), connection, tx);
            state.Parameters.AddWithValue(matchId);
            await using var row = await state.ExecuteReaderAsync(ct);
            await row.ReadAsync(ct); var hasReferees = row.GetBoolean(0); var hasParticipants = row.GetBoolean(1); var hasCleanup = row.GetBoolean(2); var settlementPending = row.GetBoolean(3);
            await row.CloseAsync();
            if (hasReferees) continue;
            if (!settlementPending) await ReleaseOutstandingCutoffsAsync(connection, tx, matchId, ct);
            if (hasParticipants || hasCleanup)
            {
                if (hasParticipants) await ScheduleParticipantRecoveryAsync(connection, tx, matchId, ct);
                else
                {
                    await using var closing = new NpgsqlCommand(Q("UPDATE {schema}.matches SET closing=true WHERE id=$1"), connection, tx);
                    closing.Parameters.AddWithValue(matchId); await closing.ExecuteNonQueryAsync(ct);
                }
                continue;
            }
            await using var delete = new NpgsqlCommand(Q("DELETE FROM {schema}.matches WHERE id=$1 RETURNING system_id"), connection, tx);
            delete.Parameters.AddWithValue(matchId);
            var system = await delete.ExecuteScalarAsync(ct);
            if (system is not null)
                await events.PublishAsync(connection, tx, "tournament.match.reset", new { matchId, systemId = (long)system }, cancellationToken: ct);
        }
        await tx.CommitAsync(ct); events.Wake(); scheduler?.Wake();
    }
}
