using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Services.Ships;

namespace Eve.Services.Space.Tests;

public sealed class ShipDropContractTests
{
    [Fact]
    public async Task Drop_launches_each_requested_quantity_and_groups_results_by_source_item()
    {
        var containers = new LaunchRecorder((itemId, attempt) => itemId * 100 + attempt);
        var service = new ShipService(null!, () => 0, containers);
        var result = await DropAsync(
            service,
            [
                new PyTuple([new PyLong(71), new PyInt(2)]),
                new PyTuple([new PyLong(72), new PyInt(1)]),
            ]);

        Assert.Equal(new long[] { 71, 71, 72 }, containers.Calls);
        Assert.Equal(new long[] { 7101, 7102 }, Ids(Value(result, 71)));
        Assert.Equal(new long[] { 7201 }, Ids(Value(result, 72)));
    }

    [Fact]
    public async Task Drop_keeps_prior_launches_and_returns_custom_notify_for_a_partial_refusal()
    {
        var containers = new LaunchRecorder((itemId, attempt) =>
            itemId == 81 && attempt == 2
                ? throw MachoErrors.ThrowUserError("DROP_DENIED")
                : itemId * 100 + attempt);
        var service = new ShipService(null!, () => 0, containers);
        var result = await DropAsync(
            service,
            [
                new PyTuple([new PyLong(81), new PyInt(3)]),
                new PyTuple([new PyLong(82), new PyInt(1)]),
            ]);

        Assert.Equal(new long[] { 81, 81, 82 }, containers.Calls);
        var refused = Assert.IsType<PyList>(Value(result, 81));
        Assert.Equal(8101L, Assert.IsType<PyLong>(refused.Items[0]).Value);
        var error = Assert.IsType<PyTuple>(refused.Items[1]);
        Assert.Equal(new PyStr("CustomNotify"), error.Items[0]);
        var payload = Assert.IsType<PyDict>(error.Items[1]);
        Assert.Equal(new PyStr("DROP_DENIED"), Assert.Single(payload.Entries).Value);
        Assert.Equal(new long[] { 8201 }, Ids(Value(result, 82)));
    }

    private static async Task<PyDict> DropAsync(ShipService service, IReadOnlyList<PyValue> requests)
    {
        const long stationId = 60_000_001;
        var session = new ClientSession();
        session.Set("stationid", new PyLong(stationId));
        var nestedCall = new PyTuple([
            new PyStr("Drop"),
            new PyTuple([new PyList(requests), new PyLong(0), PyBool.False]),
            PyNone.Instance,
        ]);
        var reply = Assert.IsType<PyTuple>(await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "MachoBindObject",
            Args = [new PyTuple([new PyLong(stationId), new PyInt(5)]), nestedCall],
            Session = session,
            CallId = new PyInt(1),
        }));
        return Assert.IsType<PyDict>(reply.Items[1]);
    }

    private static PyValue Value(PyDict values, long itemId) =>
        Assert.Single(values.Entries, entry => entry.Key is PyLong id && id.Value == itemId).Value;

    private static long[] Ids(PyValue value) =>
        Assert.IsType<PyList>(value).Items.Cast<PyLong>().Select(id => id.Value).ToArray();

    private sealed class LaunchRecorder(Func<long, int, long?> launch) : ISpaceContainers
    {
        public List<long> Calls { get; } = [];

        public bool IsInSpace(ClientSession session) => true;

        public int? SolarSystemOf(ClientSession session) => null;

        public Task<Item?> OpenAsync(ClientSession session, long containerId, CancellationToken cancellationToken) =>
            Task.FromResult<Item?>(null);

        public Task<long?> MoveAsync(
            ClientSession session, long itemId, long sourceId, long destinationId, long? quantity, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(null);

        public Task<bool?> MoveAllAsync(
            ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId, CancellationToken cancellationToken) =>
            Task.FromResult<bool?>(null);

        public Task<IReadOnlyList<long>> JettisonAsync(
            ClientSession session, IReadOnlyList<long> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<long>>([]);

        public Task<long?> LaunchFromShipAsync(ClientSession session, long itemId, CancellationToken cancellationToken)
        {
            Calls.Add(itemId);
            var attempt = Calls.Count(id => id == itemId);
            return Task.FromResult(launch(itemId, attempt));
        }
    }
}
