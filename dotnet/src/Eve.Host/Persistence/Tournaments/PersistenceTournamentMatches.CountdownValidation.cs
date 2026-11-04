using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private sealed class CountdownPreparation(PersistenceTournamentMatches owner, long[] characters, Dictionary<long, RosterSnapshot> snapshots, TournamentPhysicalRegion region) : IAsyncDisposable
    {
        public Dictionary<long, RosterSnapshot> Snapshots { get; } = snapshots;
        public TournamentPhysicalRegion Region { get; } = region;
        public async ValueTask DisposeAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await owner.RefreshTournamentRestrictionsAsync(timeout.Token, characters);
            }
            catch
            {
                owner.RestrictionNeedsRepair(characters);
                throw;
            }
        }
    }

    private async Task<CountdownPreparation?> PrepareCountdownAsync(TourneyMatchActionRequest request, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(Q("""
            SELECT m.state FROM {schema}.matches m JOIN {schema}.match_referees r ON r.match_id=m.id
            WHERE m.id=$1 AND m.system_id=$2 AND r.oid=$3 AND r.session_id=$4 AND r.character_id=$5
            """), c);
        command.Parameters.AddWithValue(request.MatchId); command.Parameters.AddWithValue(request.SystemId);
        command.Parameters.AddWithValue(request.BoundObjectId); command.Parameters.AddWithValue(request.Session.SessionId);
        command.Parameters.AddWithValue(request.Session.CharacterId ?? 0);
        var state = await command.ExecuteScalarAsync(ct);
        if (state is not int value || value != 2) return null;
        if (tournamentShips is null || tournamentCharacters is null || sessions is null || space is null)
            throw new InvalidOperationException("Tournament participant verification is unavailable.");
        var ids = new List<long>();
        await using (var members = new NpgsqlCommand(Q("SELECT character_id FROM {schema}.match_members WHERE match_id=$1 ORDER BY character_id"), c))
        {
            members.Parameters.AddWithValue(request.MatchId); await using var row = await members.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) ids.Add(row.GetInt64(0));
        }
        await c.CloseAsync();
        var characters = ids.ToArray();
        RestrictionFailClosed(characters);
        try
        {
            var region = await ReadPhysicalRegionAsync(checked((int)request.SystemId), ct);
            var snapshots = new Dictionary<long, RosterSnapshot>();
            foreach (var id in ids)
            {
                var ship = await tournamentShips.LoadAsync(id, ct) ?? throw new InvalidOperationException($"Pilot {id} has no active ship.");
                var pilot = sessions.Of(id);
                if (pilot is null || !space.IsInPublicSystem(pilot, request.SystemId) || pilot.GetInt("shipid") != ship.Ship.Id)
                    throw new InvalidOperationException($"Pilot {id} is not in the arena system.");
                var pose = await space.PilotPlaceAsync(pilot, ct);
                if (pose is not { Position: { Count: 3 } position } || pose.Value.SolarSystemId != request.SystemId ||
                    position.Any(x => !double.IsFinite(x)) ||
                    Enumerable.Range(0, 3).Sum(i => Math.Pow((position[i] - region.Center[i]) / region.RadiusMeters, 2)) > 1)
                    throw new InvalidOperationException($"Pilot {id} is outside the configured arena region.");
                snapshots.Add(id, new RosterSnapshot(ship, await items.InLocationAsync(ship.Ship.Id, id, ct)));
            }
            return new CountdownPreparation(this, characters, snapshots, region);
        }
        catch
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await RefreshTournamentRestrictionsAsync(timeout.Token, characters);
            }
            catch { RestrictionNeedsRepair(characters); }
            throw;
        }
    }

    private async Task ValidateCountdownAsync(NpgsqlConnection c, NpgsqlTransaction tx, TourneyMatchActionRequest request,
        CountdownPreparation? preparation, CancellationToken ct)
    {
        var snapshots = preparation?.Snapshots;
        if (snapshots is null || tournamentCharacters is null || sessions is null || space is null)
            throw new InvalidOperationException("Tournament participant verification changed; retry the countdown.");
        var region = preparation!.Region;
        await using (var saved = new NpgsqlCommand(Q("SELECT center_beacon_id,radius_meters FROM {schema}.arena_physical_regions WHERE system_id=$1 FOR UPDATE"), c, tx))
        {
            saved.Parameters.AddWithValue(request.SystemId); await using var row = await saved.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct) || row.GetInt64(0) != region.CenterBeaconId || row.GetDouble(1) != region.RadiusMeters)
                throw new InvalidOperationException("The arena region changed; retry the countdown.");
        }
        var center = await deployed.LockAndGetAsync(c, tx, region.CenterBeaconId, ct);
        if (center is null || center.SolarSystemId != request.SystemId || !center.Position.SequenceEqual(region.Center))
            throw new InvalidOperationException("The arena center changed; retry the countdown.");
        var locked = new bool[2];
        await using (var teams = new NpgsqlCommand(Q("SELECT team,is_locked FROM {schema}.match_teams WHERE match_id=$1 FOR UPDATE"), c, tx))
        {
            teams.Parameters.AddWithValue(request.MatchId); await using var row = await teams.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) locked[row.GetInt32(0)] = row.GetBoolean(1);
        }
        if (!locked[0] || !locked[1]) throw new InvalidOperationException("Both tournament teams must be locked.");
        // Match actions take the same advisory lock; lock saved roster and arrival rows for this decision too.
        foreach (var table in new[] { "match_roster_details", "match_participants" })
        {
            await using var rows = new NpgsqlCommand(Q($"SELECT character_id FROM {{schema}}.{table} WHERE match_id=$1 ORDER BY character_id FOR UPDATE"), c, tx);
            rows.Parameters.AddWithValue(request.MatchId); await using var reader = await rows.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) { }
        }
        var members = new List<(int Team, long Character, long Ship, int Type, int Points, byte[] Hash)>();
        await using (var command = new NpgsqlCommand(Q("""
            SELECT m.team,m.character_id,m.locked,m.eliminated,d.ship_id,d.points,d.errors,d.fit_hash,p.ship_id,p.session_id,p.outbound_complete,d.ship_type_id
            FROM {schema}.match_members m
            LEFT JOIN {schema}.match_roster_details d ON d.match_id=m.match_id AND d.team=m.team AND d.character_id=m.character_id
            LEFT JOIN {schema}.match_participants p ON p.match_id=m.match_id AND p.team=m.team AND p.character_id=m.character_id
            WHERE m.match_id=$1 ORDER BY m.character_id FOR UPDATE OF m
            """), c, tx))
        {
            command.Parameters.AddWithValue(request.MatchId); await using var row = await command.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct))
            {
                if (!row.GetBoolean(2) || row.GetBoolean(3) || row.IsDBNull(4) || row.IsDBNull(8) || row.IsDBNull(10) ||
                    row.GetFieldValue<string[]>(6).Length != 0 || row.GetInt64(4) != row.GetInt64(8) || !row.GetBoolean(10))
                    throw new InvalidOperationException("Each locked pilot needs a valid fit and confirmed arena arrival.");
                members.Add((row.GetInt32(0), row.GetInt64(1), row.GetInt64(4), row.GetInt32(11), row.GetInt32(5), row.GetFieldValue<byte[]>(7)));
            }
        }
        if (!members.Any(x => x.Team == 0) || !members.Any(x => x.Team == 1) ||
            !members.Select(x => x.Character).SequenceEqual(snapshots.Keys.Order()))
            throw new InvalidOperationException("The locked team rosters changed or are empty.");
        await using (var limit = new NpgsqlCommand(Q("SELECT maximum_points FROM {schema}.matches WHERE id=$1"), c, tx))
        {
            limit.Parameters.AddWithValue(request.MatchId); var maximum = (int)(await limit.ExecuteScalarAsync(ct))!;
            if (members.GroupBy(x => x.Team).Any(g => g.Sum(x => (long)x.Points) > maximum))
                throw new InvalidOperationException("A tournament team exceeds the configured match points.");
        }
        await using (var invalidRules = new NpgsqlCommand(Q("""
            SELECT EXISTS(SELECT 1 FROM {schema}.match_roster_details d
                JOIN {schema}.matches m ON m.id=d.match_id
                LEFT JOIN {schema}.tournament_ship_points p ON p.tournament_id=m.tournament_id AND p.type_id=d.ship_type_id
                WHERE d.match_id=$1 AND (p.type_id IS NULL OR p.points<>d.points))
            """), c, tx))
        {
            invalidRules.Parameters.AddWithValue(request.MatchId);
            if ((bool)(await invalidRules.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("Tournament ship points no longer match the configured rules.");
        }
        foreach (var member in members)
        {
            var pilot = sessions.Of(member.Character);
            if (pilot is null || pilot.GetInt("shipid") != member.Ship || !space.IsInPublicSystem(pilot, request.SystemId))
                throw new InvalidOperationException($"Pilot {member.Character} is not in the arena.");
            await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", c, tx))
            { gate.Parameters.AddWithValue(member.Character); await gate.ExecuteNonQueryAsync(ct); }
            var snapshot = snapshots[member.Character];
            var character = await tournamentCharacters.LockAndGetAsync(c, tx, member.Character, ct);
            var ship = await items.LockAndGetAsync(c, tx, member.Ship, ct);
            var contents = await items.LockAllInLocationAsync(c, tx, member.Ship, member.Character, ct);
            if (character is null || character.ShipId != member.Ship || ship is null || ship.TypeId != member.Type || ship != snapshot.Ship.Ship ||
                !contents.SequenceEqual(snapshot.Contents) || !FitHash(snapshot.Ship, contents).SequenceEqual(member.Hash))
                throw new InvalidOperationException($"Pilot {member.Character} changed ship or fitting after the roster was locked.");
        }
    }
}
