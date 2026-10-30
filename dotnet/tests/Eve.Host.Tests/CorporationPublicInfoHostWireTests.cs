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

public sealed class CorporationPublicInfoHostWireTests
{
    [PgFact]
    public async Task Corporation_public_info_returns_persisted_client_fields_through_registered_host()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var corpSchema = $"cpi_c_{suffix}";
        var characterSchema = $"cpi_h_{suffix}";
        var walletSchema = $"cpi_w_{suffix}";
        var skillSchema = $"cpi_s_{suffix}";
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
            var store = new CorporationStore(source, characters, wallets,
                new WalletTransactions(source, walletSchema), skills, () => now, corpSchema);
            long founder;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                founder = await characters.InsertAsync(connection, transaction, new NewCharacter(1,
                    $"Public Info Pilot {Guid.NewGuid():N}", 0, 1, 1, 1, 11, 1376, 1000044, null,
                    60000001, 30000001, 20000001, 10000001, null, null));
                await wallets.OpenAsync(connection, transaction, founder, 5_000_000m, 10);
                await transaction.CommitAsync();
            }

            var corporation = await store.CreateAsync(founder,
                new NewCorporation("Wire Corporation", "WIRE", "Public description", "https://corp.example", 0.05m),
                $"public-info:{founder}");
            var service = (IService)new CorpMgrService(store, characters);
            var registry = new ServiceRegistry();
            registry.Register(service);
            Assert.Same(service, registry.Lookup("corpmgr"));

            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = 42, CharacterId = founder };
            session.Set("corpid", new PyLong(corporation.Id));
            var dispatcher = new PacketDispatcher(registry, () => now);

            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket("GetPublicInfo", new PyInt(corporation.Id)), session));
            var response = Result(Assert.Single(capture.Packets));
            Assert.Equal(new PyInt(corporation.Id), Field(response, "corporationID"));
            Assert.Equal(new PyStr("Wire Corporation"), Field(response, "corporationName"));
            Assert.Equal(new PyInt(founder), Field(response, "ceoID"));
            Assert.Equal(new PyStr("Public description"), Field(response, "description"));
            Assert.IsType<PyNone>(Field(response, "allianceID"));
            Assert.Equal(PyBool.False, Field(response, "allowWar"));
            Assert.Equal(new PyInt(1), Field(response, "memberCount"));

            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket("GetPublicInfo", new PyInt(corporation.Id + 999)), session));
            Assert.IsType<PyNone>(Result(capture.Packets[^1]));
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

    private static PyValue ClientCallPacket(string method, PyValue corporationId)
    {
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(1), ClientId: new PyInt(1));
        var destination = new MachoAddress(AddressKind.Node, Service: "corpmgr",
            CallId: new PyInt(1), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        var body = new PyTuple([PyNone.Instance, new PyStr(method), new PyTuple([corporationId]), kwargs]);
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

    private static PyValue Field(PyValue value, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries
            .Single(entry => entry.Key == new PyStr(name)).Value;
}
