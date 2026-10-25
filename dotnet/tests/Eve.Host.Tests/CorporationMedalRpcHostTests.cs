using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

public sealed partial class HostServerTests
{
    [Fact]
    public async Task Corporation_medal_rpcs_use_the_registered_host_service_and_preserve_wire_outcomes()
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Database=not-used;Username=not-used;Timeout=1");
        var characters = new PgCharacterStore(source);
        var corporationStore = new CorporationStore(source, characters, new PgWalletStore(source),
            new WalletTransactions(source), new PgSkillStore(source), () => FixedNowMs);
        IService service = new CorporationService(corporationStore, characters);
        Assert.True(service.HasMethod("GetRecipientsOfMedal"));
        Assert.True(service.HasMethod("SetMedalStatus"));
        Assert.True(service.HasMethod("GiveMedalToCharacters"));

        Start(configure: registry => registry.Register(service), sessionOpened: session => session.CharacterId = null);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> Call(long callId, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "corporationSvc", method, new PyTuple(args),
                    Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return MarshalCodec.Decode(reply.Payload);
        }

        var recipientsResponse = await Call(1, "GetRecipientsOfMedal", new PyLong(7));
        var recipients = Assert.IsType<PyObject>(Result(recipientsResponse));
        var expectedRecipients = MarshalCodec.Decode(MarshalCodec.Encode(Eve.Services.Py.Rowset(
            ["medalID", "recepientID", "issuerID", "reason", "date"], [])));
        Assert.Equal(expectedRecipients, recipients);

        async Task AssertUnauthorized(string method, params PyValue[] args)
        {
            var response = Assert.IsType<MachoPacket>(MachoPacket.Decode(await Call(2, method, args)));
            Assert.Equal(MachoMessage.ErrorResponse, response.Type);
        }

        await AssertUnauthorized("SetMedalStatus",
            new PyDict([new KeyValuePair<PyValue, PyValue>(new PyLong(7), new PyInt(3))]));
        await AssertUnauthorized("GiveMedalToCharacters",
            new PyLong(7), new PyList([new PyLong(42)]), new PyWString("service award"));
        await AssertUnauthorized("GiveMedalToCharacters",
            new PyLong(7), new PyList([new PyLong(42)]), new PyWString("service award"), PyBool.True);
    }
}
