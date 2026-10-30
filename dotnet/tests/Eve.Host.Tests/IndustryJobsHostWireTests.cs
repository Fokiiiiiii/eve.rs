using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public sealed class IndustryJobsHostWireTests
{
    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class JobOperations(JsonObject table) : IIndustryJobOperations
    {
        public IndustryJobInstallRequest? LastInstallRequest { get; private set; }

        public ValueTask<long> InstallJobAsync(IndustryJobInstallRequest request, CancellationToken cancellationToken)
        {
            LastInstallRequest = request;
            table["jobs"]!["42004"] = Job(42004);
            return ValueTask.FromResult(42004L);
        }

        public ValueTask CompleteJobAsync(IndustryJobAction request, CancellationToken cancellationToken)
        {
            SetStatus(request.JobId, 100);
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteManyJobsAsync(IndustryJobBatch request, CancellationToken cancellationToken)
        {
            foreach (var job in request.Jobs) SetStatus(job.JobId, 100);
            return ValueTask.CompletedTask;
        }

        public ValueTask CancelJobAsync(IndustryJobAction request, CancellationToken cancellationToken)
        {
            SetStatus(request.JobId, 101);
            return ValueTask.CompletedTask;
        }

        private void SetStatus(long jobId, int status) =>
            table["jobs"]![jobId.ToString(System.Globalization.CultureInfo.InvariantCulture)]!["status"] = status;
    }

    private const long Now = 133000000000000000;
    private const long Start = 134000000000000001;
    private const long End = 134000000000000999;

    private static JsonObject Job(int id, int status = 1) => new()
    {
        ["activityID"] = 1, ["jobID"] = id, ["blueprintID"] = 10,
        ["blueprintTypeID"] = 681, ["blueprintCopy"] = false,
        ["blueprintLocationID"] = 20, ["blueprintLocationFlagID"] = 4,
        ["blueprintInventoryFlagID"] = 4, ["facilityID"] = 20,
        ["ownerID"] = 1001, ["status"] = status, ["installerID"] = 1001,
        ["completedCharacterID"] = 0, ["solarSystemID"] = 30, ["stationID"] = 20,
        ["startDate"] = Start, ["endDate"] = End, ["pauseDate"] = null,
        ["runs"] = 1, ["licensedRuns"] = 0, ["successfulRuns"] = 0,
        ["cost"] = 123456, ["timeInSeconds"] = 60, ["probability"] = 1,
        ["productTypeID"] = 34, ["optionalTypeID"] = 0, ["optionalTypeID2"] = 0,
        ["outputLocationID"] = 20, ["outputFlagID"] = 4,
    };

    private static JsonObject Table() => new()
    {
        ["jobs"] = new JsonObject
        {
            ["42001"] = Job(42001),
            ["42002"] = Job(42002),
            ["42003"] = Job(42003),
        },
    };

    private static PyValue ClientCallPacket(int callId, string method, PyValue[] args)
    {
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(1));
        var destination = new MachoAddress(AddressKind.Node, Service: "industryManager",
            CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        var body = new PyTuple([PyNone.Instance, new PyStr(method), new PyTuple(args), kwargs]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([
                new PyInt(MachoMessage.CallReq), source.Encode(), destination.Encode(),
                new PyInt(7), payload, new PyDict([]),
                PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
                PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
            ]));
    }

    private static PyValue Result(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<PyInt>(packet[0]).Value);
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(packet[4]).Items[0]).Decoded!;
    }

    private static PyValue Field(PyValue row, string name)
    {
        var fields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args);
        return fields.Entries.Single(entry => Assert.IsType<PyStr>(entry.Key).Value == name).Value;
    }

    [Fact]
    public async Task Registered_industry_manager_routes_keep_client_shapes_and_accept_transport_kwargs()
    {
        var table = Table();
        var operations = new JobOperations(table);
        var service = new IndustryJobsService(
            _ => ValueTask.FromResult<JsonObject?>(table),
            operations,
            () => Now);
        var registry = new ServiceRegistry();
        registry.Register(service);
        Assert.Same(service, registry.Lookup("industryManager"));
        Assert.False(service.HasMethod("get"));

        var capture = new Capture();
        var session = new ClientSession(capture)
        {
            SessionId = 42,
            UserId = 7,
            CharacterId = 1001,
        };
        session.Set("charid", new PyLong(1001));
        var dispatcher = new PacketDispatcher(registry, () => Now);
        var callId = 1;

        async Task<PyValue> Call(string method, params PyValue[] args)
        {
            capture.Packets.Clear();
            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket(callId++, method, args), session));
            return Result(Assert.Single(capture.Packets));
        }

        var job = await Call("GetJob", new PyInt(42001));
        Assert.Equal(new PyInt(42001), Field(job, "jobID"));
        Assert.Equal(new PyInt(1), Field(job, "status"));

        var ownerJobs = Assert.IsType<PyList>(await Call("GetJobsByOwner", new PyInt(1001), PyBool.False));
        Assert.Equal([42001L, 42002L, 42003L], ownerJobs.Items.Select(row => Assert.IsType<PyInt>(Field(row, "jobID")).Value));

        var counts = Assert.IsType<PyDict>(await Call("GetJobCounts", new PyInt(1001)));
        Assert.Equal([1, 3, 4, 5, 8, 9], counts.Entries.Select(entry => Assert.IsType<PyInt>(entry.Key).Value));

        var install = Py.KeyVal(
            ("activityID", new PyInt(1)), ("blueprintID", new PyInt(11)),
            ("blueprintTypeID", new PyInt(681)), ("facilityID", new PyInt(20)),
            ("solarSystemID", new PyInt(30)), ("runs", new PyInt(1)));
        Assert.Equal(new PyInt(42004), await Call("InstallJob", install));

        var inputLocation = new PyObjectEx(ObjectExKind.Ex1, new PyStr("industry.job.Location"), [],
        [
            new KeyValuePair<PyValue, PyValue>(new PyStr("itemID"), new PyLong(88)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("flagID"), new PyInt(116)),
        ]);
        var outputLocation = new PyObject(new PyStr("industry.job.Location"), new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("itemID"), new PyLong(89)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("flagID"), new PyInt(117)),
        ]));
        Assert.Equal(new PyInt(42004), await Call("InstallJob", Py.KeyVal(
            ("activityID", new PyInt(1)), ("blueprintID", new PyInt(11)),
            ("blueprintTypeID", new PyInt(681)), ("facilityID", new PyInt(20)),
            ("solarSystemID", new PyInt(30)), ("runs", new PyInt(1)),
            ("inputLocation", inputLocation), ("outputLocation", outputLocation))));
        Assert.Equal(new IndustryJobLocation(88, 116), operations.LastInstallRequest!.Job.InputLocation);
        Assert.Equal(new IndustryJobLocation(89, 117), operations.LastInstallRequest.Job.OutputLocation);

        var dictionaryLocation = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("itemID"), new PyLong(98)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("flagID"), new PyInt(118)),
        ]);
        Assert.Equal(new PyInt(42004), await Call("InstallJob", Py.KeyVal(
            ("activityID", new PyInt(1)), ("blueprintID", new PyInt(11)),
            ("blueprintTypeID", new PyInt(681)), ("facilityID", new PyInt(20)),
            ("solarSystemID", new PyInt(30)), ("runs", new PyInt(1)),
            ("inputLocation", dictionaryLocation), ("outputLocation", dictionaryLocation))));
        Assert.Equal(new IndustryJobLocation(98, 118), operations.LastInstallRequest!.Job.InputLocation);
        Assert.Equal(new IndustryJobLocation(98, 118), operations.LastInstallRequest.Job.OutputLocation);

        Assert.IsType<PyNone>(await Call("CompleteJob", new PyInt(42004), new PyInt(30)));
        Assert.Equal(new PyInt(100), Field(await Call("GetJob", new PyInt(42004)), "status"));

        Assert.IsType<PyNone>(await Call("CompleteManyJobs", new PyList([
            new PyTuple([new PyInt(42001), new PyInt(30)]),
        ])));
        Assert.Equal(new PyInt(100), Field(await Call("GetJob", new PyInt(42001)), "status"));

        Assert.IsType<PyNone>(await Call("CancelJob", new PyInt(42002), new PyInt(30)));
        Assert.Equal(new PyInt(101), Field(await Call("GetJob", new PyInt(42002)), "status"));

    }
}
