using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Paper_doll_reads_keep_none_and_batch_dictionary_shapes_through_host_wire()
    {
        ClientSession? openedSession = null;
        Start(
            sessionOpened: session => openedSession = session,
            strictRpc: true,
            configure: registry => registry.Register(new PaperDollServerRpcService()));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        Assert.NotNull(openedSession);
        openedSession!.CharacterId = 42;

        async Task<PyValue?> Call(uint callId, string method, PyTuple args)
        {
            var request = Packets.CallWithKwargs(
                open, callId, "paperDollServer", method, args, Py.Dict(("machoVersion", new PyInt(1))));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        Assert.Same(PyNone.Instance,
            await Call(1, "GetMyPaperDollData", new PyTuple([new PyInt(42)])));

        var batch = Assert.IsType<PyDict>(
            await Call(2, "GetPaperDollDataBatch", new PyTuple([new PyList([
                new PyInt(1001), new PyLong(1002), new PyInt(1001),
            ])])));
        Assert.Equal(new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyInt(1001), PyNone.Instance),
            new KeyValuePair<PyValue, PyValue>(new PyInt(1002), PyNone.Instance),
        ]), batch);
        Assert.Equal(new PyDict([]),
            await Call(3, "GetPaperDollDataBatch", new PyTuple([new PyList([])])));
    }
}
