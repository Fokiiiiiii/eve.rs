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

public sealed class TournamentFleetConsentTests
{
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    [PgFact]
    public async Task Nomination_requires_actual_session_and_fleet_membership_and_retries_only_same_request()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var schema = "tf_" + suffix; var fleet = "ff_" + suffix; var sched = "fs_" + suffix; var evt = "fe_" + suffix;
        try
        {
            await DurableScheduler.MigrateAsync(source, sched); await EventBus.MigrateAsync(source, evt);
            await using (var seed = source.CreateCommand($"""
                CREATE SCHEMA {schema}; CREATE SCHEMA {fleet};
                CREATE TABLE {schema}.tournaments (id bigint PRIMARY KEY);
                CREATE TABLE {schema}.series (tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id), id bigint NOT NULL, PRIMARY KEY(tournament_id,id));
                INSERT INTO {schema}.tournaments VALUES (1); INSERT INTO {schema}.series VALUES (1,0);
                CREATE TABLE {fleet}.fleets (fleet_id bigint PRIMARY KEY);
                CREATE TABLE {fleet}.fleet_members (fleet_id bigint NOT NULL REFERENCES {fleet}.fleets(fleet_id), character_id bigint PRIMARY KEY, job smallint NOT NULL);
                INSERT INTO {fleet}.fleets VALUES (10),(20); INSERT INTO {fleet}.fleet_members VALUES (10,201,0),(20,202,0);
                """)) await seed.ExecuteNonQueryAsync();
            await PersistenceTournamentMatches.MigrateAsync(source, schema);
            await PersistenceTournamentMatches.MigrateFleetAsync(source, schema);
            await PersistenceTournamentMatches.MigrateSpaceAsync(source, schema);
            await PersistenceTournamentMatches.MigrateRosterAsync(source, schema);
            await using (var seed = source.CreateCommand($"""
                INSERT INTO {schema}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length,state) VALUES (1,0,30000142,100,600,0,1);
                INSERT INTO {schema}.match_referees VALUES ('N=1:1',1,10,100);
                """)) await seed.ExecuteNonQueryAsync();
            await using var scheduler = new DurableScheduler(source, () => 1_700_000_000_000, schema: sched);
            var events = new EventBus(source, scheduler, schema: evt); var sessions = new CharacterSessions();
            var store = new PersistenceTournamentMatches(source, null!, null!, null!, events, null, null!, schema, scheduler, sessions, fleet);
            store.RegisterFleetHandlers();
            await events.PumpAsync();
            var referee = new ClientSession { SessionId = 10, CharacterId = 100 };
            referee.Set("accountRole", new PyLong(GameMasterRoles.Staff)); referee.Set("solarsystemid", new PyInt(30000142));
            var sink = new Sink(); var member = new ClientSession { SessionId = 21, CharacterId = 201, Sink = sink }; sessions.Enter(201, member);
            var other = new ClientSession { SessionId = 22, CharacterId = 202 }; sessions.Enter(202, other);
            var request = new TourneyFleetNominationRequest(1, 0, 201, 30000142, "N=1:1", referee, 50);
            await store.NominateFleetAsync(request, CancellationToken.None);
            await store.NominateFleetAsync(request, CancellationToken.None);
            await events.PumpAsync();
            var packet = Assert.IsType<PyObject>(Assert.Single(sink.Packets));
            var packetState = Assert.IsType<PyTuple>(packet.Args).Items;
            var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packetState[2]).Args).Items;
            Assert.Equal("OnChangeFleetBoss", Assert.IsType<PyStr>(address[1]).Value);
            var body = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(packetState[4]).Items[0]).Items[1]);
            var inner = Assert.IsType<PyTuple>(MarshalCodec.Decode(body.Value.ToArray()));
            var args = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(inner.Items[1]).Items[1]).Items;
            Assert.Equal(new PyLong(100), args[0]); Assert.Equal(new PyInt(MachoMessage.ProxyNodeId), args[1]);
            await store.NominateFleetAsync(request with { CallId = 51 }, CancellationToken.None);
            await events.PumpAsync(); Assert.Equal(2, sink.Packets.Count);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.NominateFleetAsync(request with { CandidateId = 202, CallId = 52 }, CancellationToken.None));
            var impostor = new ClientSession { SessionId = 99, CharacterId = 201 };
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AcceptFleetAsync(impostor, CancellationToken.None));
            await using (var change = source.CreateCommand($"UPDATE {fleet}.fleet_members SET fleet_id=20 WHERE character_id=201")) await change.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AcceptFleetAsync(member, CancellationToken.None));
            await using (var change = source.CreateCommand($"UPDATE {fleet}.fleet_members SET fleet_id=10 WHERE character_id=201")) await change.ExecuteNonQueryAsync();
            await store.OnCharacterLeavingAsync(member, 201, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AcceptFleetAsync(member, CancellationToken.None));
            await store.NominateFleetAsync(request with { CallId = 53 }, CancellationToken.None);
            await store.AcceptFleetAsync(member, CancellationToken.None);
            await using var check = source.CreateCommand($"SELECT red_captain_id,red_fleet_id FROM {schema}.matches WHERE id=1");
            await using var row = await check.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal(201, row.GetInt64(0)); Assert.Equal(10, row.GetInt64(1));
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema},{fleet},{sched},{evt} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
