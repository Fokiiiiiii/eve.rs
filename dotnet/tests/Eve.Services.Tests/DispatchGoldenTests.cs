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
    public async Task Inventory_calls_keep_a_bounded_budget_separate_from_other_expensive_services()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 912 };
        var registry = new ServiceRegistry();
        var market = new ExpensiveService();
        var inventory = new InventoryBudgetService();
        registry.Register(market);
        registry.Register(inventory);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };
        var getInventory = new PyTuple([
            new PyStr("GetInventory"),
            new PyTuple([new PyInt(10004), PyNone.Instance]),
            Py.Dict(),
        ]);

        await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", "GetOrders"), session);
        for (var index = 0; index < 6; index++)
            await dispatcher.DispatchAsync(CallRequestPacket("invbroker", "MachoBindObject", new PyTuple([new PyLong(60015249), new PyInt(15)]), getInventory), session);

        Assert.Equal(1, market.Calls);
        Assert.Equal(5, inventory.Calls);
        Assert.Equal(7, capture.Sent.Count);
        Assert.All(capture.Sent.Take(6), packet => Assert.Equal(7, CommandOf(packet)));
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[6]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[6])));
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
        var warService = new ExpensiveService("warRegistry");
        registry.Register(service);
        registry.Register(fleetService);
        registry.Register(inventoryService);
        registry.Register(corporationService);
        registry.Register(characterService);
        registry.Register(recruitmentService);
        registry.Register(warService);
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

        var warCapture = new Capture();
        var warSession = new ClientSession(warCapture) { SessionId = 909 };
        await dispatcher.DispatchAsync(CallRequestPacket("warRegistry","GetWars"),warSession);
        await dispatcher.DispatchAsync(CallRequestPacket("warRegistry","GetWars"),warSession);
        Assert.Equal(1,warService.Calls);
        Assert.Equal(ErrorResponseCommand,CommandOf(warCapture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded",System.Text.Encoding.Latin1.GetString(Convert.FromHexString(warCapture.Sent[1])));

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
    public async Task The_fitting_inventory_rereads_fit_the_default_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 902 };
        var registry = new ServiceRegistry();
        foreach (var serviceName in new[] { "charUnboundMgr", "invbroker", "dogmaIM" })
            registry.Register(new RpcBudgetScenarioService(serviceName));
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs);

        async Task Call(string service, string method)
        {
            await dispatcher.DispatchAsync(CallRequestPacket(service, method), session);
            Assert.Equal(7, CommandOf(capture.Sent[^1]));
        }

        await Call("charUnboundMgr", "CreateCharacterWithDoll");
        await Call("charUnboundMgr", "SelectCharacterID");
        await Call("invbroker", "GetInventory");
        await Call("invbroker", "List");
        await Call("invbroker", "GetInventory");
        await Call("invbroker", "List");
        await Call("dogmaIM", "GetAllInfo");
        await Call("dogmaIM", "GetAllInfo");

        for (var step = 0; step < 6; step++)
        {
            await Call("invbroker", "GetInventory");
            await Call("invbroker", "List");
            await Call("dogmaIM", "GetAllInfo");
        }

        Assert.Equal(26, capture.Sent.Count);
    }

    [Fact]
    public async Task Config_multi_lookups_have_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 918 };
        var registry = new ServiceRegistry();
        var service = new ConfigLookupBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("config", "GetMultiOwnersEx"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("config", "GetMultiLocationsEx"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
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
    public async Task Plex_market_history_uses_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 924 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("marketProxy");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", "GetPlexHistory"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", "GetPlexHistory"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Theory]
    [InlineData("GetTaxRate", 2)]
    [InlineData("UpdateSettings", 10)]
    public async Task Customs_office_rpc_uses_a_named_per_session_budget(string method, int cost)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 925 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("planetOrbitalRegistryBroker");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = cost };

        await dispatcher.DispatchAsync(CallRequestPacket("planetOrbitalRegistryBroker", method), session);
        await dispatcher.DispatchAsync(CallRequestPacket("planetOrbitalRegistryBroker", method), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Theory]
    [InlineData("PerformKiringServerSideAuthenticationFromCode")]
    [InlineData("GetSAuthAid")]
    [InlineData("PlaceKiringOrder")]
    public async Task Kiring_authentication_and_purchase_rpcs_use_a_named_per_session_budget(string method)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 929 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("kiringMgr");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("kiringMgr", method), session);
        await dispatcher.DispatchAsync(CallRequestPacket("kiringMgr", method), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Theory]
    [InlineData("TrackOfferDelivered")]
    [InlineData("TrackOfferSeen")]
    [InlineData("TrackOfferBuyPress")]
    [InlineData("TrackOfferClosed")]
    [InlineData("TrackOfferPurchased")]
    [InlineData("TrackOfferPurchasedSeen")]
    public async Task Contextual_offer_tracking_uses_a_named_per_session_budget(string method)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 931 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("contextualOfferMgr");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };

        await dispatcher.DispatchAsync(CallRequestPacket("contextualOfferMgr", method), session);
        await dispatcher.DispatchAsync(CallRequestPacket("contextualOfferMgr", method), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Theory]
    [InlineData("SetMotd")]
    [InlineData("CreatePlayerOwnedChannel")]
    [InlineData("AddCharacterToChannel")]
    [InlineData("GrantChannelOwnership")]
    [InlineData("ResyncSystemChannelAccess")]
    public async Task XMPP_channel_rpcs_use_a_named_per_session_budget(string method)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 930 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("XmppChatMgr");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("XmppChatMgr", method), session);
        await dispatcher.DispatchAsync(CallRequestPacket("XmppChatMgr", method), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Character_description_writes_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 926 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("charMgr");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("charMgr", "SetCharacterDescription"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("charMgr", "SetCharacterDescription"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Theory]
    [InlineData("UpdateCorporation")]
    [InlineData("UpdateLogo")]
    [InlineData("UpdateDivisionNames")]
    [InlineData("UpdateCorporationAbilities")]
    [InlineData("RegisterNewAcceptStructureSettings")]
    [InlineData("RegisterNewCorpMailRestrictionSettings")]
    [InlineData("SetStructureReinforceDefault")]
    [InlineData("RegisterNewAggressionSettings")]
    public async Task Corporation_settings_writes_use_a_named_per_session_budget(string method)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 927 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("corpRegistry");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", method), session);
        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", method), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Theory]
    [InlineData("DeclareExecutorSupport")]
    [InlineData("AddAllianceContact")]
    [InlineData("EditAllianceContact")]
    [InlineData("RemoveAllianceContacts")]
    [InlineData("EditContactsRelationshipID")]
    [InlineData("CreateLabel")]
    [InlineData("DeleteLabel")]
    [InlineData("EditLabel")]
    [InlineData("AssignLabels")]
    [InlineData("RemoveLabels")]
    [InlineData("UpdateBulletin")]
    [InlineData("DeleteBulletin")]
    [InlineData("UpdateBulletinOrder")]
    public async Task Alliance_admin_writes_use_a_named_per_session_budget(string method)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 928 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("allianceRegistry");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("allianceRegistry", method), session);
        await dispatcher.DispatchAsync(CallRequestPacket("allianceRegistry", method), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Theory]
    [InlineData("UpdateTitle")]
    [InlineData("DeleteTitle")]
    [InlineData("AddCorporateContact")]
    [InlineData("EditCorporateContact")]
    [InlineData("CreateLabel")]
    [InlineData("DeleteLabel")]
    [InlineData("EditLabel")]
    [InlineData("AssignLabels")]
    [InlineData("RemoveLabels")]
    [InlineData("AddBulletin")]
    [InlineData("UpdateBulletin")]
    [InlineData("DeleteBulletin")]
    public async Task Corporation_content_writes_use_a_named_per_session_budget(string method)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 929 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("corpRegistry");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", method), session);
        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", method), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Theory]
    [InlineData("AddCorporation")]
    [InlineData("InsertApplication")]
    [InlineData("InsertInvitation")]
    [InlineData("ResignFromCEO")]
    [InlineData("KickOutMember")]
    [InlineData("SetAccountKey")]
    [InlineData("CreateAlliance")]
    [InlineData("GetSuggestedAllianceShortNames")]
    public async Task Corporation_membership_writes_use_a_named_per_session_budget(string method)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 930 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("corpRegistry");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", method), session);
        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", method), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Theory]
    [InlineData("ApplyToJoinFleet")]
    [InlineData("GetAvailableFleetAds")]
    [InlineData("AddFleetFinderAdvert")]
    [InlineData("UpdateFleetAdvertWithNewLeader")]
    [InlineData("RemoveFleetFinderAdvert")]
    [InlineData("GetMyFleetFinderAdvert")]
    [InlineData("UpdateAdvertInfo")]
    [InlineData("UpdateAdvertAllowedEntities")]
    public async Task Fleet_proxy_operations_use_a_named_per_session_budget(string method)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 931 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("fleetProxy");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("fleetProxy", method), session);
        await dispatcher.DispatchAsync(CallRequestPacket("fleetProxy", method), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Theory]
    [InlineData("ForceLeaveFleet")]
    [InlineData("BroadcastToBubble")]
    [InlineData("BroadcastToSystem")]
    public async Task Fleet_manager_mutations_and_fanout_use_a_named_per_session_budget(string method)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 932 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("fleetMgr");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("fleetMgr", method), session);
        await dispatcher.DispatchAsync(CallRequestPacket("fleetMgr", method), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
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

    [Theory]
    [InlineData("GetRepairQuotes")]
    [InlineData("RepairItemsInStation")]
    public async Task Repair_calls_use_the_named_per_session_budget(string method)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 918 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("repairSvc");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("repairSvc", method), session);
        await dispatcher.DispatchAsync(CallRequestPacket("repairSvc", method), session);

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
    public async Task Inventory_asset_valuation_uses_the_expensive_rpc_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 915 };
        var registry = new ServiceRegistry();
        var service = new InventoryBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("invbroker", "GetAssetWorth"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("invbroker", "GetCapacity"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Theory]
    [InlineData("FitFitting")]
    [InlineData("ListStations")]
    [InlineData("ListStationItems")]
    public async Task Inventory_asset_locations_use_the_expensive_rpc_budget(string method)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 914 };
        var registry = new ServiceRegistry();
        var service = new InventoryBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("invbroker", method), session);
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
    public async Task Station_service_office_calls_share_the_office_rpc_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 920 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("stationSvc");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("stationSvc", "HasCorpImpoundedItems"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("stationSvc", "HasCorpImpoundedItems"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Station_guest_enumeration_uses_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 921 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("stationSvc");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("stationSvc", "GetGuests"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("stationSvc", "GetGuests"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Crimewatch_history_and_kill_right_lists_use_named_per_session_budgets()
    {
        foreach (var (serviceName, method) in new[]
        {
            ("crimewatch", "GetSecurityStatusTransactions"),
            ("killRightMgr", "GetKillRights"),
        })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = 923 + capture.Sent.Count };
            var registry = new ServiceRegistry();
            var service = new ExpensiveService(serviceName);
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

            await dispatcher.DispatchAsync(CallRequestPacket(serviceName, method), session);
            await dispatcher.DispatchAsync(CallRequestPacket(serviceName, method), session);

            Assert.Equal(1, service.Calls);
            Assert.Equal(2, capture.Sent.Count);
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Config_bulk_lookups_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 922 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("config");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("config", "GetMultiOwnersEx"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("config", "GetMultiLocationsEx"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Repackaging_service_calls_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 919 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("repackagingSvc");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("repackagingSvc", "RepackageItems"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("repackagingSvc", "RepackageItems"), session);

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

        await dispatcher.DispatchAsync(CallRequestPacket("officeManager", "GetMyCorporationsOffices"), session);
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
        foreach (var method in new[] { "GetApplications", "GetOldApplications", "GetAllianceApplications", "GetMyApplications", "GetMyOldApplications", "GetMyOpenInvitations" })
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
    public async Task Corporation_registry_reads_use_a_named_per_session_budget()
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
        foreach (var method in new[] { "GetPublicInfo", "GetPublicInfo3", "GetCharacterDescription", "GetCharacterCreationDate", "GetRecentShipKillsAndLosses", "GetPrivateInfo" })
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
    public async Task Corporation_manager_public_reads_use_a_named_per_session_budget()
    {
        long sessionId = 944;
        foreach (var method in new[] { "GetPublicInfo", "GetCorporations", "GetCorporationIDForCharacter" })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = sessionId++ };
            var registry = new ServiceRegistry();
            var service = new ExpensiveService("corpmgr");
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

            await dispatcher.DispatchAsync(CallRequestPacket("corpmgr", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("corpmgr", method), session);

            Assert.Equal(1, service.Calls);
            Assert.Equal(2, capture.Sent.Count);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Mutual_war_invite_methods_use_a_named_per_session_budget()
    {
        long sessionId = 937;
        foreach (var method in new[]
        {
            "GetPendingInvitesForSession", "IsCorpInvitesBlockedPlayer", "SendInviteByPlayer",
            "WithdrawInviteByPlayer", "RespondToInviteByPlayer", "SetInvitesBlockedByPlayer"
        })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = sessionId++ };
            var registry = new ServiceRegistry();
            var service = new ExpensiveService("mutualWarInviteMgr");
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

            await dispatcher.DispatchAsync(CallRequestPacket("mutualWarInviteMgr", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("mutualWarInviteMgr", method), session);

            Assert.Equal(1, service.Calls);
            Assert.Equal(2, capture.Sent.Count);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Player_owned_structure_methods_use_a_named_per_session_budget()
    {
        long sessionId = 932;
        foreach (var method in new[] { "GetControlTowers", "UnanchorStructure" })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = sessionId++ };
            var registry = new ServiceRegistry();
            var service = new ExpensiveService("posMgr");
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

            await dispatcher.DispatchAsync(CallRequestPacket("posMgr", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("posMgr", method), session);

            Assert.Equal(1, service.Calls);
            Assert.Equal(2, capture.Sent.Count);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Vault_manager_balance_reads_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 931 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("vaultManager");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("vaultManager", "get_account_balance"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("vaultManager", "get_account_balance"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Character_selection_reads_and_mutations_use_a_named_per_session_budget()
    {
        long sessionId = 951;
        foreach (var method in new[] { "GetCharacterInfo", "GetCharacterSelectionData", "CreateCharacterWithDoll", "UpdateCharacterGender", "UpdateCharacterBloodline", "SelectCharacterID", "CancelCharacterDeletePrepare", "PrepareCharacterForDelete", "DeleteCharacter" })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = sessionId++ };
            var registry = new ServiceRegistry();
            var service = new ExpensiveService("charUnboundMgr");
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

            await dispatcher.DispatchAsync(CallRequestPacket("charUnboundMgr", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("charUnboundMgr", method), session);

            Assert.Equal(1, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Fact]
    public async Task Character_name_queries_use_a_named_per_session_budget()
    {
        long sessionId = 961;
        foreach (var method in new[] { "GetValidRandomName", "ValidateNameEx" })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = sessionId++ };
            var registry = new ServiceRegistry();
            var service = new ExpensiveService("charUnboundMgr");
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 2 };

            await dispatcher.DispatchAsync(CallRequestPacket("charUnboundMgr", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("charUnboundMgr", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("charUnboundMgr", method), session);

            Assert.Equal(2, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[2]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[2])));
        }
    }

    [Fact]
    public async Task Store_checkout_methods_use_a_named_per_session_budget()
    {
        long sessionId = 941;
        foreach (var (serviceName, method) in new[] { ("storeManager", "buy_offer"), ("FastCheckoutService", "BuyOffer") })
        {
            var capture = new Capture();
            var session = new ClientSession(capture) { SessionId = sessionId++ };
            var registry = new ServiceRegistry();
            var service = new ExpensiveService(serviceName);
            registry.Register(service);
            var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

            await dispatcher.DispatchAsync(CallRequestPacket(serviceName, method), session);
            await dispatcher.DispatchAsync(CallRequestPacket(serviceName, method), session);

            Assert.Equal(1, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
        }
    }

    [Theory]
    [InlineData("IsFastCheckoutEnabledForUser")]
    [InlineData("ClearFastCheckoutCacheForUser")]
    [InlineData("GetTestingConfiguration")]
    [InlineData("GetOffersForUser")]
    public async Task Fast_checkout_fixed_reads_use_a_named_per_session_budget(string method)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 933 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("FastCheckoutService");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };

        await dispatcher.DispatchAsync(CallRequestPacket("FastCheckoutService", method), session);
        await dispatcher.DispatchAsync(CallRequestPacket("FastCheckoutService", method), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Ship_kill_counter_reads_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 934 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("shipKillCounter");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };

        await dispatcher.DispatchAsync(CallRequestPacket("shipKillCounter", "GetItemKillCountPlayer"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("shipKillCounter", "GetItemKillCountPlayer"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Security_manager_read_uses_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 935 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("securityMgr");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };

        await dispatcher.DispatchAsync(CallRequestPacket("securityMgr", "get_modified_systems"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("securityMgr", "get_modified_systems"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Ping_service_uses_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 936 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("pingService");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };

        await dispatcher.DispatchAsync(CallRequestPacket("pingService", "Ping"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("pingService", "Ping"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Chat_authentication_reads_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 937 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("chatAuthenticationService");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };

        await dispatcher.DispatchAsync(CallRequestPacket("chatAuthenticationService", "GetAuthenticationToken"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("chatAuthenticationService", "GetAuthenticationToken"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Jump_timer_reads_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 938 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("jumpTimers");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };

        await dispatcher.DispatchAsync(CallRequestPacket("jumpTimers", "GetTimers"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("jumpTimers", "GetTimers"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Epic_arc_status_reads_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 939 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("epicArcStatus");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };

        await dispatcher.DispatchAsync(CallRequestPacket("epicArcStatus", "AgentHasEpicMissionsForCharacter"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("epicArcStatus", "AgentHasEpicMissionsForCharacter"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
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
        foreach (var method in new[] { "GetAlliance", "GetAlliancePublicInfo", "GetAllianceSovereigntyStructuresInfo", "GetRelationships", "SetRelationship", "GetCapitalSystemInfo", "SetCapitalSystem", "SetPrimeHour", "CancelCapitalSystemTransition", "UpdateAlliance", "DeclareWarAgainst", "PayBill", "GetBillBalance", "GetBills", "GetBillsReceivable", "GetApplications", "GetEveOwners", "GetRankedAlliances", "GetAllianceMembers", "GetMembers", "GetEmploymentRecord", "GetAllianceMembersOlderThan", "GetDaysInAlliance", "GetAllianceContacts", "GetContactList", "GetBulletins", "GetBulletinEntries" })
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

    [Fact]
    public async Task Client_stats_logging_uses_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 914 };
        var registry = new ServiceRegistry();
        var service = new EventLogBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("eventLog", "LogClientStats"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("eventLog", "LogClientStats"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Client_log_string_calls_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 915 };
        var registry = new ServiceRegistry();
        var service = new ClientLogBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };

        await dispatcher.DispatchAsync(CallRequestPacket("clientStatLogger", "LogString"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("clientStatLogger", "LogString"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Client_stacktrace_reports_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 921 };
        var registry = new ServiceRegistry();
        var service = new AlertBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };

        await dispatcher.DispatchAsync(CallRequestPacket("alert", "SendClientStackTraceAlert"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("alert", "SendClientStackTraceAlert"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Project_discovery_calls_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 922 };
        var registry = new ServiceRegistry();
        var service = new ProjectDiscoveryBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };

        await dispatcher.DispatchAsync(CallRequestPacket("ProjectDiscovery", "get_player_state"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("ProjectDiscovery", "get_player_state"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Banned_words_routes_use_named_per_session_budgets()
    {
        var capture = new Capture();
        var registry = new ServiceRegistry();
        var service = new BannedWordsBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };
        string[] methods = ["fetch_regexes", "check_words_allowed", "check_search_words_allowed"];

        foreach (var (method, index) in methods.Select((method, index) => (method, index)))
        {
            var session = new ClientSession(capture) { SessionId = 940 + index };
            var previousCalls = service.Calls;
            var allowedCalls = method == "fetch_regexes" ? 10 : 1;
            for (var callIndex = 0; callIndex <= allowedCalls; callIndex++)
                await dispatcher.DispatchAsync(CallRequestPacket("bannedwords", method), session);
            Assert.Equal(previousCalls + allowedCalls, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[^1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[^1])));
        }
    }

    [Fact]
    public async Task Air_npe_routes_use_named_per_session_budgets()
    {
        var capture = new Capture();
        var registry = new ServiceRegistry();
        var service = new AirNpeBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };
        string[] methods = ["is_air_npe_enabled", "get_air_npe_state", "skip_air_npe"];

        foreach (var (method, index) in methods.Select((method, index) => (method, index)))
        {
            var session = new ClientSession(capture) { SessionId = 950 + index };
            var previousCalls = service.Calls;
            var allowedCalls = method == "skip_air_npe" ? 1 : 10;
            for (var callIndex = 0; callIndex <= allowedCalls; callIndex++)
                await dispatcher.DispatchAsync(CallRequestPacket("air_npe", method), session);
            Assert.Equal(previousCalls + allowedCalls, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[^1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[^1])));
        }
    }

    [Fact]
    public async Task Authentication_routes_use_named_per_session_budgets()
    {
        var capture = new Capture();
        var registry = new ServiceRegistry();
        var service = new AuthenticationBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };
        string[] methods = ["AccruedTime", "AmUnderage", "SetLanguageID"];

        foreach (var (method, index) in methods.Select((method, index) => (method, index)))
        {
            var session = new ClientSession(capture) { SessionId = 960 + index };
            var previousCalls = service.Calls;
            var allowedCalls = method == "AmUnderage" ? 1 : 10;
            for (var callIndex = 0; callIndex <= allowedCalls; callIndex++)
                await dispatcher.DispatchAsync(CallRequestPacket("authentication", method), session);
            Assert.Equal(previousCalls + allowedCalls, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[^1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[^1])));
        }
    }

    [Fact]
    public async Task Info_gathering_routes_use_named_per_session_budgets()
    {
        var capture = new Capture();
        var registry = new ServiceRegistry();
        var service = new InfoGatheringBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };
        string[] methods = ["GetStateAndConfig", "LogInfoEventsFromClient"];

        foreach (var (method, index) in methods.Select((method, index) => (method, index)))
        {
            var session = new ClientSession(capture) { SessionId = 970 + index };
            var previousCalls = service.Calls;
            var allowedCalls = method == "LogInfoEventsFromClient" ? 1 : 10;
            for (var callIndex = 0; callIndex <= allowedCalls; callIndex++)
                await dispatcher.DispatchAsync(CallRequestPacket("infoGatheringMgr", method), session);
            Assert.Equal(previousCalls + allowedCalls, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[^1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[^1])));
        }
    }

    [Fact]
    public async Task Browser_lockdown_reads_use_named_per_session_budgets()
    {
        var capture = new Capture();
        var registry = new ServiceRegistry();
        var service = new BrowserLockdownBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };
        string[] methods = ["GetDefaultHomePage", "IsBrowserInLockdown", "GetFlaggedSitesHash", "GetFlaggedSitesList"];

        foreach (var (method, index) in methods.Select((method, index) => (method, index)))
        {
            var session = new ClientSession(capture) { SessionId = 980 + index };
            var previousCalls = service.Calls;
            await dispatcher.DispatchAsync(CallRequestPacket("browserLockdownSvc", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("browserLockdownSvc", method), session);
            Assert.Equal(previousCalls + 1, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[^1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[^1])));
        }
    }

    [Fact]
    public async Task Cache_build_reads_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var registry = new ServiceRegistry();
        var service = new CacheBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };
        var session = new ClientSession(capture) { SessionId = 990 };

        await dispatcher.DispatchAsync(CallRequestPacket("cache", "GetServerVersionAndBuild"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("cache", "GetServerVersionAndBuild"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Custom_agency_routes_use_named_per_session_budgets()
    {
        var capture = new Capture();
        var registry = new ServiceRegistry();
        var service = new CustomAgencyBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        foreach (var (method, index, allowedCalls) in new[] { ("get_content_data", 0, 10), ("warp_to_content", 1, 1) })
        {
            var session = new ClientSession(capture) { SessionId = 991 + index };
            var previousCalls = service.Calls;
            for (var callIndex = 0; callIndex <= allowedCalls; callIndex++)
                await dispatcher.DispatchAsync(CallRequestPacket("custom_agency_provider", method), session);
            Assert.Equal(previousCalls + allowedCalls, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[^1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[^1])));
        }
    }

    [Fact]
    public async Task Community_track_routes_use_named_per_session_budgets()
    {
        var capture = new Capture();
        var registry = new ServiceRegistry();
        var service = new CommunityTrackBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };
        string[] methods = ["get_instance_attributes_public", "change_qa_mode"];

        foreach (var (method, index) in methods.Select((method, index) => (method, index)))
        {
            var session = new ClientSession(capture) { SessionId = 993 + index };
            var previousCalls = service.Calls;
            await dispatcher.DispatchAsync(CallRequestPacket("communityTrackMgr", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("communityTrackMgr", method), session);
            Assert.Equal(previousCalls + 1, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[^1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[^1])));
        }
    }

    [Fact]
    public async Task Subscription_routes_use_named_per_session_budgets()
    {
        var capture = new Capture();
        var registry = new ServiceRegistry();
        var subscriptions = new SubscriptionBudgetService("subscriptionMgr", ["GetSubscriptionStatus", "GetSubscriptionInfo", "GetSubscriptionTime", "GetCloneGrade"]);
        var rewards = new SubscriptionBudgetService("loginRewardFacilities", ["get_my_clone_grade"]);
        registry.Register(subscriptions);
        registry.Register(rewards);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };
        var routes = new[]
        {
            ("subscriptionMgr", "GetSubscriptionStatus"),
            ("subscriptionMgr", "GetSubscriptionInfo"),
            ("subscriptionMgr", "GetSubscriptionTime"),
            ("subscriptionMgr", "GetCloneGrade"),
            ("loginRewardFacilities", "get_my_clone_grade"),
        };

        foreach (var ((serviceName, method), index) in routes.Select((route, index) => (route, index)))
        {
            var session = new ClientSession(capture) { SessionId = 995 + index };
            var service = serviceName == "subscriptionMgr" ? subscriptions : rewards;
            var previousCalls = service.Calls;
            await dispatcher.DispatchAsync(CallRequestPacket(serviceName, method), session);
            await dispatcher.DispatchAsync(CallRequestPacket(serviceName, method), session);
            Assert.Equal(previousCalls + 1, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[^1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[^1])));
        }
    }

    [Fact]
    public async Task Escalation_details_reads_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var registry = new ServiceRegistry();
        var service = new EscalationDetailsBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };
        var session = new ClientSession(capture) { SessionId = 997 };

        await dispatcher.DispatchAsync(CallRequestPacket("dungeonExplorationMgr", "GetMyEscalatingPathDetails"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("dungeonExplorationMgr", "GetMyEscalatingPathDetails"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Abyssal_spawn_reports_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var registry = new ServiceRegistry();
        var service = new AbyssalSpawnReportBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };
        var session = new ClientSession(capture) { SessionId = 998 };

        await dispatcher.DispatchAsync(CallRequestPacket("abyssal_content_manager", "generate_abyss_npc_spawn_report"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("abyssal_content_manager", "generate_abyss_npc_spawn_report"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Daily_alpha_injector_clock_uses_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 932 };
        var registry = new ServiceRegistry();
        var service = new ExpensiveService("alphaInjectorMgr");
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };

        await dispatcher.DispatchAsync(CallRequestPacket("alphaInjectorMgr", "GetNextAvailableInjection"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("alphaInjectorMgr", "GetNextAvailableInjection"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    private sealed class AbyssalSpawnReportBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "abyssal_content_manager";
        public bool HasMethod(string method) => method == "generate_abyss_npc_spawn_report";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class EscalationDetailsBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "dungeonExplorationMgr";
        public bool HasMethod(string method) => method == "GetMyEscalatingPathDetails";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class SubscriptionBudgetService(string name, string[] methods) : IService
    {
        public int Calls { get; private set; }
        public string Name => name;
        public bool HasMethod(string method) => methods.Contains(method, StringComparer.Ordinal);
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class CommunityTrackBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "communityTrackMgr";
        public bool HasMethod(string method) => method is "get_instance_attributes_public" or "change_qa_mode";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class CustomAgencyBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "custom_agency_provider";
        public bool HasMethod(string method) => method is "get_content_data" or "warp_to_content";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class CacheBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "cache";
        public bool HasMethod(string method) => method == "GetServerVersionAndBuild";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class BrowserLockdownBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "browserLockdownSvc";
        public bool HasMethod(string method) => method is "GetDefaultHomePage" or "IsBrowserInLockdown" or "GetFlaggedSitesHash" or "GetFlaggedSitesList";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class InfoGatheringBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "infoGatheringMgr";
        public bool HasMethod(string method) => method is "GetStateAndConfig" or "LogInfoEventsFromClient";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class AuthenticationBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "authentication";
        public bool HasMethod(string method) => method is "AccruedTime" or "AmUnderage" or "SetLanguageID";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class AirNpeBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "air_npe";
        public bool HasMethod(string method) => method is "is_air_npe_enabled" or "get_air_npe_state" or "skip_air_npe";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class BannedWordsBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "bannedwords";
        public bool HasMethod(string method) => method is "fetch_regexes" or "check_words_allowed" or "check_search_words_allowed";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    [Fact]
    public async Task Resource_wars_reads_use_named_per_session_budgets()
    {
        var capture = new Capture();
        var registry = new ServiceRegistry();
        var service = new ResourceWarsBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };
        string[] methods = ["get_closest_rw_stations", "solarsystem_contains_rw_instances"];

        foreach (var (method, index) in methods.Select((method, index) => (method, index)))
        {
            var session = new ClientSession(capture) { SessionId = 923 + index };
            var previousCalls = service.Calls;
            await dispatcher.DispatchAsync(CallRequestPacket("RWManager", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("RWManager", method), session);
            Assert.Equal(previousCalls + 1, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[^1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[^1])));
        }
    }

    private sealed class ResourceWarsBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "RWManager";
        public bool HasMethod(string method) => method is "get_closest_rw_stations" or "solarsystem_contains_rw_instances";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class ProjectDiscoveryBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "ProjectDiscovery";
        public bool HasMethod(string method) => method == "get_player_state";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class AlertBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "alert";
        public bool HasMethod(string method) => method == "SendClientStackTraceAlert";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    [Fact]
    public async Task Character_energy_reads_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 916 };
        var registry = new ServiceRegistry();
        var service = new CharacterEnergyBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 1 };

        await dispatcher.DispatchAsync(CallRequestPacket("characterEnergyMgr", "GetMyEnergyState"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("characterEnergyMgr", "GetMyEnergyState"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Paper_doll_batch_reads_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 917 };
        var registry = new ServiceRegistry();
        var service = new PaperDollBatchBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("paperDollServer", "GetPaperDollDataBatch"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("paperDollServer", "GetPaperDollDataBatch"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Paper_doll_single_reads_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 918 };
        var registry = new ServiceRegistry();
        var service = new PaperDollSingleBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 2 };

        await dispatcher.DispatchAsync(CallRequestPacket("paperDollServer", "GetMyPaperDollData"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("paperDollServer", "GetPaperDollData"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("paperDollServer", "GetPaperDollPortraitDataFor"), session);

        Assert.Equal(2, service.Calls);
        Assert.Equal(3, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[2]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[2])));
    }

    [Fact]
    public async Task Achievement_state_updates_use_a_named_per_session_budget()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 919 };
        var registry = new ServiceRegistry();
        var service = new AchievementTrackerBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };

        await dispatcher.DispatchAsync(CallRequestPacket("achievementTrackerMgr", "UpdateClientAchievmentsAndCounters"), session);
        await dispatcher.DispatchAsync(CallRequestPacket("achievementTrackerMgr", "UpdateClientAchievmentsAndCounters"), session);

        Assert.Equal(1, service.Calls);
        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[1])));
    }

    [Fact]
    public async Task Abyssal_lifecycle_rpcs_use_named_per_session_budgets()
    {
        var capture = new Capture();
        var registry = new ServiceRegistry();
        var service = new AbyssalBudgetService();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry, () => FixedNowMs) { MaxExpensiveRpcUnitsPerMinute = 10 };
        string[] methods = [
            "ClientIsReady", "AbyssalEntranceDeployment", "AbyssalEntranceGateActivation",
            "AbyssalGateActivation", "AbyssalEndGateActivation",
        ];

        foreach (var method in methods)
        {
            var session = new ClientSession(capture) { SessionId = 920 + Array.IndexOf(methods, method) };
            var previousCalls = service.Calls;
            await dispatcher.DispatchAsync(CallRequestPacket("abyssalMgr", method), session);
            await dispatcher.DispatchAsync(CallRequestPacket("abyssalMgr", method), session);
            Assert.Equal(previousCalls + 1, service.Calls);
            Assert.Equal(ErrorResponseCommand, CommandOf(capture.Sent[^1]));
            Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(capture.Sent[^1])));
        }
    }

    private sealed class AbyssalBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "abyssalMgr";
        public bool HasMethod(string method) => method is "ClientIsReady" or "AbyssalEntranceDeployment" or
            "AbyssalEntranceGateActivation" or "AbyssalGateActivation" or "AbyssalEndGateActivation";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class AchievementTrackerBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "achievementTrackerMgr";
        public bool HasMethod(string method) => method is "HasEverWarped" or "GetCompletedAchievementsAndClientEventCount" or "UpdateClientAchievmentsAndCounters";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class PaperDollSingleBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "paperDollServer";
        public bool HasMethod(string method) => method is "GetMyPaperDollData" or "GetPaperDollData" or "GetPaperDollPortraitDataFor";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class PaperDollBatchBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "paperDollServer";
        public bool HasMethod(string method) => method == "GetPaperDollDataBatch";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class CharacterEnergyBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "characterEnergyMgr";
        public bool HasMethod(string method) => method == "GetMyEnergyState";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class ClientLogBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "clientStatLogger";
        public bool HasMethod(string method) => method == "LogString";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class EventLogBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "eventLog";
        public bool HasMethod(string method) => method == "LogClientStats";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
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

    private sealed class ConfigLookupBudgetService : IService
    {
        public int Calls { get; private set; }
        public string Name => "config";
        public bool HasMethod(string method) => method is "GetMultiOwnersEx" or "GetMultiCorpTickerNamesEx" or "GetMultiAllianceShortNamesEx" or "GetMultiLocationsEx";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    private sealed class RpcBudgetScenarioService(string name) : IService
    {
        public string Name => name;

        public bool HasMethod(string method) => true;

        public ValueTask<PyValue> CallAsync(ServiceCall call) => ValueTask.FromResult<PyValue>(PyNone.Instance);
    }

    private sealed class ExpensiveService(string name = "marketProxy") : IService
    {
        public int Calls { get; private set; }

        public string Name => name;

        public bool HasMethod(string method) => method is "ActivateSkinLicense" or "AddAllianceContact" or "AddBulletin" or "AddCorporateContact" or "AddCorporation" or "AddFleetFinderAdvert" or "AddMembers" or "ApplySkinToShip" or "ApplyToJoinFleet" or "AssignLabels" or "AuditMember" or "BroadcastToBubble" or "BroadcastToSystem" or "BuyMultipleItems" or "BuyOffer" or "CancelCapitalSystemTransition" or "CancelCharacterDeletePrepare" or "CancelJob" or "CharGetTransactions" or "CharPayBill" or "CheckMyDockingAccessToStructures" or "CombineSkillInjector" or "CompleteJob" or "CompleteManyJobs" or "CorpGetTransactions" or "Create" or "CreateAlliance" or "CreateCharacterWithDoll" or "CreateLabel" or "DeclareExecutorSupport" or "DeclareWarAgainst" or "Delete" or "DeleteBulletin" or "DeleteCharacter" or "DeleteLabel" or "DeleteMembers" or "DeleteTitle" or "EditAllianceContact" or "EditContactsRelationshipID" or "EditCorporateContact" or "EditLabel" or "ExecuteActions" or "ExtractSkills" or "ForceLeaveFleet" or "GMExpireSkinLicense" or "GetAggressionSettingsForCorps" or "GetAllCorpMedals" or "GetAlliance" or "GetAllianceApplications" or "GetAllianceContacts" or "GetAllianceMembers" or "GetAllianceMembersOlderThan" or "GetAlliancePublicInfo" or "GetAllianceSovereigntyStructuresInfo" or "GetApplications" or "GetAssetInventory" or "GetAssetInventoryForLocation" or "GetAutomaticPaySettings" or "GetAvailableFleetAds" or "GetAverageMarketPrices" or "GetBillBalance" or "GetBills" or "GetBillsReceivable" or "GetBulletinEntries" or "GetBulletins" or "GetCapitalSystemInfo" or "GetCharEscrow" or "GetCharOrders" or "GetCharacterCreationDate" or "GetCharacterDescription" or "GetCharacterInfo" or "GetCharacterSelectionData" or "GetContactList" or "GetContractListForOwner" or "GetControlTowers" or "GetCorporateContacts" or "GetCorporation" or "GetCorporationBills" or "GetCorporationBillsReceivable" or "GetCorporationIDForCharacter" or "GetCorporationOrders" or "GetCorporations" or "GetCorporationsWithOffices" or "GetDaysInAlliance" or "GetDiminishedSpFromInjectors" or "GetEmployementRecordAndCharacterTransfers" or "GetEmploymentRecord" or "GetEmptyOfficeCount" or "GetEveOwners" or "GetFittings" or "GetGroup" or "GetGroupLogs" or "GetGroupsMany" or "GetGuests" or "GetHistoryForManyTypeIDs" or "GetImpoundReleasePrice" or "GetJob" or "GetJobCounts" or "GetJobsByOwner" or "GetKillRights" or "GetLicencedSkins" or "GetMarketOrderHistory" or "GetMedalsReceived" or "GetMembers" or "GetMembersForMultipleGroups" or "GetMultiAllianceShortNamesEx" or "GetMultiCorpTickerNamesEx" or "GetMultiLocationsEx" or "GetMultiOwnersEx" or "GetMyApplications" or "GetMyCharacterStructures" or "GetMyCorporationInfo" or "GetMyCorporationStructures" or "GetMyDockableStructures" or "GetMyFleetFinderAdvert" or "GetMyGroups" or "GetMyOldApplications" or "GetMyOpenInvitations" or "GetNewPriceHistory" or "GetNextAvailableInjection" or "GetNumberOfPotentialCEOs" or "GetOldApplications" or "GetOldPriceHistory" or "GetOffersForUser" or "GetTestingConfiguration" or "ClearFastCheckoutCacheForUser" or "IsFastCheckoutEnabledForUser" or "GetItemKillCountPlayer" or "get_modified_systems" or "Ping" or "GetAuthenticationToken" or "GetTimers" or "GetOrders" or "AgentHasEpicMissionsForCharacter" or "GetOrganizationInfoForCharacters" or "GetPendingInvitesForSession" or "GetPlexBest" or "GetPlexHistory" or "GetPlexNewPriceHistory" or "GetPlexOldPriceHistory" or "GetPlexOrders" or "GetPrivateInfo" or "GetPublicInfo" or "GetPublicInfo3" or "GetRankedAlliances" or "GetRecentShipKillsAndLosses" or "GetRecipientsOfMedal" or "GetRecruitmentAd" or "GetRecruitmentAdsByCorpID" or "GetRecruitmentAdsForCorporation" or "GetRepairQuotes" or "RepairItemsInStation" or "GetRelationships" or "GetSecurityStatusTransactions" or "GetSuggestedAllianceShortNames" or "GetTaxRate" or "GetValidRandomName" or "GetWars" or "GiveMedalToCharacters" or "GiveSkin" or "HasCorpImpoundedItems" or "Init" or "InjectSkillIntoBrain" or "InjectSkillpoints" or "InsertApplication" or "InsertInvitation" or "InstallJob" or "Invite" or "IsCorpInvitesBlockedPlayer" or "KickOutMember" or "MassInvite" or "PayBill" or "PayCorporationBill" or "PlaceMultiSellOrder" or "PrepareCharacterForDelete" or "PrimeOfficeItem" or "RegisterNewAcceptStructureSettings" or "RegisterNewAggressionSettings" or "RegisterNewCorpMailRestrictionSettings" or "RemoveAllianceContacts" or "RemoveFleetFinderAdvert" or "RemoveLabels" or "RemoveSkin" or "RepackageItems" or "ResignFromCEO" or "RespondToInviteByPlayer" or "SearchAssets" or "SelectCharacterID" or "SendAutomaticPaySettings" or "SendBroadcast" or "SendInviteByPlayer" or "SetAccountKey" or "SetCapitalSystem" or "SetCharacterDescription" or "SetInvitesBlockedByPlayer" or "SetMedalStatus" or "SetPrimeHour" or "SetRelationship" or "SetStructureReinforceDefault" or "SplitSkillInjector" or "TrashImpoundedOffice" or "UnanchorStructure" or "UnrentOffice" or "UpdateAdvertAllowedEntities" or "UpdateAdvertInfo" or "UpdateAlliance" or "UpdateBulletin" or "UpdateBulletinOrder" or "UpdateCharacterBloodline" or "UpdateCharacterGender" or "UpdateCorporation" or "UpdateCorporationAbilities" or "UpdateDescription" or "UpdateDivisionNames" or "UpdateFleetAdvertWithNewLeader" or "UpdateLogo" or "UpdateMembershipTypes" or "UpdateName" or "UpdateSettings" or "UpdateTitle" or "ValidateNameEx" or "WithdrawInviteByPlayer" or "PerformKiringServerSideAuthenticationFromCode" or "GetSAuthAid" or "PlaceKiringOrder" or "SetMotd" or "CreatePlayerOwnedChannel" or "AddCharacterToChannel" or "GrantChannelOwnership" or "ResyncSystemChannelAccess" or "TrackOfferDelivered" or "TrackOfferSeen" or "TrackOfferBuyPress" or "TrackOfferClosed" or "TrackOfferPurchased" or "TrackOfferPurchasedSeen" or "buy_offer" or "get_account_balance";
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

        public bool HasMethod(string method) => method is "GetMyCorporationsOffices" or "HasCorpImpoundedItems" or "GetImpoundReleasePrice" or "RentOffice" or "UnrentOffice" or "GetItemsFromImpound" or "TrashImpoundedOffice";

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

        public bool HasMethod(string method) => method is "MachoBindObject" or "GetInventory" or "GetInventoryFromId" or "ListIncludingContainers" or "GetAssetWorth" or "ListStations" or "ListStationItems" or "GetCapacity" or "MultiAdd" or "MultiMerge" or "FitFitting";

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
