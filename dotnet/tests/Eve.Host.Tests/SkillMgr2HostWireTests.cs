using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Skills;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Skill_mgr2_returns_its_bound_skill_handler_moniker_over_the_host_route()
    {
        Start(configure: registry => registry.Register(new SkillMgr2Service()));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "skillMgr2", "GetMySkillHandler", new PyTuple([]), kwargs))));
        var moniker = Assert.IsType<PyObject>(Result(
            MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));

        Assert.Equal("carbon.common.script.net.moniker.Moniker", JsSemantics.StringValue(moniker.Name));
        var args = Assert.IsType<PyTuple>(moniker.Args);
        Assert.Equal("skillHandler", JsSemantics.StringValue(args.Items[0]));
        Assert.IsType<PyInt>(args.Items[2]);
    }
}
