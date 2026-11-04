using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Fleet_rows_01559_to_01610_use_registered_services_and_client_wire_shapes()
    {
        var store = new FleetStore(null!);
        ClientSession? session = null;
        Start(
            configure: registry =>
            {
                registry.Register(new FleetObjectService(store, () => FixedNowMs));
                registry.Register(new FleetManagerService(store));
            },
            strictRpc: true,
            sessionOpened: opened =>
            {
                session = opened;
                opened.Set("fleetWatchlist", new PyList([new PyLong(30_000_001)]));
            });
        var (front, open) = await OpenFrontAsync();

        async Task<PyValue> Call(long id, string service, string method, params PyValue[] args) =>
            await HostRpcAsync(front, open, id, service, method, args);

        async Task<string> Error(long id, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, id, "fleetMgr", method, new PyTuple(args), TransportKwargs()))));
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(send.Payload);
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);
            return Encoding.ASCII.GetString(send.Payload.Span);
        }

        static PyObjectEx WireSet(params PyValue[] ids) => new(
            ObjectExKind.Ex1,
            new PyTuple([new PyToken("__builtin__.set"), new PyList([new PyList(ids)])]),
            [],
            []);

        Assert.Same(PyNone.Instance, await Call(1, "fleetMgr", "ForceLeaveFleet"));
        Assert.Contains("FleetNotInFleet", await Error(2, "AddToWatchlist",
            new PyList([new PyLong(30_000_002)]), new PyList([new PyLong(30_000_001)])));
        Assert.Contains("FleetNotInFleet", await Error(3, "RemoveFromWatchlist",
            new PyLong(30_000_001), new PyList([new PyLong(30_000_002)])));
        Assert.Same(PyNone.Instance, await Call(4, "fleetMgr", "BroadcastToBubble",
            new PyStr("EnemySpotted"), new PyInt(3), PyNone.Instance, PyNone.Instance));
        Assert.Same(PyNone.Instance, await Call(5, "fleetMgr", "BroadcastToSystem",
            new PyStr("EnemySpotted"), new PyInt(3), PyNone.Instance, PyNone.Instance));
        Assert.Equal(PyBool.True, await Call(6, "fleetMgr", "RegisterForDamageUpdates", WireSet()));
        Assert.Empty(Assert.IsType<PyList>(session!.Get("fleetWatchlist")).Items);
        Assert.Equal(PyBool.True, await Call(7, "fleetMgr", "RegisterForDamageUpdates", PyNone.Instance));

        session.Set("fleetWatchlist", new PyList([new PyLong(30_000_001)]));
        Assert.Contains("FleetError", await Error(8, "RegisterForDamageUpdates", new PyStr("not-a-set")));
        Assert.Equal(new PyList([new PyLong(30_000_001)]), session.Get("fleetWatchlist"));

        var normalBurst = WireSet(Enumerable.Range(1, 100).Select(id => (PyValue)new PyLong(30_000_000 + id)).ToArray());
        Assert.Contains("FleetNotInFleet", await Error(9, "RegisterForDamageUpdates", normalBurst));

        Assert.Same(PyNone.Instance, await Call(10, "fleetObjectHandler", "CreateFleet"));
        Assert.Same(PyNone.Instance, await Call(11, "fleetObjectHandler", "Init", PyNone.Instance, PyNone.Instance));
        Assert.Same(PyNone.Instance, await Call(12, "fleetObjectHandler", "GetFleetID"));
        Assert.Same(PyNone.Instance, await Call(13, "fleetObjectHandler", "ChangeSquadName", new PyLong(1), new PyWString("Alpha")));
        Assert.Same(PyNone.Instance, await Call(14, "fleetObjectHandler", "ChangeWingName", new PyLong(1), new PyWString("Alpha")));
        Assert.Same(PyNone.Instance, await Call(15, "fleetObjectHandler", "CreateSquad", new PyLong(1)));
        Assert.Same(PyNone.Instance, await Call(16, "fleetObjectHandler", "CreateWing"));
        Assert.Same(PyNone.Instance, await Call(17, "fleetObjectHandler", "DeleteSquad", new PyLong(1)));
        Assert.Same(PyNone.Instance, await Call(18, "fleetObjectHandler", "DeleteWing", new PyLong(1)));
        Assert.Same(PyNone.Instance, await Call(19, "fleetObjectHandler", "DisbandFleet"));
        Assert.Equal(new PyList([]), await Call(20, "fleetObjectHandler", "GetFleetComposition"));
        Assert.Same(PyNone.Instance, await Call(21, "fleetObjectHandler", "GetFleetID"));
        Assert.Same(PyNone.Instance, await Call(22, "fleetObjectHandler", "SetTakesFleetWarp", PyBool.True));
        Assert.Same(PyNone.Instance, await Call(23, "fleetObjectHandler", "UpdateMemberInfo", new PyInt(587)));
    }
}
