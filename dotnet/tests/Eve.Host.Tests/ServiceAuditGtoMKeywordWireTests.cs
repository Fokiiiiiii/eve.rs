using System.Text;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task MachoNet_client_keyword_arguments_reach_the_registered_set_handler()
    {
        Start(sessionOpened: session => session.Set("accountRole", new PyLong(1L << 56)));
        var (front, open) = await OpenFrontAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("key"), new PyStr("new_mission_giver_enabled")),
            new KeyValuePair<PyValue, PyValue>(new PyStr("value"), new PyStr("1")),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "machoNet", "SetGlobalConfigValue", new PyTuple([]), kwargs))));

        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.ErrorResponse,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload))).Type);
        Assert.Contains("GlobalConfigNotWritable", Encoding.Latin1.GetString(send.Payload.Span));
    }
}
