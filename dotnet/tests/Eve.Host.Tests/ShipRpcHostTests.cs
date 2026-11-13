using System.Reflection;
using Eve.Macho;
using Eve.Marshal;
using Eve.Inventory;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Services.Space;
using Eve.Space;
using Eve.Universe;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Ship_launch_from_container_forwards_client_ids_and_returns_none_over_the_bound_host_route()
    {
        const int solarSystemId = 30_000_142;
        const long sourceId = 900_001;
        const long itemId = 900_002;
        using var timers = new Eve.Scheduler.LocalTimers();
        using var space = new SpaceSessions(DispatchProxy.Create<ISimulation, InertWormholeSimulation>(), null!, null!,
            new StationCatalog([]), new SolarSystemCatalog([]), () => FixedNowMs, localTimers: timers);
        var containers = new LaunchRecordingContainers();
        Start(
            configure: registry => registry.Register(new ShipService(space, () => FixedNowMs, containers)),
            sessionOpened: session =>
            {
                session.Set("solarsystemid", new PyInt(solarSystemId));
                session.Set("shipid", new PyLong(900_003));
            });
        var (front, open) = await OpenFrontAsync();

        var response = Assert.IsType<PyTuple>(await HostRpcAsync(front, open, 1, "ship", "MachoBindObject",
            Py.Tuple(new PyInt(solarSystemId), new PyInt(15)),
            Py.Tuple(new PyStr("LaunchFromContainer"),
                Py.Tuple(new PyLong(sourceId), new PyLong(itemId)), PyNone.Instance)));

        Assert.IsType<PySubStruct>(response.Items[0]);
        Assert.Same(PyNone.Instance, response.Items[1]);
        Assert.Equal([(sourceId, itemId)], containers.Launches);
    }

    private sealed class LaunchRecordingContainers : ISpaceContainers
    {
        public List<(long SourceId, long ItemId)> Launches { get; } = [];

        public bool IsInSpace(ClientSession session) => true;
        public int? SolarSystemOf(ClientSession session) => 30_000_142;
        public Task<Item?> OpenAsync(ClientSession session, long containerId, CancellationToken cancellationToken) =>
            Task.FromResult<Item?>(null);
        public Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity,
            CancellationToken cancellationToken) => Task.FromResult<long?>(null);
        public Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId,
            CancellationToken cancellationToken) => Task.FromResult<bool?>(null);
        public Task<IReadOnlyList<long>> JettisonAsync(ClientSession session, IReadOnlyList<long> itemIds,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<long>>([]);
        public Task LaunchFromContainerAsync(ClientSession session, long sourceId, long itemId, CancellationToken cancellationToken)
        {
            Launches.Add((sourceId, itemId));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Ship_fitting_info_and_dirt_timestamp_use_the_registered_host_routes()
    {
        var service = new ShipService(null!, () => FixedNowMs,
            getShipFittingInfo: (fittingId, _) => ValueTask.FromResult<ShipFittingInfo?>(
                fittingId == 7001 ? new ShipFittingInfo("Rifter fit", "res:/fit.png", 587, PyNone.Instance) : null));
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("stationid", new PyInt(600)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "ship", "GetShipFittingInfo", Py.Tuple(new PyInt(7001)), kwargs))));
        var fitting = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Equal("Rifter fit", JsSemantics.StringValue(fitting.Items[0]));
        Assert.Equal("res:/fit.png", JsSemantics.StringValue(fitting.Items[1]));
        Assert.Equal(new PyInt(587), fitting.Items[2]);
        Assert.Same(PyNone.Instance, fitting.Items[3]);

        var bindArgs = Py.Tuple(
            Py.Tuple(new PyInt(600), new PyInt(15)),
            Py.Tuple(new PyStr("GetDirtTimestamp"), Py.Tuple(new PyInt(9001)), kwargs));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 2, "ship", "MachoBindObject", bindArgs, kwargs))));
        var bound = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(FixedNowMs)), bound.Items[1]);
    }
}
