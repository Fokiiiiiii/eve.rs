using System.Reflection;
using System.Runtime.CompilerServices;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public sealed class AbyssalGateWireTests
{
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    [Fact]
    public async Task Encounter_and_origin_gate_calls_are_registered_and_return_the_client_error_without_a_pilot()
    {
        var host = (CharacterServicesComposition)RuntimeHelpers.GetUninitializedObject(typeof(CharacterServicesComposition));
        var handle = typeof(CharacterServicesComposition).GetMethod(
            "HandleAbyssalRpcAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<ServiceCall, CancellationToken, ValueTask<PyValue>>>(host);
        var registry = new ServiceRegistry();
        registry.Register(new AbyssalMgrService(handle));
        var dispatcher = new PacketDispatcher(registry, () => 1_800_000_000_000);
        var sink = new Sink();
        var session = new ClientSession(sink) { SessionId = 101, ClientId = 201 };

        foreach (var (callId, method, kwargs) in new[]
        {
            (1L, "AbyssalGateActivation", (PyValue)new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("violateSafetyTimer"), PyBool.True),
            ])),
            (2L, "AbyssalEndGateActivation", PyNone.Instance),
        })
        {
            Assert.True(await dispatcher.DispatchAsync(Request(callId, method, kwargs), session));
            var response = Assert.IsType<PyObject>(sink.Packets[^1]);
            var fields = Assert.IsType<PyTuple>(response.Args).Items;
            Assert.Equal(MachoMessage.ErrorResponse, ((PyInt)fields[0]).Value);
            var responsePayload = Assert.IsType<PyTuple>(fields[4]);
            var error = Assert.IsType<PySubStream>(
                Assert.IsType<PyTuple>(responsePayload.Items[2]).Items[0]).Decoded;
            Assert.Equal(MachoErrors.UserErrorPayload("AbyssError",
            [
                new KeyValuePair<PyValue, PyValue>(new PyStr("errors"), new PyList([
                    new PyTuple([new PyInt(30), new PyTuple([])]),
                ])),
            ]), error);
        }
    }

    private static PyValue Request(long callId, string method, PyValue kwargs)
    {
        var body = new PyTuple([
            PyNone.Instance,
            new PyStr(method),
            new PyTuple([new PyLong(900_001)]),
            kwargs,
        ]);
        var payload = new PyTuple([
            new PyTuple([PyBool.True, new PySubStream(body)]),
        ]);
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(201));
        var destination = new MachoAddress(
            AddressKind.Node, Service: "abyssalMgr", CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([
                new PyInt(MachoMessage.CallReq),
                source.Encode(),
                destination.Encode(),
                new PyInt(7),
                payload,
                new PyDict([]),
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
            ]));
    }
}
