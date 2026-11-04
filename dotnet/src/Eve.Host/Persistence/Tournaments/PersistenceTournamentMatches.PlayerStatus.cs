using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Persistence;

public sealed partial class PersistenceTournamentMatches
{
    private const string PlayerStatusTopic = "tournament.match.player_status";
    private sealed record ScoreRecipient(long CharacterId, int Team);
    private sealed record PlayerStatus(long MatchId, int State, long StartMs, int Length, int OvertimeLength,
        int RedPoints, int BluePoints, ScoreRecipient[] Recipients);

    private void RegisterPlayerStatusHandler()
    {
        events.Subscribe("tournament.match.player_notifications", [PlayerStatusTopic], (context, _) =>
        {
            var status = context.Event.Payload<PlayerStatus>() ?? throw new InvalidOperationException("Missing tournament player status.");
            context.AfterCommit(_ =>
            {
                var fields = Py.Dict(("matchState", new PyInt(status.State)),
                    ("matchStartTime", new PyLong(Py.FileTimeFromUnixMs(status.StartMs))),
                    ("overtimeLevel", new PyInt(0)), ("matchLength", new PyInt(status.Length)),
                    ("overtimeLength", new PyInt(status.OvertimeLength)));
                foreach (var recipient in status.Recipients)
                    if (sessions!.Of(recipient.CharacterId) is { } session)
                        session.SendNotification("OnTournamentMatchStateChange", "clientID", new PyInt(status.RedPoints),
                            new PyInt(status.BluePoints), new PyInt(recipient.Team), fields);
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });
    }

    private async Task PublishPlayerStatusAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long matchId, CancellationToken ct)
    {
        int state, length, overtime, maximum; long start;
        await using (var match = new NpgsqlCommand(Q("SELECT state,start_ms,match_length,overtime_length,maximum_points FROM {schema}.matches WHERE id=$1"), connection, transaction))
        {
            match.Parameters.AddWithValue(matchId); await using var row = await match.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct) || row.GetInt32(0) < 3 || row.IsDBNull(1)) return;
            state = row.GetInt32(0); start = row.GetInt64(1); length = row.GetInt32(2); overtime = row.GetInt32(3); maximum = row.GetInt32(4);
        }
        var locked = new bool[2];
        await using (var teams = new NpgsqlCommand(Q("SELECT team,is_locked FROM {schema}.match_teams WHERE match_id=$1"), connection, transaction))
        {
            teams.Parameters.AddWithValue(matchId); await using var row = await teams.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) locked[row.GetInt32(0)] = row.GetBoolean(1);
        }
        if (!locked[0] || !locked[1]) return;
        var alivePoints = new long[2]; var allPoints = new long[2]; var recipients = new List<ScoreRecipient>();
        await using (var members = new NpgsqlCommand(Q("""
            SELECT m.team,m.character_id,m.locked,m.eliminated,d.points FROM {schema}.match_members m
            LEFT JOIN {schema}.match_roster_details d ON d.match_id=m.match_id AND d.team=m.team AND d.character_id=m.character_id
            WHERE m.match_id=$1 ORDER BY m.team,m.character_id
            """), connection, transaction))
        {
            members.Parameters.AddWithValue(matchId); await using var row = await members.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct))
            {
                if (!row.GetBoolean(2) || row.IsDBNull(4)) return;
                var team = row.GetInt32(0); var points = row.GetInt32(4);
                allPoints[team] += points;
                if (!row.GetBoolean(3)) alivePoints[team] += points;
                recipients.Add(new ScoreRecipient(row.GetInt64(1), team));
            }
        }
        if (allPoints.Any(points => points > maximum)) throw new InvalidOperationException("A tournament team exceeds the configured match points.");
        // CCP AT victory conditions: opponent's unused points plus points of their eliminated ships.
        var red = checked((int)(maximum - alivePoints[1]));
        var blue = checked((int)(maximum - alivePoints[0]));
        await events.PublishAsync(connection, transaction, PlayerStatusTopic,
            new PlayerStatus(matchId, state, start, length, overtime, red, blue, recipients.ToArray()), cancellationToken: ct);
    }
}
