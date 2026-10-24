using Eve.Macho;
using Eve.Marshal;
using Eve.Services.CharacterSelect;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Char_unbound_get_character_lock_type_returns_none_through_the_registered_route_with_macho_version()
    {
        var service = new CharUnboundMgrService(
            accounts: null!,
            characters: null!,
            items: null!,
            training: null!,
            wallets: null!,
            creation: null!,
            catalog: null!,
            online: null!,
            entryHooks: [],
            random: Random.Shared);
        Start(configure: registry => registry.Register(service));

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "charUnboundMgr", "GetCharacterLockType",
                new PyTuple([new PyInt(900001)]), kwargs))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());

        Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(reply.Payload)));
    }
}
