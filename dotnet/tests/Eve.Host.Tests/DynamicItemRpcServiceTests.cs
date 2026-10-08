using Eve.Host;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class DynamicItemRpcServiceTests
{
    [Fact]
    public async Task GetDynamicItemInfo_reads_persisted_dynamic_metadata_as_a_keyval()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        await using var source = NpgsqlDataSource.Create(pgUrl!);
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());
        var dogma = await DogmaStatic.LoadAsync(sde);
        await dogma.EnsureTypesAsync([587]);
        var staticType = Assert.IsType<TypeDef>(dogma.Type(587));
        Assert.NotEmpty(staticType.Attributes);
        var schema = "rpc_dyn_" + Guid.NewGuid().ToString("N");
        await PgItemStore.MigrateAsync(source, schema);
        try
        {
            var items = new PgItemStore(source, schema);
            var customInfo = """
                {"evejsDynamicItem":{"itemID":0,"resultTypeID":587,"sourceTypeID":587,"sourceItemID":120,"mutatorTypeID":48948,"mutatorItemID":121,"characterID":20,"createdAtMs":1700000000000,"attributes":{"999999":7.5},"modifiers":{"64":1.25},"baseAttributes":{"38":9}}}
                """;
            long itemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                itemId = await items.CreateAsync(connection, transaction, new NewItem(587, 20, 600, 4, CustomInfo: customInfo));
                await transaction.CommitAsync();
            }
            var storedItem = await items.GetAsync(itemId);
            Assert.NotNull(storedItem);
            Assert.Equal(customInfo, storedItem.CustomInfo);

            var service = new DynamicItemRpcService(items, dogma: dogma, isKnownDynamicResultType: typeId => typeId == 587);
            var session = new ClientSession();
            session.Set("charid", new PyInt(20));
            var result = Assert.IsType<PyObject>(await service.CallAsync(new ServiceCall
            {
                Method = "GetDynamicItemInfo",
                Args = [new PyLong(itemId)],
                Session = session,
            }));
            var fields = Assert.IsType<PyDict>(result.Args);
            Assert.Equal(new PyInt(itemId), Field(fields, "itemID"));
            Assert.Equal(new PyInt(587), Field(fields, "typeID"));
            Assert.Equal(new PyInt(587), Field(fields, "sourceTypeID"));
            Assert.Equal(new PyInt(20), Field(fields, "characterID"));
            var attributes = Assert.IsType<PyDict>(Field(fields, "attributes"));
            Assert.Contains(attributes.Entries, pair => pair.Key == new PyInt(999999) && pair.Value == new PyReal(7.5));
            foreach (var (attributeId, value) in staticType.Attributes)
                Assert.Contains(attributes.Entries, pair => pair.Key == new PyInt(attributeId) && pair.Value == Py.Number(value));
            Assert.Contains(Assert.IsType<PyDict>(Field(fields, "modifiers")).Entries,
                pair => pair.Key == new PyInt(64) && pair.Value == new PyReal(1.25));
            Assert.True(((IService)service).HasMethod("GetDynamicItemInfo"));

            long fallbackItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                fallbackItemId = await items.CreateAsync(connection, transaction, new NewItem(587, 20, 600, 4));
                await transaction.CommitAsync();
            }
            var fallback = Assert.IsType<PyObject>(await service.CallAsync(new ServiceCall
            {
                Method = "GetDynamicItemInfo",
                Args = [new PyLong(fallbackItemId)],
                Session = session,
            }));
            var fallbackFields = Assert.IsType<PyDict>(fallback.Args);
            Assert.Equal(new PyInt(587), Field(fallbackFields, "mutatorTypeID"));
            Assert.Equal(new PyInt(0), Field(fallbackFields, "sourceItemID"));
            Assert.Empty(Assert.IsType<PyDict>(Field(fallbackFields, "modifiers")).Entries);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static PyValue Field(PyDict fields, string name) =>
        fields.Entries.Single(pair => JsSemantics.StringValue(pair.Key) == name).Value;
}
