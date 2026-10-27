using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Services.Space;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private sealed class WireContainers : ISpaceContainers
    {
        public List<long> LaunchedItems { get; } = [];
        public List<long> JettisonRequests { get; } = [];
        public IReadOnlyList<long> JettisonedItems { get; set; } = [];
        public bool IsInSpace(ClientSession session) => true;
        public int? SolarSystemOf(ClientSession session) => 31_000_005;
        public Task<Item?> OpenAsync(ClientSession session, long containerId, CancellationToken cancellationToken) => Task.FromResult<Item?>(null);
        public Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity, CancellationToken cancellationToken) => Task.FromResult<long?>(null);
        public Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId, CancellationToken cancellationToken) => Task.FromResult<bool?>(null);
        public Task<IReadOnlyList<long>> JettisonAsync(ClientSession session, IReadOnlyList<long> itemIds, CancellationToken cancellationToken)
        {
            JettisonRequests.AddRange(itemIds);
            return Task.FromResult(JettisonedItems);
        }
        public Task<long?> LaunchFromShipAsync(ClientSession session, long itemId, CancellationToken cancellationToken)
        {
            LaunchedItems.Add(itemId);
            return Task.FromResult<long?>(9000 + LaunchedItems.Count);
        }
    }

    private sealed class WireDrones : ISpaceDrones
    {
        public List<DroneRequest> Requests { get; private set; } = [];
        public Task<PyValue> LaunchAsync(ClientSession session, IReadOnlyList<DroneRequest> requests, CancellationToken cancellationToken)
        {
            Requests = [.. requests];
            return Task.FromResult<PyValue>(new PyDict(requests.Select(request =>
                new KeyValuePair<PyValue, PyValue>(new PyLong(request.ItemId), new PyList([new PyLong(9501)]))).ToArray()));
        }
        public Task<PyValue> CommandAsync(ClientSession session, byte command, long targetId, IReadOnlyList<long> droneIds, CancellationToken cancellationToken) => Task.FromResult<PyValue>(new PyDict([]));
        public Task<PyValue> ReconnectAsync(ClientSession session, CancellationToken cancellationToken) => Task.FromResult<PyValue>(new PyDict([]));
        public Task<PyValue> ScoopAsync(ClientSession session, IReadOnlyList<long> droneIds, CancellationToken cancellationToken) => Task.FromResult<PyValue>(new PyDict([]));
    }

    [Fact]
    public async Task Ship_drop_and_drone_launch_keep_client_args_and_replies_through_host_wire()
    {
        var containers = new WireContainers();
        var drones = new WireDrones();
        Start(
            configure: registry => registry.Register(new ShipService(null!, () => FixedNowMs, containers, drones)),
            sessionOpened: session => session.Set("stationid", new PyInt(600)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        PyValue BoundArgs(string method, PyValue args) => Py.Tuple(
            Py.Tuple(new PyInt(600), new PyInt(15)),
            Py.Tuple(new PyStr(method), args, kwargs));
        async Task<PyValue> BoundCall(uint callId, string method, PyValue args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "ship", "MachoBindObject", BoundArgs(method, args), kwargs))));
            var result = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
                Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
            return result.Items[1];
        }

        var dropped = Assert.IsType<PyDict>(await BoundCall(1, "Drop", Py.Tuple(
            new PyList([Py.Tuple(new PyLong(5001), new PyInt(2))]),
            new PyLong(7001),
            PyBool.False)));
        Assert.Equal(new PyList([new PyLong(9001), new PyLong(9002)]), dropped.Entries.Single().Value);
        Assert.Equal([5001L, 5001L], containers.LaunchedItems);

        var launched = Assert.IsType<PyDict>(await BoundCall(2, "LaunchDrones", Py.Tuple(
            new PyList([Py.Tuple(new PyLong(6001), new PyInt(2)), Py.Tuple(new PyLong(6002), new PyInt(1))]),
            new PyLong(7001),
            PyBool.False)));
        Assert.Equal(new PyList([new PyLong(9501)]), launched.Entries[0].Value);
        Assert.Equal([new DroneRequest(6001, 2), new DroneRequest(6002, 1)], drones.Requests);
    }

    [Fact]
    public async Task Ship_jettison_returns_the_two_client_tuples_through_host_wire()
    {
        var containers = new WireContainers { JettisonedItems = [5001] };
        Start(
            configure: registry => registry.Register(new ShipService(null!, () => FixedNowMs, containers)),
            sessionOpened: session => session.Set("stationid", new PyInt(600)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var boundArgs = Py.Tuple(
            Py.Tuple(new PyInt(600), new PyInt(15)),
            Py.Tuple(new PyStr("Jettison"), Py.Tuple(new PyList([new PyInt(5001), new PyLong(5002)])), kwargs));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "ship", "MachoBindObject", boundArgs, kwargs))));
        var reply = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        var jettisoned = Assert.IsType<PyTuple>(reply.Items[1]);

        Assert.Equal([5001L, 5002L], containers.JettisonRequests);
        Assert.Equal(new PyInt(5001), Assert.Single(Assert.IsType<PyTuple>(jettisoned.Items[0]).Items));
        Assert.Empty(Assert.IsType<PyTuple>(jettisoned.Items[1]).Items);
    }
}
