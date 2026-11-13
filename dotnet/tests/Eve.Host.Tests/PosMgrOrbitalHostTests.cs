using Eve.Data;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Planets;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Planets;
using Eve.Ships;
using Eve.Sde;
using Eve.Space;
using Npgsql;
using System.Text;
using System.Text.Json;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private class OrbitalSimulation : System.Reflection.DispatchProxy
    {
        public List<SimSetSlimField> SlimFields { get; } = [];

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "SetSlimField" && args is [long itemId, string fieldName, SimSlimValue value])
                SlimFields.Add(new SimSetSlimField(itemId, fieldName, value));
            var returnType = method?.ReturnType ?? typeof(void);
            if (returnType == typeof(void)) return null;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (returnType == typeof(Task<string>)) return Task.FromResult<string?>(null);
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

    [PgFact]
    public async Task PosMgr_orbital_calls_transition_and_persist_the_real_inventory_item_through_host_wire()
    {
        const long corporationId = 980_000_031;
        const long characterId = 90_000_031;
        const int systemId = 30_000_012;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var planetSchema = "pmo_p" + suffix;
        var inventorySchema = "pmo_i" + suffix;
        var schedulerSchema = "pmo_j" + suffix;
        await PlanetStore.MigrateAsync(source, planetSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await DurableScheduler.MigrateAsync(source, schedulerSchema);
        DurableScheduler scheduler = null!;
        try
        {
            var planets = new PlanetStore(source, planetSchema);
            var items = new PgItemStore(source, inventorySchema);
            var sde = new SdeReader(source);
            var dogma = await DogmaStatic.LoadAsync(sde);
            await dogma.EnsureTypesAsync([3_962, 2_233]);
            var gantryType = Assert.IsType<TypeDef>(dogma.Type(3_962));
            Assert.Equal(46, gantryType.CategoryId);
            var nowMs = FixedNowMs;
            var simulation = System.Reflection.DispatchProxy.Create<ISimulation, OrbitalSimulation>();
            var simRecorder = (OrbitalSimulation)(object)simulation;
            scheduler = new DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            PlanetMgrService CreateService(DurableScheduler target)
            {
                var service = new PlanetMgrService(source, planets,
                    new PlanetCatalog(new Dictionary<int, TypeInfo>(), new Dictionary<int, Schematic>()),
                    new DeterministicResourceField(), items, null!, null!, () => nowMs,
                    (_, _) => Task.CompletedTask, _ => Task.CompletedTask, inventorySchema, simulation: simulation, dogma: dogma);
                service.RegisterJobs(target);
                return service;
            }
            double OrbitalDelay(TypeDef type, string name, string? fallback = null)
            {
                var attributes = fallback is null ? new[] { name } : new[] { name, fallback };
                foreach (var attribute in attributes)
                {
                    if (dogma.AttributeId(attribute) is not { } attributeId) continue;
                    if (type.Attributes.TryGetValue(attributeId, out var delay)) return delay;
                    if (dogma.Attribute(attributeId) is { } definition) return definition.DefaultValue;
                }
                throw new InvalidOperationException($"Pinned SDE has no {name} for type {type.TypeId}.");
            }
            var currentService = CreateService(scheduler);

            long itemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                itemId = await items.CreateAsync(connection, transaction,
                    new NewItem(3_962, corporationId, systemId, ItemFlags.InSpace, Singleton: true));
                await transaction.CommitAsync();
            }

            var materialIds = new List<long>();
            var missingMaterialTypeId = 0;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("SELECT doc::text FROM sde.\"typeMaterials\" WHERE key = '2233'", connection))
            using (var materials = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!))
            {
                await using var transaction = await connection.BeginTransactionAsync();
                var requirements = materials.RootElement.GetProperty("materials").EnumerateArray().ToArray();
                Assert.NotEmpty(requirements);
                for (var index = 0; index < requirements.Length; index++)
                {
                    var row = requirements[index];
                    var typeId = row.GetProperty("materialTypeID").GetInt32();
                    var quantity = row.GetProperty("quantity").GetInt64();
                    if (index == 0)
                    {
                        Assert.True(quantity > 1);
                        missingMaterialTypeId = typeId;
                        quantity--;
                    }
                    materialIds.Add(await items.CreateAsync(connection, transaction,
                        new NewItem(typeId, corporationId, itemId, ItemFlags.SpecializedMaterialBay, quantity)));
                }
                await transaction.CommitAsync();
            }

            Start(posMgrOperation: call => currentService.PosMgrOrbitalOperationAsync(call),
                sessionOpened: session =>
                {
                    session.CharacterId = characterId;
                    session.Set("corpid", new PyLong(corporationId));
                    session.Set("solarsystemid2", new PyInt(systemId));
                    session.Set("shipid", new PyLong(90_000_032));
                });
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var concurrentFront = await Handshaken(await ConnectAsync());
            var concurrentOpen = Open(1_790_646_377_001UL * 15, 43);
            await concurrentFront.SendAsync(concurrentOpen);
            _ = await concurrentFront.ReceiveAsync();
            var notifications = new List<string>();

            async Task<Send> OrbitalResponse(uint callId, string method)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, callId, "posMgr", method, Py.Tuple(new PyLong(itemId)), MachoVersion()))));
                while (true)
                {
                    var reply = Assert.IsType<Send>(await front.ReceiveAsync());
                    var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(reply.Payload)));
                    if (packet.Type == MachoMessage.Notification)
                    {
                        var state = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(MarshalCodec.Decode(reply.Payload)).Args).Items;
                        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(state[2]).Args).Items;
                        notifications.Add(Assert.IsType<PyStr>(address[1]).Value);
                        continue;
                    }
                    return reply;
                }
            }

            async Task<PyValue> OrbitalCall(uint callId, string method)
            {
                var reply = await OrbitalResponse(callId, method);
                Assert.True(Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(reply.Payload))).Type == MachoMessage.CallRsp,
                    Encoding.UTF8.GetString(reply.Payload.Span));
                return Result(MarshalCodec.Decode(reply.Payload)) ?? PyNone.Instance;
            }

            async Task<PyValue> AnchorOn(TestFront target, SessionOpen targetOpen, uint callId)
            {
                await target.SendAsync(new Packet(targetOpen.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                    targetOpen, callId, "posMgr", "AnchorOrbital", Py.Tuple(new PyLong(itemId)), MachoVersion()))));
                var reply = Assert.IsType<Send>(await target.ReceiveAsync());
                Assert.Equal(MachoMessage.CallRsp, Envelope(reply).Type);
                return Result(MarshalCodec.Decode(reply.Payload)) ?? PyNone.Instance;
            }

            async Task AssertUpgradeMaterialsRefused(uint callId)
            {
                var reply = await OrbitalResponse(callId, "OnlineOrbital");
                Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(reply.Payload))).Type);
                var wire = Encoding.UTF8.GetString(reply.Payload.Span);
                Assert.Contains("CustomNotify", wire);
                Assert.Contains("required construction materials", wire);
            }

            var anchorResults = await Task.WhenAll(AnchorOn(front, open, 1), AnchorOn(concurrentFront, concurrentOpen, 1));
            Assert.All(anchorResults, result => Assert.IsType<PyNone>(result));
            Assert.Equal(-6, await OrbitalStateAsync(source, planetSchema, itemId));
            Assert.Single(await scheduler.ListAsync("planet.orbital.transition", "pending"));
            Assert.Contains(simRecorder.SlimFields, field => field.FieldName == "orbitalState" && field.Value == new SimSlimInt(-6));

            await scheduler.DisposeAsync();
            scheduler = new DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            currentService = CreateService(scheduler);
            nowMs += checked((long)OrbitalDelay(gantryType, "anchoringDelay"));
            Assert.Equal(1, await scheduler.RunDueAsync());
            Assert.Equal(-4, await OrbitalStateAsync(source, planetSchema, itemId));

            await AssertUpgradeMaterialsRefused(2);
            Assert.Equal(-4, await OrbitalStateAsync(source, planetSchema, itemId));
            foreach (var materialId in materialIds) Assert.NotNull(await items.GetAsync(materialId));

            long finalMaterialId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                finalMaterialId = await items.CreateAsync(connection, transaction,
                    new NewItem(missingMaterialTypeId, corporationId, itemId, ItemFlags.SpecializedMaterialBay, 1));
                await transaction.CommitAsync();
            }
            materialIds.Add(finalMaterialId);

            Assert.IsType<PyNone>(await OrbitalCall(3, "OnlineOrbital"));
            Assert.Equal(-5, await OrbitalStateAsync(source, planetSchema, itemId));
            Assert.Equal(materialIds.Count, notifications.Count(name => name == "OnItemChange"));
            foreach (var materialId in materialIds) Assert.Null(await items.GetAsync(materialId));
            nowMs += checked((long)OrbitalDelay(gantryType, "onliningDelay"));
            Assert.Equal(1, await scheduler.RunDueAsync());
            Assert.Equal(0, await OrbitalStateAsync(source, planetSchema, itemId));
            Assert.Equal(2_233, (await items.GetAsync(itemId))!.TypeId);
            Assert.Contains(simRecorder.SlimFields, field => field.ItemId == itemId && field.FieldName == "typeID" && field.Value == new SimSlimInt(2_233));
            Assert.Contains(simRecorder.SlimFields, field => field.ItemId == itemId && field.FieldName == "groupID" && field.Value == new SimSlimInt(1_025));
            Assert.Contains(simRecorder.SlimFields, field => field.ItemId == itemId && field.FieldName == "orbitalState" && field.Value == new SimSlimInt(0));

            Assert.IsType<PyNone>(await OrbitalCall(4, "UnanchorOrbital"));
            Assert.Equal(-3, await OrbitalStateAsync(source, planetSchema, itemId));
            var officeType = Assert.IsType<TypeDef>(dogma.Type(2_233));
            nowMs += checked((long)OrbitalDelay(officeType, "unanchoringDelay", "anchoringDelay"));
            Assert.Equal(1, await scheduler.RunDueAsync());
            Assert.Equal(-2, await OrbitalStateAsync(source, planetSchema, itemId));

            Assert.IsType<PyNone>(await OrbitalCall(5, "UnanchorOrbital"));
            Assert.Equal(-2, await OrbitalStateAsync(source, planetSchema, itemId));
        }
        finally
        {
            if (scheduler is not null) await scheduler.DisposeAsync();
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{planetSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{inventorySchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{schedulerSchema}\" CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task<int> OrbitalStateAsync(NpgsqlDataSource source, string schema, long itemId)
    {
        await using var command = source.CreateCommand($"SELECT state FROM \"{schema}\".orbital_states WHERE item_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = itemId });
        return (int)(short)(await command.ExecuteScalarAsync())!;
    }
}
