using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class CorporationAssetInventoryHostWireTests
{
    [PgFact]
    public async Task Corporation_asset_locations_read_persisted_items_through_registered_host()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var corpSchema = $"cai_c_{suffix}";
        var characterSchema = $"cai_h_{suffix}";
        var walletSchema = $"cai_w_{suffix}";
        var skillSchema = $"cai_s_{suffix}";
        var itemSchema = $"cai_i_{suffix}";
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        try
        {
            await PgCharacterStore.MigrateAsync(source, characterSchema);
            await PgWalletStore.MigrateAsync(source, walletSchema);
            await PgSkillStore.MigrateAsync(source, skillSchema);
            await CorporationStore.MigrateAsync(source, corpSchema);
            await CorporationOfficeStore.MigrateAsync(source, corpSchema);
            await PgItemStore.MigrateAsync(source, itemSchema);

            var characters = new PgCharacterStore(source, characterSchema);
            var wallets = new PgWalletStore(source, walletSchema);
            var skills = new PgSkillStore(source, skillSchema);
            var items = new PgItemStore(source, itemSchema);
            var offices = new CorporationOfficeStore(source, corpSchema);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var corporations = new CorporationStore(source, characters, wallets,
                new WalletTransactions(source, walletSchema), skills, () => now, corpSchema);
            long founder;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                founder = await characters.InsertAsync(connection, transaction, new NewCharacter(1,
                    $"Asset Pilot {Guid.NewGuid():N}", 0, 1, 1, 1, 11, 1376, 1000004, null,
                    60000001, 30000001, 20000001, 10000001, null, null));
                await wallets.OpenAsync(connection, transaction, founder, 5_000_000m, 10);
                await transaction.CommitAsync();
            }

            var corporation = await corporations.CreateAsync(founder,
                new NewCorporation("Asset Wire Corp", $"AW{suffix[..3]}", "", "", 0.05m),
                $"asset-wire:{founder}");
            const long stationId = 60000001;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await offices.RentAsync(connection, transaction, corporation.Id, stationId + 1000,
                    new CorporationOfficeStation(stationId, 1531, 1000004, 30000142, 0.8), 1000m, now);
                await transaction.CommitAsync();
            }
            long officeItemId;
            long impoundedItemId;
            long propertyItemId;
            long deliveryItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                officeItemId = await items.CreateAsync(connection, transaction,
                    new NewItem(34, corporation.Id, stationId + 1000, ItemFlags.CorpSAG3, 3,
                        CustomName: "office stack"));
                impoundedItemId = await items.CreateAsync(connection, transaction,
                    new NewItem(34, corporation.Id, stationId, CorporationOfficeStore.ImpoundedFlag, 4,
                        CustomName: "impounded stack"));
                propertyItemId = await items.CreateAsync(connection, transaction,
                    new NewItem(34, corporation.Id, stationId + 1, ItemFlags.CorpSAG3, 5,
                        CustomName: "corporate stack"));
                deliveryItemId = await items.CreateAsync(connection, transaction,
                    new NewItem(34, corporation.Id, stationId, 62, 2, CustomName: "corporate delivery"));
                await transaction.CommitAsync();
            }

            var service = (IService)new CorpMgrService(corporations, characters, items, offices: offices,
                resolveAssetLocation: id => id == stationId || id == stationId + 1
                    ? (30000142L, (int?)1531)
                    : null);
            var registry = new ServiceRegistry();
            registry.Register(service);
            Assert.Same(service, registry.Lookup("corpmgr"));

            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = 44, CharacterId = founder };
            await new CorporationLoginHook(corporations, characters)
                .OnCharacterEnteredAsync(session, founder, default);
            Assert.Equal(corporation.Id, session.GetInt("corpid"));
            Assert.Equal(new PyStr("carbon.common.script.net.machoNetPacket.SessionChangeNotification"),
                Assert.IsType<PyObject>(Assert.Single(capture.Packets)).Name);
            var dispatcher = new PacketDispatcher(registry, () => now);

            Assert.True(await dispatcher.DispatchAsync(
                ClientCallPacket("GetAssetInventory", new PyInt(corporation.Id), new PyStr("offices")), session));
            Assert.Collection(capture.Packets, _ => { }, _ => { });
            var officeLocations = Rowset(Result(capture.Packets[1]));
            Assert.Equal(new PyStr("carbon.common.script.sys.crowset.CRowset"), Assert.IsType<PyObject>(officeLocations).Name);
            Assert.Equal(new PyValue[]
            {
                Py.Tuple(new PyStr("locationID"), new PyInt(0x14)),
                Py.Tuple(new PyStr("solarsystemID"), new PyInt(0x14)),
                Py.Tuple(new PyStr("typeID"), new PyInt(0x03)),
            }, Descriptor.DescriptorColumns(Field(officeLocations, "header")));
            var officeValues = RowValues(Assert.Single(Lines(officeLocations)));
            Assert.Equal(new PyValue[] { new PyLong(stationId), new PyLong(30000142), new PyInt(1531) }, officeValues);

            Assert.True(await dispatcher.DispatchAsync(
                ClientCallPacket("GetAssetInventory", new PyInt(corporation.Id), new PyStr("property")), session));
            Assert.Collection(capture.Packets, _ => { }, _ => { }, _ => { });
            Assert.Equal(new PyStr("carbon.common.script.net.machoNetPacket.CallRsp"),
                Assert.IsType<PyObject>(capture.Packets[2]).Name);
            var propertyLocations = Rowset(Result(capture.Packets[2]));
            Assert.Equal(new PyValue[]
            {
                Py.Tuple(new PyStr("locationID"), new PyInt(0x14)),
                Py.Tuple(new PyStr("solarsystemID"), new PyInt(0x14)),
                Py.Tuple(new PyStr("typeID"), new PyInt(0x03)),
            }, Descriptor.DescriptorColumns(Field(propertyLocations, "header")));
            var propertyRow = Assert.Single(Lines(propertyLocations));
            var propertyValues = RowValues(propertyRow);
            Assert.Equal(new PyValue[] { new PyLong(stationId + 1), new PyLong(30000142), new PyInt(1531) }, propertyValues);

            Assert.True(await dispatcher.DispatchAsync(
                ClientCallPacket("GetAssetInventory", new PyInt(corporation.Id), new PyStr("deliveries")), session));
            Assert.Collection(capture.Packets, _ => { }, _ => { }, _ => { }, _ => { });
            Assert.Equal(new PyStr("carbon.common.script.net.machoNetPacket.CallRsp"),
                Assert.IsType<PyObject>(capture.Packets[3]).Name);
            var deliveryLocations = Rowset(Result(capture.Packets[3]));
            Assert.Equal(new PyValue[]
            {
                Py.Tuple(new PyStr("locationID"), new PyInt(0x14)),
                Py.Tuple(new PyStr("solarsystemID"), new PyInt(0x14)),
                Py.Tuple(new PyStr("typeID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("itemCount"), new PyInt(0x03)),
            }, Descriptor.DescriptorColumns(Field(deliveryLocations, "header")));
            var deliveryValues = RowValues(Assert.Single(Lines(deliveryLocations)));
            Assert.Equal(new PyValue[]
            {
                new PyLong(stationId), new PyLong(30000142), new PyInt(1531), new PyInt(1)
            }, deliveryValues);

            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket("GetAssetInventoryForLocation",
                new PyInt(corporation.Id), new PyLong(stationId), new PyStr("offices")), session));
            Assert.Equal(new PyStr("carbon.common.script.net.machoNetPacket.CallRsp"),
                Assert.IsType<PyObject>(capture.Packets[4]).Name);
            var officeItems = Rowset(Result(capture.Packets[4]));
            Assert.Equal(new PyValue[]
            {
                Py.Tuple(new PyStr("itemID"), new PyInt(0x14)),
                Py.Tuple(new PyStr("typeID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("ownerID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("locationID"), new PyInt(0x14)),
                Py.Tuple(new PyStr("flagID"), new PyInt(0x02)),
                Py.Tuple(new PyStr("quantity"), new PyInt(0x03)),
                Py.Tuple(new PyStr("groupID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("categoryID"), new PyInt(0x03)),
                Py.Tuple(new PyStr("customInfo"), new PyInt(0x81)),
                Py.Tuple(new PyStr("stacksize"), new PyInt(0x03)),
                Py.Tuple(new PyStr("singleton"), new PyInt(0x03)),
            }, Descriptor.DescriptorColumns(Field(officeItems, "header")));
            Assert.Equal(new PyValue[]
            {
                new PyLong(officeItemId), new PyInt(34), new PyInt(corporation.Id), new PyLong(stationId + 1000),
                new PyInt(ItemFlags.CorpSAG3), new PyInt(3), new PyInt(0), new PyInt(0),
                new PyWString("office stack"), new PyInt(3), new PyInt(0),
            }, RowValues(Assert.Single(Lines(officeItems))));

            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket("GetAssetInventoryForLocation",
                new PyInt(corporation.Id), new PyLong(stationId), new PyStr("deliveries")), session));
            var deliveryItems = Rowset(Result(capture.Packets[5]));
            Assert.Equal(new PyLong(deliveryItemId), RowValues(Assert.Single(Lines(deliveryItems)))[0]);

            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket("GetAssetInventoryForLocation",
                new PyInt(corporation.Id), new PyLong(stationId), new PyStr("impounded")), session));
            var impoundedItems = Rowset(Result(capture.Packets[6]));
            Assert.Equal(new PyLong(impoundedItemId), RowValues(Assert.Single(Lines(impoundedItems)))[0]);

            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket("GetAssetInventoryForLocation",
                new PyInt(corporation.Id), new PyLong(stationId + 1), new PyStr("property")), session));
            var propertyItems = Rowset(Result(capture.Packets[7]));
            Assert.Equal(new PyLong(propertyItemId), RowValues(Assert.Single(Lines(propertyItems)))[0]);

            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket("GetAssetInventoryForLocation",
                new PyInt(corporation.Id), new PyLong(stationId), new PyStr("property")), session));
            var emptyItems = Rowset(Result(capture.Packets[8]));
            Assert.Empty(Lines(emptyItems));

            Assert.Equal(officeItemId, (await items.GetAsync(officeItemId))!.Id);
            Assert.Equal(propertyItemId, (await items.GetAsync(propertyItemId))!.Id);
            Assert.Equal(deliveryItemId, (await items.GetAsync(deliveryItemId))!.Id);
            Assert.Equal(9, capture.Packets.Count);
        }
        finally
        {
            foreach (var schema in new[] { corpSchema, characterSchema, walletSchema, skillSchema, itemSchema })
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

    private static PyValue Rowset(PyValue cached)
    {
        var cachedResult = Assert.IsType<PyObject>(cached);
        Assert.Equal(new PyStr(ObjectCaching.ResultClass), cachedResult.Name);
        var rowset = Assert.IsType<PyObject>(
            Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(cachedResult.Args).Items[1]).Decoded!);
        Assert.Equal(new PyStr("carbon.common.script.sys.crowset.CRowset"), rowset.Name);
        return rowset;
    }

    private static IReadOnlyList<PyValue> Lines(PyValue rowset) =>
        Assert.IsType<PyList>(Field(rowset, "lines")).Items;

    private static IReadOnlyList<PyValue> RowValues(PyValue row) =>
        Assert.IsType<PyPackedRow>(row).Values;

    private static PyValue Field(PyValue value, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries
            .Single(entry => entry.Key == new PyStr(name)).Value;
}
