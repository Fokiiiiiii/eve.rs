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
    public async Task Registered_planet_update_consumes_one_command_center_from_a_stack_and_keeps_the_remainder()
    {
        const long owner = 90_091;
        const long shipId = 90_092;
        const long planetId = 40_486_618;
        const int commandCenterTypeId = 2_524;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var planetSchema = "picp_" + suffix;
        var inventorySchema = "pici_" + suffix;
        var skillSchema = "pics_" + suffix;
        await PlanetStore.MigrateAsync(source, planetSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgSkillStore.MigrateAsync(source, skillSchema);

        try
        {
            var planets = new PlanetStore(source, planetSchema);
            var items = new PgItemStore(source, inventorySchema);
            var skills = new PgSkillStore(source, skillSchema);
            var catalog = await PlanetCatalogLoader.LoadAsync(source);
            var dogma = await Eve.Dogma.DogmaStatic.LoadAsync(new Eve.Sde.SdeReader(source));
            _ = Assert.IsType<PlanetBody>(await PlanetCatalogLoader.PlanetAsync(source, planetId));

            long stackItemId;
            long duplicateItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                stackItemId = await items.CreateAsync(connection, transaction,
                    new NewItem(commandCenterTypeId, owner, shipId, ItemFlags.Cargo, Quantity: 4));
                duplicateItemId = await items.CreateAsync(connection, transaction,
                    new NewItem(commandCenterTypeId, owner, shipId, ItemFlags.Cargo, Quantity: 2));
                await transaction.CommitAsync();
            }

            ClientSession? liveSession = null;
            var stateAtPlanetNotification = new List<(long? ItemQuantity, int PinCount)>();
            var service = new PlanetMgrService(source, planets, catalog, new DeterministicResourceField(), items, null!, skills,
                () => FixedNowMs,
                async (recipient, changedPlanet) =>
                {
                    var savedItem = await items.GetAsync(stackItemId);
                    var savedColony = await planets.ReadAsync(recipient, planetId);
                    stateAtPlanetNotification.Add((savedItem?.Quantity, savedColony?.Pins.Count ?? 0));
                    liveSession!.SendNotification("OnMajorPlanetStateUpdate", "clientID", changedPlanet ?? PyNone.Instance, PyValue.Of(false));
                },
                _ => Task.CompletedTask,
                inventorySchema,
                dogma: dogma);

            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    session.CharacterId = owner;
                    session.Set("charid", new PyLong(owner));
                    session.Set("shipid", new PyLong(shipId));
                    liveSession = session;
                });

            using var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 1, "planetMgr", "MachoBindObject", new PyTuple([new PyLong(planetId), PyNone.Instance]), kwargs))));
            var bindReply = Assert.IsType<Send>(await front.ReceiveAsync());
            var bindWire = MarshalCodec.Decode(bindReply.Payload);
            var bindPacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(bindWire));
            Assert.Equal(MachoMessage.CallRsp, bindPacket.Type);
            var bound = Assert.IsType<PySubStruct>(Assert.IsType<PyTuple>(Result(bindWire)).Items[0]);
            var boundReference = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
            var objectId = Assert.IsType<string>(JsSemantics.StringValue(boundReference.Items[0]));

            var createPin = Py.Tuple(new PyInt(1), Py.Tuple(
                new PyLong(stackItemId), new PyInt(commandCenterTypeId), new PyReal(0.5), new PyReal(1.0)));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                open, 2, objectId, "UserUpdateNetwork", new PyTuple([new PyList([createPin])]), kwargs))));

            var inventoryNotice = Assert.IsType<Send>(await front.ReceiveAsync());
            var inventoryNoticeWire = MarshalCodec.Decode(inventoryNotice.Payload);
            var inventoryNoticePacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(inventoryNoticeWire));
            Assert.Equal(MachoMessage.Notification, inventoryNoticePacket.Type);
            Assert.Equal("OnItemChange", inventoryNoticePacket.Dest.BroadcastId);
            Assert.Equal("clientID", inventoryNoticePacket.Dest.IdType);
            var itemChangeArgs = NotificationArgs(inventoryNoticePacket);
            var changedStackRow = Assert.IsType<PyPackedRow>(itemChangeArgs.Items[0]);
            var changedStackFields = changedStackRow.Fields();
            Assert.Equal(InventoryWire.Columns, changedStackFields.Select(field => field.Key));
            Assert.Equal(InventoryWire.Id(stackItemId), changedStackFields[0].Value);
            Assert.Equal(InventoryWire.Id(3), changedStackFields[5].Value);
            Assert.Equal(
                [new KeyValuePair<PyValue, PyValue>(new PyInt(9), InventoryWire.Id(4))],
                Assert.IsType<PyDict>(itemChangeArgs.Items[1]).Entries);
            Assert.IsType<PyNone>(itemChangeArgs.Items[2]);

            var planetNotice = Assert.IsType<Send>(await front.ReceiveAsync());
            var planetNoticeWire = MarshalCodec.Decode(planetNotice.Payload);
            var planetNoticePacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(planetNoticeWire));
            Assert.Equal(MachoMessage.Notification, planetNoticePacket.Type);
            Assert.Equal("OnMajorPlanetStateUpdate", planetNoticePacket.Dest.BroadcastId);

            var updateReply = Assert.IsType<Send>(await front.ReceiveAsync());
            var updateWire = MarshalCodec.Decode(updateReply.Payload);
            var updatePacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(updateWire));
            Assert.Equal(MachoMessage.CallRsp, updatePacket.Type);
            Assert.IsType<PyObject>(Result(updateWire));

            var remainingStack = Assert.IsType<Item>(await items.GetAsync(stackItemId));
            Assert.Equal((owner, shipId, ItemFlags.Cargo, 3L, false),
                (remainingStack.OwnerId, remainingStack.LocationId, remainingStack.Flag, remainingStack.Quantity, remainingStack.Singleton));
            var persisted = Assert.IsType<ColonyDocument>(await new PlanetStore(source, planetSchema).ReadAsync(owner, planetId));
            var placedPin = Assert.Single(persisted.Pins);
            Assert.Equal((stackItemId, commandCenterTypeId), (placedPin.Id, placedPin.TypeId));
            Assert.Equal(((long?)3, 1), Assert.Single(stateAtPlanetNotification));

            var duplicateCreate = Py.Tuple(new PyInt(1), Py.Tuple(
                new PyLong(duplicateItemId), new PyInt(commandCenterTypeId), new PyReal(0.75), new PyReal(0.75)));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                open, 3, objectId, "UserUpdateNetwork", new PyTuple([new PyList([duplicateCreate])]), kwargs))));
            var duplicateReply = Assert.IsType<Send>(await front.ReceiveAsync());
            var duplicatePacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(duplicateReply.Payload)));
            Assert.Equal(MachoMessage.ErrorResponse, duplicatePacket.Type);
            Assert.Equal(2L, Assert.IsType<Item>(await items.GetAsync(duplicateItemId)).Quantity);
            Assert.Equal(3L, Assert.IsType<Item>(await items.GetAsync(stackItemId)).Quantity);
            Assert.Single((await new PlanetStore(source, planetSchema).ReadAsync(owner, planetId))!.Pins);
            Assert.Single(stateAtPlanetNotification);
        }
        finally
        {
            foreach (var schema in new[] { planetSchema, inventorySchema, skillSchema })
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    private static PyTuple NotificationArgs(MachoPacket packet)
    {
        var payload = Assert.IsType<PyTuple>(packet.Payload);
        var bytes = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(payload.Items[0]).Items[1]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.Decode(bytes.Value));
        return Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(decoded.Items[1]).Items[1]);
    }
}
