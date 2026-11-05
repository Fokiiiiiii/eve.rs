using Eve.Host;
using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Cache_version_and_client_logs_round_trip_through_the_host_registry()
    {
        var messages = new List<string>();
        Start(configure: registry => registry.Register(new ClientStatLoggerRpcService(messages.Add)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.Call(open, 1, "cache", "GetServerVersionAndBuild"))));
        var version = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Collection(version.Items,
            value => Assert.True(value is PyReal { Value: > 0 }),
            value => Assert.True(value is PyInt { Value: > 0 }));

        const string first = "JumpMonitor: 30000142->30000143 type[/tr]";
        const string second = "Failed to get a valid ballpark in time after trying 2 times";
        foreach (var (callId, message) in new[] { (2, first), (3, second) })
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, callId, "clientStatLogger", "LogString", new PyStr(message)))));
            Assert.IsType<PyNone>(Result(MarshalCodec.Decode(
                Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        }

        Assert.Equal([first, second], messages);
    }
}
