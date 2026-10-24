using Eve.Host;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Skills;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Skill_bootstrap_and_character_reads_use_the_registered_wire_routes()
    {
        var training = new Eve.Skills.CharacterTraining(
            null!, null!, null!, null!,
            new Eve.Skills.TrainingSettings { Speed = 1.5, Attribute = (_, id) => id },
            () => 0);
        var handler = new SkillHandlerService(null!, null!, training, null!, null!, null!, null!, () => 0, null!);
        Start(configure: registry =>
        {
            registry.Register(new SkillMgr2Service());
            registry.Register(handler);
        });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue?> Call(long id, string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, id, service, method, new PyTuple(args), kwargs))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        for (var callId = 10; callId <= 11; callId++)
        {
            var moniker = Assert.IsType<PyObject>(await Call(callId, "skillMgr2", "GetMySkillHandler"));
            Assert.Equal("carbon.common.script.net.moniker.Moniker", T(moniker.Name));
            Assert.Equal("skillHandler", T(((PyTuple)moniker.Args).Items[0]));
        }

        Assert.Equal(PyNone.Instance, await Call(12, "skillHandler", "GetSkills"));
        Assert.Equal(PyNone.Instance, await Call(13, "skillHandler", "GetSkillPoints"));
        Assert.Equal(new PyInt(0), await Call(14, "skillHandler", "GetFreeSkillPoints"));
        Assert.Equal(new PyInt(0), await Call(15, "skillHandler", "GetFreeSkillPoints"));
        Assert.Equal(new PyInt(0), await Call(16, "skillHandler", "GetDiminishedSpFromInjectors", new PyInt(40520), new PyInt(1), new PyInt(0)));
        Assert.IsType<PyList>(await Call(17, "skillHandler", "GetSkillChangesForISIS"));
        Assert.IsType<PyList>(await Call(18, "skillHandler", "GetSkillHistory", new PyInt(50)));
        Assert.IsType<PyDict>(await Call(19, "skillHandler", "GetBoosters"));
        Assert.IsType<PyDict>(await Call(20, "skillHandler", "GetImplants"));
        var attributes = Assert.IsType<PyDict>(await Call(21, "skillHandler", "GetAttributes"));
        Assert.Equal(5, attributes.Entries.Count);
        Assert.Equal(Py.Number(246), attributes.Entries.Single(entry => entry.Key == new PyInt(164)).Value);
        var respec = Assert.IsType<PyObject>(await Call(22, "skillHandler", "GetRespecInfo"));
        Assert.Equal("util.KeyVal", T(respec.Name));
        Assert.IsType<PyTuple>(await Call(23, "skillHandler", "GetSkillQueueAndFreePoints"));
        var bundle = Assert.IsType<PyTuple>(await Call(24, "skillHandler", "GetSkillBundleInfo", new PyInt(123)));
        Assert.Equal(string.Empty, T(bundle.Items[0]));
        Assert.Same(PyNone.Instance, bundle.Items[1]);
        Assert.Equal(PyNone.Instance, await Call(25, "skillHandler", "InjectSkillpoints", new PyInt(90000001), new PyInt(1)));
        Assert.Equal(PyNone.Instance, await Call(26, "skillHandler", "SplitSkillInjector", new PyInt(90000001), new PyInt(1)));
    }
}
