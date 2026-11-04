using Eve.Characters;
using Eve.Data;
using Eve.Events;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Ships;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class TournamentArenaPersistenceTests
{
    [PgFact]
    public async Task Arena_import_is_atomic_idempotent_and_refuses_changed_or_invalid_configuration()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var chars = "tch_" + suffix; var inventory = "tin_" + suffix; var space = "tsp_" + suffix;
        var schedulerSchema = "tsc_" + suffix; var eventSchema = "tev_" + suffix; var tournamentSchema = "ttm_" + suffix;
        const int system = 30_000_142, rollbackSystem = 30_000_144;
        const long ownerId = 140_000_801;
        try
        {
            await PgCharacterStore.MigrateAsync(source, chars);
            await PgItemStore.MigrateAsync(source, inventory);
            await Migrations.ApplyAsync(source, space, PgSpaceItemStore.MigrationSet().Where(m => m.Name != "0022_tournament_beacon_kind"));
            Assert.Contains("0022_tournament_beacon_kind", await PgSpaceItemStore.MigrateAsync(source, space));
            Assert.Empty(await PgSpaceItemStore.MigrateAsync(source, space));
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventSchema);
            await PersistenceTournamentArenas.MigrateAsync(source, tournamentSchema);
            await using (var seed = source.CreateCommand($"""
                INSERT INTO {chars}.characters (id,account_id,name,name_key,gender,race_id,bloodline_id,ancestry_id,school_id,type_id,
                    corporation_id,station_id,home_station_id,solar_system_id,constellation_id,region_id)
                VALUES ({ownerId},1,'Arena Owner','arena owner',0,1,1,1,1,1,98000001,60000001,60000001,{system},20000001,10000001)
                """)) await seed.ExecuteNonQueryAsync();
            var scheduler = new DurableScheduler(source, () => 1_700_000_000_000, schema: schedulerSchema);
            var events = new EventBus(source, scheduler, schema: eventSchema);
            var itemStore = new PgItemStore(source, inventory);
            var deployed = new PgDeployedStore(source, space);
            var characters = new PgCharacterStore(source, chars);
            var store = new PersistenceTournamentArenas(source, new SdeReader(source), characters, itemStore, deployed, events, tournamentSchema);
            var input = new TournamentArenaImport(system, ownerId,
                [new TournamentBeaconImport(10645, "Arena Center", [1, 2, 3], 5)]);
            await Task.WhenAll(store.ImportIfMissingAsync(input), store.ImportIfMissingAsync(input));
            var restored = new PersistenceTournamentArenas(source, new SdeReader(source), characters, itemStore, deployed, events, tournamentSchema);
            var arena = Assert.Single(await restored.ReadSystemsAsync());
            var id = Assert.Single(arena.BeaconIds);
            Assert.Equal(system, arena.SolarSystemId);
            var item = Assert.IsType<Item>(await itemStore.GetAsync(id));
            var body = Assert.IsType<DeployedRecord>(await deployed.GetAsync(id));
            Assert.Equal(10645, item.TypeId); Assert.Equal(system, item.LocationId);
            Assert.Equal(PersistenceTournamentArenas.BeaconKind, body.Kind); Assert.Equal(new double[] { 1, 2, 3 }, body.Position);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportIfMissingAsync(input with
            { Beacons = [new TournamentBeaconImport(10645, "Arena Center", [1, 2, 3], 6)] }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ImportIfMissingAsync(input with
            { SolarSystemId = rollbackSystem, Beacons = [new TournamentBeaconImport(587, "Not a beacon", [1, 2, 3], 5)] }));
            // A missing event schema forces publication to fail after the item and deployed inserts.
            // The common transaction must roll back every preceding write.
            var failedScheduler = new DurableScheduler(source, () => 1_700_000_000_000, schema: "tfail_" + suffix);
            var failedEvents = new EventBus(source, failedScheduler, schema: "tmissing_" + suffix);
            var failing = new PersistenceTournamentArenas(source, new SdeReader(source), characters, itemStore, deployed, failedEvents, tournamentSchema);
            await Assert.ThrowsAnyAsync<Exception>(() => failing.ImportIfMissingAsync(input with { SolarSystemId = rollbackSystem }));
            await using (var missing = source.CreateCommand($"SELECT count(*) FROM {tournamentSchema}.arena_systems WHERE system_id={rollbackSystem}"))
                Assert.Equal(0L, (long)(await missing.ExecuteScalarAsync())!);
            await using var counts = source.CreateCommand($"SELECT (SELECT count(*) FROM {inventory}.items), (SELECT count(*) FROM {space}.deployed), (SELECT count(*) FROM {eventSchema}.outbox)");
            await using var reader = await counts.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt64(0)); Assert.Equal(1, reader.GetInt64(1)); Assert.Equal(1, reader.GetInt64(2));
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {chars},{inventory},{space},{schedulerSchema},{eventSchema},{tournamentSchema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
