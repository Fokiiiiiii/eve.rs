using Eve.Host;
using Eve.Marshal;
using Eve.Services.Wallet;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Account_contact_cost_calls_use_character_state_and_keep_client_wire_values()
    {
        var hasStoredCost = false;
        int? storedCost = null;
        var service = new AccountService(null!)
        {
            ReadContactCost = (_, _) => ValueTask.FromResult<int?>(hasStoredCost ? storedCost : 0),
            WriteContactCost = (_, cost, _) =>
            {
                hasStoredCost = true;
                storedCost = cost;
                return ValueTask.CompletedTask;
            },
        };
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("charid", new PyInt(90_001)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        long callId = 0;
        async Task<PyValue?> Call(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, ++callId, "account", method, args))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload));
        }

        Assert.Equal(new PyInt(0), await Call("GetDefaultContactCost"));
        Assert.IsType<PyNone>(await Call("SetContactCost", new PyInt(25)));
        Assert.Equal(new PyInt(25), await Call("GetDefaultContactCost"));
        Assert.IsType<PyNone>(await Call("SetContactCost", PyNone.Instance));
        Assert.True(hasStoredCost);
        Assert.Null(storedCost);
        Assert.IsType<PyNone>(await Call("GetDefaultContactCost"));
    }
}
