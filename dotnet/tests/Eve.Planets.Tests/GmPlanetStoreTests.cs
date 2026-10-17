using Eve.Planets;
using Npgsql;

namespace Eve.Planets.Tests;

/// <summary>
/// The game master's planet calls against PostgreSQL: a colony is read, the call is applied as the planet service applies it, and the saved colony is read
/// back with what the client is told (the forced program, the commodity, the command center type).
/// </summary>
public sealed class GmPlanetStoreTests
{
    private static NpgsqlDataSource Source() => NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));

    private static string Schema() => "pl_" + Guid.NewGuid().ToString("N")[..12];

    private static async Task SaveAsync(NpgsqlDataSource source, PlanetStore store, Colony colony)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await store.SaveAsync(connection, transaction, colony.OwnerId, colony.PlanetId, ColonyDocument.From(colony), 1);
        await transaction.CommitAsync();
    }

    private static async Task<Colony> LoadAsync(PlanetStore store, Colony like)
    {
        var loaded = await store.ReadAsync(like.OwnerId, like.PlanetId);
        return loaded!.ToColony(TestCatalog.Build(), new TestCatalog.ConstantField(1));
    }

    [PgFact]
    public async Task A_game_master_program_and_commodity_are_saved_and_read_back_as_the_client_is_told()
    {
        await using var source = Source();
        var schema = Schema();
        await PlanetStore.MigrateAsync(source, schema);
        try
        {
            var store = new PlanetStore(source, schema);
            await SaveAsync(source, store, Fixtures.Chain());

            var colony = await LoadAsync(store, Fixtures.Chain());
            colony.ApplyCommands(
            [
                new GmForceInstallProgramCommand(3, TestCatalog.AqueousLiquids, PlanetRules.Sec * 60, 2 * PlanetRules.Hour, 100, 1.0),
                new GmAddCommodityCommand(2, TestCatalog.AqueousLiquids, 100),
            ], Fixtures.Ctx());
            await SaveAsync(source, store, colony);

            var back = await LoadAsync(store, Fixtures.Chain());
            Assert.Equal(TestCatalog.AqueousLiquids, back.Pins[3].ProgramType);
            Assert.Equal(100, back.Pins[3].QtyPerCycle);
            Assert.Equal(PlanetRules.Sec * 60, back.Pins[3].CycleTime);
            Assert.Equal(back.CurrentSimTime + 2 * PlanetRules.Hour, back.Pins[3].ExpiryTime);
            Assert.Equal(100, back.Pins[2].Contents[TestCatalog.AqueousLiquids]);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task A_game_master_command_center_conversion_is_saved_to_the_type_of_its_planet()
    {
        await using var source = Source();
        var schema = Schema();
        await PlanetStore.MigrateAsync(source, schema);
        try
        {
            var store = new PlanetStore(source, schema);
            var temperate = new Colony(TestCatalog.Build(), new TestCatalog.ConstantField(1.0), TestCatalog.PlanetId, 11, TestCatalog.Radius, TestCatalog.ColonyOwner)
            { CurrentSimTime = Fixtures.Now };
            temperate.ApplyCommands([new CreatePinCommand(1, TestCatalog.TemperateCommandCenter, 0.5, 1.0)], Fixtures.Ctx());
            temperate.Pins[1].TypeId = TestCatalog.CommandCenter;
            await SaveAsync(source, store, temperate);

            var loaded = await LoadAsync(store, temperate);
            Assert.Equal(TestCatalog.CommandCenter, loaded.Pins[1].TypeId);
            loaded.ApplyCommands([new GmConvertCommandCenterCommand(1)], Fixtures.Ctx());
            await SaveAsync(source, store, loaded);

            Assert.Equal(TestCatalog.TemperateCommandCenter, (await LoadAsync(store, temperate)).Pins[1].TypeId);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
