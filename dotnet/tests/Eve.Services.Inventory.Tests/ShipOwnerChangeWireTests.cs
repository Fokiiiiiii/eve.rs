using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Ships;

namespace Eve.Services.Inventory.Tests;

public sealed class ShipOwnerChangeWireTests
{
    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static PyTuple NotificationArguments(PyValue packet)
    {
        var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items;
        var envelope = Assert.IsType<PyTuple>(fields[4]).Items[0];
        var buffer = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(envelope).Items[1]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.Decode(buffer.Value));
        return Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(decoded.Items[1]).Items[1]);
    }

    private static ItemRow Row(Item item) =>
        new(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Quantity, 1, 6);

    [Theory]
    [InlineData(90000001L, 98000001L)]
    [InlineData(98000001L, 90000001L)]
    public void OnItemChange_sends_previous_owner_for_take_and_deposit(long previousOwnerId, long newOwnerId)
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 1, ClientId = 2, UserId = 3 };
        var item = new Item(70000001, 34, newOwnerId, 60000001, ItemFlags.CorpSAG1, 1, true, null);
        var change = new ItemChanged(item, 60000001, ItemFlags.Hangar, 1, previousOwnerId);

        ShipNotifications.SendItemChange(session, change, Row);

        var args = NotificationArguments(Assert.Single(capture.Packets));
        var differences = Assert.IsType<PyDict>(args.Items[1]);
        var owner = Assert.Single(differences.Entries, entry => entry.Key is PyInt { Value: 2 });
        Assert.Equal(InventoryWire.Id(previousOwnerId), owner.Value);
    }

    [Fact]
    public void OnItemsChanged_sends_previous_owner_for_fitted_item_moves()
    {
        var capture = new Capture();
        var session = new ClientSession(capture) { SessionId = 1, ClientId = 2, UserId = 3 };
        var item = new Item(70000001, 34, 90000001, 60000001, 27, 1, true, null);
        var changed = new ShipChanged(
            item.Id, 70000002,
            [new ItemMoved(item, 60000001, ItemFlags.Hangar, PreviousOwnerId: 98000001)],
            [], null);

        ShipNotifications.Send(session, item.OwnerId, changed, Row, 0);

        var args = NotificationArguments(Assert.Single(capture.Packets));
        var differences = Assert.IsType<PyDict>(args.Items[1]);
        var owner = Assert.Single(differences.Entries, entry => entry.Key is PyInt { Value: 2 });
        Assert.Equal(InventoryWire.Id(98000001), owner.Value);
    }
}
