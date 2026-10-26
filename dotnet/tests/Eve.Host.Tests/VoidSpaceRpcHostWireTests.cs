using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Void_space_rpc_host_wire_returns_none_and_the_client_error_list()
    {
        StartWithMovementServices();

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<Send> Invoke(uint id, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, id, "voidSpaceMgr", method, args))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }

        var ready = await Invoke(1, "ClientIsReady");
        var readyEnvelope = Assert.IsType<MachoPacket>(
            MachoPacket.Decode(MarshalCodec.Decode(ready.Payload)));
        Assert.Equal(MachoMessage.CallRsp, readyEnvelope.Type);
        Assert.Equal(PyNone.Instance, Result(MarshalCodec.Decode(ready.Payload)));

        var jump = await Invoke(2, "VoidSpacePlayerJump", new PyLong(9002), new PyInt(123));
        var jumpEnvelope = Assert.IsType<MachoPacket>(
            MachoPacket.Decode(MarshalCodec.Decode(jump.Payload)));
        Assert.Equal(MachoMessage.ErrorResponse, jumpEnvelope.Type);
        var errorHeader = Assert.IsType<PyTuple>(jumpEnvelope.Payload);
        Assert.Equal(new PyInt(MachoMessage.CallReq), errorHeader.Items[0]);
        Assert.Equal(new PyInt(2), errorHeader.Items[1]);
        var wrapped = Assert.IsType<PyTuple>(errorHeader.Items[2]);
        var wrappedUserError = Assert.IsType<PyObjectEx>(
            Assert.IsType<PySubStream>(Assert.Single(wrapped.Items)).Decoded);
        var expectedErrors = new PyList([new PyList([new PyInt(22), new PyList([])])]);
        var expectedPayload = Assert.IsType<PyObjectEx>(MachoErrors.UserErrorPayload(
            "VoidSpaceJumpError",
            [new KeyValuePair<PyValue, PyValue>(new PyStr("errors"), expectedErrors)]));
        var actualHeader = Assert.IsType<PyTuple>(wrappedUserError.Header);
        var expectedHeader = Assert.IsType<PyTuple>(expectedPayload.Header);
        Assert.Equal(expectedHeader.Items[0], actualHeader.Items[0]);
        var actualArgs = Assert.IsType<PyTuple>(actualHeader.Items[1]);
        Assert.Equal(new PyBytes(Encoding.UTF8.GetBytes("VoidSpaceJumpError")), actualArgs.Items[0]);
        var actualValues = Assert.IsType<PyDict>(actualArgs.Items[1]);
        Assert.Equal(expectedErrors, Assert.Single(actualValues.Entries).Value);
    }
}
