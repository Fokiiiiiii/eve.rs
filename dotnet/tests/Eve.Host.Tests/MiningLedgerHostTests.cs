using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Character_mining_ledger_returns_empty_rows_for_a_session_without_a_character()
    {
        var service = new CharacterMiningLedgerService(null!);
        Start(configure: registry => registry.Register(service));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "characterMiningLedger", "GetCharacterLogs",
                new PyTuple([]), kwargs))));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(new PyList([]), Result(MarshalCodec.Decode(reply.Payload)));
    }
}
