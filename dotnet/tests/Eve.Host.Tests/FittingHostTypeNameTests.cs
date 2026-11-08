using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class FittingHostTypeNameTests
{
    [Fact]
    public async Task A_non_ship_type_error_uses_the_english_name_from_the_loaded_sde()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(connectionString!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());
        var dogma = await DogmaStatic.LoadAsync(sde);
        var schema = "fitname_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await PgModuleStateStore.MigrateAsync(source, schema);
            var store = new PgSavedFittingStore(source, schema);
            var host = new FittingHost(dogma, null!, new CharacterSessions());
            var calls = new FittingMgrCalls(store, host, () => 1_700_000_000_000);
            IService service = new CharFittingMgrService(calls);
            var session = new ClientSession { CharacterId = 140_300_001 };
            session.Set("charid", new PyInt(140_300_001));
            var fitting = Py.KeyVal(
                ("name", new PyStr("not a ship")),
                ("description", new PyStr("text")),
                ("shipTypeID", new PyInt(2046)),
                ("fitData", new PyList([Py.Tuple(new PyInt(2046), new PyInt(11), new PyInt(1))])));

            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
            {
                Method = "SaveFitting",
                Args = [new PyInt(140_300_001), fitting],
                Session = session,
            }).AsTask());
            var errorValues = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
            var arguments = ((PyDict)errorValues[1]).Entries.ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);

            Assert.Equal("InvalidFittingDataShipNotShip", error.Error.UserErrorMessage);
            Assert.Equal(new PyStr("Damage Control I"), arguments["typeName"]);
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
