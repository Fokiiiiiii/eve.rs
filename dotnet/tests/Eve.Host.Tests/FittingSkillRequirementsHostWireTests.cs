using System.Text;
using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Inventory;
using Eve.Services;
using Eve.Services.Dogma;
using Eve.Sde;
using Eve.Ships;
using Eve.Skills;
using Eve.Universe;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Registered_invbroker_fits_module_without_required_skills_and_leaves_it_offline()
    {
        const int shipTypeId = 601;
        const int moduleTypeId = 439;
        const int mediumSlot = 19;
        const long stationId = 60_015_249;
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(connectionString!));
        var sde = new SdeReader(source);
        Assert.Equal(SdeBuildPin.Standard, await sde.BuildAsync());
        var statics = await DogmaStatic.LoadAsync(sde);
        var stations = await StationCatalog.LoadAsync(sde);
        var station = stations.Get(stationId) ?? throw new InvalidOperationException("The pinned SDE station is missing.");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var schemas = new[] { $"fitskill_c_{suffix}", $"fitskill_i_{suffix}", $"fitskill_s_{suffix}", $"fitskill_m_{suffix}" };
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
            await statics.EnsureTypesAsync([shipTypeId, moduleTypeId]);
            var moduleType = Assert.IsType<TypeDef>(statics.Type(moduleTypeId));
            Assert.NotEmpty(FittingRules.RequiredSkills(moduleType));

            long characterId;
            long shipId;
            long moduleId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                characterId = await characters.InsertAsync(connection, transaction, new NewCharacter(
                    1, $"Fit without skills {suffix}", 1, 1, 1, 1, 1, 1, 1_000_001, null,
                    station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
                shipId = await items.CreateAsync(connection, transaction,
                    new NewItem(shipTypeId, characterId, station.Id, ItemFlags.Hangar, 1, Singleton: true));
                moduleId = await items.CreateAsync(connection, transaction,
                    new NewItem(moduleTypeId, characterId, station.Id, ItemFlags.Hangar, 1, Singleton: true));
                await characters.SetShipAsync(connection, transaction, characterId, shipId);
                await transaction.CommitAsync();
            }

            Assert.Empty(await skills.ForCharacterAsync(characterId));
            var ships = new CharacterShips(source, statics, characters, items, skills, modules);
            var service = new InvBrokerService(items, new ItemTypeCatalog(sde), stations, ships, statics, () => FixedNowMs);
            var dogma = new DogmaIMService(statics, ships, () => FixedNowMs,
                (_, _, _, _, _, _) => ValueTask.FromResult<bool?>(null));
            Start(configure: registry =>
            {
                registry.Register(service);
                registry.Register(dogma);
            }, strictRpc: true, sessionOpened: session =>
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
                new KeyValuePair<PyValue, PyValue>(new PyInt(moduleTypeId), new PyList([new PyLong(moduleId)]))]);
            var keyVal = Py.KeyVal(("modulesByFlag", new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyInt(mediumSlot), new PyInt(moduleTypeId))])));
            var fitArgs = new PyTuple([
                new PyLong(shipId), new PyInt(shipTypeId), itemsByType, new PyLong(station.Id),
                keyVal, new PyDict([]), PyBool.True]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                open, 2, objectId, "FitFitting", fitArgs, machoVersion))));

            var notificationNames = new List<string>();
            PyValue? result = null;
            while (result is null)
            {
                var response = Assert.IsType<Send>(await front.ReceiveAsync());
                var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(response.Payload)));
                if (packet.Type == MachoMessage.Notification)
                {
                    notificationNames.Add(packet.Dest.BroadcastId!);
                    continue;
                }
                Assert.Equal(MachoMessage.CallRsp, packet.Type);
                result = Result(MarshalCodec.Decode(response.Payload));
            }

            Assert.Empty(Assert.IsType<PyList>(result).Items);
            Assert.Equal(["OnItemsChanged"], notificationNames);
            var fitted = Assert.Single(await items.InLocationAsync(shipId, characterId),
                item => item.Id == moduleId && item.TypeId == moduleTypeId && item.Flag == mediumSlot);
            Assert.Equal(ItemState.Offline, (await new PgModuleStateStore(source, schemas[3]).StatesAsync([fitted.Id]))[fitted.Id]);

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 3, "dogmaIM", "MachoBindObject",
                new PyTuple([new PyTuple([new PyLong(station.Id), new PyInt(15)]), PyNone.Instance]), machoVersion))));
            var dogmaBind = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(dogmaBind.Payload))).Type);
            var dogmaBindResult = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(dogmaBind.Payload)));
            var dogmaBound = Assert.IsType<PySubStruct>(dogmaBindResult.Items[0]);
            var dogmaBoundData = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(dogmaBound.Value).Decoded);
            var dogmaObjectId = Encoding.UTF8.GetString(Assert.IsType<PyBytes>(dogmaBoundData.Items[0]).Value.Span);

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                open, 4, dogmaObjectId, "SetModuleOnline",
                new PyTuple([new PyLong(shipId), new PyLong(fitted.Id)]), machoVersion))));
            var onlineReply = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(onlineReply.Payload))).Type);
            Assert.Contains("FittingHasSkillPrerequisites", Encoding.ASCII.GetString(onlineReply.Payload.Span));
            Assert.Equal(ItemState.Offline, (await modules.StatesAsync([fitted.Id]))[fitted.Id]);
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
