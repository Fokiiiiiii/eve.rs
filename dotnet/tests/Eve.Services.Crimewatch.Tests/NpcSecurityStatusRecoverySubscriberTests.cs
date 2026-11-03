using Eve.Crimewatch;
using Eve.Data;
using Eve.Events;
using Eve.Npc;
using Eve.Relations;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Services.Crimewatch.Tests;

public sealed class NpcSecurityStatusRecoverySubscriberTests
{
    [PgFact]
    public async Task A_failed_delivery_retries_once_then_the_highest_kill_settles_after_restart()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(connectionString));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        string[] schemas = ["nsr_sched_" + suffix, "nsr_events_" + suffix, "nsr_rel_" + suffix, "nsr_cw_" + suffix];
        await DurableScheduler.MigrateAsync(source, schemas[0]);
        await EventBus.MigrateAsync(source, schemas[1]);
        await RelationStore.MigrateAsync(source, schemas[2]);
        await PgCrimewatchStore.MigrateAsync(source, schemas[3]);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        DurableScheduler? scheduler = null;
        try
        {
            scheduler = new DurableScheduler(source, () => nowMs, new SchedulerOptions { Concurrency = 1 }, schemas[0]);
            var events = new EventBus(source, scheduler, new EventBusOptions { BackoffBaseMs = 1, BackoffMaxMs = 1 }, schemas[1]);
            var relations = new RelationStore(source, scheduler, events, schemas[2]);
            var crimewatch = new PgCrimewatchStore(source, schemas[3]);
            const long characterId = 90_001;
            const long npcCorporationId = 1_000_035;
            const int npcFactionId = 500_010;
            const int rogueDroneFactionId = 500_025;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await relations.SetAsync(connection, transaction, RelationKinds.SecurityStatus.Name,
                    characterId, RelationStore.World, 0.0, "test setup");
                await transaction.CommitAsync();
            }

            Eve.Services.Crimewatch.NpcSecurityStatusProfile? Profile(string profileId) => profileId switch
            {
            "low" => new Eve.Services.Crimewatch.NpcSecurityStatusProfile(100_000m, npcCorporationId, npcFactionId, 1_001, true, 0.08),
            "high" => new Eve.Services.Crimewatch.NpcSecurityStatusProfile(500_000m, npcCorporationId, npcFactionId, 1_002, true, 0.16),
            "highest" => new Eve.Services.Crimewatch.NpcSecurityStatusProfile(0m, npcCorporationId, npcFactionId, 1_003, true, 0.24),
            "rogueDrone" => new Eve.Services.Crimewatch.NpcSecurityStatusProfile(1_000_000m, npcCorporationId, rogueDroneFactionId, 1_004, false, 1.0),
                _ => null,
            };

            var recovery = new Eve.Services.Crimewatch.NpcSecurityStatusRecoverySubscriber(
                crimewatch, scheduler, events, relations, Profile,
                async (connection, transaction, id, token) =>
                {
                    Assert.Equal(characterId, id);
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                    return 1.25;
                },
                (previous, bounty, fastTalk) => NpcSecurityStatusRecovery.TryNext(bounty, previous, fastTalk));

            var failOnce = true;
            events.Subscribe(Eve.Services.Crimewatch.NpcSecurityStatusRecoverySubscriber.SubscriberName,
                [Eve.Services.Crimewatch.NpcSecurityStatusRecoverySubscriber.Topic],
                async (context, token) =>
                {
                    await recovery.HandleAsync(context, token);
                    if (failOnce)
                    {
                        failOnce = false;
                        throw new InvalidOperationException("simulated failure before subscriber commit");
                    }
                }, StartAt.Beginning);
            scheduler.Register(Eve.Services.Crimewatch.NpcSecurityStatusRecoverySubscriber.JobKind, recovery.SettleAsync);

            var firstKillEventId = await events.PublishAsync("npc.killed",
                new { characterId, typeId = 1_001, profile = "low", solarSystemId = 30_000_001L, shipId = 900_001L },
                "system:30000001");
            await events.PublishAsync("npc.killed",
                new { characterId, typeId = 1_002, profile = "high", solarSystemId = 30_000_002L, shipId = 900_002L },
                "system:30000002");
            await events.PublishAsync("npc.killed",
                new { characterId, typeId = 1_004, profile = "rogueDrone", solarSystemId = 30_000_004L, shipId = 900_004L },
                "system:30000004");

            var failed = await events.PumpAsync();
            Assert.Equal(0, failed.Delivered);
            Assert.NotNull(failed.RetryAtMs);
            Assert.Equal(0.0, (await relations.GetAsync(RelationKinds.SecurityStatus.Name,
                characterId, RelationStore.World))?.Value);

            nowMs = failed.RetryAtMs!.Value + 1;
            var retried = await events.PumpAsync();
            Assert.Equal(3, retried.Delivered);
            Assert.Equal(0.0, (await relations.GetAsync(RelationKinds.SecurityStatus.Name,
                characterId, RelationStore.World))?.Value);

            long firstKillAtMs;
            long windowEndMs;
            await using (var readWindow = await source.OpenConnectionAsync())
            {
                await using var eventTime = new NpgsqlCommand(
                    $"SELECT occurred_ms FROM {schemas[1]}.outbox WHERE id = $1", readWindow);
                eventTime.Parameters.AddWithValue(firstKillEventId);
                firstKillAtMs = (long)(await eventTime.ExecuteScalarAsync())!;
                await using var windowEnd = new NpgsqlCommand(
                    $"SELECT window_end_ms FROM {schemas[3]}.npc_security_status_recovery_windows WHERE character_id = $1",
                    readWindow);
                windowEnd.Parameters.AddWithValue(characterId);
                windowEndMs = (long)(await windowEnd.ExecuteScalarAsync())!;
            }
            Assert.Equal(firstKillAtMs + Eve.Services.Crimewatch.NpcSecurityStatusRecoverySubscriber.WindowLengthMs, windowEndMs);

            await scheduler.DisposeAsync();
            scheduler = new DurableScheduler(source, () => nowMs, new SchedulerOptions { Concurrency = 1 }, schemas[0]);
            var restartedEvents = new EventBus(source, scheduler,
                new EventBusOptions { BackoffBaseMs = 1, BackoffMaxMs = 1 }, schemas[1]);
            var restartedRelations = new RelationStore(source, scheduler, restartedEvents, schemas[2]);
            var restartedRecovery = new Eve.Services.Crimewatch.NpcSecurityStatusRecoverySubscriber(
                crimewatch, scheduler, restartedEvents, restartedRelations, Profile,
                (_, _, id, token) =>
                {
                    Assert.Equal(characterId, id);
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(1.25);
                },
                (previous, bounty, fastTalk) => NpcSecurityStatusRecovery.TryNext(bounty, previous, fastTalk));
            restartedEvents.Subscribe(Eve.Services.Crimewatch.NpcSecurityStatusRecoverySubscriber.SubscriberName,
                [Eve.Services.Crimewatch.NpcSecurityStatusRecoverySubscriber.Topic], restartedRecovery.HandleAsync, StartAt.Beginning);
            scheduler.Register(Eve.Services.Crimewatch.NpcSecurityStatusRecoverySubscriber.JobKind, restartedRecovery.SettleAsync);
            string? recoveredNotice = null;
            restartedEvents.Subscribe("test.npc-security-status-notice", ["npc.security_status.recovered"],
                (context, _) =>
                {
                    recoveredNotice = context.Event.PayloadJson;
                    return Task.CompletedTask;
                });
            await restartedEvents.PumpAsync();

            var highestEventId = await restartedEvents.PublishAsync("npc.killed",
                new { characterId, typeId = 1_003, profile = "highest", solarSystemId = 30_000_003L, shipId = 900_003L },
                "system:30000003");

            nowMs = windowEndMs;
            Assert.Equal(1, await scheduler.RunDueAsync());
            Assert.Equal(0.0, (await restartedRelations.GetAsync(RelationKinds.SecurityStatus.Name,
                characterId, RelationStore.World))?.Value);
            await restartedEvents.PumpAsync();

            nowMs += 1001;
            Assert.Equal(1, await scheduler.RunDueAsync());
            await restartedEvents.PumpAsync();

            var recoveredStatus = (await restartedRelations.GetAsync(RelationKinds.SecurityStatus.Name,
                characterId, RelationStore.World))?.Value ?? double.NaN;
            Assert.InRange(Math.Abs(recoveredStatus - 0.03), 0, 1e-12);
            Assert.NotNull(recoveredNotice);
            using (var notice = System.Text.Json.JsonDocument.Parse(recoveredNotice!))
            {
                Assert.Equal(characterId, notice.RootElement.GetProperty("characterId").GetInt64());
              Assert.InRange(Math.Abs(notice.RootElement.GetProperty("status").GetDouble() - 0.03), 0, 1e-12);
            }

            var history = await restartedRelations.HistoryAsync(RelationKinds.SecurityStatus.Name, characterId, RelationStore.World);
            var killRows = history.Where(entry => entry.Reason == Eve.Services.Crimewatch.NpcSecurityStatusRecoverySubscriber.HistoryReason).ToArray();
            var recovered = Assert.Single(killRows);
            Assert.True(recovered.Attrs.HasValue);
            Assert.Equal(0m, recovered.Attrs.Value.GetProperty("bounty").GetDecimal());
            Assert.Equal(0.24, recovered.Attrs.Value.GetProperty("securityStatusKillBonusPercent").GetDouble());
            Assert.Equal(highestEventId, recovered.Attrs.Value.GetProperty("sourceEventID").GetInt64());
            Assert.Equal(1.25, recovered.Attrs.Value.GetProperty("fastTalkMultiplier").GetDouble());
            Assert.Equal(highestEventId, recovered.Attrs.Value.GetProperty("sourceEventID").GetInt64());

            await using var verify = await source.OpenConnectionAsync();
            await using var recoveredCount = new NpgsqlCommand(
                $"SELECT count(*) FROM {schemas[1]}.outbox WHERE topic = 'npc.security_status.recovered'", verify);
            Assert.Equal(1L, (long)(await recoveredCount.ExecuteScalarAsync())!);
            await using var pendingWindows = new NpgsqlCommand(
                $"SELECT count(*) FROM {schemas[3]}.npc_security_status_recovery_windows", verify);
            Assert.Equal(0L, (long)(await pendingWindows.ExecuteScalarAsync())!);
        }
        finally
        {
            if (scheduler is not null)
            {
                await scheduler.DisposeAsync();
            }
            await using var connection = await source.OpenConnectionAsync();
            foreach (var schema in schemas)
            {
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
