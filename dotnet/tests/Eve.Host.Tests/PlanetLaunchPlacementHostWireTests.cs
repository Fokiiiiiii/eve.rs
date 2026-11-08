using System.Reflection;
using Eve.Data;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Planets;
using Eve.Services;
using Eve.Services.Planets;
using Eve.Scheduler;
using Eve.Ships;
using Eve.Skills;
using Eve.Space;
using Eve.Sde;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task A_launch_remains_a_success_when_space_placement_fails_after_its_database_commit()
    {
        const long owner = 90_091;
        const long planetId = 40_486_618;
        const long commandPinId = 800_091;
        const int commodityTypeId = 2_268;
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var planetSchema = "plp_p" + suffix;
        var inventorySchema = "plp_i" + suffix;
        var skillsSchema = "plp_s" + suffix;
        var spaceSchema = "plp_x" + suffix;
        var schedulerSchema = "plp_j" + suffix;
        await PlanetStore.MigrateAsync(source, planetSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgSkillStore.MigrateAsync(source, skillsSchema);
        await PgSpaceItemStore.MigrateAsync(source, spaceSchema);
        await DurableScheduler.MigrateAsync(source, schedulerSchema);
        try
        {
            var catalog = await PlanetCatalogLoader.LoadAsync(source);
            var body = Assert.IsType<PlanetBody>(await PlanetCatalogLoader.PlanetAsync(source, planetId));
            var planets = new PlanetStore(source, planetSchema);
            var items = new PgItemStore(source, inventorySchema);
            var spaceItems = new PgSpaceItemStore(source, spaceSchema);
            var skills = new PgSkillStore(source, skillsSchema);
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
                Contents = new Dictionary<int, long> { [commodityTypeId] = 5 },
            };
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await planets.SaveAsync(connection, transaction, owner, planetId, ColonyDocument.From(colony), FixedNowMs);
                await transaction.CommitAsync();
            }

            var simulation = DispatchProxy.Create<ISimulation, PlanetLaunchPlacementProxy>();
            var placement = (PlanetLaunchPlacementProxy)(object)simulation;
            var dogma = await DogmaStatic.LoadAsync(new SdeReader(source));
            var nowMs = FixedNowMs;
            var scheduler = new DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            ClientSession? hostSession = null;
            var launchNotifications = 0;
            var planetNotificationAttempts = 0;
            PlanetMgrService CreateService(DurableScheduler target)
            {
                var created = new PlanetMgrService(source, planets, catalog, new DeterministicResourceField(), items, null!, skills,
                    () => nowMs, (_, _) =>
                    {
                        if (Interlocked.Increment(ref planetNotificationAttempts) == 1)
                            throw new IOException("injected colony notification failure after commit");
                        return Task.CompletedTask;
                    },
                    _ =>
                    {
                        Interlocked.Increment(ref launchNotifications);
                        hostSession!.SendNotification("OnPILaunchesChange", "clientID");
                        return Task.CompletedTask;
                    }, inventorySchema, simulation, spaceItems, dogma);
                created.RegisterJobs(target);
                return created;
            }

            var service = CreateService(scheduler);
            Start(
                configure: registry => registry.Register(service),
                onProblem: (message, error) => _log.Add($"{message}: {error}"),
                sessionOpened: session =>
                {
                    session.Set("charid", new PyLong(owner));
                    hostSession = session;
                });
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
            var bound = Assert.IsType<PySubStruct>(Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(bindReply.Payload))).Items[0]);
            var reference = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
            var objectId = Assert.IsType<string>(JsSemantics.StringValue(reference.Items[0]));
            var commodities = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyInt(commodityTypeId), new PyLong(3)),
            ]);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.BoundCall(
                open, 2, objectId, "UserLaunchCommodities", new PyTuple([new PyLong(commandPinId), commodities]), kwargs))));
            var launchReply = Assert.IsType<Send>(await front.ReceiveAsync());
            var responseType = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(launchReply.Payload))).Type;
            var launches = await planets.LaunchesOfAsync(owner, Py.FileTimeFromUnixMs(FixedNowMs));
            var saved = Assert.IsType<ColonyDocument>(await planets.ReadAsync(owner, planetId));
            var failures = new List<string>();
            if (responseType != MachoMessage.CallRsp)
                failures.Add($"launch RPC returned Macho type {responseType}, expected success; recent host log: {string.Join(" | ", _log.TakeLast(3))}; wire: {System.Text.Encoding.Latin1.GetString(launchReply.Payload.Span)}");
            if (launches.Count != 1) failures.Add($"database has {launches.Count} committed launch rows, expected one");
            if (saved.Pins.Single().Contents.GetValueOrDefault(commodityTypeId) != 2) failures.Add("committed colony quantity was not decremented once");
            if (planetNotificationAttempts != 1) failures.Add("the post-commit colony notification was not attempted");
            if (launchNotifications != 0) failures.Add("launch notification ran before the launch can was placed");
            if (placement.PlacedWrecks.Count != 0) failures.Add("simulation accepted a can placement despite the injected failure");
            Assert.True(failures.Count == 0, string.Join("\n", failures));

            var launch = Assert.Single(launches);
            Assert.Equal(1, await scheduler.RunDueAsync());
            var pending = Assert.Single(await scheduler.ListAsync(state: "pending"));
            Assert.Equal(nowMs + 5_000, pending.RunAtMs);
            Assert.NotNull(await spaceItems.GetAsync(launch.ItemId));
            Assert.Empty(placement.PlacedWrecks);
            Assert.Equal(0, launchNotifications);

            await scheduler.DisposeAsync();
            nowMs += 5_000;
            await using var restartedScheduler = new DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            _ = CreateService(restartedScheduler);
            Assert.Equal(1, await restartedScheduler.RunDueAsync());
            Assert.Single(placement.PlacedWrecks);
            Assert.Equal(1, launchNotifications);
            Assert.Empty(await restartedScheduler.ListAsync());
            var placementNotice = Assert.IsType<Send>(await front.ReceiveAsync());
            var notification = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(placementNotice.Payload)));
            Assert.Equal(MachoMessage.Notification, notification.Type);
            Assert.Equal("OnPILaunchesChange", notification.Dest.BroadcastId);
            var savedSpaceItem = Assert.IsType<SpaceItem>(await spaceItems.GetAsync(launch.ItemId));
            Assert.Equal((launch.ExpiresTicks - 116_444_736_000_000_000L) / 10_000, savedSpaceItem.ExpiresAtMs);
        }
        finally
        {
            foreach (var schema in new[] { planetSchema, inventorySchema, skillsSchema, spaceSchema, schedulerSchema })
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    public class PlanetLaunchPlacementProxy : DispatchProxy
    {
        private int _failNextPlacement = 1;
        private int _failNextOfficePlacement = 1;
        public List<SimPlaceWreck> PlacedWrecks { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISimulation.PlaceWreck))
            {
                if (Interlocked.Exchange(ref _failNextPlacement, 0) == 1)
                    throw new IOException("injected space placement failure");
                PlacedWrecks.Add((SimPlaceWreck)args![0]!);
                return null;
            }
            if (targetMethod?.Name == nameof(ISimulation.PlaceStructure)
                && Interlocked.Exchange(ref _failNextOfficePlacement, 0) == 1)
                throw new IOException("injected customs office placement failure");
            if (targetMethod?.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (targetMethod?.ReturnType == typeof(ValueTask)) return ValueTask.CompletedTask;
            if (targetMethod?.ReturnType == typeof(void)) return null;
            return targetMethod?.ReturnType?.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }
}
