using Eve.Data;
using Eve.Inventory;
using Eve.Sde;
using Eve.Universe;
using Npgsql;

namespace Eve.Services.Space.Tests;

public sealed class UndockContrabandTests
{
    private const long CharacterId = 91_000_001;
    private const long OtherCharacterId = 91_000_002;
    private const int ContrabandA = 92_000_001;
    private const int ContrabandB = 92_000_002;
    private const int FittedType = 92_000_003;
    private const int OtherShipType = 92_000_004;
    private const int ForeignOwnerType = 92_000_005;
    private const int UnlistedType = 92_000_006;
    private const int FactionA = 93_000_001;
    private const int FactionB = 93_000_002;
    private const int OtherFaction = 93_000_003;
    private const int SystemAHigh = 94_000_001;
    private const int SystemALow = 94_000_002;
    private const int SystemBMid = 94_000_003;
    private const int SystemBHigh = 94_000_004;
    private const int SystemNoFaction = 94_000_005;
    private const int SystemOtherFaction = 94_000_006;
    private const int ShipType = 95_000_001;

    [PgFact]
    public async Task Inspection_uses_SDE_faction_thresholds_and_current_owned_ship_cargo()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Rig.Url!));
        var itemSchema = "undock_items_" + Guid.NewGuid().ToString("N");
        var sdeSchema = "undock_sde_" + Guid.NewGuid().ToString("N");

        try
        {
            await ExecuteAsync(source, $"CREATE SCHEMA {itemSchema}");
            await ExecuteAsync(source, $"CREATE SCHEMA {sdeSchema}");
            await ExecuteAsync(source, $"CREATE TABLE {sdeSchema}.\"contrabandTypes\" (\"key\" text NOT NULL, doc jsonb NOT NULL, ord integer NOT NULL)");
            await PgItemStore.MigrateAsync(source, itemSchema);
            await SeedSdeAsync(source, sdeSchema);

            var systems = new SolarSystemCatalog(
            [
                new SolarSystemInfo(SystemAHigh, "a-high", 1, 1, 0.6, FactionA),
                new SolarSystemInfo(SystemALow, "a-low", 1, 1, 0.3, FactionA),
                new SolarSystemInfo(SystemBMid, "b-mid", 1, 1, 0.75, FactionB),
                new SolarSystemInfo(SystemBHigh, "b-high", 1, 1, 0.85, FactionB),
                new SolarSystemInfo(SystemNoFaction, "no-faction", 1, 1, 0.95),
                new SolarSystemInfo(SystemOtherFaction, "other-faction", 1, 1, 0.95, OtherFaction),
            ]);
            var items = new PgItemStore(source, itemSchema);
            var ships = await CreateItemsAsync(
                source,
                items,
                new NewItem(ShipType, CharacterId, SystemAHigh, 0, Singleton: true),
                new NewItem(ShipType, CharacterId, SystemAHigh, 0, Singleton: true),
                new NewItem(ShipType, OtherCharacterId, SystemAHigh, 0, Singleton: true));

            var shipId = ships[0];
            var otherShipId = ships[1];
            var foreignShipId = ships[2];
            var inspector = await UndockContraband.LoadAsync(new SdeReader(source, sdeSchema), items, systems);

            Assert.Empty(await inspector.InspectAsync(CharacterId, shipId, SystemAHigh));

            await CreateItemsAsync(
                source,
                items,
                new NewItem(ContrabandA, CharacterId, shipId, ItemFlags.Cargo),
                new NewItem(ContrabandA, CharacterId, shipId, ItemFlags.Cargo),
                new NewItem(ContrabandB, CharacterId, shipId, ItemFlags.Cargo),
                new NewItem(FittedType, CharacterId, shipId, ItemFlags.HiSlot0),
                new NewItem(ForeignOwnerType, OtherCharacterId, shipId, ItemFlags.Cargo),
                new NewItem(OtherShipType, CharacterId, otherShipId, ItemFlags.Cargo),
                new NewItem(ContrabandA, CharacterId, foreignShipId, ItemFlags.Cargo),
                new NewItem(UnlistedType, CharacterId, shipId, ItemFlags.Cargo));

            Assert.Equal(new[] { ContrabandA }, await inspector.InspectAsync(CharacterId, shipId, SystemAHigh));
            Assert.Empty(await inspector.InspectAsync(CharacterId, shipId, SystemALow));
            Assert.Equal(new[] { ContrabandB }, await inspector.InspectAsync(CharacterId, shipId, SystemBMid));
            Assert.Equal(new[] { ContrabandA, ContrabandB }, await inspector.InspectAsync(CharacterId, shipId, SystemBHigh));
            Assert.Empty(await inspector.InspectAsync(CharacterId, shipId, SystemNoFaction));
            Assert.Empty(await inspector.InspectAsync(CharacterId, shipId, SystemOtherFaction));
            Assert.Empty(await inspector.InspectAsync(CharacterId, foreignShipId, SystemAHigh));

            var reloadedItems = new PgItemStore(source, itemSchema);
            var cargoBeforeInspection = await reloadedItems.InLocationAsync(shipId, CharacterId);
            var reloadedInspector = await UndockContraband.LoadAsync(new SdeReader(source, sdeSchema), reloadedItems, systems);
            Assert.Equal(new[] { ContrabandA }, await reloadedInspector.InspectAsync(CharacterId, shipId, SystemAHigh));
            Assert.Equal(cargoBeforeInspection, await reloadedItems.InLocationAsync(shipId, CharacterId));
        }
        finally
        {
            await ExecuteAsync(source, $"DROP SCHEMA IF EXISTS {sdeSchema} CASCADE");
            await ExecuteAsync(source, $"DROP SCHEMA IF EXISTS {itemSchema} CASCADE");
        }
    }

    private static async Task SeedSdeAsync(NpgsqlDataSource source, string schema)
    {
        var rows = new (int TypeId, string Document)[]
        {
            (ContrabandA, $$"""{"factions":[{"_key":{{FactionA}},"confiscateMinSec":0.4,"fineByValue":0.1,"standingLoss":0.02,"attackMinSec":0.2},{"_key":{{FactionB}},"confiscateMinSec":0.8,"fineByValue":0.3,"standingLoss":0.04,"attackMinSec":0.6}]}"""),
            (ContrabandB, $$"""{"factions":[{"_key":{{FactionB}},"confiscateMinSec":0.7,"fineByValue":0.2,"standingLoss":0.03,"attackMinSec":0.5}]}"""),
            (FittedType, $$"""{"factions":[{"_key":{{FactionA}},"confiscateMinSec":0.4,"fineByValue":0.1,"standingLoss":0.02,"attackMinSec":0.2}]}"""),
            (OtherShipType, $$"""{"factions":[{"_key":{{FactionB}},"confiscateMinSec":0.7,"fineByValue":0.2,"standingLoss":0.03,"attackMinSec":0.5}]}"""),
            (ForeignOwnerType, $$"""{"factions":[{"_key":{{FactionA}},"confiscateMinSec":0.4,"fineByValue":0.1,"standingLoss":0.02,"attackMinSec":0.2}]}"""),
            (UnlistedType, $$"""{"factions":[{"_key":"bad","confiscateMinSec":0.4},{"_key":{{FactionA}},"confiscateMinSec":"bad"}]}"""),
        };

        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        for (var index = 0; index < rows.Length; index++)
        {
            await using var command = new NpgsqlCommand(
                $$"""INSERT INTO {{schema}}."contrabandTypes" ("key", doc, ord) VALUES ($1, $2::jsonb, $3)""",
                connection,
                transaction);
            command.Parameters.AddWithValue(rows[index].TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(rows[index].Document);
            command.Parameters.AddWithValue(index);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<long[]> CreateItemsAsync(
        NpgsqlDataSource source,
        PgItemStore items,
        params NewItem[] newItems)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var ids = new long[newItems.Length];
        for (var index = 0; index < newItems.Length; index++)
        {
            ids[index] = await items.CreateAsync(connection, transaction, newItems[index]);
        }

        await transaction.CommitAsync();
        return ids;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}