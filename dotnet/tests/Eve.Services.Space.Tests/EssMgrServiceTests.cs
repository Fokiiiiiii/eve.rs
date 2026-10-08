using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;

namespace Eve.Services.Space.Tests;

public sealed class EssMgrServiceTests
{
    private const long SolarSystemId = 30000142;
    private const long CharacterId = 90000001;

    [Fact]
    public async Task Unwired_service_advertises_all_reference_methods_and_returns_empty_shapes()
    {
        var service = new EssMgrService();
        var session = Session();
        string[] methods =
        [
            "GetDataForClientSolarSystem",
            "IsClientLinkedToReserveBank",
            "AttemptLinkToMainBank",
            "AttemptLinkToReserveBank",
            "RequestMainBankUnlink",
            "RequestReserveBankUnlink",
            "RequestUnlockReserveBank",
            "GetMainBankTheftsForClientSolarSystem",
            "GetReserveBankTheftsForClientSolarSystem",
        ];
        var rpc = (IService)service;
        Assert.All(methods, method => Assert.True(rpc.HasMethod(method), method));
        Assert.False(rpc.HasMethod("Unknown"));

        Assert.Same(PyNone.Instance, await Call(rpc, session, "GetDataForClientSolarSystem"));
        Assert.Same(PyBool.False, await Call(rpc, session, "IsClientLinkedToReserveBank"));
        Assert.Equal(new PyList([]), await Call(rpc, session, "GetMainBankTheftsForClientSolarSystem"));
        Assert.Equal(new PyList([]), await Call(rpc, session, "GetReserveBankTheftsForClientSolarSystem"));
        foreach (var method in methods.Skip(2).Take(5))
            Assert.Same(PyNone.Instance, await Call(rpc, session, method, new PyInt(55914)));
    }

    [Fact]
    public async Task State_and_histories_match_the_reference_shapes_and_use_the_requested_system()
    {
        var state = State();
        var requestedSystems = new List<long>();
        var service = new EssMgrService(new EssMgrCallbacks(
            (systemId, _) =>
            {
                requestedSystems.Add(systemId);
                return ValueTask.FromResult<EssMgrSystemData?>(state);
            }));
        var rpc = (IService)service;
        var session = Session();

        var payload = Assert.IsType<PyDict>(await Call(rpc, session, "GetDataForClientSolarSystem"));
        Assert.Equal(
            ["essID", "beaconID", "typeID", "solarSystemID", "currentOutput", "mainValue", "reserveValue", "mainBankLink", "reserveBankLastPulseInitiated", "reserveBankPulsesRemaining", "reserveBankPulsesTotal", "reserveBankActiveLinks"],
            payload.Entries.Select(entry => Assert.IsType<PyStr>(entry.Key).Value));
        Assert.Equal(new PyInt(900000000), Value(payload, "essID"));
        Assert.Equal(new PyInt(900000000), Value(payload, "beaconID"));
        Assert.Equal(new PyInt(55914), Value(payload, "typeID"));
        Assert.Equal(new PyInt(SolarSystemId), Value(payload, "solarSystemID"));
        Assert.Equal(new PyReal(17.25), Value(payload, "currentOutput"));
        Assert.Equal(new PyReal(1234.567), Value(payload, "mainValue"));
        Assert.Equal(new PyInt(-98), Value(payload, "reserveValue"));
        Assert.Equal(new PyLong(1337), Value(payload, "reserveBankLastPulseInitiated"));
        Assert.Equal(new PyInt(3), Value(payload, "reserveBankPulsesRemaining"));
        Assert.Equal(new PyInt(9), Value(payload, "reserveBankPulsesTotal"));
        Assert.Equal(new PyInt(2), Value(payload, "reserveBankActiveLinks"));

        var link = Assert.IsType<PyDict>(Value(payload, "mainBankLink"));
        Assert.Equal(["linkID", "characterID", "startedAt", "completesAt"],
            link.Entries.Select(entry => Assert.IsType<PyStr>(entry.Key).Value));
        Assert.Equal(new PyStr("main-link"), Value(link, "linkID"));
        Assert.Equal(new PyInt(CharacterId), Value(link, "characterID"));
        Assert.Equal(new PyLong(1001), Value(link, "startedAt"));
        Assert.Equal(new PyLong(2002), Value(link, "completesAt"));

        var mainHistory = Assert.IsType<PyList>(await Call(rpc, session, "GetMainBankTheftsForClientSolarSystem"));
        var reserveHistory = Assert.IsType<PyList>(await Call(rpc, session, "GetReserveBankTheftsForClientSolarSystem", new PyInt(SolarSystemId + 1)));
        Assert.Equal(state.MainBankThefts, mainHistory.Items);
        Assert.Equal(state.ReserveBankThefts, reserveHistory.Items);
        Assert.Same(PyBool.True, await Call(rpc, session, "IsClientLinkedToReserveBank"));
        session.CharacterId = CharacterId + 100;
        Assert.Same(PyBool.False, await Call(rpc, session, "IsClientLinkedToReserveBank"));
        Assert.Equal([SolarSystemId, SolarSystemId, SolarSystemId + 1, SolarSystemId, SolarSystemId], requestedSystems);
    }

