using System.Reflection;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;
using Eve.Universe;

namespace Eve.Services.Space.Tests;

public sealed class StargateJumpWireTests
{
    private const int System = 30_100_001;
    private const int DestinationSystem = 30_100_002;
    private const long Gate = 50_000_001;
    private const long DestinationGate = 50_000_002;
    private const long Ship = 60_000_001;
    private const long Character = 20;
    private const long UnixMs = 1_800_000_000_000;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task Bound_client_warp_then_stargate_jump_uses_the_wire_and_clears_warp_state()
    {
        var sim = new FakeSim
        {
            WarpAnswer = new CommandReply(true, 0, null),
            JumpAnswer = new CommandReply(true, 0, null, null, DestinationSystem, DestinationGate)
        };
        using var space = new SpaceSessions(sim, null!, null!, null!, new SolarSystemCatalog([]), () => UnixMs);
        var sink = new Sink();
        var session = new ClientSession(sink) { SessionId = 7, ClientId = 7, CharacterId = Character, UserId = 1 };
        session.Set("shipid", new PyLong(Ship));
        session.Set("solarsystemid", new PyInt(System));
        session.Set("solarsystemid2", new PyInt(System));
        SeedPilotAndObserver(space, session);

        var beyonce = new BeyonceService(space, () => UnixMs);
        var registry = new ServiceRegistry();
        registry.Register(beyonce);
        var dispatcher = new PacketDispatcher(registry, () => UnixMs);

        Assert.True(await dispatcher.DispatchAsync(CallPacket(
            "MachoBindObject", new PyTuple([new PyInt(System), new PyInt(5),]), PyNone.Instance, callId: 1), session));
        var bindPackets = sink.Sent.ToArray().OfType<PyObject>().ToArray();
        Assert.Equal([MachoMessage.Notification, MachoMessage.CallRsp], bindPackets.Select(MessageType));
        var bindResponse = Assert.Single(bindPackets, packet => MessageType(packet) == MachoMessage.CallRsp);
        var bound = Assert.IsType<PyTuple>(CallResult(bindResponse));
        var oid = Rig.OidOf(bound.Items[0]);
        Assert.NotEmpty(oid);
        sink.Sent.Clear();

        Assert.True(await dispatcher.DispatchAsync(CallPacket(
            "CmdWarpToStuffAutopilot", new PyTuple([new PyLong(Gate)]), PyNone.Instance, callId: 2, oid), session));
        Assert.True(space.IsWarping(session));
        var warpResponse = Assert.Single(sink.Sent.ToArray().OfType<PyObject>());
        Assert.Equal(MachoMessage.CallRsp, MessageType(warpResponse));
        Assert.Equal(PyNone.Instance, CallResult(warpResponse));
        sink.Sent.Clear();

        Assert.True(await dispatcher.DispatchAsync(CallPacket(
            "CmdStargateJump",
            new PyTuple([new PyLong(Gate), new PyLong(DestinationGate), new PyLong(Ship)]),
            PyNone.Instance,
            callId: 3,
            oid), session));

        Assert.False(space.IsWarping(session));
        var packets = sink.Sent.ToArray().OfType<PyObject>().ToArray();
        Assert.Equal([MachoMessage.Notification, MachoMessage.CallRsp], packets.Select(MessageType));
        var notification = Assert.Single(Events.Of(session, clear: false), item => item.Name == "OnJumpQueueUpdate");
        Assert.Equal(DestinationSystem, Events.Number(notification.Args[0]));
        Assert.Equal(1000, Events.Number(notification.Args[1]));
        Assert.Equal(Character, Events.Number(Assert.Single(Assert.IsType<PyTuple>(notification.Args[2]).Items)));
        Assert.Equal(180, Events.Number(notification.Args[4]));

        var result = Assert.IsType<PyTuple>(CallResult(Assert.Single(packets, packet => MessageType(packet) == MachoMessage.CallRsp)));
        Assert.Equal(oid, Assert.IsType<PyStr>(result.Items[0]).Value);
        Assert.IsType<PyLong>(result.Items[1]);

        var commands = sim.Heard.OfType<SimCommand>().ToArray();
        Assert.Equal(2, commands.Length);
        Assert.Equal(SimCommands.WarpTo, commands[0].Code);
        Assert.Equal(new double[] { Gate, 10_000 }, commands[0].Args.ToArray());
        Assert.Equal(SimCommands.Jump, commands[1].Code);
        Assert.Equal(new double[] { Gate, DestinationGate }, commands[1].Args.ToArray());
    }

    private static void SeedPilotAndObserver(SpaceSessions space, ClientSession session)
    {
        var pilotType = typeof(SpaceSessions).GetNestedType("Pilot", BindingFlags.NonPublic)!;
        var pilot = pilotType.GetConstructors(PrivateInstance | BindingFlags.Public)
            .Single()
            .Invoke([session, Character, Ship, 1001, System, 0L, 0L, false]);
        AddPrivateEntry(space, "_pilots", (ulong)session.SessionId, pilot);
        AddPrivateEntry(space, "_structureObservers", (ulong)session.SessionId, session);
    }

    private static void AddPrivateEntry(SpaceSessions space, string fieldName, ulong key, object value)
    {
        var dictionary = typeof(SpaceSessions).GetField(fieldName, PrivateInstance)!.GetValue(space)!;
        var tryAdd = dictionary.GetType().GetMethods()
            .Single(method => method.Name == "TryAdd" && method.GetParameters().Length == 2);
        Assert.True((bool)tryAdd.Invoke(dictionary, [key, value])!);
    }

    private static PyValue CallPacket(string method, PyValue args, PyValue kwargs, long callId, string? oid = null)
    {
        var body = new PyTuple([
            oid is null ? new PyInt(1) : new PyStr(oid),
            new PyStr(method),
            args,
            kwargs
        ]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        var destination = oid is null
            ? new PyTuple([new PyInt(8), new PyStr("beyonce"), PyNone.Instance])
            : new PyTuple([new PyInt(8), PyNone.Instance, PyNone.Instance]);
        var fields = new PyValue[]
        {
            new PyInt(MachoMessage.CallReq),
            new PyTuple([new PyInt(2), new PyLong(7), new PyInt(callId), PyNone.Instance]),
            destination,
            new PyLong(1),
            payload,
            PyNone.Instance
        };
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([.. fields, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - fields.Length)]));
    }

    private static int MessageType(PyObject packet) =>
        (int)Assert.IsType<PyInt>(Assert.IsType<PyTuple>(packet.Args).Items[0]).Value;

    private static PyValue CallResult(PyObject packet)
    {
        var state = Assert.IsType<PyTuple>(packet.Args).Items;
        var payload = Assert.IsType<PyTuple>(state[4]);
        return Assert.IsType<PySubStream>(payload.Items[0]).Decoded!;
    }
}
