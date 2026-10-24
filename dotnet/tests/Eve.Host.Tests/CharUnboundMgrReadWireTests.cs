using Eve.Characters;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterSelect;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Char_unbound_mgr_stateless_reads_and_validation_toggle_are_registered_on_the_host()
    {
        var catalog = new CreationCatalog(
            [new Race(1, 601, "Ibis")],
            [new Bloodline(1, 1)],
            [
                new StarterLocation(1000006, null, 60000610, 30002775, 20000406, 10000033),
                new StarterLocation(1000007, null, 60000611, 30002775, 20000406, 10000033),
            ]);
        var service = new CharUnboundMgrService(
            null!, null!, null!, null!, null!, null!, catalog, null!, [],
            new Random(1));

        var machoVersion = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("accountRole", new PyLong(1L << 51)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> CallAsync(long callId, string method)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "charUnboundMgr", method, new PyTuple([]), machoVersion))));
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(send.Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            Assert.Equal(MachoMessage.CallRsp, packet.Type);
            return Result(decoded)!;
        }

        Assert.Equal(new PyList([]), await CallAsync(1, "GetCohortsForUser"));
        Assert.Equal(new PyList([new PyInt(30002775)]), await CallAsync(2, "GetQAStarterSystemIDs"));
        Assert.Equal(PyBool.False, await CallAsync(3, "ToggleValidation"));
        Assert.Equal(PyBool.True, await CallAsync(4, "ToggleValidation"));
    }
}
