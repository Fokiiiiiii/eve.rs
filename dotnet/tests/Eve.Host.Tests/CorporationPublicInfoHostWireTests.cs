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
    public async Task Corporation_identity_and_row_reads_use_persisted_records_through_registered_host()
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
            long member;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                founder = await characters.InsertAsync(connection, transaction, new NewCharacter(1,
                    $"Identity Pilot {Guid.NewGuid():N}", 0, 1, 1, 1, 11, 1376, 1000044, null,
                    60000001, 30000001, 20000001, 10000001, null, null));
                member = await characters.InsertAsync(connection, transaction, new NewCharacter(1,
                    $"Identity Member {Guid.NewGuid():N}", 0, 1, 1, 1, 11, 1376, 1000044, null,
                    60000001, 30000001, 20000001, 10000001, null, null));
                await wallets.OpenAsync(connection, transaction, founder, 5_000_000m, 10);
                await transaction.CommitAsync();
            }

            var corporation = await store.CreateAsync(founder,
                new NewCorporation("Identity Corporation", "IDENT", "Persisted description", "", 0.05m),
                $"identity:{founder}");
            var application = await store.ApplyAsync(member, corporation.Id, "");
            await store.RespondAsync(founder, application, 6, "", "");
            await store.RespondAsync(member, application, 2, "", "");
            var service = (IService)new CorpMgrService(store, characters);
            var registry = new ServiceRegistry();
            registry.Register(service);
            Assert.Same(service, registry.Lookup("corpmgr"));

            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = 43, CharacterId = founder };
            session.Set("corpid", new PyLong(corporation.Id));
            var dispatcher = new PacketDispatcher(registry, () => now);

            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket(
                "GetCorporationIDForCharacter", new PyInt(member)), session));
            Assert.Equal(new PyInt(corporation.Id), Result(capture.Packets[^1]));

            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket(
                "GetCorporationIDForCharacter", new PyLong(long.MaxValue)), session));
            Assert.IsType<PyNone>(Result(capture.Packets[^1]));

            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket(
                "GetCorporations", new PyInt(corporation.Id)), session));
            var row = Assert.IsType<PyObject>(Result(capture.Packets[^1]));
            Assert.Equal(new PyStr("util.Row"), row.Name);
            var rowFields = Assert.IsType<PyDict>(row.Args);
            var header = Assert.IsType<PyTuple>(Field(new PyObject(new PyStr("util.Row"), rowFields), "header")).Items;
            var values = Assert.IsType<PyTuple>(Field(new PyObject(new PyStr("util.Row"), rowFields), "line")).Items;
            Assert.Equal(header.Count, values.Count);
            int Column(string name) => header.Select((value, index) => (value, index))
                .Single(entry => entry.value == new PyStr(name)).index;
            Assert.Equal(new PyInt(corporation.Id), values[Column("corporationID")]);
            Assert.Equal(new PyStr("Identity Corporation"), values[Column("corporationName")]);
            Assert.Equal(new PyInt(2), values[Column("memberCount")]);

            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket(
                "GetCorporations", new PyInt(corporation.Id + 999)), session));
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

    private static PyValue ClientCallPacket(string method, params PyValue[] arguments)
    {
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(1), ClientId: new PyInt(1));
        var destination = new MachoAddress(AddressKind.Node, Service: "corpmgr",
            CallId: new PyInt(1), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        var body = new PyTuple([PyNone.Instance, new PyStr(method), new PyTuple(arguments), kwargs]);
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
