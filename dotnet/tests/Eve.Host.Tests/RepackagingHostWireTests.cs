using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Inventory;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Skills;
using Eve.Universe;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class RepackagingHostWireTests
{
    [PgFact]
    public async Task Repackage_items_persist_and_notify_through_registered_host_for_client_call_shapes()
    {
        const long stationId = 60000001;
        const int shipTypeId = 587;
        const int stackTypeId = 34;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var characterSchema = $"rpk_h_{suffix}";
        var skillSchema = $"rpk_s_{suffix}";
        var itemSchema = $"rpk_i_{suffix}";
        var moduleSchema = $"rpk_m_{suffix}";
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        try
        {
            await PgCharacterStore.MigrateAsync(source, characterSchema);
            await PgSkillStore.MigrateAsync(source, skillSchema);
            await PgItemStore.MigrateAsync(source, itemSchema);
            await PgModuleStateStore.MigrateAsync(source, moduleSchema);
            var characters = new PgCharacterStore(source, characterSchema);
            var skills = new PgSkillStore(source, skillSchema);
            var items = new PgItemStore(source, itemSchema);
            var modules = new PgModuleStateStore(source, moduleSchema);
            var statics = new DogmaStatic([], [],
                [
                    new TypeDef(shipTypeId, 25, 6, new Dictionary<int, double>(), [], "Rifter"),
                    new TypeDef(stackTypeId, 18, 18, new Dictionary<int, double>(), [], "Mineral"),
                ],
                categoryOfGroup: new Dictionary<int, int> { [25] = 6, [18] = 18 },
                categoryNames: new Dictionary<int, string> { [6] = "Ship", [18] = "Material" });
            var ships = new CharacterShips(source, statics, characters, items, skills, modules);
            var station = new Station(stationId, 1531, "Test Station", 0, "", null, "", 0, 0,
                30000142, "", 0, "", 0, "", 0, 0, 0);
            var inventory = new InvBrokerService(items, new ItemTypeCatalog(new SdeReader(source)),
                new StationCatalog([station]), ships, statics, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var service = (IService)new RepackagingService(inventory);
            var registry = new ServiceRegistry();
            registry.Register(service);
            Assert.Same(service, registry.Lookup("repackagingSvc"));

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long characterId;
            long[] stackIds;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                characterId = await characters.InsertAsync(connection, transaction, new NewCharacter(1,
                    $"Repackaging Pilot {Guid.NewGuid():N}", 0, 1, 1, 1, 11, 1376, 0, null,
                    stationId, 30000142, 20000142, 10000001, null, null));
                var activeShip = await items.CreateAsync(connection, transaction,
                    new NewItem(shipTypeId, characterId, stationId, ItemFlags.Hangar, 1, Singleton: true));
                await characters.SetShipAsync(connection, transaction, characterId, activeShip);
                stackIds =
                [
                    await items.CreateAsync(connection, transaction,
                        new NewItem(stackTypeId, characterId, stationId, ItemFlags.Hangar, 1,
                            Singleton: true, CustomName: "first packaged item")),
                    await items.CreateAsync(connection, transaction,
                        new NewItem(stackTypeId, characterId, stationId, ItemFlags.Hangar, 1,
                            Singleton: true, CustomName: "second packaged item")),
                    await items.CreateAsync(connection, transaction,
                        new NewItem(stackTypeId, characterId, stationId, ItemFlags.Hangar, 1,
                            Singleton: true, CustomName: "third packaged item")),
                ];
                await transaction.CommitAsync();
            }

            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = 71, CharacterId = characterId };
            session.Set("stationid", new PyInt(stationId));
            var dispatcher = new PacketDispatcher(registry, () => now);

            async Task Repackage(long itemId, bool includeDestroyable, string name)
            {
                var before = capture.Packets.Count;
                var request = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyLong(stationId), Py.List(
                    Py.Tuple(new PyLong(itemId), new PyLong(stationId))))]);
                var extra = includeDestroyable
                    ? new[] { new KeyValuePair<PyValue, PyValue>(new PyStr("destroyable"), PyBool.True) }
                    : [];
                Assert.True(await dispatcher.DispatchAsync(ClientCallPacket(name, request, extra), session));
                var responseIndex = capture.Packets.FindIndex(before, packet =>
                    packet is PyObject { Name: PyStr packetName }
                    && packetName.Value == "carbon.common.script.net.machoNetPacket.CallRsp");
                Assert.True(responseIndex > before);
                var response = Assert.IsType<PyObject>(capture.Packets[responseIndex]);
                Assert.Equal(new PyStr("carbon.common.script.net.machoNetPacket.CallRsp"), response.Name);
                Assert.Equal(PyNone.Instance, Assert.IsType<PySubStream>(Assert.Single(
                    Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(response.Args).Items[4]).Items)).Decoded);
                var notifications = capture.Packets.Skip(before).Take(responseIndex - before)
                    .Select(NotificationName).OfType<string>().ToArray();
                Assert.Equal(["OnItemChange"], notifications);
                Assert.False((await items.GetAsync(itemId))!.Singleton);
                Assert.Equal(1, (await items.GetAsync(itemId))!.Quantity);
            }

            await Repackage(stackIds[0], false, "itemsByStation");
            await Repackage(stackIds[1], true, "itemsByStation");
            await Repackage(stackIds[2], true, "newItemsByStation");
        }
        finally
        {
            foreach (var schema in new[] { characterSchema, skillSchema, itemSchema, moduleSchema })
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

    private static PyValue ClientCallPacket(string method, PyValue argument,
        IReadOnlyList<KeyValuePair<PyValue, PyValue>> extraKwargs)
    {
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(1), ClientId: new PyInt(1));
        var destination = new MachoAddress(AddressKind.Node, Service: "repackagingSvc",
            CallId: new PyInt(1), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            .. extraKwargs]);
        var body = new PyTuple([PyNone.Instance, new PyStr("RepackageItems"), new PyTuple([argument]), kwargs]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([new PyInt(MachoMessage.CallReq), source.Encode(), destination.Encode(),
                new PyInt(7), payload, new PyDict([]), PyNone.Instance, PyNone.Instance, PyNone.Instance,
                PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance]));
    }

    private static string? NotificationName(PyValue packet)
    {
        if (packet is not PyObject { Name: PyStr name, Args: PyTuple args } ||
            !name.Value.EndsWith("Notification", StringComparison.Ordinal)) return null;
        return MachoAddress.Decode(args.Items[2]).BroadcastId;
    }
}
