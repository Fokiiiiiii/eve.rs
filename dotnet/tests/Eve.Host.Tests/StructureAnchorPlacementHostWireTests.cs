using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Eve.Structures;
using Npgsql;
using System.Text;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [StructureControlPgFact]
    public async Task Structure_anchor_rpc_rejects_a_location_inside_the_client_moon_clearance_and_preserves_the_item()
    {
        const int characterId = 98_710_071;
        const int corporationId = 98_710_072;
        const long shipId = 9_871_007_201;
        const int solarSystemId = 30_000_001;
        const int astrahusTypeId = 35_832;
        const int moonId = 40_000_004;
        const long stargateId = 50_000_056;
        const long asteroidBeltId = 40_000_003;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is required");
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        var prefix = "anchor_moon_" + Guid.NewGuid().ToString("N")[..12];
        var itemSchema = prefix + "i";
        var structureSchema = prefix + "r";
        var schedulerSchema = prefix + "q";
        var eventSchema = prefix + "e";
        var schemas = new[] { itemSchema, structureSchema, schedulerSchema, eventSchema };
        try
        {
            await PgItemStore.MigrateAsync(source, itemSchema);
            await StructureStore.MigrateAsync(source, structureSchema);
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventSchema);

            var sde = new SdeReader(source);
            Assert.Equal(3_396_210L, await sde.BuildAsync());
            var dogma = await DogmaStatic.LoadAsync(sde);
            var rules = await StructureRulesSource.LoadAsync(sde);
            var moon = Assert.Single(await sde.ReadAsync("mapMoons", [moonId]));
            Assert.Equal(solarSystemId, moon.Doc.GetProperty("solarSystemID").GetInt32());
            var position = moon.Doc.GetProperty("position");
            var x = position.GetProperty("x").GetDouble();
            var y = position.GetProperty("y").GetDouble();
            var z = position.GetProperty("z").GetDouble();
            var gate = Assert.Single(await sde.ReadAsync("mapStargates", [stargateId]));
            Assert.Equal(solarSystemId, gate.Doc.GetProperty("solarSystemID").GetInt32());
            var gatePosition = gate.Doc.GetProperty("position");
            var gateX = gatePosition.GetProperty("x").GetDouble();
            var gateY = gatePosition.GetProperty("y").GetDouble();
            var gateZ = gatePosition.GetProperty("z").GetDouble();
            var belt = Assert.Single(await sde.ReadAsync("mapAsteroidBelts", [asteroidBeltId]));
            Assert.Equal(solarSystemId, belt.Doc.GetProperty("solarSystemID").GetInt32());
            var beltPosition = belt.Doc.GetProperty("position");
            var beltX = beltPosition.GetProperty("x").GetDouble();
            var beltY = beltPosition.GetProperty("y").GetDouble();
            var beltZ = beltPosition.GetProperty("z").GetDouble();
            var items = new PgItemStore(source, itemSchema);
            var structures = new StructureStore(source, structureSchema);
            var nowMs = FixedNowMs;
            var shipAt = new WarpCoordinates(solarSystemId, x, y, z);
            var scheduler = new DurableScheduler(source, () => nowMs, new SchedulerOptions { Concurrency = 1 }, schedulerSchema, _ => { });
            var events = new EventBus(source, scheduler, new EventBusOptions { MaxAttempts = 3 }, eventSchema, _ => { });
            var timers = new StructureTimers(structures, scheduler, events, rules);
            timers.Register();
            var anchored = new List<StructureAnchored>();
            events.Subscribe("anchor.moon.test", [StructureAnchorAuthority.AnchoredTopic], (context, _) =>
            {
                anchored.Add(context.Event.Payload<StructureAnchored>()!);
                return Task.CompletedTask;
            });
            await events.PumpAsync();

            long itemId;
            long distantItemId;
            long gateItemId;
            long beltItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                distantItemId = await items.CreateAsync(connection, transaction,
                    new NewItem(astrahusTypeId, characterId, shipId, ItemFlags.Cargo, 1, true));
                itemId = await items.CreateAsync(connection, transaction,
                    new NewItem(astrahusTypeId, characterId, shipId, ItemFlags.Cargo, 1, true));
                gateItemId = await items.CreateAsync(connection, transaction,
                    new NewItem(astrahusTypeId, characterId, shipId, ItemFlags.Cargo, 1, true));
                beltItemId = await items.CreateAsync(connection, transaction,
                    new NewItem(astrahusTypeId, characterId, shipId, ItemFlags.Cargo, 1, true));
                await transaction.CommitAsync();
            }

            var authority = new StructureAnchorAuthority(
                source,
                structures,
                timers,
                rules,
                scheduler,
                events,
                items,
                dogma,
                (_, _, _, _) => Task.FromResult<long?>(nowMs - 30 * StructureTimes.Day),
                (_, _, _) => Task.FromResult<WarpCoordinates?>(shipAt),
                _ => false,
                _ => Task.CompletedTask,
                (_, _) => ValueTask.FromResult<IReadOnlyList<StructureProfileRecord>>([new(41, "Default", "", true)]));
            var callbacks = new StructureRpcCallbacks
            {
                AnchorAsync = (call, request) => new ValueTask(authority.AnchorAsync(call, request, call.Cancellation)),
            };
            var rpcErrors = new List<Exception>();

            Start(
                configure: registry => registry.Register(new StructureRpcService("structureDeployment", callbacks)),
                onProblem: (_, error) => { if (error is not null) rpcErrors.Add(error); },
                sessionOpened: session =>
                {
                    session.CharacterId = characterId;
                    session.Set("charid", new PyLong(characterId));
                    session.Set("corpid", new PyLong(corporationId));
                    session.Set("corprole", new PyLong(1L << 11));
                    session.Set("shipid", new PyLong(shipId));
                    session.Set("solarsystemid2", new PyInt(solarSystemId));
                });
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();

            // V24 clamps deployment offsets at 800 km. This 100 km client offset is valid; the old server
            // rejected it using a 75 km limit before checking the deployment point.
            shipAt = new WarpCoordinates(solarSystemId, 1e16, 1e16, 1e16);
            var distantArgs = new PyTuple([
                new PyLong(distantItemId), new PyReal(100_000), new PyReal(0), new PyReal(0), new PyLong(41),
                new PyStr("Long Offset Regression"), new PyStr(""), new PyInt(2), new PyInt(10), new PyDict([]),
            ]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 1, "structureDeployment", "Anchor", distantArgs,
                new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))])))));
            var placementNotices = new List<(string Name, string IdType, PyTuple Args)>();
            while (true)
            {
                var placementResponse = Assert.IsType<Send>(await front.ReceiveAsync());
                var placementPacket = Envelope(placementResponse);
                if (placementPacket.Type == MachoMessage.Notification)
                {
                    placementNotices.Add(DecodeClientNotification(placementResponse));
                    continue;
                }
                Assert.True(placementPacket.Type == MachoMessage.CallRsp,
                    placementPacket.Type == MachoMessage.ErrorResponse
                        ? UserErrorMessage(Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(placementResponse.Payload))))
                        : placementPacket.Type.ToString());
                Assert.IsType<PyNone>(Result(MarshalCodec.Decode(placementResponse.Payload)));
                break;
            }
            Assert.Equal(["OnItemChange"], placementNotices.Select(notice => notice.Name));
            Assert.Equal("clientID", placementNotices[0].IdType);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.Null(await items.LockAndGetAsync(connection, transaction, distantItemId));
                var placed = Assert.IsType<StructureRecord>(await structures.GetAsync(connection, transaction, distantItemId, false));
                Assert.Equal(100_000d, placed.X - shipAt.X, 2);
                Assert.Equal(solarSystemId, placed.SolarSystemId);
                await transaction.CommitAsync();
            }

            shipAt = new WarpCoordinates(solarSystemId, x, y, z);
            var args = new PyTuple([
                new PyLong(itemId), new PyReal(x), new PyReal(z), new PyReal(0), new PyLong(41),
                new PyStr("Moon Clearance Regression"), new PyStr(""), new PyInt(2), new PyInt(10), new PyDict([]),
            ]);
            var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 2, "structureDeployment", "Anchor", args, kwargs))));

            var response = Assert.IsType<Send>(await front.ReceiveAsync());
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(response.Payload)));
            Assert.Equal(MachoMessage.ErrorResponse, packet.Type);
            Assert.True(rpcErrors.Count == 0, string.Join("\n", rpcErrors.Select(error => error.ToString())));
            Assert.Equal("CantDeployBlocked", UserErrorMessage(packet));
            Assert.Empty(anchored);
            await events.PumpAsync();
            Assert.Equal(distantItemId, Assert.Single(anchored).StructureId);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.NotNull(await items.LockAndGetAsync(connection, transaction, itemId));
                Assert.Null(await structures.GetAsync(connection, transaction, itemId, false));
                await transaction.CommitAsync();
            }

            // The client/EveJS reject an ordinary structure within 1,000 km plus the gate and structure radii.
            // Put the ship 600 km away so this is a valid client offset, then target the gate itself.
            shipAt = new WarpCoordinates(solarSystemId, gateX - 600_000, gateY, gateZ);
            var gateArgs = new PyTuple([
                new PyLong(gateItemId), new PyReal(600_000), new PyReal(0), new PyReal(0), new PyLong(41),
                new PyStr("Stargate Clearance Regression"), new PyStr(""), new PyInt(2), new PyInt(10), new PyDict([]),
            ]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 3, "structureDeployment", "Anchor", gateArgs, kwargs))));
            var gateResponse = Assert.IsType<Send>(await front.ReceiveAsync());
            var gatePacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(gateResponse.Payload)));
            Assert.Equal(MachoMessage.ErrorResponse, gatePacket.Type);
            Assert.Equal("CantDeployBlocked", UserErrorMessage(gatePacket));
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.NotNull(await items.LockAndGetAsync(connection, transaction, gateItemId));
                Assert.Null(await structures.GetAsync(connection, transaction, gateItemId, false));
                await transaction.CommitAsync();
            }

            // The client includes the belt and structure radii in its 1,000 km celestial clearance. This point is
            // only 1,050 km from the belt, while the ship's clamped 800 km offset remains valid.
            shipAt = new WarpCoordinates(solarSystemId, beltX + 250_000, beltY, beltZ);
            var beltArgs = new PyTuple([
                new PyLong(beltItemId), new PyReal(800_000), new PyReal(0), new PyReal(0), new PyLong(41),
                new PyStr("Asteroid Belt Clearance Regression"), new PyStr(""), new PyInt(2), new PyInt(10), new PyDict([]),
            ]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 4, "structureDeployment", "Anchor", beltArgs, kwargs))));
            var beltResponse = Assert.IsType<Send>(await front.ReceiveAsync());
            var beltPacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(beltResponse.Payload)));
            Assert.Equal(MachoMessage.ErrorResponse, beltPacket.Type);
            Assert.Equal("CantDeployBlocked", UserErrorMessage(beltPacket));
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.NotNull(await items.LockAndGetAsync(connection, transaction, beltItemId));
                Assert.Null(await structures.GetAsync(connection, transaction, beltItemId, false));
                await transaction.CommitAsync();
            }
            Assert.Empty(rpcErrors);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            foreach (var schema in schemas)
            await using (var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection))
                await command.ExecuteNonQueryAsync();
        }
    }

    private static string UserErrorMessage(MachoPacket packet)
    {
        var response = Assert.IsType<PyTuple>(packet.Payload);
        var wrapped = Assert.IsType<PyTuple>(response.Items[2]);
        var stream = Assert.IsType<PySubStream>(wrapped.Items[0]);
        var error = Assert.IsType<PyObjectEx>(stream.Decoded);
        var header = Assert.IsType<PyTuple>(error.Header);
        var arguments = Assert.IsType<PyTuple>(header.Items[1]);
        return Encoding.UTF8.GetString(Assert.IsType<PyBytes>(arguments.Items[0]).Value.Span);
    }
}
