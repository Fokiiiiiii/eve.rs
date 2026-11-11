using System.Text.Json.Nodes;
using Eve.Crimewatch;
using Eve.Data;
using Eve.Events;
using Eve.GameStore;
using Eve.Host;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Crimewatch;
using Npgsql;

namespace Eve.Host.Tests;

public sealed partial class CrimewatchKillRightHostTests
{
    [PgFact]
    public async Task A_new_kill_right_publishes_one_atomic_creation_event_then_persists_the_earned_notification()
    {
        const long createdAt = 1_900_000_000_000;
        var now = createdAt;
        const long owner = 92_000_001;
        const long target = 92_000_002;
        const long solarSystem = 30_000_142;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var crimewatchSchema = "cwce_" + suffix;
        var eventSchema = "evce_" + suffix;
        var schedulerSchema = "scce_" + suffix;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        await PgCrimewatchStore.MigrateAsync(source, crimewatchSchema);
        await DurableScheduler.MigrateAsync(source, schedulerSchema);
        await EventBus.MigrateAsync(source, eventSchema);
        await using var scheduler = new DurableScheduler(source, () => now, new SchedulerOptions { Concurrency = 1 }, schedulerSchema);
        var events = new EventBus(source, scheduler, new EventBusOptions { PollMs = 5 }, eventSchema);
        var liveOrder = new List<string>();
        var liveNotificationIds = new List<long>();
        var failFirstLiveNotification = true;
        var notifications = new JsonObject { ["boxes"] = new JsonObject() };
        var committed = (JsonObject)notifications.DeepClone();

        JsonObject ReadCache(string table)
        {
            Assert.Equal("notifications", table);
            return (JsonObject)notifications.DeepClone();
        }

        WriteResult Write(string table, string? path, JsonNode? value)
        {
            Assert.Equal("notifications", table);
            if (path is null)
            {
                notifications = (JsonObject)value!.DeepClone();
                return new WriteResult(true, null);
            }
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = notifications;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                if (current[segments[index]] is not JsonObject child)
                {
                    child = new JsonObject();
                    current[segments[index]] = child;
                }
                current = child;
            }
            current[segments[^1]] = value?.DeepClone();
            return new WriteResult(true, null);
        }

        Task<int> Flush(string table, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("notifications", table);
            committed = (JsonObject)notifications.DeepClone();
            return Task.FromResult(1);
        }

        Task<JsonObject> ReadCommitted(string table, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("notifications", table);
            return Task.FromResult((JsonObject)committed.DeepClone());
        }

        var mail = new PersistenceMailSendHostAdapter(
            ReadCache,
            Write,
            Flush,
            ReadCommitted,
            (_, _, _) => Task.FromResult<PersistenceMailSendRecipients?>(null),
            () => now,
            (recipientId, arguments) =>
            {
                Assert.Equal(owner, recipientId);
                Assert.Equal(KillRightNotificationTypes.Earned,
                    Assert.IsType<PyInt>(arguments[1]).Value);
                liveNotificationIds.Add(Assert.IsType<PyInt>(arguments[0]).Value);
                if (failFirstLiveNotification)
                {
                    failFirstLiveNotification = false;
                    throw new InvalidOperationException("injected live notification failure after persistence");
                }
                liveOrder.Add($"mail:{recipientId}:OnNotificationReceived");
            });
        var sessions = new CharacterSessions();
        var ownerSink = new NotificationSink(name => liveOrder.Add($"client:{owner}:{name}"));
        var targetSink = new NotificationSink(name => liveOrder.Add($"client:{target}:{name}"));
        sessions.Enter(owner, new ClientSession(ownerSink) { CharacterId = owner, SessionId = owner });
        sessions.Enter(target, new ClientSession(targetSink) { CharacterId = target, SessionId = target });
        CrimewatchKillRightEvents.Subscribe(events, mail, sessions);

        try
        {
            Assert.Equal(0, (await events.PumpAsync()).Delivered);
            var store = new PgCrimewatchStore(source, crimewatchSchema);
            var request = new KillRightGrantRequest(owner, target, solarSystem, now);
            var rightId = await CrimewatchKillRightEvents.CreateAndPublishAsync(source, store, events, request, CancellationToken.None);
            Assert.NotNull(rightId);
            Assert.Null(await CrimewatchKillRightEvents.CreateAndPublishAsync(source, store, events, request, CancellationToken.None));
            Assert.Empty(liveOrder);

            var firstDelivery = await events.PumpAsync();
            Assert.Equal(0, firstDelivery.Delivered);
            Assert.NotNull(firstDelivery.RetryAtMs);
            Assert.Equal([
                $"client:{owner}:OnKillRightCreated",
                $"client:{target}:OnKillRightCreated",
            ], liveOrder);

            now += 1_000;
            Assert.Equal(1, (await events.PumpAsync()).Delivered);
            Assert.Equal(0, (await events.PumpAsync()).Delivered);

            var right = Assert.Single(await store.KillRightsOfAsync(owner));
            Assert.Equal(createdAt + PgCrimewatchStore.KillRightLifetimeMs, right.ExpiresAtUnixMs);
            var persisted = committed["boxes"]![owner.ToString()]!["byID"]!.AsObject();
            var notification = Assert.Single(persisted.Select(pair => pair.Value).OfType<JsonObject>());
            Assert.Equal(KillRightNotificationTypes.Earned, notification["typeID"]!.GetValue<int>());
            Assert.Equal(KillRightNotificationTypes.MiscGroup, notification["groupID"]!.GetValue<int>());
            Assert.Equal(target, notification["senderID"]!.GetValue<long>());
            Assert.Equal(owner, notification["receiverID"]!.GetValue<long>());
            Assert.Equal($"kill-right-earned:{rightId}", notification["idempotencyKey"]!.GetValue<string>());
            Assert.Equal(target, notification["data"]!["charID"]!.GetValue<long>());

            Assert.Equal([
                $"client:{owner}:OnKillRightCreated",
                $"client:{target}:OnKillRightCreated",
                $"client:{owner}:OnKillRightCreated",
                $"client:{target}:OnKillRightCreated",
                $"mail:{owner}:OnNotificationReceived",
            ], liveOrder);
            Assert.Equal(2, liveNotificationIds.Count);
            Assert.Equal(liveNotificationIds[0], liveNotificationIds[1]);
            PyValue[] expectedCreatedArgs =
            [
                InventoryWire.Id(rightId!.Value),
                InventoryWire.Id(owner),
                InventoryWire.Id(target),
                new PyLong(Py.FileTimeFromUnixMs(right.ExpiresAtUnixMs)),
            ];
            foreach (var sink in new[] { ownerSink, targetSink })
            {
                var notes = NotificationsOf(sink);
                Assert.Equal(2, notes.Count);
                Assert.All(notes, note =>
                {
                    Assert.Equal("OnKillRightCreated", note.Name);
                    Assert.Equal("charid", note.IdType);
                    Assert.Equal(expectedCreatedArgs, note.Arguments);
                });
            }
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS {crimewatchSchema} CASCADE; DROP SCHEMA IF EXISTS {eventSchema} CASCADE; DROP SCHEMA IF EXISTS {schedulerSchema} CASCADE",
                connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
