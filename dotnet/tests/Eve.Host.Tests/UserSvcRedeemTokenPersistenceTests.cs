using Eve.GameStore;
using Eve.Host;
using Eve.Data;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;
using System.Text.Json.Nodes;

namespace Eve.Host.Tests;

public sealed class UserSvcRedeemTokenPersistenceTests
{
    [Fact]
    public async Task Create_redeem_token_qa_persists_token_and_returns_evejs_payload()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(pgUrl!));
        var schema = "user_redeem_" + Guid.NewGuid().ToString("N");
        await using var store = new PersistenceStore(source, schema);
        try
        {
            await store.EnsureSchemaAsync();
            await using var owner = await OwnerSession.StartAsync(
                store, PersistenceRoles.World, "user-redeem-" + Guid.NewGuid().ToString("N"));
            OwnerFence ownerFence = owner.Fence;
            var runtime = new PersistenceNewEdenStoreRuntime(
                source, store, ownerFence, null!, null!, null!, null!, null!,
                () => 1_700_000_000_123,
                _ => Task.FromResult(new JsonObject()));
            var service = (IService)(object)new RedeemService(
                runtime,
                userRpcHandler: async (rpc, cancellationToken) => rpc.Method == "CreateRedeemTokenQA"
                    ? await runtime.CreateRedeemTokenQAAsync(rpc, cancellationToken).ConfigureAwait(false)
                    : null);
            var call = new ServiceCall
            {
                Method = "CreateRedeemTokenQA",
                Args = [],
                Kwargs = new PyDict(
                [
                    new(new PyStr("typeID"), new PyInt(34)),
                    new(new PyStr("quantity"), new PyInt(2)),
                    new(new PyStr("expiry"), new PyStr("133801234567890123")),
                    new(new PyStr("label"), new PyStr("QA token")),
                    new(new PyStr("description"), new PyStr("persisted")),
                    new(new PyStr("blueprintRuns"), new PyInt(3)),
                    new(new PyStr("blueprintMaterialLevel"), new PyInt(2)),
                    new(new PyStr("blueprintProductivityLevel"), new PyInt(4)),
                ]),
                Session = new ClientSession { UserId = 35 },
            };

            var payload = Assert.IsType<PyObject>(await service.CallAsync(call));
            var payloadFields = Assert.IsType<PyDict>(payload.Args);
            Assert.Equal(new PyInt(700_000_123), Field(payloadFields, "tokenID"));
            Assert.Equal(new PyInt(34), Field(payloadFields, "typeID"));
            Assert.Equal(new PyInt(2), Field(payloadFields, "quantity"));
            Assert.Equal(new PyStr("QA token"), Field(payloadFields, "label"));
            Assert.IsType<PyObject>(Field(payloadFields, "blueprintInfo"));

            await service.CallAsync(call);
            var stored = Assert.IsType<PyList>(await runtime.GetRedeemTokensAsync(35, CancellationToken.None));
            Assert.Equal(2, stored.Items.Count);
            Assert.Equal(new PyLong(700_000_123), Field(Assert.IsType<PyDict>(stored.Items[0]), "tokenID"));
            Assert.Equal(new PyLong(700_000_124), Field(Assert.IsType<PyDict>(stored.Items[1]), "tokenID"));
            Assert.Equal(new PyLong(133801234567890123), Field(Assert.IsType<PyDict>(stored.Items[0]), "expireDateTime"));
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static PyValue Field(PyDict fields, string name) =>
        fields.Entries.Single(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == name).Value;
}
