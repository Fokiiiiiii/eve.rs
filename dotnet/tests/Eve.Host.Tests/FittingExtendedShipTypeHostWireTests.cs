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
using Eve.Sde;
using Eve.Ships;
using Eve.Skills;
using Eve.Universe;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Registered_invbroker_honors_the_twelfth_ship_fit_restriction_from_the_sde()
    {
        const int odysseusTypeId = 89_607;
        const int rifterTypeId = 601;
        const int cloakTypeId = 11_578;
        const int highSlot = Slots.HighFirst;
        const int canFitShipType12 = 5_948;
        const long stationId = 60_015_249;

        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(connectionString!));
        var sde = new SdeReader(source);
        Assert.Equal(3_396_210L, await sde.BuildAsync());
        var statics = await DogmaStatic.LoadAsync(sde);
        var stations = await StationCatalog.LoadAsync(sde);
        var station = stations.Get(stationId) ?? throw new InvalidOperationException("The pinned SDE station is missing.");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var schemas = new[] { $"fit_type_c_{suffix}", $"fit_type_i_{suffix}", $"fit_type_s_{suffix}", $"fit_type_m_{suffix}" };

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
            await statics.EnsureTypesAsync([odysseusTypeId, rifterTypeId, cloakTypeId]);
            var cloak = Assert.IsType<TypeDef>(statics.Type(cloakTypeId));
            Assert.Equal(odysseusTypeId, (int)cloak.Attributes[canFitShipType12]);
            Assert.Equal("canFitShipType12", statics.Attribute(canFitShipType12)?.Name);
            Assert.Contains(FittingRules.RequiredSkills(cloak), requirement => requirement.Skill == 11_579 && requirement.Level == 4);

            long characterId;
            long odysseusId;
            long rifterId;
            long allowedCloakId;
            long refusedCloakId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                characterId = await characters.InsertAsync(connection, transaction, new NewCharacter(
                    1, $"Ship type restriction {suffix}", 1, 1, 1, 1, 1, 1, 1_000_001, null,
                    station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
                odysseusId = await items.CreateAsync(connection, transaction,
                    new NewItem(odysseusTypeId, characterId, station.Id, ItemFlags.Hangar, 1, Singleton: true));
                rifterId = await items.CreateAsync(connection, transaction,
                    new NewItem(rifterTypeId, characterId, station.Id, ItemFlags.Hangar, 1, Singleton: true));
                allowedCloakId = await items.CreateAsync(connection, transaction,
                    new NewItem(cloakTypeId, characterId, station.Id, ItemFlags.Hangar, 1, Singleton: true));
                refusedCloakId = await items.CreateAsync(connection, transaction,
                    new NewItem(cloakTypeId, characterId, station.Id, ItemFlags.Hangar, 1, Singleton: true));
                await skills.GrantAsync(connection, transaction, characterId,
                    FittingRules.RequiredSkills(cloak).Select(skill => new CharacterSkill(skill.Skill, skill.Level, 0)));
                await characters.SetShipAsync(connection, transaction, characterId, odysseusId);
                await transaction.CommitAsync();
            }

            var ships = new CharacterShips(source, statics, characters, items, skills, modules);
            var service = new InvBrokerService(items, new ItemTypeCatalog(sde), stations, ships, statics, () => FixedNowMs);
            ClientSession? session = null;
            Start(configure: registry => registry.Register(service), strictRpc: true, sessionOpened: opened =>
            {
                session = opened;
                opened.CharacterId = characterId;
                opened.Set("charid", new PyInt(characterId));
                opened.Set("stationid", new PyInt(station.Id));
                opened.Set("stationid2", new PyInt(station.Id));
                opened.Set("shipid", new PyInt(odysseusId));
            });

            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var machoVersion = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(3396210))]);

            async Task<string> BindShipAsync(long shipId, int callId)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, callId, "invbroker", "GetInventoryFromId", new PyTuple([new PyLong(shipId)]), machoVersion))));
                var reply = Assert.IsType<Send>(await front.ReceiveAsync());
                Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(reply.Payload))).Type);
                var bound = Assert.IsType<PySubStruct>(Result(MarshalCodec.Decode(reply.Payload)));
                var data = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
                return Encoding.UTF8.GetString(Assert.IsType<PyBytes>(data.Items[0]).Value.Span);
            }

            async Task<(PyValue Result, IReadOnlyList<string> Notifications)> FitAsync(long shipId, int shipTypeId, long cloakId, string objectId, int callId)
            {
                var itemsByType = new PyDict([
                    new KeyValuePair<PyValue, PyValue>(new PyInt(cloakTypeId), new PyList([new PyLong(cloakId)]))]);
                var keyVal = Py.KeyVal(("modulesByFlag", new PyDict([
                    new KeyValuePair<PyValue, PyValue>(new PyInt(highSlot), new PyInt(cloakTypeId))])));
                var args = new PyTuple([
                    new PyLong(shipId), new PyInt(shipTypeId), itemsByType, new PyLong(station.Id),
                    keyVal, new PyDict([]), PyBool.True]);
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                    open, callId, objectId, "FitFitting", args, machoVersion))));

                var notifications = new List<string>();
                while (true)
                {
                    var response = Assert.IsType<Send>(await front.ReceiveAsync());
                    var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(response.Payload)));
                    if (packet.Type == MachoMessage.Notification)
                    {
                        notifications.Add(packet.Dest.BroadcastId!);
                        continue;
                    }
                    Assert.Equal(MachoMessage.CallRsp, packet.Type);
                    return (Result(MarshalCodec.Decode(response.Payload)) ?? PyNone.Instance, notifications);
                }
            }

            var odysseusObject = await BindShipAsync(odysseusId, 1);
            var (allowedResult, allowedNotifications) = await FitAsync(
                odysseusId, odysseusTypeId, allowedCloakId, odysseusObject, 2);
            Assert.Empty(Assert.IsType<PyList>(allowedResult).Items);
            Assert.Equal(["OnMultiEvent", "OnItemsChanged"], allowedNotifications);
            var fittedCloak = Assert.Single(await new PgItemStore(source, schemas[1]).InLocationAsync(odysseusId, characterId),
                item => item.Id == allowedCloakId && item.TypeId == cloakTypeId && item.Flag == highSlot);
            Assert.Equal(ItemState.Online, (await new PgModuleStateStore(source, schemas[3]).StatesAsync([fittedCloak.Id]))[fittedCloak.Id]);

            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await characters.SetShipAsync(connection, transaction, characterId, rifterId);
                await transaction.CommitAsync();
            }
            Assert.NotNull(session);
            session!.Set("shipid", new PyInt(rifterId));

            var rifterObject = await BindShipAsync(rifterId, 3);
            var (refusedResult, refusedNotifications) = await FitAsync(
                rifterId, rifterTypeId, refusedCloakId, rifterObject, 4);
            var refused = Assert.IsType<PyList>(refusedResult);
            var refusal = Assert.IsType<PyTuple>(Assert.Single(refused.Items));
            Assert.Equal(cloakTypeId, Assert.IsType<PyInt>(refusal.Items[0]).Value);
            Assert.Equal(1, Assert.IsType<PyInt>(refusal.Items[1]).Value);
            Assert.Empty(refusedNotifications);
            var unchangedCloak = Assert.Single(await items.InLocationAsync(station.Id, characterId), item => item.Id == refusedCloakId);
            Assert.Equal(ItemFlags.Hangar, unchangedCloak.Flag);
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
