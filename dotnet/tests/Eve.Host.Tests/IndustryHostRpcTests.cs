using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public sealed class IndustryHostRpcTests
{
    [Fact]
    public async Task Industry_manager_routes_client_calls_and_returns_the_consumed_shapes()
    {
        var operations = new RecordingOperations();
        var jobs = new JsonObject
        {
            ["jobs"] = new JsonObject
            {
                ["42001"] = new JsonObject
                {
                    ["activityID"] = 1, ["jobID"] = 42001, ["blueprintID"] = 52001,
                    ["blueprintTypeID"] = 600, ["blueprintCopy"] = true,
                    ["blueprintLocationID"] = 60001, ["blueprintLocationFlagID"] = 4,
                    ["facilityID"] = 60001, ["ownerID"] = 90000002, ["installerID"] = 90000002,
                    ["status"] = 1, ["solarSystemID"] = 30000142, ["stationID"] = 60001,
                    ["startDate"] = 133000000000000000L, ["endDate"] = 134000000000000000L,
                    ["runs"] = 1, ["licensedRuns"] = 0, ["successfulRuns"] = 0,
                    ["timeInSeconds"] = 60, ["totalCost"] = 0, ["productTypeID"] = 601,
                    ["outputLocationID"] = 60001, ["outputFlagID"] = 4
                }
            }
        };
        var registry = new ServiceRegistry();
        registry.Register(new IndustryJobsService(
            _ => ValueTask.FromResult<JsonObject?>((JsonObject)jobs.DeepClone()),
            operations,
            currentFileTime: () => 133000000000000000L));
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 7, UserId = 90000002 };
        session.Set("charid", new PyLong(90000002));
        session.Set("corpid", new PyLong(98000001));
        var dispatcher = new PacketDispatcher(registry);

        var job = Assert.IsType<PyObject>(await Dispatch(dispatcher, capture, session, "GetJob", new PyLong(42001)));
        Assert.Equal(new PyInt(42001), Field(job, "jobID"));

        var ownerJobs = Assert.IsType<PyList>(await Dispatch(
            dispatcher, capture, session, "GetJobsByOwner", new PyLong(90000002), PyValue.Of(false)));
        Assert.Single(ownerJobs.Items);
        Assert.Equal(new PyInt(42001), Field(Assert.IsType<PyObject>(ownerJobs.Items[0]), "jobID"));

        var counts = Assert.IsType<PyDict>(await Dispatch(
            dispatcher, capture, session, "GetJobCounts", new PyLong(90000002)));
        Assert.Equal(new PyInt(1), counts.Entries.Single(pair => pair.Key == new PyInt(1)).Value);

        Assert.IsType<PyInt>(await Dispatch(dispatcher, capture, session, "InstallJob", InstallPayload()));
        Assert.Equal(90000002, operations.Installed?.Context.Actor.CharacterId);
        Assert.Equal(1, operations.Installed?.Job.ActivityId);

        Assert.IsType<PyNone>(await Dispatch(dispatcher, capture, session, "CompleteJob",
            new PyLong(42001), new PyLong(30000142)));
        Assert.Equal(42001, operations.Completed?.JobId);

        var batch = new PyList([
            new PyTuple([new PyLong(42001), new PyLong(30000142)]),
            new PyTuple([new PyLong(42002), new PyLong(30000142)])
        ]);
        Assert.IsType<PyNone>(await Dispatch(dispatcher, capture, session, "CompleteManyJobs", batch));
        Assert.Equal([42001L, 42002L], operations.CompletedMany?.Jobs.Select(action => action.JobId));

        Assert.IsType<PyNone>(await Dispatch(dispatcher, capture, session, "CancelJob",
            new PyLong(42001), new PyLong(30000142)));
        Assert.Equal(42001, operations.Cancelled?.JobId);
    }

    [Fact]
    public async Task Industry_monitor_is_registered_for_connect_disconnect_and_material_updates()
    {
        var materials = new Queue<IReadOnlyDictionary<int, long>>([
            new Dictionary<int, long> { [34] = 5 },
            new Dictionary<int, long> { [34] = 4, [35] = 2 }
        ]);
        var monitor = new IndustryMonitorRpcService(
            availableMaterials: (_, _) => ValueTask.FromResult(materials.Dequeue()));
        var registry = new ServiceRegistry();
        registry.Register(monitor);
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 9, UserId = 90000002, CharacterId = 90000002 };
        var dispatcher = new PacketDispatcher(registry);

        var connected = Assert.IsType<PyTuple>(await Dispatch(
            dispatcher, capture, session, "ConnectJob", "industryMonitor",
            Py.KeyVal(
                ("blueprintID", new PyLong(52001)),
                ("blueprintTypeID", new PyInt(600)),
                ("activityID", new PyInt(1)),
                ("facilityID", new PyLong(60001)),
                ("inputLocation", Py.KeyVal(
                    ("itemID", new PyLong(60001)), ("ownerID", new PyLong(90000002)),
                    ("flagID", new PyInt(4)), ("canTake", PyValue.Of(true)))))));
        var monitorId = connected.Items[0];
        Assert.True(monitorId is PyInt or PyLong);
        Assert.Contains(Assert.IsType<PyDict>(connected.Items[1]).Entries,
            pair => pair.Key == new PyInt(34) && pair.Value == new PyInt(5));

        await monitor.InventoryChangedAsync(session, CancellationToken.None);
        Assert.Equal(2, capture.Packets.Count);
        Assert.IsType<PyObject>(capture.Packets[^1]);

        Assert.IsType<PyNone>(await Dispatch(
            dispatcher, capture, session, "DisconnectJob", "industryMonitor", monitorId));
        capture.Packets.Clear();
        await monitor.InventoryChangedAsync(session, CancellationToken.None);
        Assert.Empty(capture.Packets);
    }

    private static async Task<PyValue> Dispatch(
        PacketDispatcher dispatcher, Capture capture, ClientSession session, string method, params PyValue[] args) =>
        await Dispatch(dispatcher, capture, session, method, "industryManager", args);

    private static async Task<PyValue> Dispatch(
        PacketDispatcher dispatcher, Capture capture, ClientSession session, string method, string service, params PyValue[] args)
    {
        capture.Packets.Clear();
        Assert.True(await dispatcher.DispatchAsync(Request(service, method, args), session));
        var response = Assert.IsType<PyObject>(Assert.Single(capture.Packets));
        var packetArgs = Assert.IsType<PyTuple>(response.Args);
        var payload = Assert.IsType<PyTuple>(packetArgs.Items[4]);
        return payload.Items[0] is PySubStream { Decoded: { } decoded } ? decoded : payload.Items[0];
    }

    private static PyObject Request(string service, string method, PyValue[] args)
    {
        var source = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(7), new PyInt(9), PyNone.Instance]);
        var body = new PyTuple([new PyInt(1), new PyStr(method), new PyTuple(args), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        var head = new PyValue[]
        {
            new PyInt(6), source, MachoAddress.Any(service).Encode(), new PyInt(90000002), payload, PyNone.Instance,
        };
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - head.Length)]));
    }

    private static PyValue InstallPayload() => Py.KeyVal(
        ("activityID", new PyInt(1)), ("blueprintID", new PyLong(52001)),
        ("blueprintTypeID", new PyInt(600)), ("blueprintCopy", PyValue.Of(true)),
        ("facilityID", new PyLong(60001)), ("solarSystemID", new PyLong(30000142)),
        ("runs", new PyInt(1)));

    private static PyValue Field(PyValue value, string name)
    {
        var dict = Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args);
        return dict.Entries.Single(pair => JsSemantics.StringValue(pair.Key) == name).Value;
    }

    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class RecordingOperations : IIndustryJobOperations
    {
        public IndustryJobInstallRequest? Installed { get; private set; }
        public IndustryJobAction? Completed { get; private set; }
        public IndustryJobBatch? CompletedMany { get; private set; }
        public IndustryJobAction? Cancelled { get; private set; }

        public ValueTask<long> InstallJobAsync(IndustryJobInstallRequest request, CancellationToken cancellationToken)
        {
            Installed = request;
            return ValueTask.FromResult(42002L);
        }

        public ValueTask CompleteJobAsync(IndustryJobAction request, CancellationToken cancellationToken)
        {
            Completed = request;
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteManyJobsAsync(IndustryJobBatch request, CancellationToken cancellationToken)
        {
            CompletedMany = request;
            return ValueTask.CompletedTask;
        }

        public ValueTask CancelJobAsync(IndustryJobAction request, CancellationToken cancellationToken)
        {
            Cancelled = request;
            return ValueTask.CompletedTask;
        }
    }
}
