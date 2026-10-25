using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Structure_cyno_travel_uses_the_host_route_with_transport_macho_version()
    {
        string? calledMethod = null;
        var bridgeResult = PyBool.True;
        var groupJumpResult = new PyTuple([new PyStr("N=ProxyNode:structureCynoBeaconMgr"), new PyLong(123456789)]);
        var travel = new StructureBeaconTravel(
            (_, _) => ValueTask.FromResult<StructureBeaconInfo?>(
                new(StructureBeaconTravel.PharoluxCynoBeaconType, 30_000_142, true)),
            (_, _) => ValueTask.FromResult(false),
            (_, _, _, _) => ValueTask.FromResult(true),
            (_, _, _, _, _, _) => Task.FromResult<PyValue>(PyNone.Instance),
            bridge: (call, _, _) =>
            {
                calledMethod = call.Method;
                return ValueTask.FromResult<PyValue>(bridgeResult);
            },
            groupJump: (call, _, _) =>
            {
                calledMethod = call.Method;
                return ValueTask.FromResult<PyValue>(groupJumpResult);
            });
        Start(
            configure: registry => registry.Register(
                new StructureTravelRpcService("structureCynoBeaconMgr", travel.TravelAsync)),
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(90_001));
                session.Set("solarsystemid2", new PyInt(30_000_001));
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        var requestId = 1;
        foreach (var method in new[] { "CmdBridgeToStructureBeacon", "CmdGroupJumpToStructureBeacon" })
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open,
                requestId++,
                "structureCynoBeaconMgr",
                method,
                new PyTuple([new PyLong(90_002), new PyInt(30_000_142)]),
                new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))])))));

            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var response = MarshalCodec.Decode(reply.Payload);
            Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(response)).Type);
            Assert.Equal(method, calledMethod);
            if (method == "CmdBridgeToStructureBeacon")
            {
                Assert.Same(bridgeResult, Result(response));
            }
            else
            {
                var returned = Assert.IsType<PyTuple>(Result(response));
                Assert.IsType<PyBytes>(returned.Items[0]);
                Assert.Equal(123456789L, Assert.IsType<PyLong>(returned.Items[1]).Value);
            }
        }
    }
}
