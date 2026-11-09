using Eve.Data;
using Eve.Events;
using Eve.Relations;
using Eve.Scheduler;
using Npgsql;
using Xunit;

namespace Eve.Services.Space.Tests;

/// <summary>
/// per pilot, faction and system; the standing is stored, so it survives a restart.
/// </summary>
public sealed class NpcFactionStandingTests
{
    private const long Pilot = 96_100_001;
    private const int Faction = 500_010, System = 30_000_142;

    [PgFact]
    public async Task A_kill_moves_the_standing_once_per_window_and_the_standing_survives_a_restart()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        string[] schemas = ["nfs_sched_" + suffix, "nfs_events_" + suffix, "nfs_rel_" + suffix];
        await DurableScheduler.MigrateAsync(source, schemas[0]);
        await EventBus.MigrateAsync(source, schemas[1]);
        await RelationStore.MigrateAsync(source, schemas[2]);
        try
        {
            var scheduler = new DurableScheduler(source, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new SchedulerOptions { Concurrency = 1 }, schemas[0]);
            var events = new EventBus(source, scheduler, new EventBusOptions { BackoffBaseMs = 1, BackoffMaxMs = 1 }, schemas[1]);
            var relations = new RelationStore(source, scheduler, events, schemas[2]);
            long now = 1_000_000;
            var standing = new NpcFactionStanding(relations, () => now);

            const long npcCorporation = 1_000_142;
            const int npcType = 17720;
            var first = await standing.ApplyAsync(Pilot, Faction, System, npcCorporationId: npcCorporation, npcTypeId: npcType);
            Assert.NotNull(first);
            var stored = await relations.ValueAsync(RelationKinds.Standing.Name, Faction, Pilot, 0);
            Assert.Equal(first!.Value, stored, 6);
            Assert.True(stored < 0);
            var history = await relations.HistoryAsync(RelationKinds.Standing.Name, Faction, Pilot);
            using (var reason = global::System.Text.Json.JsonDocument.Parse(Assert.Single(history).Reason!))
            {
                Assert.Equal(77, reason.RootElement.GetProperty("eventTypeID").GetInt32());
                Assert.Equal(NpcFactionStanding.LossPerKillRaw, reason.RootElement.GetProperty("modification").GetDouble());
                Assert.Equal("Combat ship kill", reason.RootElement.GetProperty("msg").GetString());
                Assert.Equal(npcCorporation, reason.RootElement.GetProperty("int_1").GetInt64());
                Assert.Equal(System, reason.RootElement.GetProperty("int_2").GetInt32());
                Assert.Equal(npcType, reason.RootElement.GetProperty("int_3").GetInt32());
            }

            // another kill inside the ten-minute window moves nothing; a kill in another system is its own window
            now += 60_000;
            Assert.Null(await standing.ApplyAsync(Pilot, Faction, System));
            Assert.Equal(stored, await relations.ValueAsync(RelationKinds.Standing.Name, Faction, Pilot, 0));
            Assert.NotNull(await standing.ApplyAsync(Pilot, Faction, System + 1));

            // after the window the next kill applies again, and a restart (a new service over the same store) keeps the standing it stored
            now += 11 * 60_000;
            var after = await standing.ApplyAsync(Pilot, Faction, System);
            Assert.NotNull(after);
            Assert.True(after < stored);
            // a restart: a new process (its own scheduler and bus) over the same tables
            var restartedScheduler = new DurableScheduler(source, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new SchedulerOptions { Concurrency = 1 }, schemas[0]);
            var restartedEvents = new EventBus(source, restartedScheduler, new EventBusOptions { BackoffBaseMs = 1, BackoffMaxMs = 1 }, schemas[1]);
            var restarted = new NpcFactionStanding(new RelationStore(source, restartedScheduler, restartedEvents, schemas[2]), () => now);
            Assert.Equal(after!.Value, await relations.ValueAsync(RelationKinds.Standing.Name, Faction, Pilot, 0), 6);
            Assert.Null(await restarted.ApplyAsync(0, Faction, System));
        }
        finally
        {
            foreach (var schema in schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Concurrent_kills_in_different_systems_apply_each_loss_to_the_latest_standing()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        var applicationName = "nfs_" + Guid.NewGuid().ToString("N")[..10];
        var builder = new NpgsqlConnectionStringBuilder(PgConnection.ForTest(url))
        {
            ApplicationName = applicationName,
        };
        await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        string[] schemas = ["nfs_sched_" + suffix, "nfs_events_" + suffix, "nfs_rel_" + suffix];
        await DurableScheduler.MigrateAsync(source, schemas[0]);
        await EventBus.MigrateAsync(source, schemas[1]);
        await RelationStore.MigrateAsync(source, schemas[2]);
        try
        {
            var scheduler = new DurableScheduler(source, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new SchedulerOptions { Concurrency = 1 }, schemas[0]);
            var events = new EventBus(source, scheduler, new EventBusOptions { BackoffBaseMs = 1, BackoffMaxMs = 1 }, schemas[1]);
            var relations = new RelationStore(source, scheduler, events, schemas[2]);
            var standing = new NpcFactionStanding(relations, () => 1_000_000);

            await relations.SetAsync(RelationKinds.Standing.Name, Faction, Pilot, 0, "seed");
            await using (var trigger = source.CreateCommand($"""
                CREATE FUNCTION {schemas[2]}.pause_standing_update() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.kind = 'standing' AND NEW.subject = {Faction} AND NEW.object = {Pilot} THEN
                        PERFORM pg_sleep(0.5);
                    END IF;
                    RETURN NEW;
                END
                $$;
                CREATE TRIGGER pause_standing_update
                    BEFORE UPDATE ON {schemas[2]}.relations
                    FOR EACH ROW EXECUTE FUNCTION {schemas[2]}.pause_standing_update();
                """))
            {
                await trigger.ExecuteNonQueryAsync();
            }

            var firstKill = standing.ApplyAsync(Pilot, Faction, System);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitForStandingUpdateAsync(source, applicationName, schemas[2], timeout.Token);
            var secondKill = standing.ApplyAsync(Pilot, Faction, System + 1);
            await Task.WhenAll(firstKill, secondKill);

            var afterOne = UndockContraband.StandingAfter(0, NpcFactionStanding.LossPerKillRaw);
            var afterTwo = UndockContraband.StandingAfter(afterOne, NpcFactionStanding.LossPerKillRaw);
            Assert.Equal(afterTwo, await relations.ValueAsync(RelationKinds.Standing.Name, Faction, Pilot), 8);
            Assert.Equal(3, (await relations.HistoryAsync(RelationKinds.Standing.Name, Faction, Pilot)).Count);
        }
        finally
        {
            foreach (var schema in schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task WaitForStandingUpdateAsync(
        NpgsqlDataSource source, string applicationName, string relationSchema, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE pid <> pg_backend_pid()
                      AND application_name = $1
                      AND state = 'active'
                      AND query ILIKE 'UPDATE%'
                      AND strpos(query, $2 || '.relations') > 0
                )
                """, connection);
            command.Parameters.AddWithValue(applicationName);
            command.Parameters.AddWithValue(relationSchema);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
                return;
            await Task.Delay(10, cancellationToken);
        }
    }
}
