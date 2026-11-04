using Eve.Data;
using Eve.Events;
using Eve.Host.Persistence;
using Eve.Scheduler;
using Eve.Sde;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class TournamentRulesTests
{
    [PgFact]
    public async Task Rule_import_requires_complete_real_types_and_is_retry_safe()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var schema = "tr_" + suffix; var sched = "rs_" + suffix; var evt = "re_" + suffix;
        try
        {
            await DurableScheduler.MigrateAsync(source, sched); await EventBus.MigrateAsync(source, evt);
            await using (var seed = source.CreateCommand($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.tournaments (id bigint PRIMARY KEY,ship_types integer[] NOT NULL);
                CREATE TABLE {schema}.series (tournament_id bigint NOT NULL REFERENCES {schema}.tournaments(id), id bigint NOT NULL, PRIMARY KEY(tournament_id,id));
                INSERT INTO {schema}.tournaments VALUES (1, ARRAY[587]);
                """)) await seed.ExecuteNonQueryAsync();
            await PersistenceTournamentMatches.MigrateRulesAsync(source, schema);
            await using var scheduler = new DurableScheduler(source, () => 1_700_000_000_000, schema: sched);
            var events = new EventBus(source, scheduler, schema: evt);
            var store = new PersistenceTournamentMatches(source, null!, null!, null!, events, null, new SdeReader(source), schema);
            var rules = new TournamentRulesImport(1, [new TournamentHullPoints(587, 5)], []);
            await store.ImportRulesAsync(rules); await store.ImportRulesAsync(rules);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportRulesAsync(rules with { HullPoints = [new TournamentHullPoints(587, 6)] }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ImportRulesAsync(rules with { HullPoints = [new TournamentHullPoints(10645, 5)] }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ImportRulesAsync(rules with { ProhibitedFitTypes = [int.MaxValue] }));
            await using var counts = source.CreateCommand($"SELECT (SELECT count(*) FROM {schema}.tournament_ship_points), (SELECT count(*) FROM {evt}.outbox)");
            await using var row = await counts.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal(1L, row.GetInt64(0)); Assert.Equal(1L, row.GetInt64(1));
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema},{sched},{evt} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
