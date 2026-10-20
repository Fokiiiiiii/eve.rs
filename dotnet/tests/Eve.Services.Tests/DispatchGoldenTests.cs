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

        var fleetCapture = new Capture();
        var fleetSession = new ClientSession(fleetCapture) { SessionId = 908 };
        await dispatcher.DispatchAsync(CallRequestPacket("fleetObject", "SendBroadcast"), fleetSession);
        await dispatcher.DispatchAsync(CallRequestPacket("fleetObject", "SendBroadcast"), fleetSession);
        Assert.Equal(1, fleetService.Calls);
        Assert.Equal(ErrorResponseCommand, CommandOf(fleetCapture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(fleetCapture.Sent[1])));

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
        Assert.Equal(1, characterService.Calls);
        Assert.Equal(ErrorResponseCommand, CommandOf(characterCapture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(characterCapture.Sent[1])));

        var corporationCapture = new Capture();
        var corporationSession = new ClientSession(corporationCapture) { SessionId = 905 };
        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", "GetEveOwners"), corporationSession);
        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", "GetMemberIDsByQuery"), corporationSession);
        await dispatcher.DispatchAsync(CallRequestPacket("corpRegistry", "PayoutDividend"), corporationSession);
        Assert.Equal(1, corporationService.Calls);
        Assert.Equal(3, corporationCapture.Sent.Count);
        Assert.Equal(ErrorResponseCommand, CommandOf(corporationCapture.Sent[1]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(corporationCapture.Sent[1])));
        Assert.Equal(ErrorResponseCommand, CommandOf(corporationCapture.Sent[2]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(corporationCapture.Sent[2])));

        var inventoryCapture = new Capture();
        var inventorySession = new ClientSession(inventoryCapture) { SessionId = 902 };
        const string inventoryObjectId = "N=901:902";
        registry.RegisterBoundObject(inventoryObjectId, inventoryService);
        await dispatcher.DispatchAsync(CallRequestPacket("invbroker", "GetInventoryFromId"), inventorySession);
        for (var index = 0; index < 4; index++)
            await dispatcher.DispatchAsync(BoundCallRequestPacket(inventoryObjectId, "ListIncludingContainers"), inventorySession);
        await dispatcher.DispatchAsync(CallRequestPacket("invbroker", "GetInventoryFromId"), inventorySession);
        Assert.Equal(5, inventoryService.Calls);
        Assert.Equal(ErrorResponseCommand, CommandOf(inventoryCapture.Sent[5]));
        Assert.Contains("SessionRpcBudgetExceeded", System.Text.Encoding.Latin1.GetString(Convert.FromHexString(inventoryCapture.Sent[5])));

        now += 60_000;
        await dispatcher.DispatchAsync(CallRequestPacket("marketProxy", "GetOrders"), session);
        Assert.Equal(4, service.Calls);
        Assert.Equal(7, CommandOf(capture.Sent[2]));
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

        public bool HasMethod(string method) => method is "GetOrders" or "BuyMultipleItems" or "GetHistoryForManyTypeIDs" or "GetAllianceMembers" or "GetRankedAlliances" or "SendBroadcast";

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

    private sealed class CharacterKillmailBudgetService : IService
    {
        public int Calls { get; private set; }

        public string Name => "charMgr";

        public bool HasMethod(string method) => method == "GetRecentShipKillsAndLosses";

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

        public bool HasMethod(string method) => method is "GetEveOwners" or "GetMemberIDsByQuery" or "GetMembersPaged" or "PayoutDividend";

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

        public bool HasMethod(string method) => method is "GetInventoryFromId" or "ListIncludingContainers";

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
