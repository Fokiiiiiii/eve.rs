using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Skills;
using Eve.Skills;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Skill_rpcs_accept_transport_macho_version_and_keep_the_client_wire_shapes_on_the_host()
    {
        var catalog = new SkillCatalog([], new Dictionary<int, IReadOnlyList<StartingSkill>>());
        var training = new CharacterTraining(null!, catalog, null!, null!, new TrainingSettings(), () => 0);
        var handler = new SkillHandlerService(null!, catalog, training, null!, null!, null!, null!, () => 0, null!);
        Start(strictRpc: true, configure: registry =>
        {
            registry.Register(new SkillMgr2Service());
            registry.Register(handler);
        });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);

        async Task<PyValue> WireCall(long id, string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, id, service, method, new PyTuple(args), kwargs))));
            return MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        }

        async Task<PyValue> Call(long id, string method, params PyValue[] args)
        {
            var decoded = await WireCall(id, "skillHandler", method, args);
            Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);
            return Result(decoded)!;
        }

        foreach (var id in new[] { 1L, 2L })
        {
            var moniker = Assert.IsType<PyObject>(Result(await WireCall(id, "skillMgr2", "GetMySkillHandler")));
            Assert.Equal("carbon.common.script.net.moniker.Moniker", T(moniker.Name));
            Assert.Equal("skillHandler", T(Assert.IsType<PyTuple>(moniker.Args).Items[0]));
        }

        Assert.IsType<PyList>(await Call(3, "GetSkillHistory"));
        Assert.IsType<PyNone>(await Call(4, "GetSkillPoints"));
        var queueAndFree = Assert.IsType<PyTuple>(await Call(5, "GetSkillQueueAndFreePoints"));
        Assert.IsType<PyList>(queueAndFree.Items[0]);
        Assert.Equal(new PyInt(0), queueAndFree.Items[1]);
        Assert.IsType<PyNone>(await Call(6, "GetSkills"));
        Assert.IsType<PyNone>(await Call(7, "InjectSkillpoints", new PyLong(1), new PyInt(1)));
        Assert.IsType<PyNone>(await Call(8, "SplitSkillInjector", new PyLong(1), new PyInt(1)));
        Assert.IsType<PyNone>(await Call(9, "AbortTraining"));
        Assert.IsType<PyNone>(await Call(10, "ApplyFreeSkillPoints", new PyInt(3300), new PyInt(1000)));
        Assert.IsType<PyNone>(await Call(11, "CharStartTrainingSkill", new PyLong(330000000), PyNone.Instance));

        var invalidConstraint = await WireCall(12, "skillHandler", "CheckInjectionConstraints", new PyLong(1), new PyInt(1));
        Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(invalidConstraint)).Type);

        Assert.IsType<PyNone>(await Call(13, "CombineSkillInjector", new PyLong(1), new PyInt(3)));
        var invalidExtraction = await WireCall(14, "skillHandler", "ExtractSkills",
            new PyDict([]), new PyLong(1), new PyStr("token"));
        Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(invalidExtraction)).Type);

        Assert.IsType<PyNone>(await Call(15, "GetAllSkills"));
        Assert.Equal(5, Assert.IsType<PyDict>(await Call(16, "GetAttributes")).Entries.Count);
        Assert.Empty(Assert.IsType<PyDict>(await Call(17, "GetBoosters")).Entries);
        Assert.Equal(new PyInt(0), await Call(18, "GetDiminishedSpFromInjectors", new PyInt(40520), new PyInt(1), new PyInt(0)));
        Assert.Equal(new PyInt(0), await Call(19, "GetFreeSkillPoints"));
        Assert.Empty(Assert.IsType<PyDict>(await Call(20, "GetImplants")).Entries);
        Assert.IsType<PyList>(await Call(21, "GetSkillChangesForISIS"));
        Assert.IsType<PyObject>(await Call(22, "GetRespecInfo"));

        var bundle = Assert.IsType<PyTuple>(await Call(23, "GetSkillBundleInfo", new PyInt(1)));
        Assert.Equal(2, bundle.Items.Count);
        Assert.IsType<PyWString>(bundle.Items[0]);
        Assert.IsType<PyNone>(bundle.Items[1]);

        Assert.Equal(new PyInt(0), await Call(24, "GetFreeSkillPoints"));
        Assert.IsType<PyList>(await Call(25, "GetSkillHistory"));
    }
}
