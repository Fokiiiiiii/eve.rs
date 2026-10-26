using System.Net;
using System.Net.Sockets;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Eve.Universe;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Movement_rpcs_use_registered_host_routes_with_client_wire_shapes()
    {
        var stance = 2;
        var callbacks = new GameRpcCallbacks(SetShipStanceAsync: (shipId, stanceId, characterId, _) =>
        {
            Assert.Equal(9001, shipId);
            Assert.Equal(3, stanceId);
            Assert.Equal(42, characterId);
            var old = stance;
            stance = stanceId;
            return ValueTask.FromResult<ShipStanceChange?>(new(true, old, Changed: true));
        });
        StartWithMovementServices(callbacks, session => session.Set("charid", new PyInt(42)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 1, "shipStanceMgr", "SetShipStance",
            new PyTuple([new PyLong(9001), new PyInt(3)]), MachoVersion()))));
        var notice = Assert.IsType<Send>(await front.ReceiveAsync());
        var noticePacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(notice.Payload)));
        Assert.Equal(MachoMessage.Notification, noticePacket.Type);
        Assert.Equal("OnStanceActive", noticePacket.Dest.BroadcastId);

        var stanceResponse = Assert.IsType<Send>(await front.ReceiveAsync());
        var stancePacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(stanceResponse.Payload)));
        Assert.Equal(MachoMessage.CallRsp, stancePacket.Type);
        Assert.Equal(3, stance);
        Assert.Equal(new PyInt(2), Result(MarshalCodec.Decode(stanceResponse.Payload)));

        async Task<Send> Invoke(uint id, string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, id, service, method, new PyTuple(args), MachoVersion()))));
            while (true)
            {
                var reply = Assert.IsType<Send>(await front.ReceiveAsync());
                var envelope = Assert.IsType<MachoPacket>(
                    MachoPacket.Decode(MarshalCodec.Decode(reply.Payload)));
                if (envelope.Type is MachoMessage.CallRsp or MachoMessage.ErrorResponse)
                    return reply;
            }
        }

        static MachoPacket Envelope(Send reply) =>
            Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(reply.Payload)));

        var eligible = await Invoke(2, "starterShipcasterTravelMgr", "CanCharacterJump");
        Assert.Equal(MachoMessage.CallRsp, Envelope(eligible).Type);
        Assert.Same(PyBool.False, Result(MarshalCodec.Decode(eligible.Payload)));

        var shipcasterJump = await Invoke(3, "shipcasterTravelMgr", "CmdJumpThroughShipcaster",
            new PyLong(77), new PyInt(30_000_001), new PyLong(90));
        Assert.Equal(MachoMessage.ErrorResponse, Envelope(shipcasterJump).Type);

        var starterJump = await Invoke(4, "starterShipcasterTravelMgr", "CmdJumpThroughStarterShipcaster",
            new PyLong(77));
        Assert.Equal(MachoMessage.ErrorResponse, Envelope(starterJump).Type);

        var voidSpaceExit = await Invoke(5, "voidSpaceMgr", "VoidSpaceEndGateActivation",
            new PyLong(9002));
        Assert.Equal(MachoMessage.ErrorResponse, Envelope(voidSpaceExit).Type);
    }

    private void StartWithMovementServices(
        GameRpcCallbacks? callbacks = null,
        Action<ClientSession>? sessionOpened = null)
    {
        var registry = new ServiceRegistry();
        Func<long> now = () => FixedNowMs;
        foreach (var service in CoreServices.Create(
            new NoAccounts(), now, new TestCalendarStore(), new TestCalendarAudienceReader(),
            gameRpcCallbacks: callbacks))
            registry.Register(service);
        var systems = new SolarSystemCatalog([]);
        registry.Register(new ShipcasterRpcService("shipcasterTravelMgr", systems));
        registry.Register(new ShipcasterRpcService("starterShipcasterTravelMgr", systems));

        var config = GlobalConfig.Build(Options);
        registry.Register(new MachoNetService(new Info(registry, config), now));
        var dispatcher = new PacketDispatcher(registry, now);
        var encoded = MarshalCodec.Encode(config);
        _server = new HostServer(
            new HostOptions
            {
                MaxFrontConnections = 1024,
                MaxPendingPacketsPerSession = 10_000,
                MaxPendingBytesPerSession = 16 * 1024 * 1024,
                MaxQueuedOutboundBytesPerSession = 8 * 1024 * 1024,
                SessionOpened = sessionOpened,
                Log = message => { lock (_log) _log.Add(message); },
            },
            dispatcher,
            new InMemoryAccountDirectory(new AccountOptions { AutoCreate = true }),
            () => encoded);
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(16);
        _serving = _server.ServeAsync(_listener, _cancel.Token);
    }

    private static PyDict MachoVersion() => new([
        new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
    ]);
}
