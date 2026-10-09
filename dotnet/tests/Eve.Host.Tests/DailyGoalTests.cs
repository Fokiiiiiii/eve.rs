using Eve.Content;
using Eve.Events;
using Eve.Ships;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>The daily goals of mining and salvage: one goal of each kind per pilot per period, advanced by the facts of the cycles and the wrecks.</summary>
public sealed class DailyGoalTests
{
    private const long DayMs = 86_400_000;

    private static OreMinedEvent Ore(long characterId, long quantity, string yieldKind = "ore") => new(
        characterId, 98000001, 30000142, 9000001, 11019, 9000002, 3651, MiningSource.Module, 1230, yieldKind, quantity, 0, 0);

    private static SalvageWreckedEvent Wreck(long characterId) => new(
        characterId, 30000142, 9000001, 9000002, MiningSource.Module, 9000003, 1, 2, 0);

    private static long At(int day, int hour) => new DateTimeOffset(2026, 10, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void A_period_runs_from_one_downtime_to_the_next()
    {
        // before 11:00 UTC the period is the one that began yesterday; from 11:00 UTC it is the one that begins today
        Assert.Equal((At(8, 11), At(9, 11)), DailyGoalRecorder.PeriodAt(At(9, 10) + 59 * 60_000));
        Assert.Equal((At(9, 11), At(9, 11) + DayMs), DailyGoalRecorder.PeriodAt(At(9, 11)));
    }

    [Fact]
    public async Task Ore_and_wrecks_advance_one_goal_each_per_period_and_a_finished_goal_is_not_started_again()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(connectionString!);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = "dg_sched_" + suffix;
        var eventsSchema = "dg_events_" + suffix;
        var contentSchema = "dg_content_" + suffix;
        try
        {
            var now = At(9, 12);
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventsSchema);
            await ContentRuntime.MigrateAsync(source, contentSchema);
            var scheduler = new DurableScheduler(source, () => now, null, schedulerSchema);
            var bus = new EventBus(source, scheduler, null, eventsSchema);
            var content = new ContentRuntime(source, scheduler, bus, contentSchema);
            foreach (var definition in DailyGoalRecorder.Definitions)
            {
                content.Define(definition);
            }
            var recorder = new DailyGoalRecorder(content, scheduler);
            bus.Subscribe(DailyGoalRecorder.SubscriberName, [OreMinedEvent.Topic, SalvageWreckedEvent.Topic],
                (context, token) => recorder.RecordAsync(context, token));
            await bus.PumpAsync();

            async Task PublishAsync<T>(string topic, T fact)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await bus.PublishAsync(connection, transaction, topic, fact, "daily-goal-test");
                await transaction.CommitAsync();
                bus.Wake();
                await bus.PumpAsync();
            }

            // ore counts, ice does not (EVE.js counts ore only)
            await PublishAsync(OreMinedEvent.Topic, Ore(1001, 1500));
            await PublishAsync(OreMinedEvent.Topic, Ore(1001, 500, "ice"));
            var goal = Assert.Single(await content.ActiveOfAsync(DailyGoalRecorder.Kind, 1001));
            Assert.Equal(DailyGoalRecorder.MineOre, goal.Definition);
            var objective = Assert.Single(await content.ObjectivesOfAsync(goal.Id));
            Assert.Equal((DailyGoalRecorder.MineOreTarget, 1500d, "active"), (objective.Target, objective.Progress, objective.State));

            // the goal is capped at its target and completes; a later cycle of the same period starts nothing
            await PublishAsync(OreMinedEvent.Topic, Ore(1001, 600));
            Assert.Equal("completed", (await content.GetAsync(goal.Id))!.State);
            await PublishAsync(OreMinedEvent.Topic, Ore(1001, 300));
            Assert.Empty(await content.ActiveOfAsync(DailyGoalRecorder.Kind, 1001));

            // wrecks count one each, toward their own goal
            for (var wreck = 0; wreck < 4; wreck++)
            {
                await PublishAsync(SalvageWreckedEvent.Topic, Wreck(1001));
            }
            var salvage = Assert.Single(await content.ActiveOfAsync(DailyGoalRecorder.Kind, 1001));
            Assert.Equal((DailyGoalRecorder.SalvageWrecks, 4d), (salvage.Definition, (await content.ObjectivesOfAsync(salvage.Id))[0].Progress));
            await PublishAsync(SalvageWreckedEvent.Topic, Wreck(1001));
            Assert.Empty(await content.ActiveOfAsync(DailyGoalRecorder.Kind, 1001));

            // the next period (after 11:00 UTC the next day) starts a new mining goal, and another pilot's goal is its own
            now = At(10, 12);
            await PublishAsync(OreMinedEvent.Topic, Ore(1001, 40));
            await PublishAsync(OreMinedEvent.Topic, Ore(1002, 25));
            var next = await content.ActiveOfAsync(DailyGoalRecorder.Kind, 1001);
            Assert.Equal((1, 40d), (next.Count, (await content.ObjectivesOfAsync(next[0].Id))[0].Progress));
            Assert.NotEqual(goal.Id, next[0].Id);
            var other = Assert.Single(await content.ActiveOfAsync(DailyGoalRecorder.Kind, 1002));
            Assert.Equal(25d, (await content.ObjectivesOfAsync(other.Id))[0].Progress);
        }
        finally
        {
            foreach (var schema in new[] { schedulerSchema, eventsSchema, contentSchema })
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
