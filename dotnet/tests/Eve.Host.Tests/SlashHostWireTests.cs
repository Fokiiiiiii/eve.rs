using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Slash_command_is_dispatched_through_the_registered_host_with_macho_version()
    {
        var service = new Eve.Services.Slash.SlashService(
            new Eve.Services.Slash.ShipHealthCommands(null!, null!, () => FixedNowMs));
        Start(
            configure: registry => registry.Register(service),
            strictRpc: true,
            sessionOpened: session => session.Set("accountRole", new PyLong(GameMasterRoles.Gml)));
        var (front, open) = await OpenFrontAsync();
        var kwargs = new PyDict([new(new PyStr("machoVersion"), new PyInt(1))]);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 1, "slash", "SlashCmd", new PyTuple([new PyStr("/heal 123456")]), kwargs))));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal("Select a character before using /heal.", T(Result(MarshalCodec.Decode(reply.Payload))));
    }
}
