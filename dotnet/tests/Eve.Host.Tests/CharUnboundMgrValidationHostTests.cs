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
    public async Task Char_unbound_name_validation_returns_the_numeric_code_consumed_by_creation_ui()
    {
        var catalog = new CreationCatalog(
            [new Race(1, 601, "Ibis")],
            [new Bloodline(1, 1)],
            [new StarterLocation(1000006, null, 60000610, 30002775, 20000406, 10000033)]);
        var service = new CharUnboundMgrService(
            null!, null!, null!, null!, null!, null!, catalog, null!, [], new Random(1));
        Start(configure: registry => registry.Register(service));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "charUnboundMgr", "ValidateNameEx",
                new PyTuple([new PyStr("A"), new PyInt(0)]), kwargs))));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(new PyInt((int)NameCheck.TooShort), Result(MarshalCodec.Decode(reply.Payload)));
    }
}
