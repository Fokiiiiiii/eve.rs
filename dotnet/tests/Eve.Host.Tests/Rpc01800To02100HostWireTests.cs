using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private static PyValue ClientKwargs() => Py.Dict(("machoVersion", new PyInt(1)));

    private async Task<PyValue?> CallOverHost(TestFront front, SessionOpen open, uint callId, string service, string method, PyValue args)
    {
        await front.SendAsync(new Packet(open.SessionId,
            MarshalCodec.Encode(Packets.CallWithKwargs(open, callId, service, method, args, ClientKwargs()))));
        return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
    }

    private static PyValue RpcCoverageWireField(PyValue item, string name)
    {
        var objectValue = Assert.IsType<PyObject>(item);
        var fields = Assert.IsType<PyDict>(objectValue.Args);
        return fields.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;
    }

    [Fact]
    public async Task Lookup_owner_searches_use_the_registered_service_and_client_keyvals_over_host_wire()
    {
        var sources = new LookupSearchSources(
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([new(100, "Shared", CorporationId: 200, AllianceId: 300)]),
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([
                new(150, "Shared", TickerName: "NPC", IsNpc: true, WarPermit: 0),
                new(200, "Shared", TickerName: "PLY", WarPermit: 1),
            ]),
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([
                new(300, "Shared", TickerName: "SHR", CorporationId: 200),
            ]),
            _ => Task.FromResult<IReadOnlyList<LookupEntity>>([new(400, "Shared")]));

        Start(configure: registry => registry.Register(CoreServices.CreateLookupService(sources)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var owners = Assert.IsType<PyList>(await CallOverHost(front, open, 1, "lookupSvc", "LookupOwners",
            Py.Tuple(new PyStr("Shared"), new PyInt(2))));
        Assert.Equal([100, 150, 200, 300, 400], owners.Items.Select(item => ((PyInt)RpcCoverageWireField(item, "ownerID")).Value));
        Assert.All(owners.Items, item =>
        {
            Assert.NotEqual(PyNone.Instance, RpcCoverageWireField(item, "ownerName"));
            Assert.NotEqual(PyNone.Instance, RpcCoverageWireField(item, "typeID"));
            Assert.NotEqual(PyNone.Instance, RpcCoverageWireField(item, "groupID"));
        });

        var playerOwners = Assert.IsType<PyList>(await CallOverHost(front, open, 2, "lookupSvc", "LookupPCOwners",
            Py.Tuple(new PyStr("Shared"), new PyInt(2))));
        Assert.Equal([100, 200, 300], playerOwners.Items.Select(item => ((PyInt)RpcCoverageWireField(item, "ownerID")).Value));

        var warable = Assert.IsType<PyList>(await CallOverHost(front, open, 3, "lookupSvc", "LookupWarableCorporationsOrAlliances",
            Py.Tuple(new PyStr("s h a r e d"), PyValue.Of(true))));
        Assert.Equal(3, warable.Items.Count);
        Assert.All(warable.Items, item =>
        {
            Assert.NotEqual(PyNone.Instance, RpcCoverageWireField(item, "ownerID"));
            Assert.NotEqual(PyNone.Instance, RpcCoverageWireField(item, "ownerName"));
            Assert.NotEqual(PyNone.Instance, RpcCoverageWireField(item, "typeID"));
            Assert.NotEqual(PyNone.Instance, RpcCoverageWireField(item, "warPermit"));
        });
    }

    [Fact]
    public async Task MachoNet_config_and_cluster_statistics_keep_the_client_wire_contract()
    {
        Start();
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var config = Assert.IsType<PyDict>(await CallOverHost(front, open, 1, "machoNet", "GetGlobalConfig", Py.Tuple([])));
        Assert.Contains(config.Entries, entry => JsSemantics.StringValue(entry.Key) == "imageserverurl");
        Assert.Contains(config.Entries, entry => JsSemantics.StringValue(entry.Key) == "defaultPortraitSaveSize");

        var statistics = Assert.IsType<PyTuple>(await CallOverHost(front, open, 2, "machoNet",
            "GetClusterGameStatisticsForClient",
            Py.Tuple(new PyStr("EVE"), Py.Tuple(new PyDict([]), new PyDict([]), new PyInt(0)))));
        Assert.Equal(3, statistics.Items.Count);
        Assert.IsType<PyDict>(statistics.Items[0]);
        Assert.IsType<PyDict>(statistics.Items[1]);
        Assert.Equal(new PyInt(1), statistics.Items[2]);

        var fallback = Py.Tuple(new PyDict([]), new PyDict([]), new PyInt(0));
        Assert.Equal(fallback, await CallOverHost(front, open, 3, "machoNet",
            "GetClusterGameStatisticsForClient", Py.Tuple(new PyStr("OTHER"), fallback)));
    }

    [Fact]
    public async Task Moon_extraction_mutations_are_routed_and_the_following_read_sees_the_updated_state()
    {
        var state = new JsonObject
        {
            ["extractions"] = new JsonObject
            {
                ["1"] = new JsonObject
                {
                    ["extractionID"] = 1,
                    ["structureID"] = 30_000_142,
                    ["moonID"] = 40_000_001,
                    ["solarSystemID"] = 30_000_142,
                    ["ownerID"] = 98_000_001,
                    ["startMoveTimeMs"] = 1_700_000_000_000,
                    ["chunkAvailableTimeMs"] = 1_700_518_460_000,
                    ["naturalDecayTimeMs"] = 1_700_529_260_000,
                    ["durationSeconds"] = 518_460,
                    ["yieldMultiplier"] = 1.0,
                    ["calendarEventID"] = 42,
                    ["state"] = "active",
                },
            },
        };
        var mutations = new List<string>();
        var service = new MoonExtractionsRpcService(
            () => Task.FromResult<JsonObject?>(state),
            call =>
            {
                mutations.Add(call.Method);
                var extraction = state["extractions"]!["1"]!.AsObject();
                extraction["state"] = call.Method == "FractureChunkWithStructure" ? "fracturing" : "completed";
                if (call.Method == "CancelExtraction") extraction["completedReason"] = "cancelled";
                return ValueTask.FromResult<PyValue>(PyNone.Instance);
            });

        Start(configure: registry => registry.Register(service));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 1, "moonExtractions",
            "FractureChunkWithStructure", Py.Tuple([])));
        var fractured = Assert.IsType<PyTuple>(await CallOverHost(front, open, 2, "moonExtractions",
            "GetExtractionAndEventIDFromStructureID", Py.Tuple(new PyInt(30_000_142))));
        Assert.Equal(new PyInt(42), fractured.Items[1]);

        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 3, "moonExtractions",
            "CancelExtraction", Py.Tuple([])));
        var cancelled = Assert.IsType<PyTuple>(await CallOverHost(front, open, 4, "moonExtractions",
            "GetExtractionAndEventIDFromStructureID", Py.Tuple(new PyInt(30_000_142))));
        Assert.Equal(PyNone.Instance, cancelled.Items[0]);
        Assert.Equal(PyNone.Instance, cancelled.Items[1]);
        Assert.Equal(["FractureChunkWithStructure", "CancelExtraction"], mutations);
    }
}
