using Eve.Services;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Empty_fleet_damage_subscription_clears_watchlist_without_membership()
    {
        ClientSession? session = null;
        Start(
            configure: registry => registry.Register(new FleetManagerService(new FleetStore(null!))),
            strictRpc: true,
            sessionOpened: opened =>
            {
                session = opened;
                opened.Set("charid", new PyLong(3_000_001));
                opened.Set("fleetWatchlist", new PyList([new PyLong(3_000_002)]));
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        static PyObjectEx WireSet(params PyValue[] values) => new(
            ObjectExKind.Ex1,
            new PyTuple([new PyToken("__builtin__.set"), new PyList([new PyList(values)])]),
            [],
            []);

        async Task<PyValue> RawCall(long callId, PyValue favorites)
        {
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, callId, "fleetMgr", "RegisterForDamageUpdates",
                    new PyTuple([favorites]), kwargs))));
            return MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        }

        Assert.Equal(PyBool.True, Result(await RawCall(1, WireSet())));
        Assert.Empty(Assert.IsType<PyList>(session!.Get("fleetWatchlist")).Items);

        session.Set("fleetWatchlist", new PyList([new PyLong(3_000_003)]));
        Assert.Equal(PyBool.True, Result(await RawCall(2, PyNone.Instance)));
        Assert.Empty(Assert.IsType<PyList>(session.Get("fleetWatchlist")).Items);

        session.Set("fleetWatchlist", new PyList([new PyLong(3_000_004)]));
        var malformedResponse = await RawCall(3, new PyStr("not-a-list"));
        var errorState = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(malformedResponse).Args).Items;
        Assert.Equal((int)MachoMessage.ErrorResponse, Assert.IsType<PyInt>(errorState[0]).Value);
        Assert.Equal(3_000_004, Assert.IsType<PyLong>(
            Assert.Single(Assert.IsType<PyList>(session.Get("fleetWatchlist")).Items)).Value);
    }
}
