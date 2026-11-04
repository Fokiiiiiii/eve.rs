using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Beyonce_diagnostics_and_formations_use_registered_host_routes()
    {
        const long systemId = 31_000_005;
        const long itemId = 7123;
        var seenSystem = 0L;
        var service = new BeyonceService(null!, () => FixedNowMs)
        {
            BubbleIdForItem = (_, requested, _) =>
                ValueTask.FromResult<long?>(requested == itemId ? 987654 : null),
            CynoJammerOnlineSimTimeForSystem = (system, _) =>
            {
                seenSystem = system;
                return ValueTask.FromResult<long?>(1234567);
            },
        };
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
                session.Set("role", new PyLong(0));
                session.Set("rolesAtAll", new PyLong(GameMasterRoles.Staff));
                session.Set("shipid", new PyLong(itemId));
                session.Set("ballparkid", new PyLong(987654));
                session.Set("solarsystemid", new PyLong(systemId));
                session.Set("solarsystemid2", new PyLong(systemId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "beyonce", "AdminGetBubbleID", Py.Tuple(new PyLong(itemId)), kwargs))));
        Assert.Equal(new PyLong(987654), Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 2, "beyonce", "GetCynoJammerState", Py.Tuple(), kwargs))));
        Assert.Equal(new PyLong(1234567), Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Equal(systemId, seenSystem);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 3, "beyonce", "GetFormations", Py.Tuple(), kwargs))));
        var cached = Assert.IsType<PyObject>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.Equal(ObjectCaching.ResultClass, JsSemantics.StringValue(cached.Name));
        var formations = Assert.IsType<PyTuple>(((PySubStream)((PyTuple)cached.Args).Items[1]).Decoded);
        Assert.Equal(["Diamond", "Arrow"], formations.Items.Select(
            formation => JsSemantics.StringValue(Assert.IsType<PyTuple>(formation).Items[0])));
    }
}
