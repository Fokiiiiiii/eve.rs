using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Jump_clone_reads_return_count_and_install_validation_labels_through_host()
    {
        var service = new JumpCloneService();
        Start(configure: registry => registry.Register(service));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(long id, string method)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, id, "jumpCloneSvc", method, new PyTuple([]), kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload))!;
        }

        Assert.Equal(new PyInt(0), await Call(1, "GetNumClonesInPilotsStructure"));
        var expected = new PyList([
            new PyStr("UI/CharacterSheet/CharacterSheetWindow/JumpCloneScroll/JumpNotDockedError"),
        ]);
        Assert.Equal(MarshalCodec.Decode(MarshalCodec.Encode(expected)), await Call(2, "ValidateInstallJumpClone"));
    }
}
