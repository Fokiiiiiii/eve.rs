using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Ship_bound_calls_keep_none_for_rejected_assembly_and_leave_requests_through_host_wire()
    {
        Start(
            configure: registry => registry.Register(new ShipService(null!, () => FixedNowMs)),
            strictRpc: true,
            sessionOpened: session =>
            {
                session.CharacterId = 42;
                session.Set("stationid", new PyInt(600));
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue?> Call(long callId, string method, PyValue args)
        {
            var bindArgs = Py.Tuple(
                Py.Tuple(new PyInt(600), new PyInt(15)),
                Py.Tuple(new PyStr(method), args, kwargs));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "ship", "MachoBindObject", bindArgs, kwargs))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        var assemble = Assert.IsType<PyTuple>(await Call(
            1, "AssembleShip", Py.Tuple(new PyList([new PyLong(7001)]))));
        Assert.Same(PyNone.Instance, assemble.Items[1]);

        var leave = Assert.IsType<PyTuple>(await Call(
            2, "LeaveShip", Py.Tuple(new PyLong(0))));
        Assert.Same(PyNone.Instance, leave.Items[1]);
    }
}
