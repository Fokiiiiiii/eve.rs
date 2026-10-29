using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Abyssal_spawn_report_uses_the_registered_host_route_and_fails_closed_without_a_provider()
    {
        Start(sessionOpened: session => session.Set("accountRole", new PyLong(GameMasterRoles.Staff)));
        var (front, open) = await OpenFrontAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.Call(open, 1, "abyssal_content_manager", "generate_abyss_npc_spawn_report",
                new PyInt(172), new PyInt(215), new PyInt(35)))));

        var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(response)).Type);
        Assert.Contains("ABYSSAL_SPAWN_REPORT_UNAVAILABLE", Encoding.ASCII.GetString(MarshalCodec.Encode(response)));
    }
}
