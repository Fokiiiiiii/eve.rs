using Eve.Data;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Planets;
using Eve.Services;
using Eve.Services.Planets;
using Eve.Skills;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Negative_link_levels_are_clamped_to_zero_through_the_registered_host_wire_and_persisted()
    {
        const long owner = 90_091;
        const long planetId = 40_486_618;
        const long commandCenterId = 800_091;
        const long storageId = 800_092;
        const int commandCenterType = 2_524;
        const int storageType = 2_541;
        const int linkType = 2_280;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var planetSchema = "pll_p" + Guid.NewGuid().ToString("N")[..10];
        var skillsSchema = "pll_s" + Guid.NewGuid().ToString("N")[..10];
        var inventorySchema = "pll_i" + Guid.NewGuid().ToString("N")[..10];
        await PlanetStore.MigrateAsync(source, planetSchema);
        await PgSkillStore.MigrateAsync(source, skillsSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        try
        {
            var field = new DeterministicResourceField();
            var catalog = new PlanetCatalog(
                new Dictionary<int, TypeInfo>
                {
                    [commandCenterType] = new(commandCenterType, PlanetGroups.CommandCenter, 0, 0, 500, new Dictionary<int, double>()),
                    [storageType] = new(storageType, PlanetGroups.Storage, 0, 0, 12_000, new Dictionary<int, double>()),
                    [linkType] = new(linkType, PlanetGroups.Link, 0, 0, null, new Dictionary<int, double>
                    {
                        [PlanetAttr.LogisticalCapacity] = 1_250,
                        [PlanetAttr.CpuLoad] = 15,
                        [PlanetAttr.PowerLoad] = 10,
                        [PlanetAttr.CpuLoadPerKm] = 0.2,
                        [PlanetAttr.PowerLoadPerKm] = 0.15,
                        [PlanetAttr.CpuLoadLevelModifier] = 1.4,
                        [PlanetAttr.PowerLoadLevelModifier] = 1.2,
                    }),
                },
                new Dictionary<int, Schematic>());
            var body = Assert.IsType<PlanetBody>(await PlanetCatalogLoader.PlanetAsync(source, planetId));
            var colony = new Colony(catalog, field, planetId, body.TypeId, body.Radius, owner)
            {
                CurrentSimTime = Py.FileTimeFromUnixMs(FixedNowMs),
            };
            colony.Pins[commandCenterId] = new Pin
            {
                Id = commandCenterId,
                TypeId = commandCenterType,
                Kind = PinKind.CommandCenter,
                OwnerId = owner,
                Latitude = 0.5,
                Longitude = 1.0,
            };
            colony.Pins[storageId] = new Pin
            {
                Id = storageId,
                TypeId = storageType,
                Kind = PinKind.Storage,
                OwnerId = owner,
                Latitude = 0.501,
                Longitude = 1.001,
            };
            var store = new PlanetStore(source, planetSchema);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await store.SaveAsync(connection, transaction, owner, planetId, ColonyDocument.From(colony), FixedNowMs);
                await transaction.CommitAsync();
            }

            var notificationLevels = new List<int>();
            async Task Notify(long _, PyValue? __)
            {
                var persisted = Assert.IsType<ColonyDocument>(await store.ReadAsync(owner, planetId));
                notificationLevels.Add(Assert.Single(persisted.Links).Level);
            }

            var service = new PlanetMgrService(
                source, store, catalog, field, new PgItemStore(source, inventorySchema), null!, new PgSkillStore(source, skillsSchema),
                () => FixedNowMs, Notify, _ => Task.CompletedTask, inventorySchema);
            Exception? rpcProblem = null;
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session => session.Set("charid", new PyLong(owner)),
                onProblem: (_, error) => rpcProblem = error);

            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 1, "planetMgr", "MachoBindObject", new PyTuple([new PyLong(planetId), PyNone.Instance]), kwargs))));
            var bindReply = Assert.IsType<Send>(await front.ReceiveAsync());
            var bindResult = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(bindReply.Payload)));
            var bound = Assert.IsType<PySubStruct>(bindResult.Items[0]);
            var reference = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
            var objectId = Assert.IsType<string>(JsSemantics.StringValue(reference.Items[0]));

            async Task<PyValue> Update(uint callId, PyValue command)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                    open, callId, objectId, "UserUpdateNetwork", new PyTuple([new PyList([command])]), kwargs))));
                var response = Assert.IsType<Send>(await front.ReceiveAsync());
                var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(response.Payload)));
                Assert.True(packet.Type == MachoMessage.CallRsp,
                    packet.Type == MachoMessage.ErrorResponse ? $"RPC failed with {PlanetErrorClass(response.Payload)}: {rpcProblem}" : $"Unexpected Macho packet {packet.Type}");
                return Assert.IsAssignableFrom<PyValue>(Result(MarshalCodec.Decode(response.Payload)));
            }

            var createLink = Py.Tuple(new PyInt(3), Py.Tuple(new PyLong(commandCenterId), new PyLong(storageId), new PyInt(-2)));
            var created = await Update(2, createLink);
            Assert.Equal(0, PlanetLinkLevel(created));
            Assert.Equal(0, Assert.Single(Assert.IsType<ColonyDocument>(await store.ReadAsync(owner, planetId)).Links).Level);

            var setLinkLevel = Py.Tuple(new PyInt(5), Py.Tuple(new PyLong(commandCenterId), new PyLong(storageId), new PyInt(-3)));
            var updated = await Update(3, setLinkLevel);
            Assert.Equal(0, PlanetLinkLevel(updated));
            Assert.Equal(0, Assert.Single(Assert.IsType<ColonyDocument>(await store.ReadAsync(owner, planetId)).Links).Level);
            Assert.Equal(new[] { 0, 0 }, notificationLevels);
        }
        finally
        {
            foreach (var schema in new[] { planetSchema, skillsSchema, inventorySchema })
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    private static PyValue PlanetLinkField(PyValue value, string name)
    {
        var args = Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args);
        return Assert.Single(args.Entries, entry => JsSemantics.StringValue(entry.Key) == name).Value;
    }

    private static long PlanetLinkLevel(PyValue update)
    {
        var links = Assert.IsType<PyList>(PlanetLinkField(update, "links"));
        return Assert.IsType<PyInt>(PlanetLinkField(Assert.Single(links.Items), "level")).Value;
    }

    private static string PlanetErrorClass(ReadOnlyMemory<byte> payload)
    {
        var packet = Assert.IsType<PyObject>(MarshalCodec.Decode(payload));
        var state = Assert.IsType<PyTuple>(packet.Args).Items;
        var body = Assert.IsType<PyTuple>(state[4]).Items;
        var wrapped = Assert.IsType<PyTuple>(body[2]).Items;
        var objectEx = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped[0]).Decoded);
        var header = Assert.IsType<PyTuple>(objectEx.Header).Items;
        return Assert.IsType<PyToken>(header[0]).Value;
    }
}
