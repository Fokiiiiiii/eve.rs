using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Fitting;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Ships;
using Eve.Skills;
using Eve.Structures;
using Npgsql;
using System.Text.Json.Nodes;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private const long ServiceToggleCorporation = 98_710_001;
    private const long ServiceToggleStructure = 8_710_001;
    private const long ServiceToggleStation = 60_015_249;
    private const int ServiceToggleSystem = 30_000_142;
    private const int ServiceToggleAstrahus = 35_832;
    private const int MarketHubModule = 35_892;
    private const int HydrogenFuelBlock = 4_246;
    private const int StructureFuelBayFlag = 172;
    private const long StationManagerRole = 1L << 11;

    private sealed class ServiceToggleRules : IStructureRulesSource
    {
        public StructureRules RulesOf(int typeId, int solarSystemId) => StructureRules.Of(StructureSize.Medium, SecurityBand.HighSec);
    }

    private static (string Name, string IdType, PyTuple Args) DecodeClientNotification(Send packet)
    {
        var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(MarshalCodec.Decode(packet.Payload)).Args).Items;
        Assert.Equal(MachoMessage.Notification, Assert.IsType<PyInt>(fields[0]).Value);
        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(fields[2]).Args).Items;
        var envelope = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(fields[4]).Items[0]);
        var call = Assert.IsType<PyTuple>(MarshalCodec.Decode(Assert.IsType<PyBuffer>(envelope.Items[1]).Value));
        var callArguments = Assert.IsType<PyTuple>(call.Items[1]);
        return (
            JsSemantics.StringValue(address[1]) ?? "",
            JsSemantics.StringValue(address[3]) ?? "",
            Assert.IsType<PyTuple>(callArguments.Items[1]));
    }

    [StructureControlPgFact]
    public async Task Structure_service_module_rpc_refuses_without_fuel_then_retries_and_commits_notifications_in_order()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is required");
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        var prefix = "service_toggle_" + Guid.NewGuid().ToString("N")[..12];
        var characterSchema = prefix + "c";
        var itemSchema = prefix + "i";
        var skillSchema = prefix + "s";
        var fittingSchema = prefix + "f";
        var structureSchema = prefix + "r";
        var schedulerSchema = prefix + "q";
        var eventSchema = prefix + "e";
        var schemas = new[] { characterSchema, itemSchema, skillSchema, fittingSchema, structureSchema, schedulerSchema, eventSchema };
        try
        {
            await PgCharacterStore.MigrateAsync(source, characterSchema);
            await PgItemStore.MigrateAsync(source, itemSchema);
            await PgSkillStore.MigrateAsync(source, skillSchema);
            await PgModuleStateStore.MigrateAsync(source, fittingSchema);
            await StructureStore.MigrateAsync(source, structureSchema);
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventSchema);

            var sde = new SdeReader(source);
            Assert.NotNull(await sde.BuildAsync());
            var dogma = await DogmaStatic.LoadAsync(sde);
            await dogma.EnsureTypesAsync([HydrogenFuelBlock]);
            var characters = new PgCharacterStore(source, characterSchema);
            var items = new PgItemStore(source, itemSchema);
            var skills = new PgSkillStore(source, skillSchema);
            var modules = new PgModuleStateStore(source, fittingSchema);
            var structures = new StructureStore(source, structureSchema);
            var pilots = new StructurePilotStore(structures);
            var scheduler = new DurableScheduler(source, () => FixedNowMs, new SchedulerOptions { Concurrency = 1 }, schedulerSchema, _ => { });
            var events = new EventBus(source, scheduler, new EventBusOptions { MaxAttempts = 3 }, eventSchema, _ => { });
            var rules = new ServiceToggleRules();
            var timers = new StructureTimers(structures, scheduler, events, rules);
            timers.Register();
            var bay = new InventoryStructureFuelBay(items, dogma, events);
            var serviceFuelRules = await ServiceFuelRulesSource.LoadAsync(dogma, sde);
            var fuel = new StructureFuel(structures, scheduler, timers, serviceFuelRules, bay, new PgStructureServiceModuleStates(modules));
            fuel.Register();
            var control = new StructureControlReadModel(source, structures, pilots, items, modules, dogma,
                (_, _, _, _) => ValueTask.FromResult(true));
            var authority = new StructureServiceModuleAuthority(source, structures, pilots, control, items, modules, dogma, fuel);
            var profile = new JsonObject
            {
                ["profilesByID"] = new JsonObject
                {
                    ["1"] = new JsonObject
                    {
                        ["corporationID"] = ServiceToggleCorporation,
                        ["isDefault"] = true,
                        ["settingsBySettingID"] = new JsonObject
                        {
                            ["4"] = new JsonArray(new JsonObject { ["groupID"] = 0, ["value"] = 1 }),
                        },
                    },
                },
            };
            Task<JsonObject> ReadTable(string table, CancellationToken token) => table switch
            {
                "structures" => structures.ReadLegacyTableAsync(token),
                "structureProfiles" => Task.FromResult((JsonObject)profile.DeepClone()),
                _ => Task.FromResult(new JsonObject()),
            };
            var structureSettings = new StructureSettingsService(new PersistenceStructureDirectoryReadModel(
                ReadTable,
                (_, _) => Task.FromResult(new Eve.Relations.Affiliation(ServiceToggleCorporation, null, null))));
            var characterSessions = new CharacterSessions();
            StructureNotifications.Subscribe(events, structures, characterSessions,
                (_, _) => ValueTask.FromResult<PyValue?>(null));
            await events.PumpAsync();
            var ships = new CharacterShips(source, dogma, characters, items, skills, modules);
            var service = new DogmaIMService(dogma, ships, () => FixedNowMs,
                (_, _, _, _, _, _) => ValueTask.FromResult<bool?>(null),
                controlledStructureSnapshot: control.DogmaAsync,
                structureServiceModuleToggle: authority.ToggleAsync);

            long characterId;
            long personalShipId;
            long moduleId;
            long wrongOwnerModuleId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                characterId = await characters.InsertAsync(connection, transaction,
                    new NewCharacter(1, "Service Module Test", 1, 1, 1, 1, 1, 1, ServiceToggleCorporation, null,
                        ServiceToggleStation, ServiceToggleSystem, 20_000_020, 10_000_002, null, null));
                personalShipId = await items.CreateAsync(connection, transaction,
                    new NewItem(11_019, characterId, ServiceToggleStation, ItemFlags.Hangar, 1, true));
                await characters.SetShipAsync(connection, transaction, characterId, personalShipId);
                Assert.True(await structures.CreateAsync(connection, transaction,
                    new NewStructure(ServiceToggleStructure, ServiceToggleAstrahus, ServiceToggleSystem, 1, 2, 3, 0,
                        ServiceToggleCorporation, 0, "Service Module Test Astrahus", "")));
                moduleId = await items.CreateAsync(connection, transaction,
                    new NewItem(MarketHubModule, ServiceToggleCorporation, ServiceToggleStructure,
                        StructureServiceIds.ServiceSlotFirst, 1, true));
                wrongOwnerModuleId = await items.CreateAsync(connection, transaction,
                    new NewItem(MarketHubModule, ServiceToggleCorporation + 1, ServiceToggleStructure,
                        StructureServiceIds.ServiceSlotFirst + 1, 1, true));
                _ = await items.CreateAsync(connection, transaction,
                    new NewItem(HydrogenFuelBlock, ServiceToggleCorporation, ServiceToggleStructure,
                        StructureFuelBayFlag, 2_159, false));
                await transaction.CommitAsync();
            }

            var rule = Assert.IsType<StructureServiceModuleFuelRule>(serviceFuelRules.RuleOfModule(MarketHubModule, ServiceToggleAstrahus));
            Assert.Equal(30, rule.FuelPerHour);
            Assert.Equal(2_160, rule.FuelToOnline);

            ClientSession? client = null;
            Start(configure: registry =>
            {
                registry.Register(service);
                registry.Register(structureSettings);
            }, sessionOpened: session =>
            {
                client = session;
                session.Set("charid", new PyLong(characterId));
                session.Set("corpid", new PyLong(ServiceToggleCorporation));
                session.Set("corprole", new PyLong(StationManagerRole));
                session.Set("stationid", new PyLong(ServiceToggleStructure));
                session.Set("shipid", new PyLong(ServiceToggleStructure));
                session.Set("structureid", new PyLong(ServiceToggleStructure));
                characterSessions.Enter(characterId, session);
            });
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            Assert.NotNull(client);
            Assert.True(client!.SessionId > 0);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                var pilot = await pilots.AcquireAsync(connection, transaction,
                    new StructurePilotRequest(ServiceToggleStructure, characterId, client.SessionId,
                        personalShipId, ServiceToggleStation, false), false);
                Assert.Equal(StructurePilotAcquireStatus.Acquired, pilot.Status);
                await transaction.CommitAsync();
            }

            async Task<string> BindAsync(uint callId)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, callId, "dogmaIM", "MachoBindObject",
                    new PyTuple([
                        new PyTuple([new PyLong(ServiceToggleStructure), new PyInt(15)]),
                        new PyTuple([new PyStr("GetRequiredSkillLevels"), new PyTuple([new PyInt(MarketHubModule)]), PyNone.Instance]),
                    ]), MachoVersion()))));
                var reply = Assert.IsType<Send>(await front.ReceiveAsync());
                Assert.Equal(MachoMessage.CallRsp, Envelope(reply).Type);
                var result = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(reply.Payload)));
                var bound = Assert.IsType<PySubStruct>(result.Items[0]);
                var boundTuple = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
                return System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(boundTuple.Items[0]).Value.Span);
            }

            async Task<(Send Response, IReadOnlyList<Send> Notices)> ToggleAsync(uint callId, string oid, bool online = true, long? targetModuleId = null)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                    open, callId, oid, online ? "SetModuleOnline" : "TakeModuleOffline",
                    new PyTuple([new PyLong(ServiceToggleStructure), new PyLong(targetModuleId ?? moduleId)]), MachoVersion()))));
                var notices = new List<Send>();
                for (var i = 0; i < 4; i++)
                {
                    var packet = Assert.IsType<Send>(await front.ReceiveAsync());
                    var type = Envelope(packet).Type;
                    if (type is MachoMessage.CallRsp or MachoMessage.ErrorResponse)
                        return (packet, notices);
                    notices.Add(packet);
                }
                throw new Xunit.Sdk.XunitException("The module toggle did not return a Macho response.");
            }

            var oid = await BindAsync(1);
            var wrongOwner = await ToggleAsync(2, oid, targetModuleId: wrongOwnerModuleId);
            Assert.Equal(MachoMessage.ErrorResponse, Envelope(wrongOwner.Response).Type);
            Assert.Empty(wrongOwner.Notices);
            Assert.Equal("DeniedShipChanged",
                (await authority.ToggleAsync(client!, ServiceToggleStructure, personalShipId, moduleId, true, default))?.Error);
            Assert.Null(await authority.ToggleAsync(client!, personalShipId, personalShipId, moduleId, true, default));
            var refused = await ToggleAsync(3, oid);
            Assert.Equal(MachoMessage.ErrorResponse, Envelope(refused.Response).Type);
            Assert.Empty(refused.Notices);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.Equal(2_159, await bay.QuantityAsync(connection, transaction, ServiceToggleStructure, default));
                Assert.Equal(ItemState.Offline, await modules.StateAsync(connection, transaction, moduleId));
                Assert.False((await structures.ServicesAsync(connection, transaction, ServiceToggleStructure)).ContainsKey(StructureServiceIds.Market));
                Assert.Null(await structures.ServiceModuleFuelCycleAsync(connection, transaction, ServiceToggleStructure, moduleId));
                await using var count = new NpgsqlCommand($"SELECT count(*) FROM {eventSchema}.outbox", connection, transaction);
                Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
                await transaction.CommitAsync();
            }

            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                _ = await items.CreateAsync(connection, transaction,
                    new NewItem(HydrogenFuelBlock, ServiceToggleCorporation, ServiceToggleStructure,
                        StructureFuelBayFlag, 1, false));
                await transaction.CommitAsync();
            }
            var online = await ToggleAsync(4, oid);
            Assert.Equal(MachoMessage.CallRsp, Envelope(online.Response).Type);
            Assert.IsType<PyNone>(Result(MarshalCodec.Decode(online.Response.Payload)));
            Assert.Equal(new[] { MachoMessage.Notification }, online.Notices.Select(packet => Envelope(packet).Type));
            Assert.Equal("OnMultiEvent", DecodeClientNotification(online.Notices.Single()).Name);
            var duplicateOnline = await ToggleAsync(5, oid);
            Assert.Equal(MachoMessage.CallRsp, Envelope(duplicateOnline.Response).Type);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.Equal(0, await bay.QuantityAsync(connection, transaction, ServiceToggleStructure, default));
                Assert.Equal(ItemState.Online, await modules.StateAsync(connection, transaction, moduleId));
                Assert.Equal(ServiceState.Online,
                    (await structures.ServicesAsync(connection, transaction, ServiceToggleStructure))[StructureServiceIds.Market]);
                var cycle = Assert.IsType<StructureServiceModuleFuelCycle>(
                    await structures.ServiceModuleFuelCycleAsync(connection, transaction, ServiceToggleStructure, moduleId));
                Assert.Equal(FixedNowMs + StructureFuel.HourMs, cycle.NextFuelCycleMs);
                await using var topics = new NpgsqlCommand($"SELECT topic, id FROM {eventSchema}.outbox WHERE topic IN ($1, $2) ORDER BY id", connection, transaction);
                topics.Parameters.AddWithValue(StructureFuelBayNotifications.Topic);
                topics.Parameters.AddWithValue(StructureTimers.ServiceChangedTopic);
                var observed = new List<(string Topic, long Id)>();
                await using (var reader = await topics.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                        observed.Add((reader.GetString(0), reader.GetInt64(1)));
                }
                Assert.Equal(2, observed.Count);
                Assert.Equal(StructureFuelBayNotifications.Topic, observed[0].Topic);
                Assert.Equal(StructureTimers.ServiceChangedTopic, observed[1].Topic);
                Assert.True(observed[0].Id < observed[1].Id);
                await transaction.CommitAsync();
            }

            await events.PumpAsync();
            var serviceChanged = DecodeClientNotification(Assert.IsType<Send>(await front.ReceiveAsync()));
            Assert.Equal("OnStructureServiceChanged", serviceChanged.Name);
            Assert.Equal("clientID", serviceChanged.IdType);
            Assert.Equal(Py.Tuple(new PyLong(ServiceToggleStructure)), serviceChanged.Args);

            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 6, "structureSettings", "CharacterGetServices",
                Py.Tuple(new PyLong(ServiceToggleStructure)), MachoVersion()))));
            var servicesReply = Assert.IsType<Send>(await front.ReceiveAsync());
            Assert.Equal(MachoMessage.CallRsp, Envelope(servicesReply).Type);
            var onlineServices = Assert.IsType<PyDict>(Result(MarshalCodec.Decode(servicesReply.Payload)));
            Assert.Contains(onlineServices.Entries, entry => entry.Key == new PyInt(StructureServiceIds.Market) && entry.Value == new PyInt(ServiceState.Online));

            client!.Set("corprole", new PyLong(0));
            var refusedOffline = await ToggleAsync(7, oid, online: false);
            Assert.Equal(MachoMessage.ErrorResponse, Envelope(refusedOffline.Response).Type);
            Assert.Empty(refusedOffline.Notices);
            Assert.Contains("InsufficientRoles", System.Text.Encoding.UTF8.GetString(refusedOffline.Response.Payload.Span));
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.Equal(0, await bay.QuantityAsync(connection, transaction, ServiceToggleStructure, default));
                Assert.Equal(ItemState.Online, await modules.StateAsync(connection, transaction, moduleId));
                Assert.Equal(ServiceState.Online,
                    (await structures.ServicesAsync(connection, transaction, ServiceToggleStructure))[StructureServiceIds.Market]);
                await using var count = new NpgsqlCommand(
                    $"SELECT count(*) FROM {eventSchema}.outbox WHERE topic = $1", connection, transaction);
                count.Parameters.AddWithValue(StructureTimers.ServiceChangedTopic);
                Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
                await transaction.CommitAsync();
            }

            client.Set("corprole", new PyLong(StationManagerRole));
            var offline = await ToggleAsync(8, oid, online: false);
            Assert.Equal(MachoMessage.CallRsp, Envelope(offline.Response).Type);
            Assert.Equal(new[] { MachoMessage.Notification }, offline.Notices.Select(packet => Envelope(packet).Type));
            var duplicateOffline = await ToggleAsync(9, oid, online: false);
            Assert.Equal(MachoMessage.CallRsp, Envelope(duplicateOffline.Response).Type);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.Equal(0, await bay.QuantityAsync(connection, transaction, ServiceToggleStructure, default));
                Assert.Equal(ItemState.Offline, await modules.StateAsync(connection, transaction, moduleId));
                Assert.Equal(ServiceState.Offline,
                    (await structures.ServicesAsync(connection, transaction, ServiceToggleStructure))[StructureServiceIds.Market]);
                Assert.Null(await structures.ServiceModuleFuelCycleAsync(connection, transaction, ServiceToggleStructure, moduleId));
                await using var count = new NpgsqlCommand(
                    $"SELECT count(*) FROM {eventSchema}.outbox WHERE topic = $1", connection, transaction);
                count.Parameters.AddWithValue(StructureTimers.ServiceChangedTopic);
                Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
                await transaction.CommitAsync();
            }
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            foreach (var schema in schemas)
                await using (var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection))
                    await command.ExecuteNonQueryAsync();
        }
    }
}
