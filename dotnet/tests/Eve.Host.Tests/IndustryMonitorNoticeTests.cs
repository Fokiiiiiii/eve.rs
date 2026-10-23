using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public sealed class IndustryMonitorNoticeTests
{
    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
        public void Clear() => Packets.Clear();
    }

    private sealed class InventoryMutationService(Action<long> mutate) : IService
    {
        public string Name => "invbroker";
        public bool HasMethod(string method) => method == "Add";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            var quantity = Assert.IsType<PyInt>(call.Arg(0)).Value;
            mutate(quantity);
            call.Session.SendNotification("OnItemChange", "clientID", new PyInt(44), new PyDict([]), PyNone.Instance);
            return ValueTask.FromResult<PyValue>(new PyInt(44));
        }
    }

    private static ClientSession NewSession(long charId, long clientId, Capture capture)
    {
        var session = new ClientSession(capture) { ClientId = clientId, UserId = charId, NodeId = 65_450 };
        session.CharacterId = charId;
        return session;
    }

    private static PyValue MonitorRequest(long inputLocationId) => Py.KeyVal(
        ("blueprintID", new PyInt(10)), ("blueprintTypeID", new PyInt(681)),
        ("activityID", new PyInt(1)), ("facilityID", new PyInt(20)),
        ("inputLocation", Py.KeyVal(
            ("itemID", new PyInt(inputLocationId)), ("ownerID", new PyInt(20)),
            ("flagID", new PyInt(4)), ("canTake", PyBool.True))));

    private static PyValue Request(long callId, string service, string method, params PyValue[] args)
    {
        var body = new PyTuple([
            PyNone.Instance,
            new PyStr(method),
            new PyTuple(args),
            PyNone.Instance,
        ]);
        var payload = new PyTuple([
            new PyTuple([PyBool.True, new PySubStream(body)]),
        ]);
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(1));
        var destination = new MachoAddress(AddressKind.Node, Service: service,
            CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([
                new PyInt(MachoMessage.CallReq),
                source.Encode(),
                destination.Encode(),
                new PyInt(7),
                payload,
                new PyDict([]),
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
                PyNone.Instance,
            ]));
    }

    private static PyValue ResponseResult(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<PyInt>(packet[0]).Value);
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(packet[4]).Items[0]).Decoded!;
    }

    private static bool IsNotification(PyValue packet) =>
        packet is PyObject { Args: PyTuple args } &&
        args.Items.Count > 0 &&
        args.Items[0] is PyInt { Value: MachoMessage.Notification };

    private static (string Name, PyValue IdType, PyValue[] Args, PyValue UserId) Notification(PyValue packet)
    {
        var state = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items;
        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(state[2]).Args).Items;
        var payload = Assert.IsType<PyTuple>(state[4]).Items[0];
        var buffer = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(payload).Items[1]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(buffer.Value));
        var eventArgs = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(decoded.Items[1]).Items[1]);
        return (Assert.IsType<PyStr>(address[1]).Value, address[3], [.. eventArgs.Items], state[3]);
    }

    private static PacketDispatcher DispatcherFor(IndustryMonitorRpcService monitors, IService inventory)
    {
        var registry = new ServiceRegistry();
        registry.Register(monitors);
        registry.Register(inventory);
        return new PacketDispatcher(registry);
    }

    [Fact]
    public async Task Host_inventory_dispatch_publishes_changed_materials_once_after_item_change_and_isolates_sessions()
    {
        var quantities = new Dictionary<long, long> { [900] = 5, [901] = 7 };
        var monitors = new IndustryMonitorRpcService(availableMaterials: (request, _) =>
            ValueTask.FromResult<IReadOnlyDictionary<int, long>>(
                new Dictionary<int, long> { [34] = quantities[request.InputLocation!.ItemId] }));
        var capture1 = new Capture();
        var capture2 = new Capture();
        var session1 = NewSession(20, 2001, capture1);
        var session2 = NewSession(21, 2002, capture2);
        var inventory = new IndustryInventoryChangePublisher(
            new InventoryMutationService(quantity => quantities[900] = quantity), monitors);
        var dispatcher = DispatcherFor(monitors, inventory);

        Assert.True(await dispatcher.DispatchAsync(Request(1, "industryMonitor", "ConnectJob", MonitorRequest(900)), session1));
        var first = Assert.IsType<PyTuple>(ResponseResult(capture1.Packets[^1]));
        Assert.True(await dispatcher.DispatchAsync(Request(2, "industryMonitor", "ConnectJob", MonitorRequest(901)), session2));
        var second = Assert.IsType<PyTuple>(ResponseResult(capture2.Packets[^1]));
        Assert.Equal(new PyInt(1), first.Items[0]);
        Assert.Equal(new PyInt(2), second.Items[0]);
        capture1.Clear();
        capture2.Clear();

        Assert.True(await dispatcher.DispatchAsync(Request(3, "invbroker", "Add", new PyInt(8)), session1));

        var notices = capture1.Packets.Where(IsNotification).Select(Notification).ToArray();
        Assert.Equal(["OnItemChange", "OnIndustryMaterials"], notices.Select(notice => notice.Name));
        var notice = notices[1];
        Assert.Equal(new PyStr("clientID"), notice.IdType);
        Assert.Equal(new PyInt(20), notice.UserId);
        Assert.Equal(first.Items[0], notice.Args[0]);
        Assert.Contains(Assert.IsType<PyDict>(notice.Args[1]).Entries,
            entry => entry.Key == new PyInt(34) && entry.Value == new PyInt(8));
        Assert.DoesNotContain(capture2.Packets, IsNotification);

        capture1.Clear();
        await dispatcher.DispatchAsync(Request(4, "invbroker", "Add", new PyInt(8)), session1);
        Assert.Equal(["OnItemChange"], capture1.Packets.Where(IsNotification).Select(packet => Notification(packet).Name));
        Assert.DoesNotContain(capture2.Packets, IsNotification);
    }

    [Fact]
    public async Task Disconnect_and_reconnect_replace_the_session_monitor_through_host_dispatch()
    {
        var quantity = 5L;
        var monitors = new IndustryMonitorRpcService(availableMaterials: (_, _) =>
            ValueTask.FromResult<IReadOnlyDictionary<int, long>>(new Dictionary<int, long> { [34] = quantity }));
        var capture = new Capture();
        var session = NewSession(20, 2001, capture);
        var inventory = new IndustryInventoryChangePublisher(
            new InventoryMutationService(value => quantity = value), monitors);
        var dispatcher = DispatcherFor(monitors, inventory);

        await dispatcher.DispatchAsync(Request(1, "industryMonitor", "ConnectJob", MonitorRequest(900)), session);
        var first = Assert.IsType<PyTuple>(ResponseResult(capture.Packets[^1]));
        capture.Clear();
        await dispatcher.DispatchAsync(Request(2, "industryMonitor", "DisconnectJob", first.Items[0]), session);
        capture.Clear();
        await dispatcher.DispatchAsync(Request(3, "industryMonitor", "ConnectJob", MonitorRequest(901)), session);
        var second = Assert.IsType<PyTuple>(ResponseResult(capture.Packets[^1]));
        capture.Clear();

        await dispatcher.DispatchAsync(Request(4, "invbroker", "Add", new PyInt(9)), session);

        var notice = Notification(Assert.Single(capture.Packets, IsNotification));
        Assert.Equal("OnIndustryMaterials", notice.Name);
        Assert.Equal(second.Items[0], notice.Args[0]);
        Assert.NotEqual(first.Items[0], notice.Args[0]);
        Assert.Equal(new PyInt(9), Assert.IsType<PyDict>(notice.Args[1]).Entries.Single().Value);
    }
}
