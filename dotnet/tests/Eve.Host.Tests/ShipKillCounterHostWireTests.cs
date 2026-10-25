using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Ship_kill_counter_returns_the_persisted_player_count_through_host_dispatch()
    {
        var counters = JsonNode.Parse("""{"ships":{"9001":{"playerKills":23}}}""")!.AsObject();
        Start(configure: registry => registry.Register(new ShipKillCounterService(
            _ => Task.FromResult((JsonObject)counters.DeepClone()))));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(
            new PyStr("machoVersion"), new PyInt(1))]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 1, "shipKillCounter", "GetItemKillCountPlayer",
            new PyTuple([new PyLong(9001)]), kwargs))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());

        Assert.Equal(new PyInt(23), Result(MarshalCodec.Decode(reply.Payload)));
    }
}
