using Eve.Data;
using Eve.Events;
using Eve.Relations;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Services.Crimewatch.Tests;

public sealed class NpcSecurityStatusRecoverySubscriberTests
{
    [PgFact]
    public async Task A_failed_delivery_rolls_back_and_a_retry_applies_two_distinct_kills_once()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        string[] schemas = ["nsr_sched_" + suffix, "nsr_events_" + suffix, "nsr_rel_" + suffix];
        await DurableScheduler.MigrateAsync(source, schemas[0]);
        await EventBus.MigrateAsync(source, schemas[1]);
        await RelationStore.MigrateAsync(source, schemas[2]);

        var scheduler = new DurableScheduler(source, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new SchedulerOptions { Concurrency = 1 }, schemas[0]);
        try
        {
            var events = new EventBus(source, scheduler,
                new EventBusOptions { BackoffBaseMs = 1, BackoffMaxMs = 1 }, schemas[1]);
            var relations = new RelationStore(source, scheduler, events, schemas[2]);
            const long characterId = 90_001;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await relations.SetAsync(connection, transaction, RelationKinds.SecurityStatus.Name,
                    characterId, RelationStore.World, 5.0, "test setup");
                await transaction.CommitAsync();
            }

            var fastTalkContexts = new Dictionary<long, EventContext>();
            var subscriber = new NpcSecurityStatusRecoverySubscriber<EventContext>(
                profile => profile == "profile" ? 250_000m : null,
                async (context, id, token) =>
                {
                    Assert.Equal(characterId, id);
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                    fastTalkContexts[context.Event.Id] = context;
                    return 1.25;
                },
                (previous, bounty, fastTalk) => previous + (double)(bounty / 1_000_000m) * fastTalk,
                async (context, id, token) =>
                {
                    Assert.Same(fastTalkContexts[context.Event.Id], context);
                    return (await relations.GetAsync(RelationKinds.SecurityStatus.Name, id, RelationStore.World,
                        context.Connection, context.Transaction, token, forUpdate: true))?.Value ?? 0.0;
                },
                async (context, change, token) =>
                {
                    Assert.Same(fastTalkContexts[change.SourceEventId], context);
                    await relations.SetAsync(context.Connection, context.Transaction,
                        RelationKinds.SecurityStatus.Name, change.CharacterId, RelationStore.World, change.NextStatus,
                        reason: NpcSecurityStatusRecoverySubscriber<EventContext>.HistoryReason,
                        attrs: new
                        {
                            sourceEventId = change.SourceEventId,
                            npcShipId = change.NpcShipId,
                            npcTypeId = change.NpcTypeId,
                            npcProfile = change.ProfileId,
                            bounty = change.Bounty,
                            fastTalkMultiplier = change.FastTalkMultiplier,
                        },
                        cancellationToken: token);
                });

            var failOnce = true;
            events.Subscribe(NpcSecurityStatusRecoverySubscriber<EventContext>.SubscriberName,
                [NpcSecurityStatusRecoverySubscriber<EventContext>.Topic],
                async (context, token) =>
                {
                    await subscriber.HandleAsync(context, context.Event.Id, context.Event.PayloadJson, token);
                    if (failOnce)
                    {
                        failOnce = false;
                        throw new InvalidOperationException("simulated failure before subscriber commit");
                    }
                }, StartAt.Beginning);

            await events.PublishAsync("npc.killed",
                new { characterId, typeId = 1001, profile = "profile", solarSystemId = 30_000_001, shipId = 900_001 },
                "system:30000001");
            await events.PublishAsync("npc.killed",
                new { characterId, typeId = 1001, profile = "profile", solarSystemId = 30_000_001, shipId = 900_002 },
                "system:30000001");

            var failed = await events.PumpAsync();
            Assert.Equal(0, failed.Delivered);
            Assert.NotNull(failed.RetryAtMs);
            Assert.Equal(5.0, (await relations.GetAsync(RelationKinds.SecurityStatus.Name,
                characterId, RelationStore.World))?.Value);

            await Task.Delay(25);
            var retried = await events.PumpAsync();
            Assert.Equal(2, retried.Delivered);

            var afterRetry = (await relations.GetAsync(RelationKinds.SecurityStatus.Name,
                characterId, RelationStore.World))?.Value;
            Assert.InRange(Math.Abs(afterRetry.GetValueOrDefault() - 5.625), 0, 1e-10);

            await events.PumpAsync();
            Assert.Equal(afterRetry, (await relations.GetAsync(RelationKinds.SecurityStatus.Name,
                characterId, RelationStore.World))?.Value);

            await using var verify = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"SELECT count(*) FROM {schemas[2]}.relations WHERE kind = 'security_status' AND subject = $1 AND object = $2",
                verify);
            command.Parameters.AddWithValue(characterId);
            command.Parameters.AddWithValue(RelationStore.World);
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);

            await using var eventCount = new NpgsqlCommand(
                $"SELECT count(*) FROM {schemas[1]}.outbox WHERE topic = 'relation.security_status.changed'",
                verify);
            Assert.Equal(3L, (long)(await eventCount.ExecuteScalarAsync())!);
        }
        finally
        {
            await scheduler.DisposeAsync();
            await using var connection = await source.OpenConnectionAsync();
            foreach (var schema in schemas)
            {
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
