using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Skills;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Skill_mgr2_returns_the_character_scoped_skill_handler_moniker_over_the_host()
    {
        const long characterId = 90_000_071;
        var machoVersion = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        Start(
            configure: registry => registry.Register(new SkillMgr2Service()),
            sessionOpened: session => session.Set("charid", new PyInt(characterId)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "skillMgr2", "GetMySkillHandler", new PyTuple([]), machoVersion))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(send.Payload);
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
        Assert.Equal(MachoMessage.CallRsp, packet.Type);

        var moniker = Assert.IsType<PyObject>(Result(decoded));
        Assert.Equal("carbon.common.script.net.moniker.Moniker", T(moniker.Name));
        var args = Assert.IsType<PyTuple>(moniker.Args);
        Assert.Equal("skillHandler", T(args.Items[0]));
        Assert.Same(PyNone.Instance, args.Items[1]);
        Assert.Equal(new PyInt(characterId), args.Items[2]);
        Assert.Same(PyNone.Instance, args.Items[3]);
    }
}
