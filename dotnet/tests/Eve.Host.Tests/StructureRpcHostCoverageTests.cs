using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Structure_control_delivery_deployment_and_beacon_calls_use_registered_host_routes()
    {
        var observed = new List<string>();
        var callbacks = new StructureRpcCallbacks
        {
            DropOffItemsAsync = (_, ids, shipId, structureId) =>
            {
                observed.Add($"drop:{shipId}:{structureId}:{string.Join(',', ids)}");
                return ValueTask.FromResult<IReadOnlyList<long>>([ids[0], ids[^1]]);
            },
            GetStructurePilotAsync = (_, structureId) =>
            {
                observed.Add($"pilot:{structureId}");
                return ValueTask.FromResult<long?>(structureId == 7001 ? 9001 : null);
            },
            TakeControlAsync = (_, structureId) =>
            {
                observed.Add($"take:{structureId}");
                return ValueTask.CompletedTask;
            },
            BoardStructureAsync = (_, structureId) =>
            {
                observed.Add($"board:{structureId}");
                return ValueTask.CompletedTask;
            },
            EjectFromStructureAsync = (_, shipId) =>
            {
                observed.Add($"eject:{shipId}");
                return ValueTask.CompletedTask;
            },
            ReleaseControlAsync = _ =>
            {
                observed.Add("release");
                return ValueTask.CompletedTask;
            },
            DeliverAsync = (_, structureId, recipientId, ids) =>
            {
                observed.Add($"deliver:{structureId}:{recipientId}:{string.Join(',', ids)}");
                return ValueTask.CompletedTask;
            },
            AnchorAsync = (_, request) =>
            {
                observed.Add($"anchor:{request.ItemId}:{request.StructureName}:{request.ReinforceWeekday}:{request.ReinforceHour}");
                return ValueTask.CompletedTask;
            },
        };
        var services = new IService[]
        {
            new StructureRpcService("structureControl", callbacks),
            new StructureRpcService("structureCargoDelivery", callbacks),
            new StructureRpcService("structureDeliveries", callbacks),
            new StructureRpcService("structureDeployment", callbacks),
            new StructureTravelRpcService("structureCynoBeaconMgr", _ =>
            {
                observed.Add("beacon");
                return ValueTask.FromResult<PyValue>(PyNone.Instance);
            }),
        };

        Start(
            configure: registry =>
            {
                foreach (var service in services) registry.Register(service);
            },
            sessionOpened: session => session.Set("structureid", new PyInt(7001)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(string service, string method, PyTuple args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, (uint)(observed.Count + 1), service, method, args, kwargs))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)) ?? PyNone.Instance;
        }

        Assert.Equal(new PyInt(9001), await Call("structureControl", "GetStructurePilot", Py.Tuple(new PyInt(7001))));
        Assert.Equal(PyNone.Instance, await Call("structureControl", "TakeControl", Py.Tuple(new PyInt(7001))));
        Assert.Equal(PyNone.Instance, await Call("structureControl", "BoardStructure", Py.Tuple(new PyInt(7002))));
        Assert.Equal(PyNone.Instance, await Call("structureControl", "EjectFromStructure", Py.Tuple(new PyInt(7003))));
        Assert.Equal(PyNone.Instance, await Call("structureControl", "ReleaseControl", Py.Tuple()));

        var moved = Assert.IsType<PyList>(await Call(
            "structureCargoDelivery",
            "DropOffItems",
            Py.Tuple(new PyList([new PyInt(11), new PyInt(12), new PyInt(13)]), new PyInt(7004), new PyInt(7005))));
        Assert.Equal(new PyList([new PyInt(11), new PyInt(13)]), moved);

        Assert.Equal(PyNone.Instance, await Call(
            "structureDeliveries",
            "Deliver",
            Py.Tuple(new PyInt(7001), new PyInt(9002), new PyList([new PyInt(21), new PyInt(22)]))));
        Assert.Equal(PyNone.Instance, await Call(
            "structureDeployment",
            "Anchor",
            Py.Tuple(
                new PyInt(8001), new PyReal(1), new PyReal(2), new PyReal(0.5), new PyInt(7001),
                new PyStr("Structure"), new PyStr("bio"), new PyInt(3), new PyInt(17), new PyDict([]))));
        Assert.Equal(PyNone.Instance, await Call(
            "structureCynoBeaconMgr",
            "CmdJumpToStructureBeacon",
            Py.Tuple(new PyInt(7001), new PyInt(30000142))));

        Assert.Equal(
            [
                "pilot:7001", "take:7001", "board:7002", "eject:7003", "release",
                "drop:7004:7005:11,12,13", "deliver:7001:9002:21,22",
                "anchor:8001:Structure:3:17", "beacon",
            ],
            observed);
    }
}
