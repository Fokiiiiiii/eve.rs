using Eve.Data;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Planets;
using Eve.Services;
using Eve.Services.Planets;
using Eve.Scheduler;
using Eve.Skills;
using Npgsql;
using System.Text.Json;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Planet_launch_rejects_nonpositive_and_overstock_quantities_through_registered_host_wire()
    {
        const long owner = 90_061;
        const long planetId = 40_486_618;
        const long commandPinId = 800_061;
        const int commodityTypeId = 2_268;
        const int secondCommodityTypeId = 3_645;
        const long startingQuantity = 5;
        const long secondStartingQuantity = 2;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var planetSchema = "plq_p" + suffix;
        var inventorySchema = "plq_i" + suffix;
        var skillsSchema = "plq_s" + suffix;
        var schedulerSchema = "plq_j" + suffix;
        await PlanetStore.MigrateAsync(source, planetSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgSkillStore.MigrateAsync(source, skillsSchema);
        await DurableScheduler.MigrateAsync(source, schedulerSchema);
        try
        {
            var catalog = await PlanetCatalogLoader.LoadAsync(source);
            var body = Assert.IsType<PlanetBody>(await PlanetCatalogLoader.PlanetAsync(source, planetId));
            var planets = new PlanetStore(source, planetSchema);
            var items = new PgItemStore(source, inventorySchema);
            var skills = new PgSkillStore(source, skillsSchema);
            await using var scheduler = new DurableScheduler(source, () => FixedNowMs, schema: schedulerSchema);
            var colony = new Colony(catalog, new DeterministicResourceField(), planetId, body.TypeId, body.Radius, owner)
            {
                CurrentSimTime = Py.FileTimeFromUnixMs(FixedNowMs),
            };
            colony.Pins[commandPinId] = new Pin
            {
                Id = commandPinId,
                TypeId = 2_524,
                Kind = PinKind.CommandCenter,
                OwnerId = owner,
                Contents = new Dictionary<int, long>
                {
                    [commodityTypeId] = startingQuantity,
                    [secondCommodityTypeId] = secondStartingQuantity,
                },
            };
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await planets.SaveAsync(connection, transaction, owner, planetId, ColonyDocument.From(colony), FixedNowMs);
                await transaction.CommitAsync();
            }

            var launchNotifications = 0;
            var service = new PlanetMgrService(source, planets, catalog, new DeterministicResourceField(), items, null!, skills,
                () => FixedNowMs, (_, _) => Task.CompletedTask,
                _ => { Interlocked.Increment(ref launchNotifications); return Task.CompletedTask; }, inventorySchema);
            service.RegisterJobs(scheduler);
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session => session.Set("charid", new PyLong(owner)));

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

            var quantities = new long[] { 6, 0, -1, 3 };
            var responses = new List<int>();
            var rejectedSnapshots = new List<(long First, long Second, int LaunchCount)>();
            for (var i = 0; i < quantities.Length; i++)
            {
                var commodityMap = new PyDict([
                    new KeyValuePair<PyValue, PyValue>(new PyInt(commodityTypeId), new PyLong(quantities[i])),
                ]);
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                    open, (uint)(i + 2), objectId, "UserLaunchCommodities",
                    new PyTuple([new PyLong(commandPinId), commodityMap]), kwargs))));
                var response = Assert.IsType<Send>(await front.ReceiveAsync());
                var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(response.Payload)));
                responses.Add(packet.Type);
                if (i < 3)
                {
                    var afterRejectedLaunch = Assert.IsType<ColonyDocument>(await planets.ReadAsync(owner, planetId));
                    rejectedSnapshots.Add((
                        afterRejectedLaunch.Pins.Single().Contents.GetValueOrDefault(commodityTypeId),
                        afterRejectedLaunch.Pins.Single().Contents.GetValueOrDefault(secondCommodityTypeId),
                        (await planets.LaunchesOfAsync(owner, Py.FileTimeFromUnixMs(FixedNowMs))).Count));
                    await using var connection = await source.OpenConnectionAsync();
                    await using var transaction = await connection.BeginTransactionAsync();
                    await planets.SaveAsync(connection, transaction, owner, planetId, ColonyDocument.From(colony), FixedNowMs);
                    await transaction.CommitAsync();
                }
                else
                {
                    Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(FixedNowMs)), Result(MarshalCodec.Decode(response.Payload)));
                }
            }

            Assert.Equal(
                [MachoMessage.ErrorResponse, MachoMessage.ErrorResponse, MachoMessage.ErrorResponse, MachoMessage.CallRsp],
                responses);
            Assert.Equal([(startingQuantity, secondStartingQuantity, 0),
                (startingQuantity, secondStartingQuantity, 0),
                (startingQuantity, secondStartingQuantity, 0)], rejectedSnapshots);
            var saved = Assert.IsType<ColonyDocument>(await planets.ReadAsync(owner, planetId));
            Assert.Equal(2, saved.Pins.Single().Contents[commodityTypeId]);
            var launches = await planets.LaunchesOfAsync(owner, Py.FileTimeFromUnixMs(FixedNowMs));
            var launch = Assert.Single(launches);
            using var launchContents = JsonDocument.Parse(launch.ContentsJson);
            Assert.Equal(3, launchContents.RootElement.GetProperty(commodityTypeId.ToString()).GetInt64());
            var container = Assert.IsType<Item>(await items.GetAsync(launch.ItemId));
            Assert.Equal(23, container.TypeId);
            var launchedCommodity = Assert.IsType<Item>(await items.GetAsync(await ItemInContainerAsync(source, inventorySchema, launch.ItemId, commodityTypeId)));
            Assert.Equal(3, launchedCommodity.Quantity);
            Assert.Equal(0, launchNotifications);
            Assert.Single(await scheduler.ListAsync());
        }
        finally
        {
            foreach (var schema in new[] { planetSchema, inventorySchema, skillsSchema, schedulerSchema })
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task<long> ItemInContainerAsync(NpgsqlDataSource source, string schema, long containerId, int typeId)
    {
        await using var command = source.CreateCommand($"SELECT id FROM {schema}.items WHERE location_id = $1 AND type_id = $2");
        command.Parameters.AddWithValue(containerId);
        command.Parameters.AddWithValue(typeId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
