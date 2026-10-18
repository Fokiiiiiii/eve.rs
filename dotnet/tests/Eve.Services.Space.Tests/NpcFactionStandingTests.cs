using Eve.Data;
using Eve.Events;
using Eve.Relations;
using Eve.Scheduler;
using Npgsql;
using Xunit;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A pilot that destroys a faction's NPC loses standing with that faction (CH-14, EveJS <c>factionKillStandingRuntime</c>): one raw change per kill, debounced
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

            // the first kill: standing 0 moves to -0.00006875 * (10 + 0) = -0.0006875 (EveJS's formula, rounded to thousandths by the store)
            var first = await standing.ApplyAsync(Pilot, Faction, System);
            Assert.NotNull(first);
            var stored = await relations.ValueAsync(RelationKinds.Standing.Name, Faction, Pilot, 0);
            Assert.Equal(first!.Value, stored, 6);
            Assert.True(stored < 0);

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
}
