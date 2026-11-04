using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Skills;

namespace Eve.Host.Tests;

public sealed class AlphaInjectorHostRpcTests
{
    [Fact]
    public async Task Alpha_injection_clock_routes_and_returns_zero_when_the_session_has_no_character()
    {
        var registry = new ServiceRegistry();
        registry.Register(new AlphaInjectorMgrService(null!, null!));
        var dispatcher = new PacketDispatcher(registry);
        var capture = new PacketCapture();
        var session = new ClientSession(capture)
        {
            SessionId = 111,
            ClientId = 112,
            UserId = 113,
        };
        var open = new SessionOpen(111, 112, 113, 0, "127.0.0.1", "EN", "GB");

        var request = Packets.Call(open, 1, "alphaInjectorMgr", "GetNextAvailableInjection");
        var decodedRequest = MarshalCodec.Decode(MarshalCodec.Encode(request));
        Assert.True(await dispatcher.DispatchAsync(decodedRequest, session));
        var response = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(capture.Sent)));
        Assert.Equal(MachoMessage.CallRsp, response.Type);
        var payload = (PyTuple)response.Payload;
        var result = payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
        Assert.Equal(new PyLong(0), result);
    }

    private sealed class PacketCapture : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }
}
