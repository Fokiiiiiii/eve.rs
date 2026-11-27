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
using Eve.Services.Dogma;
using Eve.Sde;
using Eve.Ships;
using Eve.Skills;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Registered_dogma_module_online_requires_95_percent_capacitor_and_persists_client_remainder()
    {
        const int shipTypeId = 601;
        const int moduleTypeId = 439;
        const int capacitorBatteryTypeId = 1185;
        const int mediumSlot = 19;
        const long stationId = 60_015_249;
        const int solarSystemId = 30_000_142;
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(connectionString!));
        var sde = new SdeReader(source);
        Assert.Equal(SdeBuildPin.Standard, await sde.BuildAsync());
        var statics = await DogmaStatic.LoadAsync(sde);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var schemas = new[] { $"onlinecap_c_{suffix}", $"onlinecap_i_{suffix}", $"onlinecap_s_{suffix}", $"onlinecap_m_{suffix}" };
        try
        {
            await PgCharacterStore.MigrateAsync(source, schemas[0]);
            await PgItemStore.MigrateAsync(source, schemas[1]);
            await PgSkillStore.MigrateAsync(source, schemas[2]);
            await PgModuleStateStore.MigrateAsync(source, schemas[3]);
            var characters = new PgCharacterStore(source, schemas[0]);
            var items = new PgItemStore(source, schemas[1]);
            var skills = new PgSkillStore(source, schemas[2]);
            var moduleStates = new PgModuleStateStore(source, schemas[3]);
            await statics.EnsureTypesAsync([shipTypeId, moduleTypeId, capacitorBatteryTypeId]);
            var moduleType = Assert.IsType<TypeDef>(statics.Type(moduleTypeId));
            var batteryType = Assert.IsType<TypeDef>(statics.Type(capacitorBatteryTypeId));
            var requirements = FittingRules.RequiredSkills(moduleType).Concat(FittingRules.RequiredSkills(batteryType)).Distinct().ToArray();
            Assert.NotEmpty(requirements);

            long characterId;
            long shipId;
            long moduleId;
            long capacitorBatteryId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                characterId = await characters.InsertAsync(connection, transaction, new NewCharacter(
                    1, $"Module online capacitor {suffix}", 1, 1, 1, 1, 1, 1, 1_000_001, null,
                    stationId, solarSystemId, 20_000_020, 10_000_002, null, null));
                shipId = await items.CreateAsync(connection, transaction,
                    new NewItem(shipTypeId, characterId, stationId, ItemFlags.Hangar, 1, Singleton: true));
                moduleId = await items.CreateAsync(connection, transaction,
                    new NewItem(moduleTypeId, characterId, shipId, mediumSlot, 1, Singleton: true));
                capacitorBatteryId = await items.CreateAsync(connection, transaction,
                    new NewItem(capacitorBatteryTypeId, characterId, shipId, mediumSlot + 1, 1, Singleton: true));
                await skills.GrantAsync(connection, transaction, characterId,
                    requirements.Select(skill => new CharacterSkill(skill.Skill, skill.Level, 0)));
                await characters.SetShipAsync(connection, transaction, characterId, shipId);
                await transaction.CommitAsync();
            }

            var initialCondition = ShipCondition.Full with { Charge = 0.949 };
            await moduleStates.SetShipConditionAsync(shipId, initialCondition);
            var ships = new CharacterShips(source, statics, characters, items, skills, moduleStates);
            var liveHealth = new RecordingSpaceHealth(new SpaceCondition(0, 0.949, 1, 0));
            var space = new RecordingModules();
            space.OnRefresh = session => session.SendNotification("OnOnlineCapacitorRefresh", "clientID");
            var service = new DogmaIMService(statics, ships, () => FixedNowMs,
                (_, _, _, _, _, _) => ValueTask.FromResult<bool?>(null),
                space: space, modules: space, health: liveHealth);

            Start(configure: registry => registry.Register(service), strictRpc: true, sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
                session.Set("solarsystemid", new PyInt(solarSystemId));
                session.Set("shipid", new PyLong(shipId));
            });

            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 1, "dogmaIM", "MachoBindObject",
                new PyTuple([new PyTuple([new PyInt(solarSystemId), new PyInt(5)]), PyNone.Instance]), MachoVersion()))));
            var bindReply = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.CallRsp, Envelope(bindReply).Type);
            var bindResult = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(bindReply.Payload)));
            var bound = Assert.IsType<PySubStruct>(bindResult.Items[0]);
            var boundData = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
            var objectId = Encoding.UTF8.GetString(Assert.IsType<PyBytes>(boundData.Items[0]).Value.Span);

            async Task<(MachoPacket Reply, List<MachoPacket> Notifications)> SwitchAsync(uint callId, string method, long targetModuleId = 0)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                    open, callId, objectId, method, new PyTuple([new PyLong(shipId), new PyLong(targetModuleId == 0 ? moduleId : targetModuleId)]), MachoVersion()))));
                var notifications = new List<MachoPacket>();
                while (true)
                {
                    var response = Assert.IsType<Send>(await front.ReceiveAsync());
                    var packet = Envelope(response);
                    if (packet.Type == MachoMessage.Notification)
                    {
                        notifications.Add(packet);
                        continue;
                    }
                    return (packet, notifications);
                }
            }

            var capacity = (await ships.LoadShipAsync(characterId, shipId))!.World.Get(shipId, 482);
            Assert.True(capacity > 0);
            var belowThreshold = await SwitchAsync(2, "SetModuleOnline");
            Assert.Equal(MachoMessage.ErrorResponse, belowThreshold.Reply.Type);
            Assert.Equal("NotEnoughCapacitorForOnline", CapacitorErrorMessage(belowThreshold.Reply));
            var refusalData = CapacitorErrorValues(belowThreshold.Reply);
            Assert.Equal(moduleTypeId, CapacitorInteger(CapacitorErrorValue(refusalData, "module")));
            Assert.Equal(94.9, CapacitorNumber(CapacitorErrorValue(refusalData, "have")), 6);
            Assert.Equal(95, CapacitorNumber(CapacitorErrorValue(refusalData, "need")), 6);
            Assert.Empty(belowThreshold.Notifications);
            Assert.Equal(ItemState.Offline, (await moduleStates.StatesAsync([moduleId]))[moduleId]);
            Assert.Equal(initialCondition, await moduleStates.ShipConditionAsync(shipId));

            liveHealth.Current = new SpaceCondition(0, 0.96, 1, 0);
            liveHealth.BeforeReturn = () => moduleStates.SetShipConditionAsync(
                shipId, initialCondition with { Charge = 0.95 });
            var concurrentChange = await SwitchAsync(3, "SetModuleOnline");
            Assert.Equal(MachoMessage.ErrorResponse, concurrentChange.Reply.Type);
            Assert.Equal("DeniedShipChanged", CapacitorErrorMessage(concurrentChange.Reply));
            Assert.Equal(["OnOnlineCapacitorRefresh"], concurrentChange.Notifications.Select(packet => packet.Dest.BroadcastId));
            Assert.Equal(ItemState.Offline, (await moduleStates.StatesAsync([moduleId]))[moduleId]);
            Assert.Equal(initialCondition with { Charge = 0.95 }, await moduleStates.ShipConditionAsync(shipId));

            liveHealth.BeforeReturn = null;
            liveHealth.Current = new SpaceCondition(0, 0.95, 1, 0);
            var atThreshold = await SwitchAsync(4, "SetModuleOnline");
            Assert.Equal(MachoMessage.CallRsp, atThreshold.Reply.Type);
            Assert.Equal(["OnMultiEvent", "OnOnlineCapacitorRefresh"], atThreshold.Notifications.Select(packet => packet.Dest.BroadcastId));
            var onlineNotice = atThreshold.Notifications[0];
            Assert.Equal("OnMultiEvent", onlineNotice.Dest.BroadcastId);
            var eventRows = CapacitorNotificationArguments(onlineNotice).Items[0] as PyList;
            Assert.NotNull(eventRows);
            var capacitorAttribute = Assert.Single(eventRows!.Items, row =>
            {
                var tuple = Assert.IsType<PyTuple>(row);
                var values = Assert.IsType<PyTuple>(tuple.Items[0]);
                return values.Items[0] is PyStr { Value: "OnModuleAttributeChange" }
                    && CapacitorInteger(values.Items[2]) == shipId && CapacitorInteger(values.Items[3]) == 18;
            });
            var capacitorChange = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(capacitorAttribute).Items[0]);
            Assert.Equal(capacity * 0.33, CapacitorNumber(capacitorChange.Items[5]), 6);
            Assert.Equal(capacity * 0.95, CapacitorNumber(capacitorChange.Items[6]), 6);
            Assert.Equal(ItemState.Online, (await moduleStates.StatesAsync([moduleId]))[moduleId]);
            Assert.Equal(ShipCondition.Full with { Charge = 0.33 }, await moduleStates.ShipConditionAsync(shipId));

            liveHealth.Current = new SpaceCondition(0, 0.33, 1, 0);
            var repeated = await SwitchAsync(5, "SetModuleOnline");
            Assert.Equal(MachoMessage.CallRsp, repeated.Reply.Type);
            Assert.Equal(["OnMultiEvent", "OnOnlineCapacitorRefresh"], repeated.Notifications.Select(packet => packet.Dest.BroadcastId));
            Assert.DoesNotContain(CapacitorNotificationArguments(repeated.Notifications[0]).Items[0] is PyList rows
                ? rows.Items
                : [], row =>
            {
                var values = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(row).Items[0]);
                return values.Items[0] is PyStr { Value: "OnModuleAttributeChange" }
                    && CapacitorInteger(values.Items[2]) == shipId && CapacitorInteger(values.Items[3]) == 18;
            });
            Assert.Equal(ShipCondition.Full with { Charge = 0.33 }, await moduleStates.ShipConditionAsync(shipId));

            var offline = await SwitchAsync(6, "TakeModuleOffline");
            Assert.Equal(MachoMessage.CallRsp, offline.Reply.Type);
            Assert.Equal(ItemState.Offline, (await moduleStates.StatesAsync([moduleId]))[moduleId]);
            Assert.Equal(ShipCondition.Full with { Charge = 0.33 }, await moduleStates.ShipConditionAsync(shipId));

            await moduleStates.SetShipConditionAsync(shipId, initialCondition with { Charge = 0.95 });
            liveHealth.Current = new SpaceCondition(0, 0.95, 1, 0);
            var capacityBeforeBattery = (await ships.LoadShipAsync(characterId, shipId))!.World.Get(shipId, 482);
            var batteryOnline = await SwitchAsync(7, "SetModuleOnline", capacitorBatteryId);
            Assert.Equal(MachoMessage.CallRsp, batteryOnline.Reply.Type);
            Assert.Equal(["OnMultiEvent", "OnOnlineCapacitorRefresh"], batteryOnline.Notifications.Select(packet => packet.Dest.BroadcastId));
            var batteryRows = Assert.IsType<PyList>(CapacitorNotificationArguments(batteryOnline.Notifications[0]).Items[0]);
            var batteryChargeRow = Assert.Single(batteryRows.Items, row =>
            {
                var values = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(row).Items[0]);
                return values.Items[0] is PyStr { Value: "OnModuleAttributeChange" }
                    && CapacitorInteger(values.Items[2]) == shipId && CapacitorInteger(values.Items[3]) == 18;
            });
            var batteryChargeChange = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(batteryChargeRow).Items[0]);
            Assert.Equal(capacityBeforeBattery * 0.33, CapacitorNumber(batteryChargeChange.Items[5]), 6);
            Assert.Equal(capacityBeforeBattery * 0.95, CapacitorNumber(batteryChargeChange.Items[6]), 6);
            var capacityAfterBattery = (await ships.LoadShipAsync(characterId, shipId))!.World.Get(shipId, 482);
            Assert.True(capacityAfterBattery > capacityBeforeBattery);
            Assert.Equal(ItemState.Online, (await moduleStates.StatesAsync([capacitorBatteryId]))[capacitorBatteryId]);
            Assert.Equal(ShipCondition.Full with { Charge = capacityBeforeBattery * 0.33 / capacityAfterBattery },
                await moduleStates.ShipConditionAsync(shipId));
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

    private sealed class RecordingSpaceHealth(SpaceCondition current) : ISpaceHealth
    {
        public SpaceCondition Current { get; set; } = current;
        public Func<Task>? BeforeReturn { get; set; }

        public async Task<SpaceCondition?> ConditionAsync(ClientSession session, CancellationToken cancellationToken)
        {
            if (BeforeReturn is { } beforeReturn)
                await beforeReturn();
            return Current;
        }

        public Task<bool> HealAsync(ClientSession session, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private static (MachoPacket Packet, PyTuple Arguments) DecodeOnlineCapNotification(MachoPacket packet)
    {
        var payload = Assert.IsType<PyTuple>(packet.Payload);
        var wrapped = Assert.IsType<PyTuple>(payload.Items[0]);
        var buffer = Assert.IsType<PyBuffer>(wrapped.Items[1]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.Decode(buffer.Value));
        var invocation = Assert.IsType<PyTuple>(decoded.Items[1]);
        return (packet, Assert.IsType<PyTuple>(invocation.Items[1]));
    }

    private static string CapacitorErrorMessage(MachoPacket packet)
    {
        var response = Assert.IsType<PyTuple>(packet.Payload);
        var wrapped = Assert.IsType<PyTuple>(response.Items[2]);
        var stream = Assert.IsType<PySubStream>(wrapped.Items[0]);
        var error = Assert.IsType<PyObjectEx>(stream.Decoded);
        var header = Assert.IsType<PyTuple>(error.Header);
        var arguments = Assert.IsType<PyTuple>(header.Items[1]);
        return JsSemantics.PlainText(arguments.Items[0]) ?? "";
    }

    private static PyDict CapacitorErrorValues(MachoPacket packet)
    {
        var response = Assert.IsType<PyTuple>(packet.Payload);
        var wrapped = Assert.IsType<PyTuple>(response.Items[2]);
        var stream = Assert.IsType<PySubStream>(wrapped.Items[0]);
        var error = Assert.IsType<PyObjectEx>(stream.Decoded);
        var header = Assert.IsType<PyTuple>(error.Header);
        var arguments = Assert.IsType<PyTuple>(header.Items[1]);
        return Assert.IsType<PyDict>(arguments.Items[1]);
    }

    private static PyValue CapacitorErrorValue(PyDict values, string key) =>
        values.Entries.Single(pair => JsSemantics.PlainText(pair.Key) == key).Value;

    private static PyTuple CapacitorNotificationArguments(MachoPacket packet) => DecodeOnlineCapNotification(packet).Arguments;

    private static long CapacitorInteger(PyValue value) => value switch
    {
        PyLong number => number.Value,
        PyInt number => number.Value,
        _ => throw new InvalidOperationException("expected an integer in the online notification"),
    };

    private static double CapacitorNumber(PyValue value) => value switch
    {
        PyReal number => number.Value,
        PyLong number => number.Value,
        PyInt number => number.Value,
        _ => throw new InvalidOperationException("expected a number in the online notification"),
    };
}
