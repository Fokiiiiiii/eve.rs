using Eve.Corporations;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;

namespace Eve.Host.Tests;

public sealed class BillMgrHostRpcTests
{
    [Fact]
    public async Task Bill_manager_routes_empty_corporation_reads_and_refuses_unavailable_payments()
    {
        var registry = new ServiceRegistry();
        registry.Register(new BillMgrService(null!, null!, null!, _ => null, () => 0));
        var dispatcher = new PacketDispatcher(registry);
        var capture = new PacketCapture();
        var session = new ClientSession(capture)
        {
            SessionId = 101,
            ClientId = 102,
            UserId = 103,
            CharacterId = 104,
        };
        var open = new SessionOpen(101, 102, 103, 0, "127.0.0.1", "EN", "GB");

        async Task<(int Type, PyValue? Result)> Call(long callId, string method, params PyValue[] args)
        {
            capture.Sent.Clear();
            var request = Packets.Call(open, callId, "billMgr", method, args);
            var decodedRequest = MarshalCodec.Decode(MarshalCodec.Encode(request));
            Assert.True(await dispatcher.DispatchAsync(decodedRequest, session));
            var responseValue = Assert.Single(capture.Sent, packet =>
                MachoPacket.Decode(packet)?.Type is MachoMessage.CallRsp or MachoMessage.ErrorResponse);
            var response = Assert.IsType<MachoPacket>(MachoPacket.Decode(responseValue));
            return (response.Type, response.Type == MachoMessage.CallRsp ? Result(response.Payload) : null);
        }

        var payable = await Call(1, "GetCorporationBills");
        Assert.Equal(MachoMessage.CallRsp, payable.Type);
        Assert.Empty(Assert.IsType<PyList>(payable.Result).Items);
        var receivable = await Call(2, "GetCorporationBillsReceivable");
        Assert.Equal(MachoMessage.CallRsp, receivable.Type);
        Assert.Empty(Assert.IsType<PyList>(receivable.Result).Items);

        foreach (var (callId, method, args) in new[]
                 {
                     (3L, "CharPayBill", new PyValue[] { new PyInt(7_001) }),
                     (4L, "GetAutomaticPaySettings", Array.Empty<PyValue>()),
                     (5L, "PayCorporationBill", new PyValue[] { new PyInt(7_001) }),
                     (6L, "SendAutomaticPaySettings", new PyValue[] { Py.Dict() }),
                 })
        {
            var refusal = await Call(callId, method, args);
            Assert.Equal(MachoMessage.ErrorResponse, refusal.Type);
        }
    }

    private static PyValue? Result(PyValue response)
    {
        var payload = (PyTuple)response;
        return payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
    }

    private sealed class PacketCapture : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }
}
