using Eve.Marshal;

namespace Eve.Services.Core.Tests;

public sealed class LoginSupportRpcTests
{
    [Fact]
    public void Core_registers_all_client_authentication_and_support_Rpcs()
    {
        var services = CoreServices.Create(new NoAccounts(), () => 0);

        AssertRegistered("authentication", "AccruedTime", "AmUnderage", "SetLanguageID");
        AssertRegistered("infoGatheringMgr", "GetStateAndConfig", "LogInfoEventsFromClient");
        AssertRegistered("eveguard_report", "heartbeat_started", "heartbeat_stopped");

        void AssertRegistered(string name, params string[] methods)
        {
            var service = Assert.Single(services, candidate => candidate.Name == name);
            Assert.All(methods, method => Assert.True(service.HasMethod(method), $"{name}.{method}"));
        }
    }

    [Fact]
    public async Task Authentication_matches_default_shapes_and_updates_the_live_session_language()
    {
        var service = new AuthenticationRpcService();
        var session = new ClientSession();

        Assert.Empty(Assert.IsType<PyList>(await ((IService)service).CallAsync(Call("AccruedTime", session))).Items);
        Assert.Same(PyBool.False, await ((IService)service).CallAsync(Call("AmUnderage", session, new PyStr("jwt"))));
        Assert.Same(PyNone.Instance, await ((IService)service).CallAsync(Call("SetLanguageID", session, new PyStr("  ja  "))));
        AssertSessionLanguage(session, "ja");
        Assert.Same(PyNone.Instance, await ((IService)service).CallAsync(Call("SetLanguageID", session, new PyStr("  "))));
        AssertSessionLanguage(session, "EN");
    }

    [Fact]
    public async Task Authentication_backends_receive_the_full_call_and_preserve_the_accrued_wire_value()
    {
        ServiceCall? accruedCall = null;
        ServiceCall? underageCall = null;
        var rows = new PyList([new PyTuple([new PyInt(17), new PyInt(23)])]);
        var service = new AuthenticationRpcService(
            accruedTime: (call, _) =>
            {
                accruedCall = call;
                return ValueTask.FromResult<PyValue>(rows);
            },
            amUnderage: (call, _) =>
            {
                underageCall = call;
                return ValueTask.FromResult(true);
            });
        var session = new ClientSession();
        var accruedRequest = Call("AccruedTime", session);
        var jwt = new PyStr("login-jwt");
        var underageRequest = Call("AmUnderage", session, jwt);

        Assert.Same(rows, await ((IService)service).CallAsync(accruedRequest));
        Assert.Same(accruedRequest, accruedCall);
        Assert.Same(PyBool.True, await ((IService)service).CallAsync(underageRequest));
        Assert.Same(underageRequest, underageCall);
        Assert.Same(jwt, underageCall!.Arg(0));
    }

    [Fact]
    public async Task Info_gathering_matches_keyval_and_status_dictionary_shapes_and_allows_backend_wiring()
    {
        var session = new ClientSession();
        var defaultService = new InfoGatheringMgrService();
        var expectedConfig = Eve.Services.Py.KeyVal(
            ("isEnabled", new PyInt(0)),
            ("infoTypes", new PyList([])),
            ("clientWorkerInterval", new PyInt(0)),
            ("infoTypesOncePerRun", new PyList([])),
            ("infoTypeAggregates", new PyDict([])),
            ("infoTypeParameters", new PyDict([])));
        Assert.Equal(expectedConfig, await ((IService)defaultService).CallAsync(Call("GetStateAndConfig", session)));
        Assert.Empty(Assert.IsType<PyDict>(await ((IService)defaultService).CallAsync(Call("LogInfoEventsFromClient", session))).Entries);

        ServiceCall? stateCall = null;
        ServiceCall? logCall = null;
        PyValue? loggedEvents = null;
        var customConfig = new PyStr("configured");
        var statuses = new PyDict([new(new PyStr("event-1"), new PyInt(0))]);
        var service = new InfoGatheringMgrService(
            getStateAndConfig: (call, _) =>
            {
                stateCall = call;
                return ValueTask.FromResult<PyValue>(customConfig);
            },
            logInfoEventsFromClient: (call, events, _) =>
            {
                logCall = call;
                loggedEvents = events;
                return ValueTask.FromResult<PyValue>(statuses);
            });
        var stateRequest = Call("GetStateAndConfig", session);
        var events = new PyDict([new(new PyStr("entries"), new PyList([new PyStr("event")]))]);
        var logRequest = Call("LogInfoEventsFromClient", session, events);

        Assert.Same(customConfig, await ((IService)service).CallAsync(stateRequest));
        Assert.Same(stateRequest, stateCall);
        Assert.Same(statuses, await ((IService)service).CallAsync(logRequest));
        Assert.Same(logRequest, logCall);
        Assert.Same(events, loggedEvents);
    }

    [Fact]
    public async Task Eveguard_heartbeats_report_lifecycle_events_and_return_none()
    {
        var events = new List<string>();
        ServiceCall? lastCall = null;
        var service = new EveguardReportService((eventType, call, _) =>
        {
            events.Add(eventType);
            lastCall = call;
            return ValueTask.CompletedTask;
        });
        var session = new ClientSession();
        var started = Call("heartbeat_started", session);
        var stopped = Call("heartbeat_stopped", session);

        Assert.Same(PyNone.Instance, await ((IService)service).CallAsync(started));
        Assert.Same(PyNone.Instance, await ((IService)service).CallAsync(stopped));
        Assert.Equal(["started", "stopped"], events);
        Assert.Same(stopped, lastCall);
    }

    [Fact]
    public void Core_composition_accepts_callback_wired_service_instances()
    {
        var authentication = new AuthenticationRpcService(amUnderage: (_, _) => ValueTask.FromResult(true));
        var infoGathering = new InfoGatheringMgrService();
        var eveguard = new EveguardReportService();
        var services = CoreServices.Create(
            new NoAccounts(), () => 0,
            authentication: authentication,
            infoGatheringMgr: infoGathering,
            eveguardReport: eveguard);

        Assert.Same(authentication, Assert.Single(services, service => service.Name == "authentication"));
        Assert.Same(infoGathering, Assert.Single(services, service => service.Name == "infoGatheringMgr"));
        Assert.Same(eveguard, Assert.Single(services, service => service.Name == "eveguard_report"));
    }

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session,
    };

    private static void AssertSessionLanguage(ClientSession session, string expected)
    {
        foreach (var key in new[] { "languageID", "languageId", "languageid", "userLanguageID" })
            Assert.Equal(new PyStr(expected), session.Get(key));
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => OmegaLicenses.DefaultOmegaExpiryText;
    }
}
