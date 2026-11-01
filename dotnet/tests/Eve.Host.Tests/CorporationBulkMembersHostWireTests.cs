using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class CorporationBulkMembersHostWireTests
{
    [PgFact]
    public async Task Registered_corp_registry_returns_member_rows_for_ids_beyond_one_database_batch()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var corpSchema = $"cbm_c_{suffix}";
        var characterSchema = $"cbm_h_{suffix}";
        var walletSchema = $"cbm_w_{suffix}";
        var skillSchema = $"cbm_s_{suffix}";
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        try
        {
            await PgCharacterStore.MigrateAsync(source, characterSchema);
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await PgSkillStore.MigrateAsync(source, skillSchema);
            await CorporationStore.MigrateAsync(source, corpSchema);

            var characters = new PgCharacterStore(source, characterSchema);
            var wallets = new PgWalletStore(source, walletSchema);
            var skills = new PgSkillStore(source, skillSchema);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var store = new CorporationStore(source, characters, wallets, new WalletTransactions(source, walletSchema),
                skills, () => now, corpSchema);
            long founder;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                founder = await characters.InsertAsync(connection, transaction, new NewCharacter(1,
                    $"Bulk Members Pilot {Guid.NewGuid():N}", 0, 1, 1, 1, 11, 1376, 1000044, null,
                    60000001, 30000001, 20000001, 10000001, null, null));
                await wallets.OpenAsync(connection, transaction, founder, 5_000_000m, 10);
                await transaction.CommitAsync();
            }
            var corporation = await store.CreateAsync(founder,
                new NewCorporation("Bulk Member Test", "BMT", "", "", 0.05m), $"bulk-member:{founder}");
            var service = (IService)new CorpRegistryService(store, characters, () => now);
            var registry = new ServiceRegistry();
            registry.Register(service);
            Assert.Same(service, registry.Lookup("corpRegistry"));

            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = 42, CharacterId = founder };
            session.Set("charid", new PyLong(founder));
            session.Set("corpid", new PyLong(corporation.Id));
            var dispatcher = new PacketDispatcher(registry, () => now);
            var ids = new PyList(Enumerable.Range(1, 500)
                .Select(id => (PyValue)new PyLong(20_000_000L + id))
                .Append(new PyLong(founder)).ToArray());
            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket("GetMembersByIds", ids), session));

            var result = Assert.IsType<PyList>(Result(Assert.Single(capture.Packets)));
            var row = Assert.IsType<PyPackedRow>(Assert.Single(result.Items));
            Assert.Equal(founder, Assert.IsType<PyInt>(row.Values[0]).Value);
        }
        finally
        {
            foreach (var schema in new[] { corpSchema, characterSchema, walletSchema, skillSchema })
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static PyValue ClientCallPacket(string method, PyValue ids)
    {
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(1), ClientId: new PyInt(1));
        var destination = new MachoAddress(AddressKind.Node, Service: "corpRegistry",
            CallId: new PyInt(1), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        var body = new PyTuple([PyNone.Instance, new PyStr(method), new PyTuple([ids]), kwargs]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([
                new PyInt(MachoMessage.CallReq), source.Encode(), destination.Encode(),
                new PyInt(7), payload, new PyDict([]),
                PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
                PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
            ]));
    }

    private static PyValue Result(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<PyInt>(packet[0]).Value);
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(packet[4]).Items[0]).Decoded!;
    }
}
