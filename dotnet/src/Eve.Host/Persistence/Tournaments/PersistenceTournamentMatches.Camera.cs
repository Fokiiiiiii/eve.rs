using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    public async ValueTask<PyValue> CameraDetailsAsync(long systemId, long matchId, ClientSession session, bool fancy, CancellationToken ct)
    {
        if (session.CharacterId is not > 0 || space is null || !space.IsInPublicSystem(session, systemId) ||
            session.DockedLocationId is not null || session.GetInt("solarsystemid") != systemId)
            throw new InvalidOperationException("Camera is not authorized for this system.");
        await using var connection = await source.OpenConnectionAsync(ct);
        int state, length, overtime, maximum; long start;
        await using (var match = new NpgsqlCommand(Q("SELECT state,start_ms,match_length,overtime_length,maximum_points FROM {schema}.matches WHERE id=$1 AND system_id=$2 AND NOT closing"), connection))
        {
            match.Parameters.AddWithValue(matchId); match.Parameters.AddWithValue(systemId);
            await using var row = await match.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct) || row.GetInt32(0) < 3 || row.IsDBNull(1))
                throw new InvalidOperationException("The match is not yet visible to cameras.");
            state = row.GetInt32(0); start = row.GetInt64(1); length = row.GetInt32(2); overtime = row.GetInt32(3); maximum = row.GetInt32(4);
        }
        if (!fancy)
        {
            var teams = new[] { new List<PyValue>(), new List<PyValue>() };
            await using var command = new NpgsqlCommand(Q("""
                SELECT d.team,d.character_id,d.ship_type_id,d.ship_id,d.points FROM {schema}.match_roster_details d
                JOIN {schema}.match_members m ON m.match_id=d.match_id AND m.team=d.team AND m.character_id=d.character_id
                WHERE d.match_id=$1 AND m.locked ORDER BY d.team,d.character_id
                """), connection);
            command.Parameters.AddWithValue(matchId); await using var row = await command.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) teams[row.GetInt32(0)].Add(Py.Tuple(new PyLong(row.GetInt64(1)), new PyInt(row.GetInt32(2)), new PyLong(row.GetInt64(3)), new PyInt(row.GetInt32(4))));
            return Py.Tuple(new PyList(teams[0]), new PyList(teams[1]));
        }
        var bans = new[] { new List<PyValue>(), new List<PyValue>() };
        await using (var command = new NpgsqlCommand(Q("SELECT team,type_id FROM {schema}.match_bans WHERE match_id=$1 ORDER BY team,type_id"), connection))
        {
            command.Parameters.AddWithValue(matchId); await using var row = await command.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) bans[row.GetInt32(0)].Add(new PyInt(row.GetInt32(1)));
        }
        var metrics = await CameraMetricsAsync(connection, matchId, checked((int)systemId), start, session, ct);
        return Py.Tuple(metrics[0], metrics[1],
            Py.Tuple(new PyInt(state), new PyLong(Py.FileTimeFromUnixMs(start)), new PyInt(0), new PyInt(length), new PyInt(overtime), new PyInt(maximum)),
            Py.Tuple(new PyList(bans[0]), new PyList(bans[1])));
    }

    public async ValueTask<PyValue> OtherMatchAsync(long systemId, string oid, ClientSession session, CancellationToken ct)
    {
        if (session.CharacterId is not > 0 || session.SessionId <= 0) throw new InvalidOperationException("Invalid referee session.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(Q("""
            SELECT EXISTS(SELECT 1 FROM {schema}.matches m WHERE m.system_id=$1 AND NOT m.closing
                AND NOT EXISTS(SELECT 1 FROM {schema}.match_referees r WHERE r.match_id=m.id AND r.oid=$2 AND r.session_id=$3 AND r.character_id=$4))
            """), connection);
        command.Parameters.AddWithValue(systemId); command.Parameters.AddWithValue(oid);
        command.Parameters.AddWithValue(session.SessionId); command.Parameters.AddWithValue(session.CharacterId.Value);
        return (bool)(await command.ExecuteScalarAsync(ct))! ? PyBool.True : PyBool.False;
    }

    private async Task<int> CameraNebulaAsync(long systemId, CancellationToken ct)
    {
        var systems = await sde.ReadAsync("mapSolarSystems", [systemId], ct);
        if (systems.Count != 1 || !systems[0].Doc.TryGetProperty("regionID", out var regionValue) || !regionValue.TryGetInt64(out var regionId))
            throw new InvalidOperationException("Camera system has no SDE region.");
        var regions = await sde.ReadAsync("mapRegions", [regionId], ct);
        if (regions.Count != 1 || !regions[0].Doc.TryGetProperty("nebulaID", out var nebula) || !nebula.TryGetInt32(out var nebulaId))
            throw new InvalidOperationException("Camera region has no SDE nebula.");
        return nebulaId;
    }

    public async ValueTask<PyValue> ActiveCameraAsync(long systemId, ClientSession session, CancellationToken ct)
    {
        if (session.CharacterId is not > 0 || space is null || !space.IsInPublicSystem(session, systemId) ||
            session.DockedLocationId is not null || session.GetInt("solarsystemid") != systemId)
            throw new InvalidOperationException("Camera is not authorized for this system.");
        long id; string red, blue;
        await using (var connection = await source.OpenConnectionAsync(ct))
        await using (var command = new NpgsqlCommand(Q("""
            SELECT m.id,s.red_team,s.blue_team FROM {schema}.matches m
            JOIN {schema}.series s ON s.tournament_id=m.tournament_id AND s.id=m.series_id
            WHERE m.system_id=$1 AND NOT m.closing
            """), connection))
        {
            command.Parameters.AddWithValue(systemId); await using var row = await command.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) return PyNone.Instance;
            id = row.GetInt64(0); red = row.GetString(1); blue = row.GetString(2);
        }
        var nebulaId = await CameraNebulaAsync(systemId, ct);
        return Py.Dict(("matchID", new PyLong(id)), ("teamOneName", new PyStr(red)),
            ("teamTwoName", new PyStr(blue)), ("nebulaID", new PyInt(nebulaId)));
    }
}
