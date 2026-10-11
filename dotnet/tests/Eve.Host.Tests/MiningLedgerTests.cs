using System.Text.Json.Nodes;
using Eve.Events;
using Eve.Scheduler;
using Eve.Services.Core;
using Eve.Ships;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>
/// The mining ledger (M7) as a table: a granted cycle's fact becomes one row in the subscriber's transaction, a replay writes nothing more, a grant that rolled back
/// writes nothing, the retention prunes what is older, and the EVE.js rows the game store held are copied once.
/// </summary>
public sealed class MiningLedgerTests
{
    private const long ClockMs = 1_800_000_000_000;
    private const long TenMinutesMs = 600_000;

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

    private static async Task<long> CountAsync(NpgsqlDataSource source, string table)
    {
        await using var command = source.CreateCommand($"SELECT count(*) FROM {table}");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_granted_cycle_is_one_row_a_replay_writes_nothing_more_and_the_pilot_sees_it_after_the_delay()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(connectionString!));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var spaceSchema = "ml_space_" + suffix;
        var eventsSchema = "ml_events_" + suffix;
        var schedulerSchema = "ml_sched_" + suffix;
        var clock = ClockMs;
        try
        {
            await PgSpaceItemStore.MigrateAsync(source, spaceSchema);
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventsSchema);
            var bus = new EventBus(source, new DurableScheduler(source, () => clock, null, schedulerSchema), null, eventsSchema);
            var recorder = new MiningLedgerRecorder(spaceSchema);
            bus.Subscribe("mining.ledger", [OreMinedEvent.Topic], async (context, token) =>
            {
                // the same event handled twice (a replay): the row is there once
                await recorder.RecordAsync(context, token);
                await recorder.RecordAsync(context, token);
            });
            await bus.PumpAsync();

            await PublishAsync(source, bus, Fact(1001, 100), commit: true);
            await PublishAsync(source, bus, Fact(1002, 40), commit: false);   // a grant that rolled back announces nothing
            await bus.PumpAsync();

            Assert.Equal(1, await CountAsync(source, $"{spaceSchema}.mining_ledger_entries"));
            var reads = MiningLedgerQueries.Register(new Eve.ReadSide.ReadSide(source, new Dictionary<string, string> { [MiningLedgerQueries.SpaceSchema] = spaceSchema }));
            var eventDate = MiningLedgerStore.FileTimeOf(clock);
            var notYet = await reads.RowsAsync(MiningLedgerQueries.Character, [1001L, eventDate, eventDate - MiningLedgerStore.RetentionFileTimeTicks]);
            Assert.Empty(notYet.Rows);
            var shown = await reads.RowsAsync(MiningLedgerQueries.Character,
                [1001L, eventDate + TenMinutesMs * 10_000, eventDate - MiningLedgerStore.RetentionFileTimeTicks]);
            var row = Assert.Single(shown.Rows);
            object? Value(string name) => row[shown.Columns.ToList().FindIndex(c => c.Name == name)];
            Assert.Equal(100L, Value("quantity"));
            Assert.Equal(2L, Value("quantityWasted"));
            Assert.Equal(11019L, Value("shipTypeID"));
            Assert.Equal(3651L, Value("moduleTypeID"));
            Assert.Equal(30000142L, Value("solarSystemID"));
            Assert.Equal(1230L, Value("typeID"));
            Assert.Equal("ore", Value("yieldKind"));
            Assert.Equal(eventDate, Value("eventDate"));
        }
        finally
        {
            foreach (var schema in new[] { spaceSchema, eventsSchema, schedulerSchema })
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task Entries_older_than_the_retention_are_pruned_when_a_newer_one_is_written_and_the_game_store_rows_are_copied_once()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(connectionString!));
        var spaceSchema = "ml_prune_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await PgSpaceItemStore.MigrateAsync(source, spaceSchema);
            var old = MiningLedgerStore.FileTimeOf(ClockMs);
            var ore = Fact(1001, 100);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await MiningLedgerStore.RecordAsync(connection, transaction, spaceSchema, 1, ore, old);
                await MiningLedgerStore.RecordAsync(connection, transaction, spaceSchema, 2, ore,
                    old + MiningLedgerStore.RetentionFileTimeTicks + 1);
                await transaction.CommitAsync();
            }
            Assert.Equal(1, await CountAsync(source, $"{spaceSchema}.mining_ledger_entries"));

            // the game store's rows, as EVE.js kept them: one recent entry, one older than the retention, and an observer with one
            var table = JsonNode.Parse("""
                {
                  "characters": { "1001": { "entries": [
                    { "entryID": 77, "eventDate": "134100000000000001", "characterID": 1001, "corporationID": 98000001,
                      "solarSystemID": 30000142, "typeID": 1230, "quantity": 9, "quantityWasted": 0, "quantityCritical": 0,
                      "shipTypeID": 11019, "moduleTypeID": 3651, "observerItemID": 0, "yieldKind": "ore",
                      "characterVisibleAfter": "134100000000000001" },
                    { "entryID": 78, "eventDate": "133900000000000000", "characterID": 1001, "corporationID": 98000001,
                      "solarSystemID": 30000142, "typeID": 1230, "quantity": 9, "quantityWasted": 0, "quantityCritical": 0,
                      "shipTypeID": 11019, "moduleTypeID": 3651, "observerItemID": 0, "yieldKind": "ore",
                      "characterVisibleAfter": "133900000000000000" } ] } },
                  "observers": { "100": { "itemID": 100, "corporationID": 98000001, "solarSystemID": 30000142, "itemName": "Zeta",
                    "entries": [ { "entryID": 79, "eventDate": "134100000000000001", "observerVisibleAfter": "134100000000000001" } ] } }
                }
                """)!.AsObject();
            var now = 134100000000000100;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.Equal(2, await MiningLedgerStore.ImportAsync(connection, transaction, spaceSchema, table, now));
                await transaction.CommitAsync();
            }
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.Equal(0, await MiningLedgerStore.ImportAsync(connection, transaction, spaceSchema, table, now));
                await transaction.CommitAsync();
            }

            Assert.Equal(3, await CountAsync(source, $"{spaceSchema}.mining_ledger_entries"));
            Assert.Equal(1, await CountAsync(source, $"{spaceSchema}.mining_ledger_observers"));
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {spaceSchema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }
}
