using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Keeper_gate_activation_forwards_the_gate_and_returns_the_ignored_client_result()
    {
        const long characterId = 20;
        const long gateId = 30000142;
        long? activatedGateId = null;
        long? activatedCharacterId = null;

        Start(configure: registry => registry.Register(new KeeperService(new KeeperRpcCallbacks(
            ActivateAccelerationGateAsync: (session, gate, _) =>
            {
                activatedGateId = gate is PyLong number ? number.Value : null;
                activatedCharacterId = session.GetInt("charid");
                return ValueTask.FromResult<PyValue?>(PyNone.Instance);
            }))), sessionOpened: session => session.Set("charid", new PyInt(characterId)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.Call(open, 1, "keeper", "ActivateAccelerationGate", [new PyLong(gateId)]))));

        Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Equal(gateId, activatedGateId);
        Assert.Equal(characterId, activatedCharacterId);
    }
}
