using Eve.Planets;
using Npgsql;

namespace Eve.Planets.Tests;

/// <summary>A test that needs PostgreSQL: skipped (not passed) when EVE_PG_URL is not set.</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null)
            Skip = "EVE_PG_URL is not set";
    }
}

public sealed class StoreTests
{
    private static NpgsqlDataSource Source() => NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));

    private static string Schema() => "pl_" + Guid.NewGuid().ToString("N")[..12];

    [PgFact]
    public async Task A_colony_round_trips_through_its_document_and_keeps_its_route_counter()
    {
        await using var source = Source();
        var schema = Schema();
        await PlanetStore.MigrateAsync(source, schema);
        try
        {
            var store = new PlanetStore(source, schema);
            var colony = Fixtures.Chain();
            colony.CreateRoute(colony.NewRouteId(), [3, 2], TestCatalog.AqueousLiquids, 3);
            var doc = ColonyDocument.From(colony);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await store.SaveAsync(connection, transaction, colony.OwnerId, colony.PlanetId, doc, 1);
                await transaction.CommitAsync();
            }
            var loaded = await store.ReadAsync(colony.OwnerId, colony.PlanetId);
            Assert.NotNull(loaded);
            var back = loaded!.ToColony(TestCatalog.Build(), new TestCatalog.ConstantField(1));
            Assert.Equal(colony.Pins.Count, back.Pins.Count);
            Assert.Equal(colony.Routes.Count, back.Routes.Count);
            Assert.Equal(colony.RouteCounter, back.RouteCounter);
            Assert.Equal(doc.ToJson(), ColonyDocument.From(back).ToJson());
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task A_launch_is_listed_until_it_is_deleted_or_its_time_is_up()
    {
        await using var source = Source();
        var schema = Schema();
        await PlanetStore.MigrateAsync(source, schema);
        try
        {
            var store = new PlanetStore(source, schema);
            await using var connection = await source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var id = await store.InsertLaunchAsync(connection, transaction,
                new LaunchRecord(0, 7, 40000011, 30000001, 0, true, 100, 1000, 1.0, 2.0, 3.0, "{\"3645\":5}"));
            await transaction.CommitAsync();
            Assert.Single(await store.LaunchesOfAsync(7, 500));
            Assert.Empty(await store.LaunchesOfAsync(7, 2000));
            Assert.False(await store.DeleteLaunchAsync(8, id));
            Assert.True(await store.DeleteLaunchAsync(7, id));
            Assert.Empty(await store.LaunchesOfAsync(7, 500));
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task The_static_data_of_the_pins_comes_from_the_sde_tables()
    {
        await using var source = Source();
        var catalog = await PlanetCatalogLoader.LoadAsync(source);
        await using (var connection = await source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            "SELECT doc::text FROM sde.types WHERE key = $1",
            connection)
        {
            Parameters = { new() { Value = TestCatalog.CommandCenter.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
        })
        {
            var storedTypeJson = (string?)await command.ExecuteScalarAsync();
            Assert.NotNull(storedTypeJson);
            using var storedType = System.Text.Json.JsonDocument.Parse(storedTypeJson!);
            var type = catalog.Type(TestCatalog.CommandCenter);
            Assert.Equal(storedType.RootElement.GetProperty("groupID").GetInt32(), type.GroupId);
            Assert.Equal(storedType.RootElement.GetProperty("volume").GetDouble(), type.Volume);
            Assert.Equal((long)storedType.RootElement.GetProperty("basePrice").GetDouble(), type.BasePrice);
            Assert.Equal(storedType.RootElement.GetProperty("capacity").GetDouble(), type.Capacity);
        }
        // capacities are on the type record: 500 m3 command center, 12000 m3 storage facility (sde.types.capacity)
        Assert.Equal(500, catalog.Type(TestCatalog.CommandCenter).Capacity);
        Assert.Equal(12000, catalog.Type(TestCatalog.Storage).Capacity);
        // the ECU has no decay entry of its own: the attribute default applies (sde.dogmaAttributes 1683 = 0.012)
        Assert.Equal(0.012, catalog.Type(TestCatalog.Ecu).Attr(PlanetAttr.EcuDecayFactor));
        Assert.Equal(PinKind.Ecu, catalog.KindOf(TestCatalog.Ecu));
        Assert.Equal(TestCatalog.Link, catalog.LinkTypeId);
        var schematic = catalog.Schematic(TestCatalog.Schematic);
        Assert.NotNull(schematic);
        Assert.Equal(3000, schematic!.Inputs[TestCatalog.AqueousLiquids]);
        // a real barren planet of the data (sde.mapPlanets 40486618): type 2016, radius 2310000 m
        var planet = await PlanetCatalogLoader.PlanetAsync(source, 40486618);
        Assert.NotNull(planet);
        Assert.Equal(TestCatalog.Barren, planet!.TypeId);
        Assert.Equal(2310000, planet.Radius);
    }
}
