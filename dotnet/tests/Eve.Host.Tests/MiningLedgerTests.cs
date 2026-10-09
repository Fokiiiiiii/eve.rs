using System.Globalization;
using Eve.Events;
using Eve.GameStore;
using Eve.Scheduler;
using Eve.Ships;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>The mining ledger (M7): a granted cycle's fact becomes one entry of the character's row, in the subscriber's transaction.</summary>
public sealed class MiningLedgerTests
{
    private static OreMinedEvent Fact(long characterId, long quantity) => new(
        characterId, 98000001, 30000142, 9000001, 11019, 9000002, 3651, MiningSource.Module, 1230, "ore", quantity, 2, 0);

    private static async Task PublishAsync(NpgsqlDataSource source, EventBus bus, OreMinedEvent fact, bool commit)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await bus.PublishAsync(connection, transaction, OreMinedEvent.Topic, fact, $"char:{fact.CharacterId}");
        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }
        bus.Wake();
    }

    [Fact]
    public async Task A_granted_cycle_is_one_entry_of_the_character_and_a_replay_writes_nothing_more()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(connectionString!);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var storeSchema = "ml_store_" + suffix;
        var eventsSchema = "ml_events_" + suffix;
        var schedulerSchema = "ml_sched_" + suffix;
        try
        {
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventsSchema);
            var bus = new EventBus(source, new DurableScheduler(source, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, schedulerSchema), null, eventsSchema);
            var store = new PersistenceStore(source, storeSchema);
            await store.EnsureSchemaAsync();
            var recorder = new MiningLedgerRecorder(store, await store.AcquireOwnerAsync(PersistenceRoles.World, "ledger-test"));
            bus.Subscribe("mining.ledger", [OreMinedEvent.Topic], async (context, token) =>
            {
                // the same event handled twice (a replay): the entry is there once
                await recorder.RecordAsync(context, token);
                await recorder.RecordAsync(context, token);
            });
            await bus.PumpAsync();

            await PublishAsync(source, bus, Fact(1001, 100), commit: true);
            await PublishAsync(source, bus, Fact(1002, 40), commit: false);   // a grant that rolled back announces nothing
            await bus.PumpAsync();

            var table = await store.LoadTableAsync(MiningLedgerRecorder.Table);
            var characters = table["characters"]!.AsObject();
            Assert.Equal(new[] { "1001" }, characters.Select(pair => pair.Key));
            var entries = characters["1001"]!.AsObject()["entries"]!.AsArray();
            var entry = Assert.Single(entries)!.AsObject();
            Assert.Equal(
                (1001L, 98000001L, 30000142L, 3651L, 100L, 2L, 0L, "ore", 11019L, 0L),
                (entry["characterID"]!.GetValue<long>(), entry["corporationID"]!.GetValue<long>(), entry["solarSystemID"]!.GetValue<long>(),
                    entry["moduleTypeID"]!.GetValue<long>(), entry["quantity"]!.GetValue<long>(), entry["quantityWasted"]!.GetValue<long>(),
                    entry["quantityCritical"]!.GetValue<long>(), entry["yieldKind"]!.GetValue<string>(), entry["shipTypeID"]!.GetValue<long>(),
                    entry["observerItemID"]!.GetValue<long>()));
            // the personal entry is shown ten minutes after the cycle (miningCharacterLedgerDelayMs)
            var eventDate = long.Parse(entry["eventDate"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            Assert.Equal(
                eventDate + MiningLedgerRecorder.CharacterDelayMs * 10_000,
                long.Parse(entry["characterVisibleAfter"]!.GetValue<string>(), CultureInfo.InvariantCulture));
        }
        finally
        {
            foreach (var schema in new[] { storeSchema, eventsSchema, schedulerSchema })
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
