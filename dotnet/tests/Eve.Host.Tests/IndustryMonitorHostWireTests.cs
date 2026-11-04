using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Industry_monitor_connect_disconnect_and_material_notice_cross_the_registered_host_wire()
    {
        var quantity = 5L;
        var monitor = new IndustryMonitorRpcService(availableMaterials: (_, _) =>
            ValueTask.FromResult<IReadOnlyDictionary<int, long>>(new Dictionary<int, long> { [34] = quantity }));
        ClientSession? monitoredSession = null;
        Start(
            configure: registry => registry.Register(monitor),
            sessionOpened: session =>
            {
                session.CharacterId = 90_000_003;
                monitoredSession = session;
            });

        var (front, open) = await OpenFrontAsync();
        var connected = Assert.IsType<PyTuple>(await HostRpcAsync(
            front, open, 1, "industryMonitor", "ConnectJob",
            Py.KeyVal(
                ("blueprintID", new PyLong(52_001)),
                ("blueprintTypeID", new PyInt(600)),
                ("activityID", new PyInt(1)),
                ("facilityID", new PyLong(60_001)),
                ("inputLocation", Py.KeyVal(
                    ("itemID", new PyLong(60_001)),
                    ("ownerID", new PyLong(90_000_003)),
                    ("flagID", new PyInt(4)),
                    ("canTake", PyBool.True))))));
        var monitorId = connected.Items[0];
        Assert.Equal(new PyInt(1), monitorId);
        Assert.Contains(Assert.IsType<PyDict>(connected.Items[1]).Entries,
            entry => entry.Key == new PyInt(34) && entry.Value == new PyInt(5));

        quantity = 4;
        await monitor.InventoryChangedAsync(monitoredSession!, CancellationToken.None);
        var notification = Assert.IsType<Send>(await front.ReceiveAsync());
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(notification.Payload)));
        Assert.Equal(MachoMessage.Notification, packet.Type);
        Assert.Equal("OnIndustryMaterials", packet.Dest.BroadcastId);
        var arguments = NotificationArguments(packet);
        Assert.Equal(2, arguments.Count);
        Assert.Equal(monitorId, arguments[0]);
        Assert.Contains(Assert.IsType<PyDict>(arguments[1]).Entries,
            entry => entry.Key == new PyInt(34) && entry.Value == new PyInt(4));

        Assert.Same(PyNone.Instance, await HostRpcAsync(
            front, open, 2, "industryMonitor", "DisconnectJob", monitorId));
    }
}
