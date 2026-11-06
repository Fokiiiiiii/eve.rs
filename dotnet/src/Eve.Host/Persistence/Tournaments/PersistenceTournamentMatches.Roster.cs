using Eve.Characters;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Eve.Ships;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private sealed record RosterSnapshot(ShipSnapshot Ship, IReadOnlyList<Eve.Inventory.Item> Contents);
    private sealed record RosterRow(long CharacterId, int ShipTypeId, long ShipId, long LocationId, int Points, string[] Errors);

    public static Task MigrateRosterAsync(NpgsqlDataSource source, string schema = PersistenceTournamentStore.DefaultSchema, CancellationToken ct = default) =>
        Eve.Data.Migrations.ApplyAsync(source, Eve.Data.Sql.CheckSchema(schema),
        [new Eve.Data.Migration("0014_tournament_roster", """
            CREATE TABLE match_roster_details (
                match_id bigint NOT NULL REFERENCES matches(id) ON DELETE CASCADE, team integer NOT NULL CHECK(team IN (0,1)),
                character_id bigint NOT NULL, ship_type_id integer NOT NULL, ship_id bigint NOT NULL, location_id bigint NOT NULL,
                points integer NOT NULL CHECK(points > 0), errors text[] NOT NULL, fit_hash bytea NOT NULL, PRIMARY KEY(match_id,character_id)
            );
            CREATE TABLE match_error_overrides (
                match_id bigint NOT NULL REFERENCES matches(id) ON DELETE CASCADE, character_id bigint NOT NULL,
                error text NOT NULL, fit_hash bytea NOT NULL, PRIMARY KEY(match_id,character_id,error)
            );
            """)], ct);

    private static byte[] FitHash(ShipSnapshot ship, IReadOnlyList<Eve.Inventory.Item> contents) =>
        System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            shipId = ship.Ship.Id, typeId = ship.Ship.TypeId,
            contents = contents.OrderBy(x => x.Id).Select(x => new { x.Id, x.TypeId, x.Flag, x.Quantity, x.Singleton }).ToArray()
        }));

    public async ValueTask<PyValue> RosterActionAsync(TourneyMatchActionRequest request, CancellationToken ct)
    {
        if (tournamentShips is null || tournamentCharacters is null) throw new InvalidOperationException("Tournament roster authority is unavailable.");
        static long Number(PyValue x) => x switch { PyInt n => n.Value, PyLong n => n.Value, _ => -1 };
        if (request.Operation == TourneyMatchAction.OverrideError) return await OverrideRosterErrorAsync(request, ct);
        if (request.Arguments.Count < 2 || Number(request.Arguments[1]) is not (0 or 1))
            throw new ArgumentException("Invalid tournament team.");
        var team = (int)Number(request.Arguments[1]);
        if (request.KeywordArguments is not PyNone) throw new ArgumentException("Invalid roster keywords.");
        if (request.Operation == TourneyMatchAction.AddPlayer) return await AddRosterPlayerAsync(request, team, ct);
        if (request.Operation == TourneyMatchAction.RemovePlayer)
        {
            if (request.Arguments.Count != 3 || Number(request.Arguments[2]) is not > 0) throw new ArgumentException("Invalid roster member.");
            var characterId = Number(request.Arguments[2]);
            await using var connection = await source.OpenConnectionAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
            await AuthorizeRosterAsync(connection, tx, request, ct);
            await using (var active = new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.match_participants WHERE match_id=$1 AND character_id=$2)"), connection, tx))
            { active.Parameters.AddWithValue(request.MatchId); active.Parameters.AddWithValue(characterId); if ((bool)(await active.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("Return this pilot before removing them."); }
            await using (var remove = new NpgsqlCommand(Q("DELETE FROM {schema}.match_members WHERE match_id=$1 AND team=$2 AND character_id=$3"), connection, tx))
            {
                remove.Parameters.AddWithValue(request.MatchId); remove.Parameters.AddWithValue(team); remove.Parameters.AddWithValue(characterId);
                if (await remove.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Pilot is not on that team.");
            }
            await using (var clear = new NpgsqlCommand(Q("WITH cleared AS (DELETE FROM {schema}.match_roster_details WHERE match_id=$1 AND team=$2 AND character_id=$3) DELETE FROM {schema}.match_error_overrides WHERE match_id=$1 AND character_id=$3"), connection, tx))
            { clear.Parameters.AddWithValue(request.MatchId); clear.Parameters.AddWithValue(team); clear.Parameters.AddWithValue(characterId); await clear.ExecuteNonQueryAsync(ct); }
            await PublishUpdateAsync(connection, tx, request.MatchId, ct); await tx.CommitAsync(ct); events.Wake();
            return PyNone.Instance;
        }
        if (request.Operation is not (TourneyMatchAction.UpdateFleetDetails or TourneyMatchAction.LockMemberList or TourneyMatchAction.UnlockMemberList))
            throw new InvalidOperationException("Tournament roster operation is not wired.");
        if (request.Operation == TourneyMatchAction.UnlockMemberList)
        {
            if (request.Arguments.Count != 2) throw new ArgumentException("Invalid unlock arguments.");
            await using var connection = await source.OpenConnectionAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
            await AuthorizeRosterAsync(connection, tx, request, ct);
            await using (var active = new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.match_participants WHERE match_id=$1 AND team=$2)"), connection, tx))
            { active.Parameters.AddWithValue(request.MatchId); active.Parameters.AddWithValue(team); if ((bool)(await active.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("Return the team before unlocking its roster."); }
            await using (var members = new NpgsqlCommand(Q("UPDATE {schema}.match_members SET locked=false WHERE match_id=$1 AND team=$2"), connection, tx))
            { members.Parameters.AddWithValue(request.MatchId); members.Parameters.AddWithValue(team); await members.ExecuteNonQueryAsync(ct); }
            await using var cmd = new NpgsqlCommand(Q("UPDATE {schema}.match_teams SET is_locked=false WHERE match_id=$1 AND team=$2"), connection, tx);
            cmd.Parameters.AddWithValue(request.MatchId); cmd.Parameters.AddWithValue(team); await cmd.ExecuteNonQueryAsync(ct);
            await PublishUpdateAsync(connection, tx, request.MatchId, ct); await tx.CommitAsync(ct); events.Wake();
            return PyNone.Instance;
        }
        if (request.Arguments.Count != (request.Operation == TourneyMatchAction.LockMemberList ? 3 : 2))
            throw new ArgumentException("Invalid roster arguments.");
        long candidate, fleetId;
        await using (var connection = await source.OpenConnectionAsync(ct))
        await using (var command = new NpgsqlCommand(Q("""
            SELECT CASE WHEN $2=0 THEN m.red_captain_id ELSE m.blue_captain_id END,
                   CASE WHEN $2=0 THEN m.red_fleet_id ELSE m.blue_fleet_id END
            FROM {schema}.matches m JOIN {schema}.match_referees r ON r.match_id=m.id
            WHERE m.id=$1 AND m.system_id=$3 AND r.oid=$4 AND r.session_id=$5 AND r.character_id=$6 AND m.state=1 AND NOT m.closing
            """), connection))
        {
            command.Parameters.AddWithValue(request.MatchId); command.Parameters.AddWithValue(team); command.Parameters.AddWithValue(request.SystemId);
            command.Parameters.AddWithValue(request.BoundObjectId); command.Parameters.AddWithValue(request.Session.SessionId);
            command.Parameters.AddWithValue(request.Session.CharacterId ?? 0);
            await using var row = await command.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct) || row.IsDBNull(0) || row.IsDBNull(1)) throw new InvalidOperationException("Team fleet consent is required.");
            candidate = row.GetInt64(0); fleetId = row.GetInt64(1);
        }
        var fleets = new FleetStore(source, fleetSchema);
        var fleet = await fleets.SnapshotAsync(fleetId, ct) ?? throw new InvalidOperationException("Consented fleet no longer exists.");
        var memberIds = fleet.Members.Select(x => x.CharacterId).Order().ToArray();
        if (!memberIds.Contains(candidate)) throw new InvalidOperationException("Consenting member left the fleet.");
        var snapshots = new Dictionary<long,RosterSnapshot>();
        foreach (var id in memberIds)
        {
            var ship = await tournamentShips.LoadAsync(id, ct) ?? throw new InvalidOperationException($"Character {id} has no active ship.");
            var contents = await items.InLocationAsync(ship.Ship.Id, id, ct);
            snapshots.Add(id, new RosterSnapshot(ship, contents));
        }
        if (request.Operation == TourneyMatchAction.LockMemberList)
        {
            var list = request.Arguments[2] switch { PyList l => l.Items, PyTuple t => t.Items, _ => null };
            if (list is null || list.Any(x => Number(x) <= 0) || !list.Select(Number).Order().SequenceEqual(memberIds))
                throw new InvalidOperationException("Team membership changed; refresh before locking.");
        }
        await using var c = await source.OpenConnectionAsync(ct); await using var transaction = await c.BeginTransactionAsync(ct);
        await AuthorizeRosterAsync(c, transaction, request, ct);
        await using (var check = new NpgsqlCommand(Q("""
            SELECT CASE WHEN $2=0 THEN red_captain_id ELSE blue_captain_id END,
                   CASE WHEN $2=0 THEN red_fleet_id ELSE blue_fleet_id END
            FROM {schema}.matches WHERE id=$1
            """), c, transaction))
        {
            check.Parameters.AddWithValue(request.MatchId); check.Parameters.AddWithValue(team);
            await using var row = await check.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct) || row.IsDBNull(0) || row.IsDBNull(1) || row.GetInt64(0) != candidate || row.GetInt64(1) != fleetId)
                throw new InvalidOperationException("Team fleet consent changed while refreshing.");
        }
        await using (var locked = new NpgsqlCommand(Q("SELECT is_locked FROM {schema}.match_teams WHERE match_id=$1 AND team=$2"), c, transaction))
        {
            locked.Parameters.AddWithValue(request.MatchId); locked.Parameters.AddWithValue(team);
            if (await locked.ExecuteScalarAsync(ct) is true) throw new InvalidOperationException("Unlock the team before refreshing its roster.");
        }
        if (await LockCurrentFleetMemberAsync(c, transaction, candidate, ct) != fleetId)
            throw new InvalidOperationException("Consenting member changed fleets.");
        var lockedMembers = new List<long>();
        await using (var cmd = new NpgsqlCommand(F("SELECT character_id FROM {schema}.fleet_members WHERE fleet_id=$1 ORDER BY character_id FOR UPDATE"), c, transaction))
        { cmd.Parameters.AddWithValue(fleetId); await using var rows = await cmd.ExecuteReaderAsync(ct); while (await rows.ReadAsync(ct)) lockedMembers.Add(rows.GetInt64(0)); }
        if (!lockedMembers.SequenceEqual(memberIds)) throw new InvalidOperationException("Fleet changed while refreshing the roster.");
        int[] prohibited; var points = new Dictionary<int,int>();
        await using (var cmd = new NpgsqlCommand(Q("SELECT prohibited_fit_types FROM {schema}.tournament_rules t JOIN {schema}.matches m ON m.tournament_id=t.tournament_id WHERE m.id=$1"), c, transaction))
        { cmd.Parameters.AddWithValue(request.MatchId); prohibited = (int[]?)await cmd.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Tournament hull points and fit rules are not configured."); }
        await using (var cmd = new NpgsqlCommand(Q("SELECT p.type_id,p.points FROM {schema}.tournament_ship_points p JOIN {schema}.matches m ON m.tournament_id=p.tournament_id WHERE m.id=$1"), c, transaction))
        { cmd.Parameters.AddWithValue(request.MatchId); await using var rows = await cmd.ExecuteReaderAsync(ct); while (await rows.ReadAsync(ct)) points.Add(rows.GetInt32(0), rows.GetInt32(1)); }
        var result = new List<(RosterRow Row, byte[] FitHash)>();
        foreach (var id in memberIds)
        {
            await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", c, transaction)) { gate.Parameters.AddWithValue(id); await gate.ExecuteNonQueryAsync(ct); }
            var snapshot = snapshots[id];
            var character = await tournamentCharacters.LockAndGetAsync(c, transaction, id, ct);
            var ship = await items.LockAndGetAsync(c, transaction, snapshot.Ship.Ship.Id, ct);
            var contents = await items.LockAllInLocationAsync(c, transaction, snapshot.Ship.Ship.Id, id, ct);
            if (character is null || character.ShipId != snapshot.Ship.Ship.Id || ship != snapshot.Ship.Ship ||
                !contents.SequenceEqual(snapshot.Contents)) throw new InvalidOperationException("Pilot ship or fitting changed while refreshing.");
            if (!points.TryGetValue(ship.TypeId, out var point))
                throw new InvalidOperationException($"No configured tournament points for ship type {ship.TypeId}.");
            var hash = FitHash(snapshot.Ship, contents);
            await using (var clear = new NpgsqlCommand(Q("DELETE FROM {schema}.match_error_overrides WHERE match_id=$1 AND character_id=$2 AND fit_hash<>$3"), c, transaction))
            { clear.Parameters.AddWithValue(request.MatchId); clear.Parameters.AddWithValue(id); clear.Parameters.AddWithValue(hash); await clear.ExecuteNonQueryAsync(ct); }
            var approved = new HashSet<string>(StringComparer.Ordinal);
            await using (var prior = new NpgsqlCommand(Q("SELECT error FROM {schema}.match_error_overrides WHERE match_id=$1 AND character_id=$2 AND fit_hash=$3"), c, transaction))
            { prior.Parameters.AddWithValue(request.MatchId); prior.Parameters.AddWithValue(id); prior.Parameters.AddWithValue(hash); await using var rows = await prior.ExecuteReaderAsync(ct); while (await rows.ReadAsync(ct)) approved.Add(rows.GetString(0)); }
            var errors = snapshot.Ship.Fitted.Concat(snapshot.Ship.Charges).Where(x => prohibited.Contains(x.TypeId))
                .Select(x => $"Prohibited fitted type {x.TypeId}").Where(x => !approved.Contains(x)).Distinct().Order().ToArray();
            var location = character.StationId > 0 ? character.StationId : character.SolarSystemId;
            result.Add((new RosterRow(id, ship.TypeId, ship.Id, location, point, errors), hash));
        }
        await using (var clear = new NpgsqlCommand(Q("WITH cleared AS (DELETE FROM {schema}.match_roster_details WHERE match_id=$1 AND team=$2) DELETE FROM {schema}.match_members WHERE match_id=$1 AND team=$2"), c, transaction))
        { clear.Parameters.AddWithValue(request.MatchId); clear.Parameters.AddWithValue(team); await clear.ExecuteNonQueryAsync(ct); }
        foreach (var entry in result)
        {
            var row = entry.Row;
            await using var insert = new NpgsqlCommand(Q("""
                WITH member AS (INSERT INTO {schema}.match_members(match_id,team,character_id,locked) VALUES ($1,$2,$3,$4) RETURNING character_id)
                INSERT INTO {schema}.match_roster_details SELECT $1,$2,character_id,$5,$6,$7,$8,$9,$10 FROM member
                """), c, transaction);
            insert.Parameters.AddWithValue(request.MatchId); insert.Parameters.AddWithValue(team); insert.Parameters.AddWithValue(row.CharacterId);
            insert.Parameters.AddWithValue(request.Operation == TourneyMatchAction.LockMemberList); insert.Parameters.AddWithValue(row.ShipTypeId);
            insert.Parameters.AddWithValue(row.ShipId); insert.Parameters.AddWithValue(row.LocationId); insert.Parameters.AddWithValue(row.Points); insert.Parameters.AddWithValue(row.Errors); insert.Parameters.AddWithValue(entry.FitHash);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var update = new NpgsqlCommand(Q("INSERT INTO {schema}.match_teams VALUES ($1,$2,$3) ON CONFLICT(match_id,team) DO UPDATE SET is_locked=EXCLUDED.is_locked"), c, transaction))
        { update.Parameters.AddWithValue(request.MatchId); update.Parameters.AddWithValue(team); update.Parameters.AddWithValue(request.Operation == TourneyMatchAction.LockMemberList); await update.ExecuteNonQueryAsync(ct); }
        await PublishUpdateAsync(c, transaction, request.MatchId, ct); await transaction.CommitAsync(ct); events.Wake();
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> AddRosterPlayerAsync(TourneyMatchActionRequest request, int team, CancellationToken ct)
    {
        static long Number(PyValue x) => x switch { PyInt n => n.Value, PyLong n => n.Value, _ => -1 };
        if (request.Arguments.Count != 3 || Number(request.Arguments[2]) is not > 0 || tournamentShips is null || tournamentCharacters is null)
            throw new ArgumentException("Invalid tournament pilot.");
        var characterId = Number(request.Arguments[2]);
        await using (var connection = await source.OpenConnectionAsync(ct))
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await AuthorizeRosterAsync(connection, tx, request, ct);
            await using var locked = new NpgsqlCommand(Q("SELECT is_locked FROM {schema}.match_teams WHERE match_id=$1 AND team=$2"), connection, tx);
            locked.Parameters.AddWithValue(request.MatchId); locked.Parameters.AddWithValue(team);
            if (await locked.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Lock the team before manually adding a pilot.");
            await tx.CommitAsync(ct);
        }
        var snapshot = await tournamentShips.LoadAsync(characterId, ct) ?? throw new InvalidOperationException("Pilot has no active ship.");
        var contents = await items.InLocationAsync(snapshot.Ship.Id, characterId, ct);
        await using var c = await source.OpenConnectionAsync(ct); await using var transaction = await c.BeginTransactionAsync(ct);
        await AuthorizeRosterAsync(c, transaction, request, ct);
        await using (var locked = new NpgsqlCommand(Q("SELECT is_locked FROM {schema}.match_teams WHERE match_id=$1 AND team=$2"), c, transaction))
        { locked.Parameters.AddWithValue(request.MatchId); locked.Parameters.AddWithValue(team); if (await locked.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Team is no longer locked."); }
        long consentCandidate, consentFleet;
        await using (var consent = new NpgsqlCommand(Q("""
            SELECT CASE WHEN $2=0 THEN red_captain_id ELSE blue_captain_id END,
                   CASE WHEN $2=0 THEN red_fleet_id ELSE blue_fleet_id END FROM {schema}.matches WHERE id=$1
            """), c, transaction))
        {
            consent.Parameters.AddWithValue(request.MatchId); consent.Parameters.AddWithValue(team);
            await using var row = await consent.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct) || row.IsDBNull(0) || row.IsDBNull(1)) throw new InvalidOperationException("Team fleet consent is required.");
            consentCandidate = row.GetInt64(0); consentFleet = row.GetInt64(1);
        }
        if (await LockCurrentFleetMemberAsync(c, transaction, consentCandidate, ct) != consentFleet)
            throw new InvalidOperationException("Consenting member changed fleets.");
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", c, transaction)) { gate.Parameters.AddWithValue(characterId); await gate.ExecuteNonQueryAsync(ct); }
        var character = await tournamentCharacters.LockAndGetAsync(c, transaction, characterId, ct);
        var ship = await items.LockAndGetAsync(c, transaction, snapshot.Ship.Id, ct);
        var currentContents = await items.LockAllInLocationAsync(c, transaction, snapshot.Ship.Id, characterId, ct);
        if (character is null || character.IsDeleted || character.ShipId != snapshot.Ship.Id || ship != snapshot.Ship || !currentContents.SequenceEqual(contents))
            throw new InvalidOperationException("Pilot ship or fitting changed; retry the addition.");
        int points; int[] prohibited;
        await using (var rules = new NpgsqlCommand(Q("""
            SELECT p.points,r.prohibited_fit_types FROM {schema}.matches m
            JOIN {schema}.tournament_rules r ON r.tournament_id=m.tournament_id
            JOIN {schema}.tournament_ship_points p ON p.tournament_id=m.tournament_id AND p.type_id=$2
            WHERE m.id=$1
            """), c, transaction))
        {
            rules.Parameters.AddWithValue(request.MatchId); rules.Parameters.AddWithValue(ship.TypeId);
            await using var row = await rules.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) throw new InvalidOperationException("Tournament hull points and fit rules are not configured for this ship.");
            points = row.GetInt32(0); prohibited = row.GetFieldValue<int[]>(1);
        }
        var errors = snapshot.Fitted.Concat(snapshot.Charges).Where(x => prohibited.Contains(x.TypeId))
            .Select(x => $"Prohibited fitted type {x.TypeId}").Distinct().Order().ToArray();
        var location = character.StationId > 0 ? character.StationId : character.SolarSystemId;
        await using (var insert = new NpgsqlCommand(Q("""
            WITH member AS (INSERT INTO {schema}.match_members(match_id,team,character_id,locked) VALUES ($1,$2,$3,true) RETURNING character_id)
            INSERT INTO {schema}.match_roster_details SELECT $1,$2,character_id,$4,$5,$6,$7,$8,$9 FROM member
            """), c, transaction))
        {
            insert.Parameters.AddWithValue(request.MatchId); insert.Parameters.AddWithValue(team); insert.Parameters.AddWithValue(characterId);
            insert.Parameters.AddWithValue(ship.TypeId); insert.Parameters.AddWithValue(ship.Id); insert.Parameters.AddWithValue(location);
            insert.Parameters.AddWithValue(points); insert.Parameters.AddWithValue(errors); insert.Parameters.AddWithValue(FitHash(snapshot, contents));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await PublishUpdateAsync(c, transaction, request.MatchId, ct); await transaction.CommitAsync(ct); events.Wake();
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> OverrideRosterErrorAsync(TourneyMatchActionRequest request, CancellationToken ct)
    {
        static long Number(PyValue value) => value switch { PyInt n => n.Value, PyLong n => n.Value, _ => -1 };
        if (request.Arguments.Count != 3 || request.KeywordArguments is not PyNone || Number(request.Arguments[1]) is not > 0 ||
            Eve.Macho.JsSemantics.StringValue(request.Arguments[2]) is not { Length: > 0 } error ||
            tournamentShips is null || tournamentCharacters is null)
            throw new ArgumentException("Invalid tournament error override.");
        var characterId = Number(request.Arguments[1]);
        // Check authorization before loading another pilot's fitting, then repeat under the write lock.
        await using (var connection = await source.OpenConnectionAsync(ct))
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await AuthorizeRosterAsync(connection, tx, request, ct);
            await using var present = new NpgsqlCommand(Q("SELECT EXISTS(SELECT 1 FROM {schema}.match_roster_details WHERE match_id=$1 AND character_id=$2 AND $3=ANY(errors))"), connection, tx);
            present.Parameters.AddWithValue(request.MatchId); present.Parameters.AddWithValue(characterId); present.Parameters.AddWithValue(error);
            if (!(bool)(await present.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("The reported error is no longer present.");
            await tx.CommitAsync(ct);
        }
        var snapshot = await tournamentShips.LoadAsync(characterId, ct) ?? throw new InvalidOperationException("Pilot has no active ship.");
        var contents = await items.InLocationAsync(snapshot.Ship.Id, characterId, ct);
        var hash = FitHash(snapshot, contents);
        await using var c = await source.OpenConnectionAsync(ct); await using var transaction = await c.BeginTransactionAsync(ct);
        await AuthorizeRosterAsync(c, transaction, request, ct);
        long candidate, fleet; int team;
        await using (var roster = new NpgsqlCommand(Q("""
            SELECT d.team, CASE WHEN d.team=0 THEN m.red_captain_id ELSE m.blue_captain_id END,
                   CASE WHEN d.team=0 THEN m.red_fleet_id ELSE m.blue_fleet_id END
            FROM {schema}.match_roster_details d JOIN {schema}.matches m ON m.id=d.match_id
            WHERE d.match_id=$1 AND d.character_id=$2 AND d.fit_hash=$3 AND $4=ANY(d.errors) FOR UPDATE OF d
            """), c, transaction))
        {
            roster.Parameters.AddWithValue(request.MatchId); roster.Parameters.AddWithValue(characterId); roster.Parameters.AddWithValue(hash); roster.Parameters.AddWithValue(error);
            await using var row = await roster.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct) || row.IsDBNull(1) || row.IsDBNull(2))
                throw new InvalidOperationException("Pilot fitting or team consent changed; refresh the roster.");
            team = row.GetInt32(0); candidate = row.GetInt64(1); fleet = row.GetInt64(2);
        }
        if (await LockCurrentFleetMemberAsync(c, transaction, candidate, ct) != fleet)
            throw new InvalidOperationException("Consenting member changed fleets.");
        long consentCandidate, consentFleet;
        await using (var consent = new NpgsqlCommand(Q("""
            SELECT CASE WHEN $2=0 THEN red_captain_id ELSE blue_captain_id END,
                   CASE WHEN $2=0 THEN red_fleet_id ELSE blue_fleet_id END FROM {schema}.matches WHERE id=$1
            """), c, transaction))
        {
            consent.Parameters.AddWithValue(request.MatchId); consent.Parameters.AddWithValue(team);
            await using var row = await consent.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct) || row.IsDBNull(0) || row.IsDBNull(1)) throw new InvalidOperationException("Team fleet consent is required.");
            consentCandidate = row.GetInt64(0); consentFleet = row.GetInt64(1);
        }
        if (await LockCurrentFleetMemberAsync(c, transaction, consentCandidate, ct) != consentFleet)
            throw new InvalidOperationException("Consenting member changed fleets.");
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", c, transaction)) { gate.Parameters.AddWithValue(characterId); await gate.ExecuteNonQueryAsync(ct); }
        var character = await tournamentCharacters.LockAndGetAsync(c, transaction, characterId, ct);
        var ship = await items.LockAndGetAsync(c, transaction, snapshot.Ship.Id, ct);
        var lockedContents = await items.LockAllInLocationAsync(c, transaction, snapshot.Ship.Id, characterId, ct);
        if (character is null || character.ShipId != snapshot.Ship.Id || ship != snapshot.Ship || !lockedContents.SequenceEqual(contents))
            throw new InvalidOperationException("Pilot ship or fitting changed; refresh the roster.");
        await using (var approve = new NpgsqlCommand(Q("INSERT INTO {schema}.match_error_overrides VALUES ($1,$2,$3,$4) ON CONFLICT (match_id,character_id,error) DO UPDATE SET fit_hash=EXCLUDED.fit_hash"), c, transaction))
        { approve.Parameters.AddWithValue(request.MatchId); approve.Parameters.AddWithValue(characterId); approve.Parameters.AddWithValue(error); approve.Parameters.AddWithValue(hash); await approve.ExecuteNonQueryAsync(ct); }
        await using (var update = new NpgsqlCommand(Q("UPDATE {schema}.match_roster_details SET errors=array_remove(errors,$3) WHERE match_id=$1 AND character_id=$2"), c, transaction))
        { update.Parameters.AddWithValue(request.MatchId); update.Parameters.AddWithValue(characterId); update.Parameters.AddWithValue(error); await update.ExecuteNonQueryAsync(ct); }
        await PublishUpdateAsync(c, transaction, request.MatchId, ct); await transaction.CommitAsync(ct); events.Wake();
        _ = team;
        return PyNone.Instance;
    }

    private async Task AuthorizeRosterAsync(NpgsqlConnection c, NpgsqlTransaction tx, TourneyMatchActionRequest request, CancellationToken ct)
    {
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), c, tx)) await gate.ExecuteNonQueryAsync(ct);
        await using var cmd = new NpgsqlCommand(Q("""
            SELECT 1 FROM {schema}.matches m JOIN {schema}.match_referees r ON r.match_id=m.id
            WHERE m.id=$1 AND m.system_id=$2 AND m.state=1 AND NOT m.closing AND r.oid=$3 AND r.session_id=$4 AND r.character_id=$5 FOR UPDATE OF m,r
            """), c, tx);
        cmd.Parameters.AddWithValue(request.MatchId); cmd.Parameters.AddWithValue(request.SystemId); cmd.Parameters.AddWithValue(request.BoundObjectId);
        cmd.Parameters.AddWithValue(request.Session.SessionId); cmd.Parameters.AddWithValue(request.Session.CharacterId ?? 0);
        if (await cmd.ExecuteScalarAsync(ct) is null || request.Session.DockedLocationId is not null ||
            request.Session.GetInt("solarsystemid") != request.SystemId || !GameMasterRoles.Has(request.Session, GameMasterRoles.Staff))
            throw new InvalidOperationException("Referee is not authorized for this match.");
    }
}
