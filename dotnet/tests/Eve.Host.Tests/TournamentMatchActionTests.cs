using Eve.Data;
using Eve.Events;
using Eve.Host.Persistence;
using Eve.Marshal;
using Eve.Macho;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class TournamentMatchActionTests
{
    [PgFact]
    public async Task Bans_and_countdown_are_transactional_authorized_and_retry_safe()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var schema = "ta_" + suffix; var schedSchema = "ts_" + suffix; var eventSchema = "te_" + suffix;
        long now = 1_700_000_000_000;
        try
        {
            await DurableScheduler.MigrateAsync(source, schedSchema);
            await EventBus.MigrateAsync(source, eventSchema);
            await using (var seed = source.CreateCommand($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.tournaments (id bigint PRIMARY KEY, ship_types integer[] NOT NULL);
                CREATE TABLE {schema}.series (tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id), id bigint NOT NULL, red_team text NOT NULL DEFAULT 'Red', blue_team text NOT NULL DEFAULT 'Blue', PRIMARY KEY(tournament_id,id));
                INSERT INTO {schema}.tournaments VALUES (1, ARRAY[601,602]);
                INSERT INTO {schema}.series(tournament_id,id) VALUES (1,1);
                """)) await seed.ExecuteNonQueryAsync();
            await PersistenceTournamentMatches.MigrateAsync(source, schema);
            await PersistenceTournamentMatches.MigrateSpaceAsync(source, schema);
            await PersistenceTournamentMatches.MigratePhysicalRegionsAsync(source, schema);
            await PersistenceTournamentMatches.MigrateCleanupGuardsAsync(source, schema);
            await PersistenceTournamentMatches.MigrateRefereeTravelAsync(source, schema);
            await PersistenceTournamentMatches.MigratePostgameAsync(source, schema);
            await PersistenceTournamentMatches.MigrateCompletionPolicyAsync(source, schema); await PersistenceTournamentMatches.MigrateCutoffIntentAsync(source, schema);
            await PersistenceTournamentMatches.MigrateRosterAsync(source, schema);
            await PersistenceTournamentMatches.MigrateParticipantDeathsAsync(source, schema);
            await PersistenceTournamentMatches.MigrateAutobansAsync(source, schema);
            await using (var seed = source.CreateCommand($"""
                INSERT INTO {schema}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length)
                VALUES (1,1,30000142,100,600,0);
                INSERT INTO {schema}.match_referees SELECT 'N=1:1',id,10,100 FROM {schema}.matches;
                """)) await seed.ExecuteNonQueryAsync();
            await using var scheduler = new DurableScheduler(source, () => now, schema: schedSchema);
            var events = new EventBus(source, scheduler, schema: eventSchema);
            var sessions = new CharacterSessions();
            var store = new PersistenceTournamentMatches(source, null!, null!, null!, events, null, new Eve.Sde.SdeReader(source), schema, scheduler, sessions);
            store.RegisterActionHandlers();
            var sink = new RecordingSink();
            var session = new ClientSession { SessionId = 10, CharacterId = 100, UserId = 1, Sink = sink };
            sessions.Enter(100, session);
            await events.PumpAsync();
            session.Set("solarsystemid", new PyInt(30000142));
            TourneyMatchActionRequest Request(TourneyMatchAction action, params PyValue[] tail) =>
                new(action, 1, [new PyInt(1), .. tail], PyNone.Instance, 30000142, "N=1:1", session);
            async Task<PyValue> Run(TourneyMatchAction action, params PyValue[] tail) =>
                await store.ActionAsync(Request(action, tail), CancellationToken.None);
            async Task<long> Scalar(string sql)
            { await using var command = source.CreateCommand(sql); return Convert.ToInt64(await command.ExecuteScalarAsync()); }

            await Run(TourneyMatchAction.BanShipType, new PyInt(601), new PyInt(0));
            await Run(TourneyMatchAction.BanShipType, new PyInt(601), new PyInt(0));
            Assert.Equal(1, await Scalar($"SELECT count(*) FROM {schema}.match_bans"));
            await Assert.ThrowsAsync<ArgumentException>(async () => await Run(TourneyMatchAction.BanShipType, new PyInt(999), new PyInt(0)));
            var wrong = new ClientSession { SessionId = 11, CharacterId = 100 }; wrong.Set("solarsystemid", new PyInt(30000142));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.ActionAsync(Request(TourneyMatchAction.UnBanShipType, new PyInt(601), new PyInt(0)) with { Session = wrong }, CancellationToken.None));
            await Run(TourneyMatchAction.UnBanShipType, new PyInt(601), new PyInt(0));
            Assert.Equal(0, await Scalar($"SELECT count(*) FROM {schema}.match_bans"));
            var redSink = new RecordingSink(); var blueSink = new RecordingSink();
            var red = new ClientSession { SessionId = 21, CharacterId = 201, UserId = 2, Sink = redSink };
            var blue = new ClientSession { SessionId = 22, CharacterId = 202, UserId = 3, Sink = blueSink };
            sessions.Enter(201, red); sessions.Enter(202, blue);
            var autoban = new TourneyMatchActionRequest(TourneyMatchAction.StartAutobanning, 1, [new PyInt(1),new PyInt(1)],
                Py.Dict(("redCharID",new PyInt(201)),("blueCharID",new PyInt(202)),("totalNumBans",new PyInt(1))), 30000142, "N=1:1", session);
            await store.ActionAsync(autoban, CancellationToken.None);
            await store.ActionAsync(autoban, CancellationToken.None);
            Assert.Equal(1, await Scalar($"SELECT count(*) FROM {schema}.autoban_rounds"));
            var redBan = await Scalar($"SELECT id FROM {schema}.autoban_choices WHERE team=0");
            var blueBan = await Scalar($"SELECT id FROM {schema}.autoban_choices WHERE team=1");
            await events.PumpAsync();
            var prompt = Assert.Single(Notifications(redSink), x => x.Name == "OnTournamentPerformBan");
            Assert.Equal(new PyLong(redBan), prompt.Args[0]); Assert.Equal(new PyInt(1), prompt.Args[1]);
            Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(now + 60000)), prompt.Args[3]);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SubmitBanAsync(new TourneyBanSubmitRequest(redBan, [new PyInt(601)], blue), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await Run(TourneyMatchAction.FinalizeBans));
            await Assert.ThrowsAsync<ArgumentException>(async () => await store.SubmitBanAsync(new TourneyBanSubmitRequest(redBan, [new PyInt(999)], red), CancellationToken.None));
            await store.SubmitBanAsync(new TourneyBanSubmitRequest(redBan, [new PyInt(601)], red), CancellationToken.None);
            await store.SubmitBanAsync(new TourneyBanSubmitRequest(redBan, [new PyInt(601)], red), CancellationToken.None);
            var oldBlue = blue; var newBlueSink = new RecordingSink();
            blue = new ClientSession { SessionId = 23, CharacterId = 202, UserId = 3, Sink = newBlueSink };
            sessions.Enter(202, blue); now += 5000;
            await store.ActionAsync(autoban, CancellationToken.None);
            Assert.Equal(1, await Scalar($"SELECT count(*) FROM {schema}.autoban_rounds"));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SubmitBanAsync(new TourneyBanSubmitRequest(blueBan, [], oldBlue), CancellationToken.None));
            await events.PumpAsync();
            var resumed = Assert.Single(Notifications(newBlueSink), x => x.Name == "OnTournamentPerformBan");
            Assert.Equal(new PyLong(blueBan), resumed.Args[0]);
            Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(now + 55000)), resumed.Args[3]);
            await store.SubmitBanAsync(new TourneyBanSubmitRequest(blueBan, [], blue), CancellationToken.None);
            await events.PumpAsync();
            Assert.Contains(Notifications(newBlueSink), x => x.Name == "OnTournamentPerformBan" && x.Args[1] is PyInt { Value: 0 });
            await store.SubmitBanAsync(new TourneyBanSubmitRequest(redBan, [], red), CancellationToken.None);
            Assert.Equal(1, await Scalar($"SELECT count(*) FROM {schema}.match_bans WHERE type_id=601"));
            await Run(TourneyMatchAction.UnBanShipType, new PyInt(601), new PyInt(0));
            await store.ActionAsync(autoban, CancellationToken.None);
            now += 60000;
            await scheduler.RunDueAsync();
            Assert.Equal(0, await Scalar($"SELECT count(*) FROM {schema}.autoban_rounds WHERE NOT finished"));
            await Run(TourneyMatchAction.FinalizeBans);
            await Run(TourneyMatchAction.CompletePregameChecks);
            // With no real space/ship provider or arrived participants, countdown must not start.
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await Run(TourneyMatchAction.StartCountdown));
            Assert.Equal(2, await Scalar($"SELECT state FROM {schema}.matches WHERE id=1"));
            await using (var seed = source.CreateCommand($"""
                INSERT INTO {schema}.series(tournament_id,id) VALUES (1,2);
                INSERT INTO {schema}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length,state)
                VALUES (1,2,30000143,100,600,0,1);
                INSERT INTO {schema}.match_referees SELECT 'N=1:2',id,10,100 FROM {schema}.matches WHERE system_id=30000143;
                """)) await seed.ExecuteNonQueryAsync();
            var oldId = await Scalar($"SELECT id FROM {schema}.matches WHERE system_id=30000143");
            session.Set("solarsystemid", new PyInt(30000143));
            await store.ActionAsync(new TourneyMatchActionRequest(TourneyMatchAction.ResetMatch, oldId, [new PyLong(oldId)], PyNone.Instance, 30000143, "N=1:2", session), CancellationToken.None);
            Assert.Equal(0, await Scalar($"SELECT count(*) FROM {schema}.matches WHERE id={oldId}"));
            await events.PumpAsync();
            var updates = Notifications(sink).Where(x => x.Name == "ProcessTournamentMatchUpdate").Select(x => Assert.IsType<PyDict>(Assert.Single(x.Args))).ToArray();
            Assert.NotEmpty(updates);
            var closed = Assert.Single(updates, x => x.Entries.Any(kv => Key(kv.Key) == "state" && kv.Value is PyInt { Value: 7 }));
            Assert.Contains(closed.Entries, kv => Key(kv.Key) == "matchID" && kv.Value is PyLong value && value.Value == oldId);
            Assert.Contains(closed.Entries, kv => Key(kv.Key) == "solarsystemID" && kv.Value is PyLong value && value.Value == 30000143);
            await using (var seed = source.CreateCommand($"""
                INSERT INTO {schema}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length)
                VALUES (1,2,30000143,100,600,0);
                """)) await seed.ExecuteNonQueryAsync();
            var replacement = await Scalar($"SELECT id FROM {schema}.matches WHERE system_id=30000143");
            Assert.NotEqual(oldId, replacement);
            await scheduler.ScheduleAsync(new JobSpec("tournament.match.countdown", now) { Key = $"match:{oldId}", Payload = new { matchId = oldId, startMs = now } });
            await scheduler.RunDueAsync();
            Assert.Equal(0, await Scalar($"SELECT state FROM {schema}.matches WHERE id={replacement}"));
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema},{schedSchema},{eventSchema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static string? Key(PyValue value) => value switch { PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span), PyStr text => text.Value, _ => null };

    private sealed class RecordingSink : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }

    private static IEnumerable<(string Name, PyValue[] Args)> Notifications(RecordingSink sink)
    {
        foreach (var packet in sink.Sent.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification }) continue;
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
            var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
            var args = ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items;
            yield return (((PyStr)address[1]).Value, [.. args]);
        }
    }
}
