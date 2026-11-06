using Eve.Data;
using Eve.Events;
using Eve.Host.Persistence;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class TournamentCompletionTests
{
    [PgFact]
    public async Task Configured_deadline_waits_for_authoritative_settlement_and_manual_completion_stays_atomic()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..10]; var schema = "ce_" + suffix; var jobs = "cj_" + suffix; var outbox = "cv_" + suffix;
        long now = 1_700_000_000_000;
        try
        {
            await DurableScheduler.MigrateAsync(source, jobs); await EventBus.MigrateAsync(source, outbox);
            await using (var seed = source.CreateCommand($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.tournaments(id bigint PRIMARY KEY,ship_types integer[] NOT NULL);
                CREATE TABLE {schema}.series(tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id),id bigint NOT NULL,PRIMARY KEY(tournament_id,id));
                INSERT INTO {schema}.tournaments VALUES(1,ARRAY[601]),(2,ARRAY[601]);
                INSERT INTO {schema}.series VALUES(1,0),(2,0);
                """)) await seed.ExecuteNonQueryAsync();
            await PersistenceTournamentMatches.MigrateAsync(source, schema); await PersistenceTournamentMatches.MigrateSpaceAsync(source, schema);
            await PersistenceTournamentMatches.MigrateRosterAsync(source, schema); await PersistenceTournamentMatches.MigratePhysicalRegionsAsync(source, schema);
            await PersistenceTournamentMatches.MigrateCleanupGuardsAsync(source, schema); await PersistenceTournamentMatches.MigrateRefereeTravelAsync(source, schema);
            await PersistenceTournamentMatches.MigratePostgameAsync(source, schema); await PersistenceTournamentMatches.MigrateCompletionPolicyAsync(source, schema); await PersistenceTournamentMatches.MigrateCutoffIntentAsync(source, schema);
            await using var scheduler = new DurableScheduler(source, () => now, schema: jobs);
            var events = new EventBus(source, scheduler, schema: outbox); var sessions = new CharacterSessions();
            var store = new PersistenceTournamentMatches(source, null!, null!, null!, events, null, null!, schema, scheduler, sessions);
            store.RegisterActionHandlers();
            await store.ImportCompletionPolicyAsync(new TournamentCompletionPolicyImport(1,"alliance_tournament"));
            await store.ImportCompletionPolicyAsync(new TournamentCompletionPolicyImport(1,"alliance_tournament"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportCompletionPolicyAsync(new TournamentCompletionPolicyImport(1,"manual")));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ImportCompletionPolicyAsync(new TournamentCompletionPolicyImport(2,"invented")));
            await using (var seed = source.CreateCommand($"""
                INSERT INTO {schema}.matches(tournament_id,series_id,system_id,maximum_points,match_length,overtime_length,state,start_ms)
                    VALUES (1,0,30000142,100,120,0,3,{now}), (2,0,30000143,100,120,0,3,{now});
                INSERT INTO {schema}.match_referees(oid,match_id,session_id,character_id) SELECT 'N=1:1',id,10,100 FROM {schema}.matches WHERE tournament_id=1;
                """)) await seed.ExecuteNonQueryAsync();
            // Seeded countdown state isolates the scheduler; authoritative StartCountdown is covered separately with real arrivals.
            for (var id = 1; id <= 2; id++)
                await scheduler.ScheduleAsync(new JobSpec("tournament.match.countdown", now) { Key = $"match:{id}", Payload = new { MatchId = id, StartMs = now } });
            await scheduler.RunDueAsync();
            async Task<long> Count(string sql) { await using var command = source.CreateCommand(sql); return Convert.ToInt64(await command.ExecuteScalarAsync()); }
            Assert.Equal(2, await Count($"SELECT count(*) FROM {schema}.matches WHERE state=5"));
            Assert.Equal(1, await Count($"SELECT count(*) FROM {jobs}.jobs WHERE kind='tournament.match.deadline' AND state='pending'"));
            now += 120_000; await scheduler.RunDueAsync();
            Assert.Equal(1, await Count($"SELECT count(*) FROM {schema}.matches WHERE tournament_id=1 AND state=5 AND settlement_pending AND winner_team IS NULL"));
            Assert.Equal(1, await Count($"SELECT count(*) FROM {schema}.matches WHERE tournament_id=2 AND state=5 AND NOT settlement_pending"));
            Assert.Equal(1, await Count($"SELECT count(*) FROM {outbox}.outbox WHERE topic='tournament.match.settlement_pending'"));
            await scheduler.RunDueAsync();
            Assert.Equal(1, await Count($"SELECT count(*) FROM {outbox}.outbox WHERE topic='tournament.match.settlement_pending'"));
            // Startup clears stale referee leases, but cannot discard an unsettled result.
            await store.ResetAfterRestartAsync();
            Assert.Equal(1, await Count($"SELECT count(*) FROM {schema}.matches WHERE id=1 AND settlement_pending AND closing"));
            Assert.Equal(0, await Count($"SELECT count(*) FROM {schema}.match_referees WHERE match_id=1"));
            await using (var rebind = source.CreateCommand($"INSERT INTO {schema}.match_referees(oid,match_id,session_id,character_id) VALUES ('N=1:1',1,10,100)")) await rebind.ExecuteNonQueryAsync();
            var referee = new ClientSession { SessionId=10, CharacterId=100 }; referee.Set("solarsystemid",new PyInt(30000142));
            await store.ActionAsync(new TourneyMatchActionRequest(TourneyMatchAction.MatchCompleted,1,[new PyInt(1)],PyNone.Instance,30000142,"N=1:1",referee),CancellationToken.None);
            Assert.Equal(1, await Count($"SELECT count(*) FROM {schema}.matches WHERE id=1 AND state=6 AND NOT settlement_pending AND winner_team IS NULL AND completion_reason='manual'"));
            Assert.Equal(1, await Count($"SELECT count(*) FROM {jobs}.jobs WHERE kind='tournament.match.postgame' AND state='pending'"));
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema},{jobs},{outbox} CASCADE"); await cleanup.ExecuteNonQueryAsync();
        }
    }
}
