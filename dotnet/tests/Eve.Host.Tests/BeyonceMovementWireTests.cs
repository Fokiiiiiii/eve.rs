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

    [Fact]
    public async Task Beyonce_align_to_resolves_the_client_bookmark_keyword_through_bound_host_wire()
    {
        const long characterId = 90_000_011;
        const long systemId = 31_000_005;
        const long bookmarkId = 41_000_001;
        long? resolvedBookmark = null;
        var service = new BeyonceService(null!, () => FixedNowMs)
        {
            BookmarkTargetForCharacter = (call, requestedId, _) =>
            {
                Assert.Equal(characterId, call.Session.CharacterId);
                Assert.Equal("CmdAlignTo", call.Method);
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
    }

    [Fact]
    public async Task Beyonce_warp_to_bookmark_without_a_space_pilot_returns_none_through_bound_host_wire()
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

        var response = Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        Assert.Same(PyNone.Instance, response);
        Assert.Null(resolvedBookmark);
    }
}
