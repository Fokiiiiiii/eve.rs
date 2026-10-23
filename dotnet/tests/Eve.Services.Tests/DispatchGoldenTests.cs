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
        => CallRequestPacket(service, method, []);

    internal static PyValue CallRequestPacket(string service, string method, params PyValue[] args)
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
    public async Task Character_global_asset_calls_have_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 910 };
        var registry = new ServiceRegistry();
        var service = new CharacterKillmailBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("charMgr", "List"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("charMgr", "ListStations"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Bound_bind_invocations_consume_the_nested_expensive_rpc_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 911 };
        var registry = new ServiceRegistry();
        var service = new RecruitmentBindBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };
        var nestedSearch = new PyTuple([new PyStr("SearchCorpAds"), new PyTuple([])]);

        await dispatcher.DispatchAsync(CallRequestPacket("corpRecProxy", "MachoBindObject", new PyLong(1001), nestedSearch), session);
        await dispatcher.DispatchAsync(CallRequestPacket("corpRecProxy", "MachoBindObject", new PyLong(1001), nestedSearch), session);

        Assert.Equal(1, service.NestedSearches);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Expensive_market_calls_have_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 901 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService();
        var fleetService = new ExpensiveService("fleetObject");
        var inventoryService = new InventoryBudgetService();
        var corporationService = new CorporationBudgetService();
        var characterService = new CharacterKillmailBudgetService();
        var recruitmentService = new RecruitmentSearchBudgetService();
        registry.Register(service);
        registry.Register(fleetService);
        registry.Register(inventoryService);
        registry.Register(corporationService);
        registry.Register(characterService);
        registry.Register(recruitmentService);
        var now = FixedNowMs;
        var dispatcher = new PacketDispatcher(registry, () => now) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", "GetOrders"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", "BuyMultipleItems"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", "PlaceMultiSellOrder"), session);

        var fleetCapture = new Capture();
        var fleetSession = new ClientSession(fleetCapture) { SessionId = 908 };
        await dispatcher.DispatchAsync(CallRequestPacket("fleetObject", "SendBroadcast"), fleetSession);
        await dispatcher.DispatchAsync(CallRequestPacket("fleetObject", "SendBroadcast"), fleetSession);
        Assert.Equal(1, fleetService.Calls);
        Assert.Equal(ErrorResponseCommand, CommandOf(fleetCapture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(fleetCapture.Sent[1])));

        Assert.Equal(1, service.Calls);
        Assert.Equal(3, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[2]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[2])));

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

        var recruitmentCapture = new Capture();
        var recruitmentSession = new ClientSession(recruitmentCapture) { SessionId = 907 };
        await dispatcher.DispatchAsync(CallRequestPacket("corpRecProxy", "SearchCorpAds"), recruitmentSession);
        await dispatcher.DispatchAsync(CallRequestPacket("corpRecProxy", "SearchCorpAds"), recruitmentSession);
        Assert.Equal(1, recruitmentService.Calls);
        Assert.Equal(ErrorResponseCommand, CommandOf(recruitmentCapture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(recruitmentCapture.Sent[1])));

        var characterCapture = new Capture();
        var characterSession = new ClientSession(characterCapture) { SessionId = 906 };
        await dispatcher.DispatchAsync(CallRequestPacket("charMgr", "GetRecentShipKillsAndLosses"), characterSession);
        await dispatcher.DispatchAsync(CallRequestPacket("charMgr", "GetRecentShipKillsAndLosses"), characterSession);
        await dispatcher.DispatchAsync(CallRequestPacket("charMgr", "GetContactList"), characterSession);
        Assert.Equal(1, characterService.Calls);
        Assert.Equal(3, characterCapture.Sent.Count);
        foreach (var refused in characterCapture.Sent.Skip(1))
        {
            Assert.Equal(ErrorResponseCommand, CommandOf(refused));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(refused)));
        }

        var personalWriteCapture = new Capture();
        var personalWriteSession = new ClientSession(personalWriteCapture) { SessionId = 909 };
        await dispatcher.DispatchAsync(CallRequestPacket("charMgr", "AddOwnerNote"), personalWriteSession);
        await dispatcher.DispatchAsync(CallRequestPacket("charMgr", "AddOwnerNote"), personalWriteSession);
        Assert.Equal(2, characterService.Calls);
        Assert.Equal(2, personalWriteCapture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(personalWriteCapture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(personalWriteCapture.Sent[1])));

        var corporationCapture = new Capture();
        var corporationSession = new ClientSession(corporationCapture) { SessionId = 905 };
        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", "GetEveOwners"), corporationSession);
        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", "GetMemberIDsByQuery"), corporationSession);
        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", "PayoutDividend"), corporationSession);
        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", "UpdateApplicationOffer"), corporationSession);
        Assert.Equal(1, corporationService.Calls);
        Assert.Equal(4, corporationCapture.Sent.Count);
        foreach (var (refused, index) in corporationCapture.Sent.Skip(1).Select((packet, index) => (packet, index + 1)))
        {
            Assert.Equal(ErrorResponseCommand, CommandOf(refused));
            Assert.True(System.Text.Encoding.Latin1.GetString(Convert.FromHexString(refused)).Contains("SessionRpcBudgetExceeded"),
                $"Budget refusal was missing from corporation RPC response {index}.");
        }

        var inventoryCapture = new Capture();
        var inventorySession = new ClientSession(inventoryCapture) { SessionId = 902 };
        const string inventoryObjectId = "N=901:902";
        registry.RegisterBoundObject(inventoryObjectId, inventoryService);
        await dispatcher.DispatchAsync(CallRequestPacket("invbroker", "GetInventoryFromId"), inventorySession);
        for (var index = 0; index < 4; index++)
            await dispatcher.DispatchAsync(BoundCallRequestPacket(inventoryObjectId, "GetCapacity"), inventorySession);
        await dispatcher.DispatchAsync(CallRequestPacket("invbroker", "GetInventoryFromId"), inventorySession);
        Assert.Equal(5, inventoryService.Calls);
        Assert.Equal(6, inventoryCapture.Sent.Count);
        Assert.Equal(7, CommandOf(inventoryCapture.Sent[0]));
        foreach (var capacity in inventoryCapture.Sent.Skip(1).Take(4))
            Assert.Equal(7, CommandOf(capacity));
        Assert.Equal(ErrorResponseCommand, CommandOf(inventoryCapture.Sent[5]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(inventoryCapture.Sent[5])));

        now += 60_000;
        await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", "GetOrders"), session);
        Assert.Equal(4, service.Calls);
        Assert.Equal(7, CommandOf(capture.Sent[3]));
    }

    [Fact]
    public async Task Bill_manager_calls_have_a_named_per_session_budget()
    {
        var captureRegistry = new ServiceRegistry();
        var service = new ExpensiveService("billMgr");
        captureRegistry.Register(service);
        var dispatcher = new PacketDispatcher(captureRegistry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };
        var methods = new[]
        {
            "CharPayBill",
            "GetAutomaticPaySettings",
            "GetCorporationBills",
            "GetCorporationBillsReceivable",
            "PayCorporationBill",
            "SendAutomaticPaySettings",
        };

        for (var index = 0; index < methods.Length; index++)
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = 940 + index };
            await dispatcher.DispatchAsync(CallRequestPacket("billMgr", methods[index]), session);
            await dispatcher.DispatchAsync(CallRequestPacket("billMgr", methods[index]), session);

            Assert.Equal(index + 1, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Market_owner_history_reads_have_a_named_per_session_budget()
    {
        var captureRegistry = new ServiceRegistry();
        var service = new ExpensiveService();
        captureRegistry.Register(service);
        var now = FixedNowMs;
        var dispatcher = new PacketDispatcher(captureRegistry, () => now) { MaxExpensiveRpcUnitsPerMinute = 10 };
        var methods = new[]
        {
            "GetCharOrders",
            "GetCorporationOrders",
            "GetCharEscrow",
            "GetMarketOrderHistory",
            "GetOldPriceHistory",
            "GetNewPriceHistory",
            "GetPlexOrders",
            "GetPlexBest",
            "GetPlexHistory",
            "GetPlexOldPriceHistory",
            "GetPlexNewPriceHistory",
            "CharGetTransactions",
            "CorpGetTransactions",
        };

        for (var index = 0; index < methods.Length; index++)
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = 920 + index };
            await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", methods[index]), session);
            await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", methods[index]), session);

            Assert.Equal(index + 1, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Corporation_potential_CEO_member_enumeration_uses_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 916 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("corpRegistry");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", "GetNumberOfPotentialCEOs"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", "GetNumberOfPotentialCEOs"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Corporation_member_actions_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 915 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("corpRegistry");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", "ExecuteActions"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", "ExecuteActions"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Inventory_recursive_and_validation_calls_use_the_expensive_rpc_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 917 };
        var registry = new ServiceRegistry();
        var service = new InventoryBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("invbroker", "ListIncludingContainers"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("invbroker", "GetCapacity"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Inventory_bulk_mutations_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 916 };
        var registry = new ServiceRegistry();
        var service = new InventoryBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("invbroker", "MultiAdd"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("invbroker", "MultiMerge"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Fitting_manager_calls_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 917 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("charFittingMgr");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("charFittingMgr", "GetFittings"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("charFittingMgr", "GetFittings"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Industry_job_calls_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 918 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("industryManager");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("industryManager", "GetJobsByOwner"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("industryManager", "GetJobsByOwner"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Ship_cosmetics_license_calls_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 919 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("shipCosmeticsMgr");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("shipCosmeticsMgr", "ActivateSkinLicense"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("shipCosmeticsMgr", "ActivateSkinLicense"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Corporation_asset_and_audit_reads_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 918 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("corpMgr");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("corpMgr", "GetAssetInventory"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("corpMgr", "GetAssetInventoryForLocation"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("corpMgr", "SearchAssets"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("corpMgr", "GetAggressionSettingsForCorps"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("corpMgr", "AuditMember"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(5, capture.Sent.Count);
        for (var index = 1; index < capture.Sent.Count; index++)
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[index])));
    }

    [Fact]
    public async Task Alliance_member_enumeration_uses_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 909 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("allianceRegistry");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("allianceRegistry", "GetAllianceMembers"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("allianceRegistry", "GetAllianceMembers"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Theory]
    [InlineData("InjectSkillIntoBrain")]
    [InlineData("InjectSkillpoints")]
    [InlineData("ExtractSkills")]
    [InlineData("GetDiminishedSpFromInjectors")]
    [InlineData("SplitSkillInjector")]
    [InlineData("CombineSkillInjector")]
    public async Task Skill_item_operations_use_the_named_per_session_budget(string method)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 912 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("skillHandler");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("skillHandler", method), session);
        await dispatcher.DispatchAsync(CallRequestPacket("skillHandler", method), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }


    [Fact]
    public async Task Contract_operations_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 913 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("contractProxy");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("contractProxy", "GetContractListForOwner"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("contractProxy", "GetContractListForOwner"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Corporation_structure_directory_reads_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 912 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("structureDirectory");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("structureDirectory", "GetMyCorporationStructures"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("structureDirectory", "GetMyCorporationStructures"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Corporation_office_impound_reads_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 919 };
        var registry = new ServiceRegistry();
        var service = new OfficeManagerBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("officeManager", "HasCorpImpoundedItems"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("officeManager", "GetImpoundReleasePrice"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Corporation_office_item_mutations_use_the_named_per_session_budget()
    {
        var methods = new[] { "RentOffice", "UnrentOffice", "GetItemsFromImpound", "TrashImpoundedOffice" };
        for (var index = 0; index < methods.Length; index++)
        {
            var method = methods[index];
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = 920 + index };
            var registry = new ServiceRegistry();
            var service = new OfficeManagerBudgetService();
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

            await dispatcher.DispatchAsync(CallRequestPacket("officeManager", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("officeManager", "HasCorpImpoundedItems"), session);

            Assert.Equal(1, service.Calls);
            Assert.Equal(2, capture.Sent.Count);
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Corporation_station_binds_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 916 };
        var registry = new ServiceRegistry();
        var service = new CorpStationBindBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("corpStationMgr", "MachoBindObject"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("corpStationMgr", "MachoBindObject"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Insurance_contract_scans_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 917 };
        var registry = new ServiceRegistry();
        var service = new InsuranceBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("insuranceSvc", "GetContracts"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("insuranceSvc", "GetContracts"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Jump_bridge_structure_reads_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 918 };
        var registry = new ServiceRegistry();
        var service = new StructureJumpBridgeBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("structureJumpBridgeMgr", "GetJbStructureDestination"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("structureJumpBridgeMgr", "GetLinkedStructure"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Character_structure_directory_reads_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 911 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("structureDirectory");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("structureDirectory", "GetMyCharacterStructures"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("structureDirectory", "GetMyCharacterStructures"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Alliance_rankings_use_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 910 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("allianceRegistry");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("allianceRegistry", "GetRankedAlliances"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("allianceRegistry", "GetRankedAlliances"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Corporation_application_lists_and_history_use_a_named_per_session_budget()
    {
        long sessionId = 914;
        foreach (var method in new[] { "GetApplications", "GetOldApplications", "GetMyApplications", "GetMyOldApplications", "GetMyOpenInvitations" })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = sessionId++ };
            var registry = new ServiceRegistry();
            var service = new ExpensiveService("corpRegistry");
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

            await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", method), session);

            Assert.Equal(1, service.Calls);
            Assert.Equal(2, capture.Sent.Count);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Corporation_contact_and_bulletin_lists_use_a_named_per_session_budget()
    {
        long sessionId = 919;
        foreach (var method in new[] { "GetCorporation", "GetCorporateContacts", "GetContactList", "GetBulletins", "GetBulletinEntries", "GetRecruitmentAd", "GetRecruitmentAdsByCorpID", "GetRecruitmentAdsForCorporation" })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = sessionId++ };
            var registry = new ServiceRegistry();
            var service = new ExpensiveService("corpRegistry");
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

            await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", method), session);

            Assert.Equal(1, service.Calls);
            Assert.Equal(2, capture.Sent.Count);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Character_profile_reads_use_a_named_per_session_budget()
    {
        long sessionId = 978;
        foreach (var method in new[] { "GetPublicInfo", "GetPublicInfo3", "GetCharacterDescription", "GetCharacterCreationDate", "GetPrivateInfo" })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = sessionId++ };
            var registry = new ServiceRegistry();
            var service = new ExpensiveService("charMgr");
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

            await dispatcher.DispatchAsync(CallRequestPacket("charMgr", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("charMgr", method), session);

            Assert.Equal(1, service.Calls);
            Assert.Equal(2, capture.Sent.Count);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Corporation_service_list_and_bulk_methods_use_a_named_per_session_budget()
    {
        long sessionId = 969;
        foreach (var method in new[] { "GetEmploymentRecord", "GetEmployementRecordAndCharacterTransfers", "GetMyCorporationInfo", "GetMedalsReceived", "GetAllCorpMedals", "GetRecipientsOfMedal", "SetMedalStatus", "GiveMedalToCharacters" })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = sessionId++ };
            var registry = new ServiceRegistry();
            var service = new ExpensiveService("corporationSvc");
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

            await dispatcher.DispatchAsync(CallRequestPacket("corporationSvc", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("corporationSvc", method), session);

            Assert.Equal(1, service.Calls);
            Assert.Equal(2, capture.Sent.Count);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Owner_group_reads_and_mutations_use_a_named_per_session_budget()
    {
        long sessionId = 951;
        foreach (var method in new[] { "GetMyGroups", "GetGroup", "GetGroupsMany", "GetMembers", "GetMembersForMultipleGroups", "Create", "Delete", "UpdateName", "UpdateDescription", "AddMembers", "DeleteMembers", "UpdateMembershipTypes", "GetGroupLogs" })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = sessionId++ };
            var registry = new ServiceRegistry();
            var service = new ExpensiveService("ownerGroupManager");
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

            await dispatcher.DispatchAsync(CallRequestPacket("ownerGroupManager", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("ownerGroupManager", method), session);

            Assert.Equal(1, service.Calls);
            Assert.Equal(2, capture.Sent.Count);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Alliance_expensive_methods_use_a_named_per_session_budget()
    {
        long sessionId = 913;
        foreach (var method in new[] { "GetAlliance", "GetAlliancePublicInfo", "GetAllianceSovereigntyStructuresInfo", "GetRelationships", "SetRelationship", "GetCapitalSystemInfo", "SetCapitalSystem", "SetPrimeHour", "CancelCapitalSystemTransition", "UpdateAlliance", "DeclareWarAgainst", "PayBill", "GetBillBalance", "GetBills", "GetBillsReceivable", "GetApplications", "GetEveOwners", "GetRankedAlliances", "GetAllianceMembers", "GetMembers", "GetEmploymentRecord", "GetAllianceMembersOlderThan", "GetDaysInAlliance", "GetAllianceApplications", "GetAllianceContacts", "GetContactList", "GetBulletins", "GetBulletinEntries" })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = sessionId++ };
            var registry = new ServiceRegistry();
            var service = new ExpensiveService("allianceRegistry");
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

            await dispatcher.DispatchAsync(CallRequestPacket("allianceRegistry", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("allianceRegistry", method), session);

            Assert.Equal(1, service.Calls);
            Assert.Equal(2, capture.Sent.Count);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Fleet_object_expensive_methods_use_a_named_per_session_budget()
    {
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("fleetObject");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        long sessionId = 911;
        foreach (var method in new[] { "Invite", "MassInvite", "Init", "SendBroadcast" })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = sessionId++ };
            var callsBefore = service.Calls;

            await dispatcher.DispatchAsync(CallRequestPacket("fleetObject", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("fleetObject", method), session);

            Assert.Equal(callsBefore + 1, service.Calls);
            Assert.Equal(2, capture.Sent.Count);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Character_organization_batch_uses_the_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 911 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("charMgr");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("charMgr", "GetOrganizationInfoForCharacters"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("charMgr", "GetOrganizationInfoForCharacters"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Corporation_item_lock_reads_have_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 909 };
        var registry = new ServiceRegistry();
        var service = new ItemLockingBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("itemLocking", "GetItemsByLocation"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("itemLocking", "GetLockedItemLocations"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Alliance_contact_list_aliases_have_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 910 };
        var registry = new ServiceRegistry();
        var service = new AllianceContactsBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("allianceRegistry", "GetAllianceContacts"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("allianceRegistry", "GetContactList"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Alliance_bulletin_list_aliases_have_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 911 };
        var registry = new ServiceRegistry();
        var service = new AllianceBulletinsBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("allianceRegistry", "GetBulletins"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("allianceRegistry", "GetBulletinEntries"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Character_settings_reads_and_writes_have_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 912 };
        var registry = new ServiceRegistry();
        var service = new CharacterSettingsBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("charMgr", "GetCharacterSettings"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("charMgr", "SaveCharacterSetting"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Fleet_member_composition_reads_have_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 913 };
        var registry = new ServiceRegistry();
        var service = new FleetCompositionBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("fleetObject", "GetInitState"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("fleetObject", "GetFleetComposition"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Expensive_wallet_calls_have_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 908 };
        var registry = new ServiceRegistry();
        var service = new WalletBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("account", "GiveCash"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("account", "GetTransactions"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Loyalty_store_calls_have_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 909 };
        var registry = new ServiceRegistry();
        var service = new LoyaltyStoreBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("LPStoreMgr", "TakeOfferForCharacter"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("LPStoreMgr", "GetAvailableOffersFromCorp"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    private sealed class LoyaltyStoreBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "LPStoreMgr";
        public bool HasMethod(string method) => method is "GetAvailableOffersFromCorp" or "TakeOfferForCharacter" or "TakeOfferForCorporation";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    [Fact]
    public async Task Mailbox_reads_and_writes_have_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 910 };
        var registry = new ServiceRegistry();
        var service = new MailBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("mailMgr", "MarkAsRead"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("mailMgr", "SendMail"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Mailing_list_work_has_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 911 };
        var registry = new ServiceRegistry();
        var service = new MailingListBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("mailingListsMgr", "GetMembers"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("mailingListsMgr", "SendWelcomeMail"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    private sealed class MailingListBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "mailingListsMgr";
        public bool HasMethod(string method) => method is "GetMembers" or "SendWelcomeMail";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class MailBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "mailMgr";
        public bool HasMethod(string method) => method is "SyncMail" or "GetMailHeaders" or "GetBody" or "MarkAsRead" or "SendMail" or "PokePlayerAboutChatMsgGm";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class ItemLockingBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "itemLocking";

        public bool HasMethod(string method) => method is "GetItemsByLocation" or "GetLockedItemLocations";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class AllianceContactsBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "allianceRegistry";

        public bool HasMethod(string method) => method is "GetAllianceContacts" or "GetContactList";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class AllianceBulletinsBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "allianceRegistry";

        public bool HasMethod(string method) => method is "GetBulletins" or "GetBulletinEntries";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class CharacterSettingsBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "charMgr";

        public bool HasMethod(string method) => method is "GetCharacterSettings" or "SaveCharacterSetting";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class FleetCompositionBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "fleetObject";

        public bool HasMethod(string method) => method is "GetInitState" or "GetFleetComposition";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class WalletBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "account";

        public bool HasMethod(string method) => method is "GetTransactions" or "GiveCash" or "GiveCashFromCorpAccount";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class ExpensiveService(string name = "marketProxy") : IService
    {
        public int Calls { get; private set; }

        public string Name => name;

        public bool HasMethod(string method) => method is "GetOrders" or "GetCharOrders" or "GetCorporationOrders" or "GetCharEscrow" or "GetMarketOrderHistory" or "GetOldPriceHistory" or "GetNewPriceHistory" or "GetPlexOrders" or "GetPlexBest" or "GetPlexHistory" or "GetPlexOldPriceHistory" or "GetPlexNewPriceHistory" or "CharGetTransactions" or "CorpGetTransactions" or "BuyMultipleItems" or "PlaceMultiSellOrder" or "GetHistoryForManyTypeIDs" or "GetJobCounts" or "GetJob" or "GetJobsByOwner" or "InstallJob" or "CompleteJob" or "CompleteManyJobs" or "CancelJob" or "ActivateSkinLicense" or "ApplySkinToShip" or "GetLicencedSkins" or "GiveSkin" or "RemoveSkin" or "GMExpireSkinLicense" or "GetAlliance" or "GetAlliancePublicInfo" or "GetAllianceSovereigntyStructuresInfo" or "GetCapitalSystemInfo" or "SetCapitalSystem" or "SetPrimeHour" or "CancelCapitalSystemTransition" or "UpdateAlliance" or "DeclareWarAgainst" or "PayBill" or "GetBillBalance" or "GetRelationships" or "SetRelationship" or "GetBillBalance" or "GetBills" or "GetBillsReceivable" or "GetApplications" or "GetEveOwners" or "GetEmploymentRecord" or "GetAllianceMembersOlderThan" or "GetAllianceApplications" or "GetAllianceContacts" or "GetAllianceMembers" or "GetDaysInAlliance" or "GetMyGroups" or "GetGroup" or "GetGroupsMany" or "GetMembers" or "GetMembersForMultipleGroups" or "Create" or "Delete" or "UpdateName" or "UpdateDescription" or "AddMembers" or "DeleteMembers" or "UpdateMembershipTypes" or "GetGroupLogs" or "GetRankedAlliances" or "GetOrganizationInfoForCharacters" or "SendBroadcast" or "Invite" or "MassInvite" or "Init" or "ExecuteActions" or "GetNumberOfPotentialCEOs" or "GetFittings" or "GetAssetInventory" or "GetAssetInventoryForLocation" or "SearchAssets" or "GetAggressionSettingsForCorps" or "AuditMember" or "CheckMyDockingAccessToStructures" or "GetMyDockableStructures" or "GetMyCharacterStructures" or "GetMyCorporationStructures" or "InjectSkillIntoBrain" or "InjectSkillpoints" or "ExtractSkills" or "GetDiminishedSpFromInjectors" or "SplitSkillInjector" or "CombineSkillInjector" or "GetContractListForOwner" or "GetApplications" or "GetOldApplications" or "GetCorporateContacts" or "GetContactList" or "GetBulletins" or "GetBulletinEntries" or "GetCorporation" or "GetRecruitmentAd" or "GetRecruitmentAdsByCorpID" or "GetRecruitmentAdsForCorporation" or "GetPublicInfo" or "GetPublicInfo3" or "GetCharacterDescription" or "GetCharacterCreationDate" or "GetPrivateInfo" or "GetEmploymentRecord" or "GetEmployementRecordAndCharacterTransfers" or "GetMyCorporationInfo" or "GetMedalsReceived" or "GetAllCorpMedals" or "GetRecipientsOfMedal" or "SetMedalStatus" or "GiveMedalToCharacters" or "GetMyApplications" or "GetMyOldApplications" or "GetMyOpenInvitations" or "CharPayBill" or "GetAutomaticPaySettings" or "GetCorporationBills" or "GetCorporationBillsReceivable" or "PayCorporationBill" or "SendAutomaticPaySettings";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class RecruitmentSearchBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "corpRecProxy";

        public bool HasMethod(string method) => method == "SearchCorpAds";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class RecruitmentBindBudgetService : IService
    {
        public int NestedSearches { get; private set; }

        public string Name => "corpRecProxy";

        public bool HasMethod(string method) => method == "MachoBindObject";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            if (call.Args.Count > 1 && call.Args[1] is PyTuple { Items.Count: > 0 } nested &&
                nested.Items[0] is PyStr { Value: "SearchCorpAds" })
                NestedSearches++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class CharacterKillmailBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "charMgr";

        public bool HasMethod(string method) => method is "GetRecentShipKillsAndLosses" or "GetContactList" or "AddOwnerNote" or "EditOwnerNote" or "RemoveOwnerNote" or "SetNote" or "AddContact" or "EditContact" or "DeleteContacts" or "BlockOwners" or "UnblockOwners" or "EditContactsRelationshipID" or "AssignLabels" or "RemoveLabels" or "CreateLabel" or "DeleteLabel" or "EditLabel" or "List" or "ListStations" or "ListStationItems" or "ListIncludingContainers" or "GetAssetWorth";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class CorporationBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "corpRegistry";

        public bool HasMethod(string method) => method is "GetEveOwners" or "GetMemberIDsByQuery" or "GetMembersPaged" or "PayoutDividend" or "UpdateApplicationOffer";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class OfficeManagerBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "officeManager";

        public bool HasMethod(string method) => method is "HasCorpImpoundedItems" or "GetImpoundReleasePrice" or "RentOffice" or "UnrentOffice" or "GetItemsFromImpound" or "TrashImpoundedOffice";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class CorpStationBindBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "corpStationMgr";

        public bool HasMethod(string method) => method == "MachoBindObject";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class InsuranceBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "insuranceSvc";

        public bool HasMethod(string method) => method == "GetContracts";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class StructureJumpBridgeBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "structureJumpBridgeMgr";

        public bool HasMethod(string method) => method is "GetJbStructureDestination" or "GetLinkedStructure";

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

        public bool HasMethod(string method) => method is "GetInventoryFromId" or "ListIncludingContainers" or "GetCapacity" or "MultiAdd" or "MultiMerge";

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
