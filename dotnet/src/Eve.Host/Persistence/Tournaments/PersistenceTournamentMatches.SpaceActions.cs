using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private sealed record TournamentReturn(long CharacterId, long ShipId, int SystemId, long DockId, Guid OperationId, long CurrentShipId, long? DeathHomeId);
    private static long TournamentNumber(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, _ => -1 };

    public async ValueTask<PyValue> SpaceActionAsync(TourneyMatchActionRequest request, CancellationToken ct)
    {
        if (space is null || sessions is null) throw new InvalidOperationException("Tournament space is unavailable.");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand(Q("SELECT pg_advisory_xact_lock(hashtext('{schema}.release'))"), c, tx))
            await gate.ExecuteNonQueryAsync(ct);
        await using (var auth = new NpgsqlCommand(Q("""
            SELECT 1 FROM {schema}.matches m JOIN {schema}.match_referees r ON r.match_id=m.id
            WHERE m.id=$1 AND m.system_id=$2 AND NOT m.closing AND r.oid=$3 AND r.session_id=$4 AND r.character_id=$5 FOR UPDATE OF m,r
            """), c, tx))
        {
            auth.Parameters.AddWithValue(request.MatchId); auth.Parameters.AddWithValue(request.SystemId);
            auth.Parameters.AddWithValue(request.BoundObjectId); auth.Parameters.AddWithValue(request.Session.SessionId);
            auth.Parameters.AddWithValue(request.Session.CharacterId ?? 0);
            if (await auth.ExecuteScalarAsync(ct) is null || request.Session.DockedLocationId is not null ||
                request.Session.GetInt("solarsystemid") != request.SystemId)
                throw new InvalidOperationException("Referee is not authorized for this match.");
        }
        if (request.Operation == TourneyMatchAction.CleanupGrid)
        {
            if (request.Arguments.Count != 1) throw new ArgumentException("Cleanup accepts only the match ID.");
            await using (var state = new NpgsqlCommand(Q("SELECT state,cleanup_required,postgame_pending FROM {schema}.matches WHERE id=$1"), c, tx))
            {
                state.Parameters.AddWithValue(request.MatchId);
                await using var row = await state.ExecuteReaderAsync(ct);
                if (!await row.ReadAsync(ct) || row.GetInt32(0) != 6)
                    throw new InvalidOperationException("The tournament match is not complete.");
                if (row.GetBoolean(1) && !row.GetBoolean(2))
                    throw new InvalidOperationException("Tournament cleanup has no durable recovery job.");
            }
            await tx.CommitAsync(ct);
            // The client treats a successful return as completed cleanup. Each worker
            // invocation claims and confirms at most one target without holding DB locks
            // across simulator calls; the existing postgame job remains the retry owner.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            for (var attempt = 0; attempt < 256; attempt++)
            {
                var result = await CleanupCompletedGridStepAsync(request.MatchId, deadline.Token);
                if (result == CleanupProgress.Completed) return PyNone.Instance;
                if (result == CleanupProgress.Blocked) break;
            }
            throw new InvalidOperationException("Tournament cleanup remains pending; retry later.");
        }
        if (request.Operation is TourneyMatchAction.OverrideMoveRestrict or TourneyMatchAction.OverrideWarpRestrict or TourneyMatchAction.OverrideLockRestrict)
        {
            if (request.Arguments.Count != 2) throw new ArgumentException("A restriction value is required.");
            var restricted = request.Arguments[1] switch { PyBool b => b.Value, PyInt i when i.Value is 0 or 1 => i.Value == 1, _ => throw new ArgumentException("Invalid restriction value.") };
            var column = request.Operation switch { TourneyMatchAction.OverrideMoveRestrict => "restrict_move", TourneyMatchAction.OverrideWarpRestrict => "restrict_warp", _ => "restrict_lock" };
            var affected = new List<long>();
            await using (var members = new NpgsqlCommand(Q("SELECT character_id FROM {schema}.match_participants WHERE match_id=$1"), c, tx))
            {
                members.Parameters.AddWithValue(request.MatchId);
                await using var rows = await members.ExecuteReaderAsync(ct);
                while (await rows.ReadAsync(ct)) affected.Add(rows.GetInt64(0));
            }
            RestrictionFailClosed(affected);
            try
            {
                await using var update = new NpgsqlCommand(Q($"UPDATE {{schema}}.matches SET {column}=$2 WHERE id=$1"), c, tx);
                update.Parameters.AddWithValue(request.MatchId); update.Parameters.AddWithValue(restricted);
                await update.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
                await RefreshTournamentRestrictionsAsync(ct, affected);
            }
            catch { RestrictionNeedsRepair(affected); throw; }
            return PyNone.Instance;
        }
        if (request.Operation == TourneyMatchAction.TeleportPlayers)
        {
            if (request.Arguments.Count != 3) throw new ArgumentException("Team and beacon are required.");
            var team = TournamentNumber(request.Arguments[1]); var index = TournamentNumber(request.Arguments[2]);
            if (team is < 0 or > 1 || index < 0) throw new ArgumentException("Invalid team or beacon.");
            // Participants cannot enter an arena whose physical cleanup region is unknown.
            var region = await ReadPhysicalRegionAsync(checked((int)request.SystemId), ct);
            var arena = (await arenas.ReadSystemsAsync(ct)).SingleOrDefault(x => x.SolarSystemId == request.SystemId)
                ?? throw new InvalidOperationException("Tournament arena is unavailable.");
            if (index >= arena.BeaconIds.Count) throw new ArgumentException("Invalid beacon.");
            var beacon = await deployed.GetAsync(arena.BeaconIds[(int)index], ct)
                ?? throw new InvalidOperationException("Tournament beacon is unavailable.");
            if (beacon.SolarSystemId != request.SystemId || beacon.Position.Length != 3 || !beacon.Position.All(double.IsFinite))
                throw new InvalidOperationException("Tournament beacon is invalid.");
            var x = (beacon.Position[0] - region.Center[0]) / region.RadiusMeters;
            var y = (beacon.Position[1] - region.Center[1]) / region.RadiusMeters;
            var z = (beacon.Position[2] - region.Center[2]) / region.RadiusMeters;
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) || x * x + y * y + z * z > 1)
                throw new InvalidOperationException("The selected beacon lies outside the configured arena region.");
            var members = new List<long>();
            await using (var cmd = new NpgsqlCommand(Q("""
                SELECT mm.character_id FROM {schema}.match_members mm
                JOIN {schema}.match_teams t ON t.match_id=mm.match_id AND t.team=mm.team
                WHERE mm.match_id=$1 AND mm.team=$2 AND mm.locked AND t.is_locked ORDER BY mm.character_id FOR UPDATE OF mm,t
                """), c, tx))
            {
                cmd.Parameters.AddWithValue(request.MatchId); cmd.Parameters.AddWithValue((int)team);
                await using var rows = await cmd.ExecuteReaderAsync(ct);
                while (await rows.ReadAsync(ct)) members.Add(rows.GetInt64(0));
            }
            if (members.Count == 0) throw new InvalidOperationException("The team has no locked members.");
            var pilots = new List<(ClientSession Session, Guid OperationId, bool Complete, long? Station, long? Structure, long OriginSystem)>();
            foreach (var character in members)
            {
                var pilot = sessions.Of(character) ?? throw new InvalidOperationException("A team member is offline.");
                var station = pilot.GetInt("stationid"); var structure = pilot.GetInt("structureid");
                var system = pilot.GetInt("solarsystemid2"); var ship = pilot.GetInt("shipid");
                if (pilot.CharacterId != character || ship is not > 0 || system is not > 0)
                    throw new InvalidOperationException("A team member has no active ship or system.");
                await using var prior = new NpgsqlCommand(Q("""
                    SELECT team,ship_id,destination_beacon_id,outbound_operation_id,outbound_complete,
                           original_station_id,original_structure_id,original_system_id
                    FROM {schema}.match_participants WHERE match_id=$1 AND character_id=$2 FOR UPDATE
                    """), c, tx);
                prior.Parameters.AddWithValue(request.MatchId); prior.Parameters.AddWithValue(character);
                Guid? previousOperation = null; bool complete = false; long? originStation = null; long? originStructure = null; long originSystem = 0;
                await using (var row = await prior.ExecuteReaderAsync(ct))
                {
                    if (await row.ReadAsync(ct))
                    {
                        if (row.GetInt32(0) != team || row.GetInt64(1) != ship.Value || row.IsDBNull(2) || row.GetInt64(2) != beacon.DeployableId)
                            throw new InvalidOperationException("The participant is already recorded for another move.");
                        previousOperation = row.GetGuid(3); complete = row.GetBoolean(4);
                        originStation = row.IsDBNull(5) ? null : row.GetInt64(5);
                        originStructure = row.IsDBNull(6) ? null : row.GetInt64(6); originSystem = row.GetInt64(7);
                    }
                }
                if (previousOperation is { } existing)
                {
                    pilots.Add((pilot, existing, complete, originStation, originStructure, originSystem));
                    continue;
                }
                if (station is not > 0 && structure is not > 0)
                    throw new InvalidOperationException("Every new team member must be docked in an active ship.");
                await using var journal = new NpgsqlCommand(Q("""
                    INSERT INTO {schema}.match_participants(match_id,team,character_id,ship_id,session_id,original_system_id,original_station_id,original_structure_id,destination_beacon_id)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) RETURNING outbound_operation_id
                    """), c, tx);
                journal.Parameters.AddWithValue(request.MatchId); journal.Parameters.AddWithValue((int)team); journal.Parameters.AddWithValue(character);
                journal.Parameters.AddWithValue(ship.Value); journal.Parameters.AddWithValue(pilot.SessionId); journal.Parameters.AddWithValue(system.Value);
                journal.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = (object?)station ?? DBNull.Value });
                journal.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = (object?)structure ?? DBNull.Value });
                journal.Parameters.AddWithValue(beacon.DeployableId);
                var operationId = (Guid)(await journal.ExecuteScalarAsync(ct))!;
                pilots.Add((pilot, operationId, false, station, structure, system.Value));
            }
            RestrictionFailClosed(members);
            try
            {
                await tx.CommitAsync(ct);
                await RefreshTournamentRestrictionsAsync(ct, members);
            }
            catch { RestrictionNeedsRepair(members); throw; }
            if (scheduler is null) throw new InvalidOperationException("Tournament movement requires the scheduler.");
            foreach (var entry in pilots)
            {
                var pilot = entry.Session;
                if (!ReferenceEquals(sessions.Of(pilot.CharacterId!.Value), pilot))
                    throw new InvalidOperationException("A participant session changed; retry the team.");
                if (entry.Complete) continue;
                var token = Guid.NewGuid();
                await using (var claim = new NpgsqlCommand(Q("""
                    UPDATE {schema}.match_participants SET return_lease=$3,return_lease_until_ms=$4
                    WHERE match_id=$1 AND character_id=$2 AND NOT outbound_complete
                      AND (return_lease_until_ms IS NULL OR return_lease_until_ms <= $5)
                    """), c))
                {
                    claim.Parameters.AddWithValue(request.MatchId); claim.Parameters.AddWithValue(pilot.CharacterId.Value);
                    claim.Parameters.AddWithValue(token); claim.Parameters.AddWithValue(scheduler.Now + 30000); claim.Parameters.AddWithValue(scheduler.Now);
                    if (await claim.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("A participant move is already being processed.");
                }
                var stationNow = pilot.GetInt("stationid"); var structureNow = pilot.GetInt("structureid");
                var acknowledgedHere = false;
                if (stationNow is > 0 || structureNow is > 0)
                {
                    if (stationNow != entry.Station || structureNow != entry.Structure || pilot.GetInt("solarsystemid2") != entry.OriginSystem)
                        throw new InvalidOperationException("A participant is no longer at the recorded departure dock.");
                    var refusal = await space.RelocateAsync(pilot, checked((int)request.SystemId), beacon.Position, [-1d, 0d, 0d], ct, entry.OperationId.ToString("D"));
                    if (refusal is not null) throw new InvalidOperationException(refusal);
                    // The acknowledged SimEnter used this journal's exact beacon position.
                    acknowledgedHere = true;
                }
                var place = await space.PilotPlaceAsync(pilot, ct);
                if (place is null || place.Value.SolarSystemId != request.SystemId || place.Value.Position is not { Count: 3 } position ||
                    position.Any(value => !double.IsFinite(value)))
                    throw new InvalidOperationException("A participant move has an ambiguous result; return remains pending.");
                if (acknowledgedHere)
                {
                    // The simulator may resolve a ship collision after accepting an exact
                    // entry. The operator's arena boundary is the allowed physical region.
                    var px = (position[0] - region.Center[0]) / region.RadiusMeters;
                    var py = (position[1] - region.Center[1]) / region.RadiusMeters;
                    var pz = (position[2] - region.Center[2]) / region.RadiusMeters;
                    if (!double.IsFinite(px) || !double.IsFinite(py) || !double.IsFinite(pz) || px * px + py * py + pz * pz > 1)
                        throw new InvalidOperationException("A participant arrived outside the configured arena; return remains pending.");
                }
                else if (!position.SequenceEqual(beacon.Position))
                {
                    // A lost docked-entry reply has no durable sim receipt. Proximity to
                    // the arena alone cannot prove that this operation placed the ship.
                    throw new InvalidOperationException("A prior participant move has an ambiguous result; return remains pending.");
                }
                await using var complete = new NpgsqlCommand(Q("""
                    UPDATE {schema}.match_participants SET outbound_complete=true,return_lease=NULL,return_lease_until_ms=NULL
                    WHERE match_id=$1 AND character_id=$2 AND outbound_operation_id=$3 AND return_lease=$4
                    """), c);
                complete.Parameters.AddWithValue(request.MatchId); complete.Parameters.AddWithValue(pilot.CharacterId.Value);
                complete.Parameters.AddWithValue(entry.OperationId); complete.Parameters.AddWithValue(token);
                if (await complete.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Participant move acknowledgement is pending.");
            }
            return PyNone.Instance;
        }
        if (request.Operation is TourneyMatchAction.ReturnTeamPlayers or TourneyMatchAction.ReturnAllPlayers)
        {
            long? team = null;
            if (request.Operation == TourneyMatchAction.ReturnTeamPlayers)
            {
                if (request.Arguments.Count != 2 || TournamentNumber(request.Arguments[1]) is < 0 or > 1)
                    throw new ArgumentException("Invalid team.");
                team = TournamentNumber(request.Arguments[1]);
            }
            var pending = new List<TournamentReturn>();
            await using (var cmd = new NpgsqlCommand(Q("""
                SELECT character_id,ship_id,original_system_id,COALESCE(original_station_id,original_structure_id),return_operation_id,COALESCE(return_ship_id,ship_id),death_home_id
                FROM {schema}.match_participants WHERE match_id=$1 AND ($2::integer IS NULL OR team=$2) ORDER BY character_id FOR UPDATE
                """), c, tx))
            {
                cmd.Parameters.AddWithValue(request.MatchId);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = team is null ? DBNull.Value : (int)team.Value });
                await using var rows = await cmd.ExecuteReaderAsync(ct);
                while (await rows.ReadAsync(ct))
                {
                    if (rows.IsDBNull(3)) throw new InvalidOperationException("Participant has no recorded dock.");
                    pending.Add(new TournamentReturn(rows.GetInt64(0), rows.GetInt64(1), checked((int)rows.GetInt64(2)), rows.GetInt64(3), rows.GetGuid(4), rows.GetInt64(5), rows.IsDBNull(6) ? null : rows.GetInt64(6)));
                }
            }
            await tx.CommitAsync(ct);
            foreach (var record in pending)
            {
                if (scheduler is null) throw new InvalidOperationException("Tournament return requires the scheduler.");
                var pilot = sessions.Of(record.CharacterId);
                if (pilot is null && record.DeathHomeId is null) throw new InvalidOperationException("A participant is offline; return remains pending.");
                var token = Guid.NewGuid();
                await using (var claim = new NpgsqlCommand(Q("""
                    UPDATE {schema}.match_participants SET return_lease=$4,return_lease_until_ms=$5
                    WHERE match_id=$1 AND character_id=$2 AND ship_id=$3
                      AND (return_lease_until_ms IS NULL OR return_lease_until_ms <= $6)
                    """), c))
                {
                    claim.Parameters.AddWithValue(request.MatchId); claim.Parameters.AddWithValue(record.CharacterId);
                    claim.Parameters.AddWithValue(record.ShipId); claim.Parameters.AddWithValue(token);
                    claim.Parameters.AddWithValue(scheduler.Now + 30000); claim.Parameters.AddWithValue(scheduler.Now);
                    if (await claim.ExecuteNonQueryAsync(ct) != 1)
                        throw new InvalidOperationException("A participant return is already being processed.");
                }
                if (record.DeathHomeId is not null)
                {
                    if (!await IsProvenDeathHomeAsync(record, ct)) throw new InvalidOperationException("The participant clone home is not confirmed.");
                }
                else
                {
                    var refusal = await space.ReturnTournamentPilotToDockAsync(pilot!, record.SystemId, record.DockId, record.CurrentShipId, ct, record.OperationId.ToString("D"));
                    if (refusal is not null) throw new InvalidOperationException(refusal);
                }
                await using var clear = new NpgsqlCommand(Q("DELETE FROM {schema}.match_participants WHERE match_id=$1 AND character_id=$2 AND ship_id=$3 AND return_lease=$4 AND COALESCE(return_ship_id,ship_id)=$5 AND return_operation_id=$6"), c);
                clear.Parameters.AddWithValue(request.MatchId); clear.Parameters.AddWithValue(record.CharacterId); clear.Parameters.AddWithValue(record.ShipId); clear.Parameters.AddWithValue(token);
                clear.Parameters.AddWithValue(record.CurrentShipId); clear.Parameters.AddWithValue(record.OperationId);
                RestrictionFailClosed([record.CharacterId]);
                try
                {
                    if (await clear.ExecuteNonQueryAsync(ct) != 1)
                        throw new InvalidOperationException("Participant return changed while being acknowledged.");
                    await RefreshTournamentRestrictionsAsync(ct, [record.CharacterId]);
                }
                catch { RestrictionNeedsRepair([record.CharacterId]); throw; }
            }
            return PyNone.Instance;
        }
        throw new InvalidOperationException("This tournament space action is unavailable.");
    }
}
