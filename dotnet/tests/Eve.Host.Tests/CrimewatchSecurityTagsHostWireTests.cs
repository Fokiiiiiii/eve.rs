using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Crimewatch;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Crimewatch_bound_apply_tags_for_security_status_uses_the_registered_wire_route()
    {
        const long characterId = 90000001;
        var applied = new TaskCompletionSource<(double Requested, double Current)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new CrimewatchService(null!, null!, () => 1_800_000_000_000)
        {
            ApplySecurityTagsAsync = (call, requested, current, _) =>
            {
                Assert.Equal(characterId, call.Session.CharacterId);
                applied.TrySetResult((requested, current));
                return ValueTask.CompletedTask;
            },
        };
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
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(
            new PyStr("machoVersion"), new PyInt(1))]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "crimewatch", "MachoBindObject",
                new PyTuple([new PyTuple([new PyInt(30000142), new PyInt(1)]), PyNone.Instance]), kwargs))));
        var bind = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        var binding = Assert.IsType<PyTuple>(Result(bind));
        var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, 2, oid, "ApplyTagsForSecStatus",
                new PyTuple([new PyReal(0), new PyReal(-1)]), kwargs))));
        var reply = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(reply)).Type);
        Assert.Equal(PyNone.Instance, Result(reply));
        Assert.Equal((0d, -1d), await applied.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
