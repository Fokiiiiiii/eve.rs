using Eve.Host;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Petition_support_reads_and_mutations_keep_the_EveJS_wire_shapes_through_the_host()
    {
        Start(strictRpc: true);
        var (front, open) = await OpenFrontAsync();
        var kwargs = new PyDict([new(new PyStr("machoVersion"), new PyInt(1))]);
        uint callId = 0;

        async Task<PyValue?> Call(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, ++callId, "petitioner", method, new PyTuple(args), kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload));
        }

        var options = Assert.IsType<PyList>(await Call("PropertyPopulationInfo",
            new PyStr("StarbaseLocation"), new PyInt(90000001)));
        Assert.Empty(options.Items);

        Assert.IsType<PyNone>(await Call("UnClaimPetition", new PyInt(321)));
        Assert.IsType<PyNone>(await Call("UpdatePetitionRating",
            new PyInt(321), new PyInt(3), new PyInt(4), new PyInt(5), new PyStr("Helpful answer")));
    }
}
