using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Project_discovery_task_id_fix_is_registered_and_fails_closed_without_a_backend()
    {
        const long characterId = 900_679;
        Start(sessionOpened: session =>
        {
            session.CharacterId = characterId;
            session.Set("charid", new PyInt(characterId));
        });

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 1, "ProjectDiscovery", "fix_task_id", Py.Tuple(new PyLong(123)), ClientKwargs()))));
        var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(response)).Type);
        Assert.Contains("PROJECT_DISCOVERY_TASK_FIX_UNAVAILABLE", Encoding.ASCII.GetString(MarshalCodec.Encode(response)));
    }
}
