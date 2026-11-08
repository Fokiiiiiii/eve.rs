using System.Text;
using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Inventory;
using Eve.Sde;
using Eve.Ships;
using Eve.Skills;
using Eve.Universe;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Registered_invbroker_fits_one_module_from_a_stack_and_persists_the_remaining_unit()
    {
        const int shipTypeId = 601;
        const int moduleTypeId = 21857;
        const int mediumSlot = 19;
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(connectionString!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());
        var statics = await DogmaStatic.LoadAsync(sde);
        var stations = await StationCatalog.LoadAsync(sde);
        var station = stations.Get(60015249) ?? throw new InvalidOperationException("The pinned SDE station is missing.");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var schemas = new[] { $"fithost_c_{suffix}", $"fithost_i_{suffix}", $"fithost_s_{suffix}", $"fithost_m_{suffix}" };
        try
        {
            await PgCharacterStore.MigrateAsync(source, schemas[0]);
            await PgItemStore.MigrateAsync(source, schemas[1]);
            await PgSkillStore.MigrateAsync(source, schemas[2]);
            await PgModuleStateStore.MigrateAsync(source, schemas[3]);
            var characters = new PgCharacterStore(source, schemas[0]);
            var items = new PgItemStore(source, schemas[1]);
            var skills = new PgSkillStore(source, schemas[2]);
            var modules = new PgModuleStateStore(source, schemas[3]);
            long characterId;
            long shipId;
            long stackId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                characterId = await characters.InsertAsync(connection, transaction, new NewCharacter(
                    1, $"Fitting wire {suffix}", 1, 1, 1, 1, 1, 1, 1_000_001, null,
                    station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
                shipId = await items.CreateAsync(connection, transaction,
                    new NewItem(shipTypeId, characterId, station.Id, ItemFlags.Hangar, 1, Singleton: true));
                stackId = await items.CreateAsync(connection, transaction,
                    new NewItem(moduleTypeId, characterId, station.Id, ItemFlags.Hangar, 2, Singleton: false));
                await characters.SetShipAsync(connection, transaction, characterId, shipId);
                await skills.GrantAsync(connection, transaction, characterId,
                    [new CharacterSkill(3386, 1, 0), new CharacterSkill(24241, 1, 0),
                        new CharacterSkill(3436, 1, 0), new CharacterSkill(3425, 1, 0)]);
                await transaction.CommitAsync();
            }

            var ships = new CharacterShips(source, statics, characters, items, skills, modules);
            var service = new InvBrokerService(items, new ItemTypeCatalog(sde), stations, ships, statics, () => FixedNowMs);
            Start(configure: registry => registry.Register(service), strictRpc: true, sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyInt(characterId));
                session.Set("stationid", new PyInt(station.Id));
                session.Set("stationid2", new PyInt(station.Id));
                session.Set("shipid", new PyInt(shipId));
            });

            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var machoVersion = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(3396210))]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 1, "invbroker", "GetInventoryFromId", new PyTuple([new PyLong(shipId)]), machoVersion))));
            var bindReply = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(bindReply.Payload))).Type);
            var bound = Assert.IsType<PySubStruct>(Result(MarshalCodec.Decode(bindReply.Payload)));
            var boundData = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
            var objectId = Encoding.UTF8.GetString(Assert.IsType<PyBytes>(boundData.Items[0]).Value.Span);

            var itemsByType = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyInt(moduleTypeId), new PyList([new PyLong(stackId)]))]);
            var keyVal = Py.KeyVal(("modulesByFlag", new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyInt(mediumSlot), new PyInt(moduleTypeId))])));
            var fitArgs = new PyTuple([
                new PyLong(shipId), new PyInt(shipTypeId), itemsByType, new PyLong(station.Id),
                keyVal, new PyDict([]), PyBool.True]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                open, 2, objectId, "FitFitting", fitArgs, machoVersion))));

            var notificationNames = new List<string>();
            for (var index = 0; index < 3; index++)
            {
                var notification = Assert.IsType<Send>(await front.ReceiveAsync());
                var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(notification.Payload)));
                Assert.Equal(MachoMessage.Notification, packet.Type);
                notificationNames.Add(packet.Dest.BroadcastId!);
            }
            Assert.Equal(["OnMultiEvent", "OnItemsChanged", "OnItemChange"], notificationNames);

            var fitReply = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(fitReply.Payload))).Type);
            Assert.Empty(Assert.IsType<PyList>(Result(MarshalCodec.Decode(fitReply.Payload))).Items);
            var remainder = (await items.GetAsync(stackId))!;
            Assert.Equal((station.Id, ItemFlags.Hangar, 1L, false),
                (remainder.LocationId, remainder.Flag, remainder.Quantity, remainder.Singleton));
            var fitted = Assert.Single(await items.InLocationAsync(shipId, characterId),
                item => item.TypeId == moduleTypeId && item.Flag == mediumSlot);
            Assert.NotEqual(stackId, fitted.Id);
            Assert.Equal((1L, true), (fitted.Quantity, fitted.Singleton));
            Assert.True((await modules.StatesAsync([fitted.Id]))[fitted.Id] >= ItemState.Online);

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                open, 3, objectId, "FitFitting", fitArgs, machoVersion))));
            var retryReply = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(retryReply.Payload))).Type);
            Assert.Empty(Assert.IsType<PyList>(Result(MarshalCodec.Decode(retryReply.Payload))).Items);
            Assert.Equal((station.Id, 1L), ((await items.GetAsync(stackId))!.LocationId, (await items.GetAsync(stackId))!.Quantity));
        }
        finally
        {
            foreach (var schema in schemas)
            {
                await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
        }
    }
}
