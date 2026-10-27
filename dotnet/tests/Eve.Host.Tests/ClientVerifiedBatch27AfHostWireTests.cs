using System.Reflection;
using System.Runtime.CompilerServices;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Browser_lockdown_reads_match_client_consumed_values_through_host()
    {
        Start();
        var (front, open) = await OpenFrontAsync();

        Assert.Equal("about:blank",
            WireText(await HostRpcAsync(front, open, 1, "browserLockdownSvc", "GetDefaultHomePage")));
        Assert.Same(PyBool.False,
            await HostRpcAsync(front, open, 2, "browserLockdownSvc", "IsBrowserInLockdown"));
        Assert.Equal("d751713988987e9331980363e24189ce",
            WireText(await HostRpcAsync(front, open, 3, "browserLockdownSvc", "GetFlaggedSitesHash")));
        Assert.Empty(Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 4, "browserLockdownSvc", "GetFlaggedSitesList")).Items);
    }

    [Fact]
    public async Task Banned_word_queries_keep_the_client_wire_shapes_through_host()
    {
        Start();
        var (front, open) = await OpenFrontAsync();

        Assert.Empty(Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 1, "bannedwords", "fetch_regexes", new PyStr("blocked"))).Items);
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 2, "bannedwords", "check_words_allowed", new PyStr("ship name")));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 3, "bannedwords", "check_search_words_allowed", new PyStr("search")));
    }

    [Fact]
    public async Task Alert_beancount_and_stacktrace_reply_pairs_cross_host_wire()
    {
        Start();
        var (front, open) = await OpenFrontAsync();

        var beanCount = Assert.IsType<PyTuple>(
            await HostRpcAsync(front, open, 1, "alert", "BeanCount", new PyLong(9_000_001)));
        Assert.Equal(2, beanCount.Items.Count);
        Assert.Same(PyNone.Instance, beanCount.Items[0]);
        Assert.Same(PyNone.Instance, beanCount.Items[1]);

        var stackTrace = Assert.IsType<PyTuple>(await HostRpcAsync(front, open, 2, "alert", "SendClientStackTraceAlert",
            new PyTuple([new PyLong(9_000_001), new PyStr("stack-key")]),
            new PyStr("trace text"), new PyStr("Error"), new PyLong(9_000_002)));
        Assert.Equal(2, stackTrace.Items.Count);
        Assert.IsType<PyLong>(stackTrace.Items[0]);
        Assert.Equal("D", WireText(stackTrace.Items[1]));
    }

    [Fact]
    public async Task Achievement_maps_and_client_updates_cross_host_wire_and_round_trip()
    {
        const long characterId = 42;
        var persisted = new AchievementTrackerPersistedState(
            new PyDict([Entry("client_openMap", new PyLong(3))]),
            new PyDict([Entry(9, new PyLong(134273341290000000))]));
        long? savedCharacterId = null;
        var writeCount = 0;
        var service = new AchievementTrackerMgrService(
            readState: (_, _) => ValueTask.FromResult<AchievementTrackerPersistedState?>(persisted),
            writeState: (id, completed, events, _) =>
            {
                savedCharacterId = id;
                writeCount++;
                persisted = new AchievementTrackerPersistedState(events, completed);
                return ValueTask.CompletedTask;
            });
        Start(configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyInt(characterId)));
        var (front, open) = await OpenFrontAsync();

        var state = Assert.IsType<PyDict>(
            await HostRpcAsync(front, open, 1, "achievementTrackerMgr", "GetCompletedAchievementsAndClientEventCount"));
        Assert.Equal(new PyLong(3), DictField(DictField(state, "eventDict"), "client_openMap"));
        Assert.Equal(new PyLong(134273341290000000), DictField(DictField(state, "completedDict"), 9));

        var completedUpdate = new PyDict([Entry(15, new PyLong(134273341290000123))]);
        var eventUpdate = new PyDict([Entry("client_dock", new PyLong(5))]);
        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 2, "achievementTrackerMgr",
            "UpdateClientAchievmentsAndCounters", completedUpdate, eventUpdate));
        Assert.Equal(characterId, savedCharacterId);

        var updatedState = Assert.IsType<PyDict>(
            await HostRpcAsync(front, open, 3, "achievementTrackerMgr", "GetCompletedAchievementsAndClientEventCount"));
        Assert.Equal(new PyLong(5), DictField(DictField(updatedState, "eventDict"), "client_dock"));
        Assert.Equal(new PyLong(134273341290000123), DictField(DictField(updatedState, "completedDict"), 15));

        var invalidEventUpdate = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyBytes(new byte[] { 0xff }), new PyLong(6)),
        ]);
        var error = Assert.IsType<PyObjectEx>(await HostErrorRpcAsync(front, open, 4, "achievementTrackerMgr",
            "UpdateClientAchievmentsAndCounters", new PyDict([]), invalidEventUpdate));
        var errorArgs = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(error.Header).Items[1]).Items;
        Assert.Equal("ACHIEVEMENT_STATE_INVALID", WireText(errorArgs[0]));
        Assert.Equal(1, writeCount);
    }

    [Fact]
    public async Task Abyssal_player_calls_use_the_registered_host_handler_and_error_envelope()
    {
        var compositionType = typeof(HostServer).Assembly.GetType("Eve.Host.CharacterServicesComposition")!;
        var composition = RuntimeHelpers.GetUninitializedObject(compositionType);
        var method = compositionType.GetMethod("HandleAbyssalRpcAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handler = (Func<ServiceCall, CancellationToken, ValueTask<PyValue>>)method.CreateDelegate(
            typeof(Func<ServiceCall, CancellationToken, ValueTask<PyValue>>), composition);
        Start(configure: registry => registry.Register(new AbyssalMgrService(handler)));
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 1, "abyssalMgr", "ClientIsReady"));
        AssertAbyssError(await HostErrorRpcAsync(front, open, 2, "abyssalMgr",
            "AbyssalEntranceDeployment", new PyLong(900_001), new PyInt(1)), 7);
        AssertAbyssError(await HostErrorRpcAsync(front, open, 3, "abyssalMgr",
            "AbyssalEntranceGateActivation", new PyLong(900_002)), 7);
    }

    private async Task<PyValue> HostErrorRpcAsync(
        TestFront front, SessionOpen open, long callId, string service, string method, params PyValue[] args)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, callId, service, method, new PyTuple(args), TransportKwargs()))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(send.Payload);
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
        Assert.Equal(MachoMessage.ErrorResponse, packet.Type);
        var response = Assert.IsType<PyObject>(decoded);
        var fields = Assert.IsType<PyTuple>(response.Args).Items;
        var responsePayload = Assert.IsType<PyTuple>(fields[4]);
        return Assert.IsType<PySubStream>(
            Assert.IsType<PyTuple>(responsePayload.Items[2]).Items[0]).Decoded!;
    }

    private static void AssertAbyssError(PyValue value, int errorCode)
    {
        var error = Assert.IsType<PyObjectEx>(value);
        Assert.Equal(ObjectExKind.Ex1, error.Kind);
        var fields = Assert.IsType<PyTuple>(error.Header).Items;
        var userError = Assert.IsType<PyTuple>(fields[1]).Items;
        Assert.Equal("AbyssError", WireText(userError[0]));
        var errors = Assert.IsType<PyList>(DictField(Assert.IsType<PyDict>(userError[1]), "errors"));
        var details = Assert.IsType<PyTuple>(Assert.Single(errors.Items)).Items;
        Assert.Equal(errorCode, WireInteger(details[0]));
        Assert.Empty(Assert.IsType<PyTuple>(details[1]).Items);
    }

    private static KeyValuePair<PyValue, PyValue> Entry(string key, PyValue value) =>
        new(new PyStr(key), value);

    private static KeyValuePair<PyValue, PyValue> Entry(long key, PyValue value) =>
        new(new PyInt(key), value);

    private static PyValue DictField(PyValue value, string key) =>
        Assert.IsType<PyDict>(value).Entries.Single(entry => WireText(entry.Key) == key).Value;

    private static PyValue DictField(PyValue value, long key) =>
        Assert.IsType<PyDict>(value).Entries.Single(entry => WireInteger(entry.Key) == key).Value;
}
