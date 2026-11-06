using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Space;
using Eve.Universe;
using System.Reflection;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Beyonce_unsupported_warp_subjects_are_refused_through_the_registered_host_wire()
    {
        const long characterId = 90_000_010;
        const long systemId = 31_000_004;
        const long shipId = 80_000_010;
        var simulation = DispatchProxy.Create<ISimulation, MovementSimulation>();
        var space = new SpaceSessions(simulation, null!, null!, new StationCatalog([]),
            new SolarSystemCatalog([new SolarSystemInfo((int)systemId, "Test", 20_000_001, 10_000_001, 0.5)]), () => FixedNowMs);
        Start(
            configure: registry => registry.Register(new BeyonceService(space, () => FixedNowMs)),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("solarsystemid", new PyLong(systemId));
                session.Set("solarsystemid2", new PyLong(systemId));
                space.EnterPilotForTest(session, characterId, shipId, 587, (int)systemId);
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        long callId = 1;

        async Task<string> WarpFailureCode(string subject)
        {
            var nested = Py.Tuple(new PyStr("CmdWarpToStuff"), Py.Tuple(new PyStr(subject), new PyLong(77_000_001)), new PyDict([]));
            var bindArgs = Py.Tuple(Py.Tuple(new PyLong(systemId), new PyInt(6)), nested);
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId++, "beyonce", "MachoBindObject", bindArgs, kwargs))));

            var packet = MachoPacket.Decode(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
            Assert.NotNull(packet);
            Assert.Equal(MachoMessage.ErrorResponse, packet!.Type);
            var state = Assert.IsType<PyTuple>(packet.Payload).Items;
            Assert.Equal((int)MachoErrorType.WrappedException, Assert.IsType<PyInt>(state[1]).Value);
            var wrapped = Assert.IsType<PyTuple>(state[2]).Items;
            var userError = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped[0]).Decoded);
            var header = Assert.IsType<PyTuple>(userError.Header).Items;
            Assert.Equal(MachoErrors.UserErrorClass, Assert.IsType<PyToken>(header[0]).Value);
            var args = Assert.IsType<PyTuple>(header[1]).Items;
            return Encoding.UTF8.GetString(Assert.IsType<PyBytes>(args[0]).Value.Span);
        }

        foreach (var subject in new[] { "moonMiningPoint", "operationDungeon", "externalDungeon", "epinstance", "mercenaryDen" })
            Assert.Equal("CustomNotify", await WarpFailureCode(subject));
    }

    [Fact]
    public async Task Beyonce_refused_bind_returns_the_bound_pair_expected_by_the_client_moniker()
    {
        const long sessionSystemId = 31_000_006;
        const long requestedSystemId = 31_000_007;
        var service = new BeyonceService(null!, () => FixedNowMs);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.Set("solarsystemid", new PyLong(sessionSystemId)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var nested = Py.Tuple(new PyStr("CmdWarpToStuff"), Py.Tuple(), new PyDict([]));
        var bindArgs = Py.Tuple(Py.Tuple(new PyLong(requestedSystemId), new PyInt(6)), nested);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "beyonce", "MachoBindObject", bindArgs, kwargs))));

        var response = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Equal(2, response.Items.Count);
        Assert.IsType<PySubStruct>(response.Items[0]);
        Assert.Same(PyNone.Instance, response.Items[1]);
    }

    private class InertSimulation : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType ?? typeof(void);
            if (returnType == typeof(void)) return null;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

    private class MovementSimulation : DispatchProxy
    {
        public List<(ulong SessionId, byte Command, double[] Args)> Commands { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "Command" && args is [ulong sessionId, byte command, double[] values])
                Commands.Add((sessionId, command, values));
            return method?.ReturnType == typeof(bool) ? true : null;
        }
    }

    [Fact]
    public async Task Beyonce_align_to_resolves_and_issues_the_client_bookmark_command_through_bound_host_wire()
    {
        const long characterId = 90_000_011;
        const long systemId = 31_000_005;
        const long bookmarkId = 41_000_001;
        const long targetItemId = 50_000_122;
        const long shipId = 80_000_011;
        long? resolvedBookmark = null;
        var simulation = DispatchProxy.Create<ISimulation, MovementSimulation>();
        var recorder = (MovementSimulation)(object)simulation;
        var space = new SpaceSessions(simulation, null!, null!, new StationCatalog([]),
            new SolarSystemCatalog([new SolarSystemInfo((int)systemId, "Test", 20_000_001, 10_000_001, 0.5)]), () => FixedNowMs);
        var service = new BeyonceService(space, () => FixedNowMs)
        {
            BookmarkTargetForCharacter = (call, requestedId, _) =>
            {
                Assert.Equal(characterId, call.Session.CharacterId);
                Assert.Equal("CmdAlignTo", call.Method);
                resolvedBookmark = requestedId;
                return Task.FromResult<BookmarkWarpTarget?>(new((int)systemId, targetItemId, null, null, null));
            },
        };
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
                session.Set("solarsystemid", new PyLong(systemId));
                session.Set("solarsystemid2", new PyLong(systemId));
                session.Set("locationid", new PyInt(systemId));
                space.EnterPilotForTest(session, characterId, shipId, 587, (int)systemId);
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var transportKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var alignKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("dstID"), PyNone.Instance),
            new KeyValuePair<PyValue, PyValue>(new PyStr("bookmarkID"), new PyLong(bookmarkId)),
        ]);
        var nested = Py.Tuple(new PyStr("CmdAlignTo"), Py.Tuple(), alignKwargs);
        var boundArgs = Py.Tuple(Py.Tuple(new PyLong(systemId), new PyInt(6)), nested);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "beyonce", "MachoBindObject", boundArgs, transportKwargs))));

        var response = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Same(PyNone.Instance, response.Items[1]);
        Assert.Equal(bookmarkId, resolvedBookmark);
        var command = Assert.Single(recorder.Commands);
        Assert.Equal(SimCommands.AlignTo, command.Command);
        Assert.Equal((double)targetItemId, Assert.Single(command.Args));
    }

    [Fact]
    public async Task Beyonce_warp_to_bookmark_without_a_space_pilot_returns_bound_pair_through_host_wire()
    {
        const long characterId = 90_000_012;
        const long systemId = 31_000_006;
        const long bookmarkId = 41_000_002;
        long? resolvedBookmark = null;
        var simulation = DispatchProxy.Create<ISimulation, InertSimulation>();
        var space = new SpaceSessions(simulation, null!, null!, new StationCatalog([]), new SolarSystemCatalog([]), () => FixedNowMs);
        var service = new BeyonceService(space, () => FixedNowMs)
        {
            BookmarkTargetForCharacter = (call, requestedId, _) =>
            {
                Assert.Equal(characterId, call.Session.CharacterId);
                Assert.Equal("CmdWarpToStuff", call.Method);
                resolvedBookmark = requestedId;
                return Task.FromResult<BookmarkWarpTarget?>(null);
            },
        };
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("solarsystemid", new PyLong(systemId));
                session.Set("solarsystemid2", new PyLong(systemId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var transportKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var warpKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("minRange"), new PyInt(0)),
        ]);
        var nested = Py.Tuple(
            new PyStr("CmdWarpToStuff"),
            Py.Tuple(new PyStr("bookmark"), new PyLong(bookmarkId)),
            warpKwargs);
        var boundArgs = Py.Tuple(Py.Tuple(new PyLong(systemId), new PyInt(6)), nested);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "beyonce", "MachoBindObject", boundArgs, transportKwargs))));

        var response = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.IsType<PySubStruct>(response.Items[0]);
        Assert.Same(PyNone.Instance, response.Items[1]);
        Assert.Null(resolvedBookmark);
    }

}
