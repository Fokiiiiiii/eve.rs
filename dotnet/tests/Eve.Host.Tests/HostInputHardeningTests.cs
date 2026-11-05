using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public sealed class HostInputHardeningTests
{
    [Fact]
    public async Task A_call_without_a_decodable_payload_gets_a_client_safe_user_error()
    {
        var registry = new ServiceRegistry();
        var sink = new PacketCapture();
        var session = new ClientSession(sink);

        Assert.True(await new PacketDispatcher(registry).DispatchAsync(
            CallPacket("noSuchService", new PyTuple([]), PyNone.Instance, PyNone.Instance), session));

        var response = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(sink.Packets)));
        Assert.Equal(MachoMessage.ErrorResponse, response.Type);
        var wire = Encoding.ASCII.GetString(MarshalCodec.Encode(sink.Packets[0]));
        Assert.Contains("exceptions.UserError", wire);
        Assert.Contains("UnMachoDestination", wire);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Positional_arguments_accept_tuple_or_list_and_ignore_macho_version(bool listArguments)
    {
        var service = new ArgumentCaptureService();
        var registry = new ServiceRegistry();
        registry.Register(service);
        var sink = new PacketCapture();
        var session = new ClientSession(sink);
        var args = new PyValue[] { new PyInt(7), new PyStr("valid") };
        PyValue positional = listArguments ? new PyList(args) : new PyTuple(args);
        var kwargs = new PyDict([new(new PyStr("machoVersion"), new PyInt(1))]);

        Assert.True(await new PacketDispatcher(registry).DispatchAsync(
            CallPacket("argumentCapture", positional, kwargs), session));

        Assert.Equal(args, service.Arguments);
        Assert.Equal(PyNone.Instance, service.Kwargs);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(sink.Packets))).Type);
    }

    private static PyValue CallPacket(string serviceName, PyValue args, PyValue kwargs, PyValue? requestPayload = null)
    {
        var source = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(17), new PyInt(23), PyNone.Instance]);
        var body = new PyTuple([new PyInt(1), new PyStr("Capture"), args, kwargs]);
        var payload = requestPayload ?? new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([
                new PyInt(MachoMessage.CallReq),
                source,
                MachoAddress.Any(serviceName).Encode(),
                new PyInt(99),
                payload,
                PyNone.Instance
            ]));
    }

    private sealed class ArgumentCaptureService : IService
    {
        public string Name => "argumentCapture";
        public IReadOnlyList<PyValue> Arguments { get; private set; } = [];
        public PyValue Kwargs { get; private set; } = PyNone.Instance;
        public bool HasMethod(string method) => method == "Capture";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            Arguments = call.Args.ToArray();
            Kwargs = call.Kwargs;
            return ValueTask.FromResult<PyValue>(new PyStr("accepted"));
        }
    }

    private sealed class PacketCapture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }
}
