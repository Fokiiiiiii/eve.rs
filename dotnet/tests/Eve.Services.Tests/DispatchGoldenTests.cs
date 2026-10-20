using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Tests;

/// <summary>
/// Replays oracle/fixtures/services/dispatch.json: request packets recorded from the reference
/// server go into the C# dispatcher and the response packets must match byte for byte, and leave the
/// same bound-object state behind.
/// </summary>
[Collection(TraceSettings.Name)]
public class DispatchGoldenTests
{
    private const long FixedNowMs = 1_700_000_000_123;

    private static readonly JsonArray Scenarios = (JsonArray)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "services", "dispatch.json")))!;

    public static IEnumerable<object[]> Ids() => Scenarios.Select(s => new object[] { (string)s!["id"]! });

    private const int ErrorResponseCommand = 15;

    /// <summary>
    /// The recorded steps (scenario id, note) whose answer is the reference's None: a call to an unknown service or
    /// method, or a handler that throws or rejects. The V24.01 server answers them with a wrapped error, so the recording
    /// is not replayed for them (DEVIATIONS.md, "Unknown service and method" and "A failing handler"); each one is still
    /// checked for its state.
    /// </summary>
    private static readonly HashSet<(string Id, string Note)> ErrorAnswers =
    [
        ("errors:plain", "throws"),
        ("results:async", "rejects"),
        ("errors:plain", "unknown method"),
        ("errors:plain", "unknown service"),
        ("errors:plain", "service name is not a string"),
        ("packets:arguments", "a table-string method name"),
        ("packets:malformed", "not a call body"),
    ];

    private static int CommandOf(string hex) =>
        (int)((PyInt)((PyTuple)((PyObject)MarshalCodec.Decode(Convert.FromHexString(hex))).Args).Items[0]).Value;

    private sealed class Capture : IPacketSink
    {
        public List<string> Sent { get; } = [];

        public void Send(PyValue packet) => Sent.Add(Convert.ToHexStringLower(MarshalCodec.Encode(packet)));
    }

    [Fact]
    public void The_recording_covers_the_dispatch_paths()
    {
        Assert.True(Scenarios.Count >= 20);
        Assert.True(Scenarios.Sum(s => ((JsonArray)s!["steps"]!).Count) >= 70);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task Every_recorded_step_gets_the_recorded_response_and_leaves_the_recorded_state(string id)
    {
        var scenario = (JsonObject)Scenarios.Single(s => (string)s!["id"]! == id)!;
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 77, UserId = 90000001 };
        var registry = new ServiceRegistry();
        Eve.Services.Generated.Eve_Services_TestsServiceCatalog.Register(registry, new EmptyProvider());
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs);
        var problems = new List<string>();
        dispatcher.OnProblem = (message, _) => problems.Add(message);

        foreach (var step in (JsonArray)scenario["steps"]!)
        {
            var note = (string)step!["note"]!;
            capture.Sent.Clear();
            var packet = MarshalCodec.Decode(Convert.FromHexString((string)step["packet"]!));
            await dispatcher.DispatchAsync(packet, session);

            var expected = ((JsonArray)step["responses"]!).Select(r => (string)r!).ToArray();
            if (ErrorAnswers.Contains((id, note)))
            {
                // The recording has the reference's None for a call it does not have; the V24.01 server answers the
                // call with a wrapped error (DEVIATIONS.md, "Unknown service and method"). Only that answer changes.
                Assert.Single(capture.Sent);
                Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[0]));
            }
            else
            {
                Assert.True(
                    expected.SequenceEqual(capture.Sent),
                    $"{id} / {note}: responses differ\n  expected {string.Join(" | ", expected.Select(Clip))}\n  actual   {string.Join(" | ", capture.Sent.Select(Clip))}");
            }

            var state = (JsonObject)step["state"]!;
            var bound = ((JsonObject)state["boundObjects"]!).ToDictionary(
                pair => pair.Key,
                pair => ((string)pair.Value!["service"]!, (string?)pair.Value["refID"]));
            var actual = registry.BoundObjects.ToDictionary(
                pair => pair.Key,
                pair => (pair.Value.Service, pair.Value.RefId?.ToString()));
            Assert.Equal(bound.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
            Assert.Equal((string?)state["currentBoundObjectID"], session.CurrentBoundObjectId);
            Assert.Equal((string?)state["lastBoundObjectID"], session.LastBoundObjectId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unhandled_rpc_is_logged_when_full_rpc_tracing_is_enabled(bool registeredService)
    {
        var priorFlag = Environment.GetEnvironmentVariable("EVE_TRACE_RPC");
        var priorError = Console.Error;
        var capture = new Capture();
        using var log = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("EVE_TRACE_RPC", "1");
            Console.SetError(log);
            var registry = new ServiceRegistry();
            if (registeredService)
                registry.Register(new NoMethodsService());
            var session = new ClientSession(capture) { SessionId = 88, UserId = 90000002 };

            Assert.True(await new PacketDispatcher(registry).DispatchAsync(
                CallRequestPacket(registeredService ? "knownSvc" : "missingSvc", "missingMethod"), session));

            Assert.Single(capture.Sent);
            Assert.Contains("[rpc-trace] request", log.ToString());
            Assert.Contains(registeredService ? "reason=method-not-registered" : "reason=service-not-registered", log.ToString());
            Assert.Contains("missingMethod", log.ToString());
            Assert.Contains(registeredService ? "knownSvc" : "missingSvc", log.ToString());
        }
        finally
        {
            Console.SetError(priorError);
            Environment.SetEnvironmentVariable("EVE_TRACE_RPC", priorFlag);
        }
    }

    [Fact]
    public async Task Rpc_trace_logs_successful_service_responses()
    {
        var priorFlag = Environment.GetEnvironmentVariable("EVE_TRACE_RPC");
        var priorError = Console.Error;
        var capture = new Capture();
        using var log = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("EVE_TRACE_RPC", "1");
            Console.SetError(log);
            var registry = new ServiceRegistry();
            registry.Register(new KnownService());
            var session = new ClientSession(capture) { SessionId = 89, UserId = 90000003 };

            await new PacketDispatcher(registry).DispatchAsync(CallRequestPacket("knownSvc", "Known"), session);

            Assert.Contains("[rpc-trace] request", log.ToString());
            Assert.Contains("[rpc-trace] response", log.ToString());
            Assert.Contains("result=PyInt", log.ToString());
        }
        finally
        {
            Console.SetError(priorError);
            Environment.SetEnvironmentVariable("EVE_TRACE_RPC", priorFlag);
        }
    }

    internal static PyValue CallRequestPacket(string service, string method)
    {
        var source = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(7), new PyInt(9), PyNone.Instance]);
        var body = new PyTuple([new PyInt(1), new PyStr(method), new PyTuple([]), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        var head = new PyValue[]
        {
            new PyInt(6), source, MachoAddress.Any(service).Encode(), new PyInt(90000002), payload, PyNone.Instance,
        };
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - head.Length)]));
    }

    private static PyValue BoundCallRequestPacket(string objectId, string method)
    {
        var source = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(7), new PyInt(9), PyNone.Instance]);
        var body = new PyTuple([new PyStr(objectId), new PyStr(method), new PyTuple([]), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        var head = new PyValue[]
        {
            new PyInt(6), source, MachoAddress.Any("").Encode(), new PyInt(90000002), payload, PyNone.Instance,
        };
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - head.Length)]));
    }

    private static string Clip(string text) => text.Length > 120 ? text[..120] + "…" : text;

    private sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class NoMethodsService : IService
    {
        public string Name => "knownSvc";

        public bool HasMethod(string method) => false;

        public ValueTask<PyValue> CallAsync(ServiceCall call) => ValueTask.FromResult<PyValue>(PyNone.Instance);
    }

    [Fact]
    public async Task Expensive_market_calls_have_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 901 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService();
        var inventoryService = new InventoryBudgetService();
        registry.Register(service);
        registry.Register(inventoryService);
        var now = FixedNowMs;
        var dispatcher = new PacketDispatcher(registry, () => now) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", "GetOrders"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", "BuyMultipleItems"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));

        var historyCapture = new Capture();
        var historySession = new ClientSession(historyCapture) { SessionId = 903 };
        await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", "GetOrders"), historySession);
        await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", "GetHistoryForManyTypeIDs"), historySession);
        Assert.Equal(2, service.Calls);
        Assert.Equal(ErrorResponseCommand, CommandOf(historyCapture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(historyCapture.Sent[1])));

        var boundCapture = new Capture();
        var boundSession = new ClientSession(boundCapture) { SessionId = 904 };
        const string marketObjectId = "N=900:904";
        registry.RegisterBoundObject(marketObjectId, service);
        await dispatcher.DispatchAsync(BoundCallRequestPacket(marketObjectId, "GetOrders"), boundSession);
        await dispatcher.DispatchAsync(BoundCallRequestPacket(marketObjectId, "BuyMultipleItems"), boundSession);
        Assert.Equal(3, service.Calls);
        Assert.Equal(2, boundCapture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(boundCapture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(boundCapture.Sent[1])));

        var inventoryCapture = new Capture();
        var inventorySession = new ClientSession(inventoryCapture) { SessionId = 902 };
        await dispatcher.DispatchAsync(CallRequestPacket("invbroker", "ListIncludingContainers"), inventorySession);
        await dispatcher.DispatchAsync(CallRequestPacket("invbroker", "ListIncludingContainers"), inventorySession);
        Assert.Equal(1, inventoryService.Calls);
        Assert.Equal(ErrorResponseCommand, CommandOf(inventoryCapture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(inventoryCapture.Sent[1])));

        now += 60_000;
        await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", "GetOrders"), session);
        Assert.Equal(4, service.Calls);
        Assert.Equal(7, CommandOf(capture.Sent[2]));
    }

    private sealed class ExpensiveService : IService
    {
        public int Calls { get; private set; }

        public string Name => "marketProxy";

        public bool HasMethod(string method) => method is "GetOrders" or "BuyMultipleItems" or "GetHistoryForManyTypeIDs";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class InventoryBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "invbroker";

        public bool HasMethod(string method) => method == "ListIncludingContainers";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class KnownService : IService
    {
        public string Name => "knownSvc";

        public bool HasMethod(string method) => method == "Known";

        public ValueTask<PyValue> CallAsync(ServiceCall call) => ValueTask.FromResult<PyValue>(new PyInt(42));
    }
}
