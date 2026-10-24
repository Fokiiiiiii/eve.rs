using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Skills;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Skill_training_and_injection_calls_reach_the_registered_host_routes()
    {
        var service = new SkillHandlerService(null!, null!, null!, null!, null!, null!, null!, () => 0, null!);
        Start(configure: registry => registry.Register(service));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(long id, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, id, "skillHandler", method, new PyTuple(args), kwargs))));
            return MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        }

        Assert.Equal(PyNone.Instance, Result(await Call(1, "CharStartTrainingSkill", new PyLong(900000000), new PyList([]))));
        Assert.Equal(PyNone.Instance, Result(await Call(2, "AbortTraining")));
        Assert.Equal(PyNone.Instance, Result(await Call(3, "ApplyFreeSkillPoints", new PyInt(3300), new PyInt(1000))));

        var rejected = await Call(4, "CheckInjectionConstraints", new PyLong(1), new PyInt(1));
        Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(rejected)).Type);
    }
}
