using Eve.Characters;
using Eve.Corporations;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;
using Npgsql;

namespace Eve.Host.Tests;

public sealed partial class HostServerTests
{
    [Fact]
    public async Task Corporation_medal_rpcs_use_registered_wire_shapes_with_macho_version()
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused;Username=unused;Timeout=1");
        var characters = new PgCharacterStore(source);
        var corporations = new CorporationStore(source, characters, null!, null!, null!, () => FixedNowMs);
        Start(configure: registry => registry.Register(new CorporationService(corporations, characters)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = Eve.Services.Py.Dict(("machoVersion", new PyInt(1)));

        async Task<Send> Call(long callId, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "corporationSvc", method, new PyTuple(args), kwargs))));
            return Assert.IsType<Send>(await front.ReceiveAsync());
        }

        static MachoPacket Packet(Send send) =>
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload)));

        var recipients = await Call(1, "GetRecipientsOfMedal", new PyInt(9001));
        Assert.Equal(MachoMessage.CallRsp, Packet(recipients).Type);
        Assert.Equal(MarshalCodec.Decode(MarshalCodec.Encode(
                Py.Rowset(["medalID", "recepientID", "issuerID", "reason", "date"], []))),
            Result(MarshalCodec.Decode(recipients.Payload)));

        var statuses = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(9001), new PyInt(2))]);
        var statusRefusal = await Call(2, "SetMedalStatus", statuses);
        Assert.Equal(MachoMessage.ErrorResponse, Packet(statusRefusal).Type);

        var listAwardRefusal = await Call(3, "GiveMedalToCharacters", new PyInt(9001),
            new PyList([new PyInt(9002), new PyInt(9003)]), new PyWString("Service award"));
        Assert.Equal(MachoMessage.ErrorResponse, Packet(listAwardRefusal).Type);

        var singleAwardRefusal = await Call(4, "GiveMedalToCharacters", new PyInt(9001),
            new PyInt(9002), new PyWString("Service award"));
        Assert.Equal(MachoMessage.ErrorResponse, Packet(singleAwardRefusal).Type);

        var confirmedAwardRefusal = await Call(5, "GiveMedalToCharacters", new PyInt(9001),
            new PyList([new PyInt(9002)]), new PyWString("Service award"), PyBool.True);
        Assert.Equal(MachoMessage.ErrorResponse, Packet(confirmedAwardRefusal).Type);
    }
}
