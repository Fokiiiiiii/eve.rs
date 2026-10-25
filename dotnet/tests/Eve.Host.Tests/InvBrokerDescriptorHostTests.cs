using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Inventory;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Invbroker_item_descriptor_is_registered_on_the_host_with_the_client_row_header()
    {
        var broker = new InvBrokerService(null!, null!, null!, null!, null!, () => FixedNowMs);
        var service = new IndustryInventoryChangePublisher(broker, new IndustryMonitorRpcService());
        Start(configure: registry => registry.Register(service));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var machoVersion = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "invbroker", "GetItemDescriptor", new PyTuple([]), machoVersion))));

        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(send.Payload);
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
        Assert.Equal(MachoMessage.CallRsp, packet.Type);

        var actual = Result(decoded);
        var expected = MarshalCodec.Decode(MarshalCodec.Encode(InventoryWire.DescriptorHeader));
        Assert.Equal(expected, actual);
    }
}
