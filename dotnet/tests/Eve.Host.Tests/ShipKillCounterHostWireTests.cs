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

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.Call(
            open, 1, "shipKillCounter", "GetItemKillCountPlayer", [new PyLong(9001)]))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());

        Assert.Equal(new PyInt(23), Result(MarshalCodec.Decode(reply.Payload)));
    }
}
