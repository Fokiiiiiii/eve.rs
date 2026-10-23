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
    }

    private sealed class InventoryMutationService(Action<ClientSession, long> mutate) : IService
    {
        public string Name => "invbroker";
        public bool HasMethod(string method) => method == "Add";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            var quantity = Assert.IsType<PyInt>(call.Arg(0)).Value;
            mutate(call.Session, quantity);
            call.Session.SendNotification("OnItemChange", "clientID", new PyInt(44), new PyDict([]), PyNone.Instance);
            return ValueTask.FromResult<PyValue>(new PyInt(44));
        }
    }

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method, Args = args, Session = session,
    };

    private static ClientSession NewSession(long charId, long clientId, Capture capture)
    {
        var session = new ClientSession(capture) { ClientId = clientId, UserId = charId, NodeId = 65_450 };
        session.CharacterId = charId;
        return session;
    }

    private static PyValue Request(long inputLocationId) => Py.KeyVal(
        ("blueprintID", new PyInt(10)), ("blueprintTypeID", new PyInt(681)),
        ("activityID", new PyInt(1)), ("facilityID", new PyInt(20)),
        ("inputLocation", Py.KeyVal(
            ("itemID", new PyInt(inputLocationId)), ("ownerID", new PyInt(20)),
            ("flagID", new PyInt(4)), ("canTake", PyBool.True))));

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
        var first = Assert.IsType<PyTuple>(await monitors.CallAsync(Call("ConnectJob", session1, Request(900))));
        var second = Assert.IsType<PyTuple>(await monitors.CallAsync(Call("ConnectJob", session2, Request(901))));
        Assert.Equal(new PyInt(1), first.Items[0]);
        Assert.Equal(new PyInt(2), second.Items[0]);

        var inventory = new IndustryInventoryChangePublisher(
            new InventoryMutationService((_, quantity) => quantities[900] = quantity), monitors);
        await inventory.CallAsync(Call("Add", session1, new PyInt(8)));

        Assert.Equal(["OnItemChange", "OnIndustryMaterials"], capture1.Packets.Select(packet => Notification(packet).Name));
        var notice = Notification(capture1.Packets[1]);
        Assert.Equal("OnIndustryMaterials", notice.Name);
        Assert.Equal(new PyStr("clientID"), notice.IdType);
        Assert.Equal(new PyInt(20), notice.UserId);
        Assert.Equal(first.Items[0], notice.Args[0]);
        Assert.Contains(Assert.IsType<PyDict>(notice.Args[1]).Entries,
            entry => entry.Key == new PyInt(34) && entry.Value == new PyInt(8));
        Assert.Empty(capture2.Packets);

        await inventory.CallAsync(Call("Add", session1, new PyInt(8)));
        Assert.Equal(["OnItemChange", "OnIndustryMaterials", "OnItemChange"], capture1.Packets.Select(packet => Notification(packet).Name));
        Assert.Empty(capture2.Packets);
    }

    [Fact]
    public async Task Disconnect_and_reconnect_replace_the_session_monitor()
    {
        var quantity = 5L;
        var monitors = new IndustryMonitorRpcService(availableMaterials: (_, _) =>
            ValueTask.FromResult<IReadOnlyDictionary<int, long>>(new Dictionary<int, long> { [34] = quantity }));
        var capture = new Capture();
        var session = NewSession(20, 2001, capture);
        var first = Assert.IsType<PyTuple>(await monitors.CallAsync(Call("ConnectJob", session, Request(900))));
        await monitors.CallAsync(Call("DisconnectJob", session, first.Items[0]));
        var second = Assert.IsType<PyTuple>(await monitors.CallAsync(Call("ConnectJob", session, Request(901))));
        quantity = 9;
        await monitors.InventoryChangedAsync(session, CancellationToken.None);

        var noticePacket = Assert.Single(capture.Packets, packet => Notification(packet).Name == "OnIndustryMaterials");
        var notice = Notification(noticePacket);
        Assert.Equal(second.Items[0], notice.Args[0]);
        Assert.NotEqual(first.Items[0], notice.Args[0]);
        Assert.Equal(new PyInt(9), Assert.IsType<PyDict>(notice.Args[1]).Entries.Single().Value);
    }
}