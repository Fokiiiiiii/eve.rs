using System.Collections.Concurrent;
using System.Reflection;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Space;
using Eve.Universe;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Wormhole_cancel_from_production_space_composition_reaches_the_registered_host_route()
    {
        const long characterId = 9002;
        using var timers = new Eve.Scheduler.LocalTimers();
        var simulation = DispatchProxy.Create<ISimulation, InertWormholeSimulation>();
        using var space = new SpaceSessions(simulation, null!, null!, new StationCatalog([]), new SolarSystemCatalog([]), () => FixedNowMs,
            localTimers: timers);
        var composition = new CharacterServicesComposition(
            null!, null!, null!, () => FixedNowMs, null!, null!, null!, null!, "", null!, null!, null!);
        void SetField(string name, object? value) => typeof(CharacterServicesComposition)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(composition, value);
        SetField("spaceSessions", space);
        var createService = typeof(CharacterServicesComposition).GetMethod("CreateWormholeMgrService", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var wormholeMgr = Assert.IsType<WormholeMgrService>(createService.Invoke(composition, null));
        var warping = (ConcurrentDictionary<ulong, byte>)typeof(SpaceSessions)
            .GetField("_warping", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(space)!;
        Start(
            configure: registry => registry.Register(wormholeMgr),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                warping[(ulong)session.SessionId] = 0;
            });

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open,
            1,
            "wormholeMgr",
            "WormholeJump",
            new PyTuple([new PyLong(654)]),
            new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))])))));

        var packets = new[]
        {
            Assert.IsType<Send>(await front.ReceiveAsync()),
            Assert.IsType<Send>(await front.ReceiveAsync()),
        }.Select(send => (Send: send, Packet: Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload))))).ToArray();
        var response = Assert.Single(packets, item => item.Packet.Type == MachoMessage.CallRsp).Send;
        var notification = Assert.Single(packets, item => item.Packet.Type == MachoMessage.Notification).Packet;
        Assert.Equal(PyNone.Instance, Result(MarshalCodec.Decode(response.Payload)));
        Assert.Equal("OnWormholeJumpCancel", notification.Dest.BroadcastId);
        Assert.Equal("clientID", notification.Dest.IdType);
    }

    [Fact]
    public async Task Wormhole_jump_cancel_notification_and_none_result_cross_the_registered_host_route()
    {
        var service = new WormholeMgrService(spaceSessions: null, isPilotWarpLandingPending: _ => true);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = 9001);

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open,
            1,
            "wormholeMgr",
            "WormholeJump",
            new PyTuple([new PyLong(654)]),
            new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))])))));

        var packets = new[]
        {
            Assert.IsType<Send>(await front.ReceiveAsync()),
            Assert.IsType<Send>(await front.ReceiveAsync()),
        }.Select(send => (Send: send, Packet: Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload))))).ToArray();
        var response = Assert.Single(packets, item => item.Packet.Type == MachoMessage.CallRsp).Send;
        var notification = Assert.Single(packets, item => item.Packet.Type == MachoMessage.Notification).Packet;

        Assert.Equal(PyNone.Instance, Result(MarshalCodec.Decode(response.Payload)));
        Assert.Equal("OnWormholeJumpCancel", notification.Dest.BroadcastId);
        Assert.Equal("clientID", notification.Dest.IdType);
    }

    [Fact]
    public async Task Wormhole_polarization_returns_client_timer_fields_through_registered_host()
    {
        long readEndpointId = 0;
        long readCharacterId = 0;
        var service = new WormholeMgrService(
            spaceSessions: null,
            readPolarization: (endpointId, characterId, _) =>
            {
                readEndpointId = endpointId;
                readCharacterId = characterId;
                return ValueTask.FromResult<WormholePolarization?>(
                    endpointId == 654 ? new WormholePolarization(1_900_000_000_000, 300) : null);
            });
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = 9001);

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> Call(long callId, long endpointId)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open,
                callId,
                "wormholeMgr",
                "GetWormholePolarization",
                new PyTuple([new PyLong(endpointId)]),
                new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))])))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload))!;
        }

        Assert.Equal(
            PyList.Of(new PyLong(Py.FileTimeFromUnixMs(1_900_000_000_000)), new PyInt(300)),
            await Call(1, 654));
        Assert.Equal(654, readEndpointId);
        Assert.Equal(9001, readCharacterId);
        Assert.Equal(PyNone.Instance, await Call(2, 655));
    }
}

public class InertWormholeSimulation : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var returnType = method?.ReturnType ?? typeof(void);
        if (returnType == typeof(void)) return null;
        if (returnType == typeof(Task)) return Task.CompletedTask;
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}