    [Fact]
    public async Task Mutations_forward_session_context_and_emit_reference_notifications_on_success()
    {
        var state = State();
        var requests = new List<EssMgrOperationRequest>();
        var sink = new RecordingSink();
        var session = Session(sink);
        var service = new EssMgrService(new EssMgrCallbacks(
            ExecuteAsync: (request, _) =>
            {
                requests.Add(request);
                return ValueTask.FromResult(new EssMgrOperationResult(true, state, state.MainBankLink));
            }));
        var rpc = (IService)service;

        foreach (var method in new[]
        {
            "AttemptLinkToMainBank",
            "AttemptLinkToReserveBank",
            "RequestMainBankUnlink",
            "RequestReserveBankUnlink",
            "RequestUnlockReserveBank",
        })
        {
            var args = method == "RequestUnlockReserveBank" ? new PyValue[] { new PyInt(55914) } : [];
            Assert.Same(PyNone.Instance, await Call(rpc, session, method, args));
        }

        Assert.Equal(
            [EssMgrOperation.AttemptLinkToMainBank, EssMgrOperation.AttemptLinkToReserveBank, EssMgrOperation.RequestMainBankUnlink, EssMgrOperation.RequestReserveBankUnlink, EssMgrOperation.RequestUnlockReserveBank],
            requests.Select(request => request.Operation));
        Assert.All(requests, request =>
        {
            Assert.Equal(SolarSystemId, request.SolarSystemId);
            Assert.Equal(CharacterId, request.CharacterId);
            Assert.Same(session, request.Call.Session);
        });
        Assert.Equal(55914, requests[^1].KeyTypeId);

        Assert.Equal(
            ["OnESSMainBankLinkNotification", "OnSystemESSDataChanged", "OnESSReserveBankLinkNotification", "OnSystemESSDataChanged", "OnMainBankPlayerLinkDisconnected", "OnSystemESSDataChanged", "OnReserveBankPlayerLinkDisconnected", "OnSystemESSDataChanged", "OnSystemESSDataChanged"],
            sink.Packets.Select(NotificationName));
        Assert.All(sink.Packets, packet => Assert.Equal("clientID", NotificationIdType(packet)));
    }

    private static EssMgrSystemData State() => new(
        EssId: 900000000,
        SolarSystemId: SolarSystemId,
        CurrentOutput: 17.25,
        MainValue: 1234.567,
        ReserveValue: -98,
        MainBankLink: new("main-link", CharacterId, 1001, 2002),
        ReserveBankLastPulseInitiatedFileTime: 1337,
        ReserveBankPulsesRemaining: 3,
        ReserveBankPulsesTotal: 9,
        ReserveLinkedCharacterIds: new HashSet<long> { CharacterId, CharacterId + 1 },
        MainBankThefts: [new PyDict([new(new PyStr("bank"), new PyStr("main"))])],
        ReserveBankThefts: [new PyDict([new(new PyStr("bank"), new PyStr("reserve"))])]);

    private static ClientSession Session(IPacketSink? sink = null)
    {
        var session = new ClientSession(sink) { CharacterId = CharacterId };
        session.Set("solarsystemid2", new PyInt(SolarSystemId));
        return session;
    }

    private static Task<PyValue> Call(IService service, ClientSession session, string method, params PyValue[] args) =>
        service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session }).AsTask();

    private static PyValue Value(PyDict dict, string key) =>
        dict.Entries.Single(entry => entry.Key == new PyStr(key)).Value;

    private static string NotificationName(PyValue value)
    {
        var packet = Assert.IsType<PyObject>(value);
        var fields = Assert.IsType<PyTuple>(packet.Args).Items;
        var destination = Assert.IsType<PyObject>(fields[2]);
        var addressFields = Assert.IsType<PyTuple>(destination.Args).Items;
        return Assert.IsType<PyStr>(addressFields[1]).Value;
    }

    private static string NotificationIdType(PyValue value)
    {
        var packet = Assert.IsType<PyObject>(value);
        var fields = Assert.IsType<PyTuple>(packet.Args).Items;
        var destination = Assert.IsType<PyObject>(fields[2]);
        var addressFields = Assert.IsType<PyTuple>(destination.Args).Items;
        return Assert.IsType<PyStr>(addressFields[3]).Value;
    }

    private sealed class RecordingSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }
}
