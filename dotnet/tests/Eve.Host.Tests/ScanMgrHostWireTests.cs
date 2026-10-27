using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task ScanMgr_full_state_and_registration_include_the_current_signature_map_over_host_wire()
    {
        const long systemId = 30_000_142;
        const long characterId = 90_000_001;
        var sessions = new CharacterSessions();
        var provider = new SignalTrackerRegistrationProvider(sessions);
        var signatures = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyLong(601), Py.KeyVal(
                ("position", new PyList([new PyReal(1), new PyReal(2), new PyReal(3)])),
                ("targetID", new PyStr("ABC-123")),
                ("difficulty", new PyInt(2)),
                ("dungeonID", PyNone.Instance),
                ("archetypeID", PyNone.Instance),
                ("deviation", new PyReal(4 * 149_597_870_700d)))),
        ]);
        var service = new IncursionAnomalyService(
            "scanMgr",
            _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            signalTrackerRegister: provider.RegisterAsync,
            readSignatures: (_, _) => ValueTask.FromResult(signatures));
        provider.ReadFullState = service.ReadFullStateForSystemAsync;
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("solarsystemid2", new PyInt(systemId));
                sessions.Enter(characterId, session);
            });

        var (front, open) = await OpenFrontAsync();
        var binding = Assert.IsType<PySubStruct>(
            await HostRpcAsync(front, open, 1, "scanMgr", "GetSystemScanMgr"));
        var objectId = BoundObjects.ExtractName(binding)!;
        var state = Assert.IsType<PyList>(
            await HostBoundRpcAsync(front, open, 2, objectId, "GetFullState", new PyTuple([])));
        Assert.Equal(4, state.Items.Count);
        Assert.IsType<PyDict>(state.Items[0]);
        var signatureMap = Assert.IsType<PyDict>(state.Items[1]);
        var signature = Assert.IsType<PyObject>(Assert.Single(signatureMap.Entries).Value);
        var wireTargetID = WireField(signature, "targetID");
        Assert.IsType<PyBytes>(wireTargetID);
        Assert.Equal("ABC-123", JsSemantics.StringValue(wireTargetID));
        Assert.Equal(new PyInt(2), WireField(signature, "difficulty"));
        Assert.Equal(PyNone.Instance, WireField(signature, "dungeonID"));
        Assert.IsType<PyDict>(state.Items[2]);
        Assert.IsType<PyDict>(state.Items[3]);

        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 3, "scanMgr", "SignalTrackerRegister"));
        var notification = Assert.IsType<Send>(await front.ReceiveAsync());
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(notification.Payload)));
        Assert.Equal(MachoMessage.Notification, packet.Type);
        Assert.Equal("OnSignalTrackerFullState", packet.Dest.BroadcastId);
        var registrationArgs = NotificationArguments(packet);
        Assert.Equal(2, registrationArgs.Count);
        Assert.Equal(new PyInt(systemId), registrationArgs[0]);
        Assert.Equal(4, Assert.IsType<PyList>(registrationArgs[1]).Items.Count);

        signatures = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyLong(602), Py.KeyVal(
                ("position", new PyList([new PyReal(4), new PyReal(5), new PyReal(6)])),
                ("targetID", new PyStr("DEF-456")),
                ("difficulty", new PyInt(3)),
                ("dungeonID", PyNone.Instance),
                ("archetypeID", PyNone.Instance),
                ("deviation", new PyReal(4 * 149_597_870_700d)))),
        ]);
        await provider.RefreshAsync();
        var update = Assert.IsType<Send>(await front.ReceiveAsync());
        var updatePacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(update.Payload)));
        Assert.Equal(MachoMessage.Notification, updatePacket.Type);
        Assert.Equal("OnSignalTrackerSignatureUpdate", updatePacket.Dest.BroadcastId);
        var deltaArgs = NotificationArguments(updatePacket);
        Assert.Equal(3, deltaArgs.Count);
        Assert.Equal(new PyInt(systemId), deltaArgs[0]);
        var added = Assert.IsType<PyDict>(deltaArgs[1]);
        Assert.Equal(new PyLong(602), Assert.Single(added.Entries).Key);
        var removed = Assert.IsType<PyList>(deltaArgs[2]);
        Assert.Equal(new PyLong(601), Assert.Single(removed.Items));
    }

    [Fact]
    public async Task ScanMgr_bound_qa_and_probe_methods_reach_the_registered_route_with_client_arguments()
    {
        var calls = new List<ServiceCall>();
        long recalledProbe = 0;
        var service = new IncursionAnomalyService(
            "scanMgr",
            _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            recallProbe: (_, probeId, _) =>
            {
                recalledProbe = probeId;
                return ValueTask.CompletedTask;
            },
            probeScans: (method, call) =>
            {
                calls.Add(call);
                return ValueTask.FromResult<PyValue>(method switch
                {
                    "GetScanTargetID" => new PyStr("ABC-123"),
                    "QAScanSites" => new PyList([]),
                    "RecoverProbes" => new PyList([new PyLong(900)]),
                    "ConeScan" => new PyList([]),
                    _ => PyNone.Instance,
                });
            });
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = 90_000_002);
        var (front, open) = await OpenFrontAsync();
        var binding = Assert.IsType<PySubStruct>(
            await HostRpcAsync(front, open, 1, "scanMgr", "GetSystemScanMgr"));
        var objectId = BoundObjects.ExtractName(binding)!;

        Assert.Same(PyNone.Instance,
            await HostBoundRpcAsync(front, open, 2, objectId, "RequestScans", new PyTuple([PyNone.Instance])));
        Assert.Same(PyNone.Instance,
            await HostBoundRpcAsync(front, open, 3, objectId, "RequestScans", new PyTuple([new PyDict([])])));
        Assert.Same(PyNone.Instance,
            await HostBoundRpcAsync(front, open, 4, objectId, "SetActivityState",
                new PyTuple([new PyList([new PyLong(900)]), PyBool.True])));
        var siteResults = Assert.IsType<PyList>(
            await HostBoundRpcAsync(front, open, 5, objectId, "QAScanSites",
                new PyTuple([new PyTuple([new PyStr("ABC-123"), new PyLong(601)])])));
        Assert.Empty(siteResults.Items);
        var targetID = await HostBoundRpcAsync(front, open, 6, objectId, "GetScanTargetID",
                new PyTuple([new PyLong(601)]),
                new PyDict([
                    new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
                    new KeyValuePair<PyValue, PyValue>(new PyStr("targetSeedID"), new PyLong(700_001)),
                ]));
        Assert.IsType<PyBytes>(targetID);
        Assert.Equal("ABC-123", JsSemantics.StringValue(targetID));
        Assert.Same(PyNone.Instance,
            await HostBoundRpcAsync(front, open, 7, objectId, "QAOverrideProbeExpiry", new PyTuple([new PyLong(60_000)])));
        var recovered = Assert.IsType<PyList>(
            await HostBoundRpcAsync(front, open, 8, objectId, "RecoverProbes", new PyTuple([new PyList([new PyLong(900)])])));
        Assert.Equal(new PyLong(900), Assert.Single(recovered.Items));
        Assert.Same(PyNone.Instance,
            await HostBoundRpcAsync(front, open, 9, objectId, "ReconnectToLostProbes", new PyTuple([])));
        Assert.Same(PyNone.Instance,
            await HostBoundRpcAsync(front, open, 10, objectId, "DestroyProbe", new PyTuple([new PyLong(900)])));
        Assert.IsType<PyList>(await HostBoundRpcAsync(front, open, 11, objectId, "ConeScan",
            new PyTuple([new PyReal(1.5), new PyReal(50_000), new PyReal(1), new PyReal(0), new PyReal(0)])));
        Assert.Same(PyNone.Instance,
            await HostBoundRpcAsync(front, open, 12, objectId, "QAOverrideProbeExpiry", new PyTuple([PyNone.Instance])));
        var noneSeedTarget = await HostBoundRpcAsync(front, open, 13, objectId, "GetScanTargetID",
            new PyTuple([new PyLong(601)]),
            new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
                new KeyValuePair<PyValue, PyValue>(new PyStr("targetSeedID"), PyNone.Instance),
            ]));
        Assert.IsType<PyBytes>(noneSeedTarget);
        Assert.Equal("ABC-123", JsSemantics.StringValue(noneSeedTarget));

        Assert.Equal(900, recalledProbe);
        Assert.Equal(
            ["RequestScans", "RequestScans", "SetActivityState", "QAScanSites", "GetScanTargetID", "QAOverrideProbeExpiry", "RecoverProbes", "ReconnectToLostProbes", "ConeScan", "QAOverrideProbeExpiry", "GetScanTargetID"],
            calls.Select(call => call.Method));
        Assert.Same(PyNone.Instance, calls[0].Arg(0));
        Assert.IsType<PyDict>(calls[1].Arg(0));
        Assert.IsType<PyTuple>(calls[3].Arg(0));
        Assert.Equal(new PyLong(700_001), calls[4].Kwarg("targetSeedID"));
        Assert.Same(PyNone.Instance, calls[10].Kwarg("targetSeedID"));
        Assert.True(service.HasMethod("QAOverrideProbeExpiry"));
        Assert.True(service.HasMethod("QAScanSites"));
        Assert.True(service.HasMethod("GetScanTargetID"));
    }

    private static IReadOnlyList<PyValue> NotificationArguments(MachoPacket packet)
    {
        var envelope = Assert.IsType<PyTuple>(packet.Payload).Items[0];
        var inner = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(envelope).Items[1]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(inner.Value));
        var eventCall = Assert.IsType<PyTuple>(decoded.Items[1]);
        return Assert.IsType<PyTuple>(eventCall.Items[1]).Items;
    }

    private async Task<PyValue> HostBoundRpcAsync(
        TestFront front,
        SessionOpen open,
        long callId,
        string objectId,
        string method,
        PyTuple args,
        PyValue? kwargs = null)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, callId, objectId, method, args, kwargs ?? TransportKwargs()))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(send.Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);
        return Result(decoded)!;
    }
}
