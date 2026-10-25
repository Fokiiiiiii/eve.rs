using Eve.Crimewatch;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Crimewatch;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Crimewatch_bound_get_character_security_status_uses_the_registered_host_route()
    {
        const long characterId = 90000001;
        const long targetId = 90000002;
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Database=eve;Username=postgres;Timeout=1;Command Timeout=1");
        var service = new CrimewatchService(
            new PgCrimewatchStore(dataSource), null!, () => 1_800_000_000_000,
            (id, _) => Task.FromResult(id == targetId ? 2.5 : 0d));
        Start(strictRpc: true,
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "crimewatch", "MachoBindObject",
                new PyTuple([new PyTuple([new PyInt(30000142), new PyInt(1)]), PyNone.Instance]), kwargs))));
        var bindResponse = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        var binding = Assert.IsType<PyTuple>(Result(bindResponse));
        var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, 2, oid, "GetCharacterSecurityStatus", new PyTuple([new PyLong(targetId)]), kwargs))));
        var reply = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(reply)).Type);
        Assert.Equal(new PyReal(2.5), Result(reply));
    }
}
