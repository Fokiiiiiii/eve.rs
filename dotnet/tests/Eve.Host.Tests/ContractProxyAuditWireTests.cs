using System.Net;
using System.Net.Sockets;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Contract_create_preserves_the_client_cargo_error_and_accepts_its_confirm_retry()
    {
        var writer = new RecordingContractWriter
        {
            NextCreateError = new ContractUserErrorException("NotEnoughCargoSpace"),
        };
        StartContractHost(writer);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var contractInfo = new PyObject(new PyStr("contracts.contractStruct.ContractStruct"), new PyTuple([
            new PyInt(1), PyBool.False, new PyInt(0), new PyInt(1440), new PyInt(0),
            new PyInt(3001), new PyInt(0), new PyInt(1000), new PyInt(0), new PyInt(0),
            new PyStr("wire contract"), new PyStr(""),
            new PyList([new PyTuple([new PyInt(7001), new PyInt(1)])]),
            new PyInt(4), new PyList([]), PyBool.False, PyBool.False]));

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "contractProxy", "CreateContract",
                new PyTuple([contractInfo]),
                Eve.Services.Py.Dict(("machoVersion", new PyInt(1)), ("confirm", PyNone.Instance))))));
        var failure = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal("NotEnoughCargoSpace", UserErrorKey(failure));

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 2, "contractProxy", "CreateContract",
                new PyTuple([contractInfo]),
                Eve.Services.Py.Dict(("machoVersion", new PyInt(1)), ("confirm", new PyInt(1)))))));
        var success = Assert.IsType<Send>(await front.ReceiveAsync());
        var created = Assert.IsType<PyList>(Result(MarshalCodec.Decode(success.Payload)));
        Assert.Equal([new PyInt(6001)], created.Items);
        Assert.Equal("wire contract", writer.CreatedRequest?.Title);
    }

    [Fact]
    public async Task Contract_bid_provider_error_keeps_the_user_error_the_client_retries()
    {
        var writer = new RecordingContractWriter
        {
            NextBidError = new ContractUserErrorException("ConBidTooLow"),
        };
        StartContractHost(writer);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "contractProxy", "PlaceBid",
                new PyTuple([new PyInt(5002), new PyInt(1000), PyBool.False]),
                Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));

        var failure = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal("ConBidTooLow", UserErrorKey(failure));
        Assert.Equal(1000m, writer.LastBid.Amount);
    }

    [Fact]
    public async Task Contract_detail_uses_the_derived_escrow_location_id_over_host_wire()
    {
        var reads = new RecordingContractReadModel();
        StartContractHost(new RecordingContractWriter(), reads);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "contractProxy", "GetContract",
                new PyTuple([new PyInt(5002)]),
                Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        var detail = Assert.IsType<PyObject>(Result(MarshalCodec.Decode(reply.Payload)));
        var contract = Assert.IsType<PyPackedRow>(Field(detail, "contract"));
        Assert.Equal(new PyInt(9_300_005_002L),
            contract.Fields().Single(field => field.Key == "crateID").Value);
    }

    private static string UserErrorKey(Send reply)
    {
        var response = Assert.IsType<PyObject>(MarshalCodec.Decode(reply.Payload));
        var state = Assert.IsType<PyTuple>(response.Args);
        Assert.Equal(new PyInt(MachoMessage.ErrorResponse), state.Items[0]);
        var callError = Assert.IsType<PyTuple>(state.Items[4]);
        var wrapped = Assert.IsType<PyTuple>(callError.Items[2]);
        var payload = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped.Items[0]).Decoded);
        var header = Assert.IsType<PyTuple>(payload.Header);
        Assert.Equal(new PyToken(MachoErrors.UserErrorClass), header.Items[0]);
        var errorArgs = Assert.IsType<PyTuple>(header.Items[1]);
        return System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(errorArgs.Items[0]).Value.Span);
    }
}
