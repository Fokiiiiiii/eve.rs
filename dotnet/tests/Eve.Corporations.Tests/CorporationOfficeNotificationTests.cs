using Eve.Corporations;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;

namespace Eve.Corporations.Tests;

public sealed class CorporationOfficeNotificationTests
{
    private sealed class PacketSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    [Fact]
    public void Rent_notices_match_EveJS_names_routes_arguments_and_recipient_union()
    {
        var sessions = new CharacterSessions();
        var stationOnly = NewSession(sessions, 101, 501, 9002, 60001);
        var corporationOnly = NewSession(sessions, 102, 502, 9001, 60002);
        var both = NewSession(sessions, 103, 503, 9001, 60001);
        var outsider = NewSession(sessions, 104, 504, 9002, 60002);
        var officeType = new ItemType(CorporationOfficeNotifications.OfficeTypeId, 321, 654);

        CorporationOfficeNotifications.Rented(sessions, 9001, 60001, 70001, officeType);

        Assert.Equal(new[] { "OnItemsChanged", "OnOfficeRentalChange" }, Names(stationOnly.Sink));
        Assert.Equal(new[] { "OnItemsChanged", "OnOfficeRentalChange" }, Names(corporationOnly.Sink));
        Assert.Equal(new[] { "OnItemsChanged", "OnOfficeRentalChange", "OnOfficeRentalChange" }, Names(both.Sink));
        Assert.Empty(outsider.Sink.Packets);

        Assert.Equal("*stationid&corpid", Address(stationOnly.Sink.Packets[0]).IdType);
        Assert.Equal("stationid", Address(stationOnly.Sink.Packets[1]).IdType);
        Assert.Equal("*stationid&corpid", Address(corporationOnly.Sink.Packets[0]).IdType);
        Assert.Equal("corpid", Address(corporationOnly.Sink.Packets[1]).IdType);
        Assert.Equal("*stationid&corpid", Address(both.Sink.Packets[0]).IdType);
        Assert.Equal("stationid", Address(both.Sink.Packets[1]).IdType);
        Assert.Equal("corpid", Address(both.Sink.Packets[2]).IdType);

        var itemArgs = Arguments(stationOnly.Sink.Packets[0]);
        var expectedRow = InventoryWire.PackedRow(
            new ItemRow(70001, 27, 9001, 60001, 2, -1, 321, 654), PyNone.Instance);
        AssertPackedRow(expectedRow, Assert.Single(Assert.IsType<PyList>(itemArgs.Items[0]).Items));
        Assert.Equal(new PyDict([new(new PyInt(3), new PyInt(0))]), itemArgs.Items[1]);
        Assert.Equal(PyNone.Instance, itemArgs.Items[2]);
        AssertOfficeRentalArgs(stationOnly.Sink.Packets[1], 9001, 70001);
        AssertOfficeRentalArgs(corporationOnly.Sink.Packets[1], 9001, 70001);
        AssertOfficeRentalArgs(both.Sink.Packets[1], 9001, 70001);
        AssertOfficeRentalArgs(both.Sink.Packets[2], 9001, 70001);
    }

    [Fact]
    public void Empty_station_unrent_sends_junkyard_item_change_then_office_change()
    {
        var sessions = new CharacterSessions();
        var stationPeer = NewSession(sessions, 201, 601, 9002, 61001);
        var corporationPeer = NewSession(sessions, 202, 602, 9001, 61002);
        var officeType = new ItemType(CorporationOfficeNotifications.OfficeTypeId, 321, 654);

        CorporationOfficeNotifications.Unrented(sessions, 9001, 61001, 71001, officeType, sendItemChange: true);

        Assert.Equal(new[] { "OnItemsChanged", "OnOfficeRentalChange" }, Names(stationPeer.Sink));
        Assert.Equal(new[] { "OnItemsChanged", "OnOfficeRentalChange" }, Names(corporationPeer.Sink));
        Assert.Equal("*stationid&corpid", Address(stationPeer.Sink.Packets[0]).IdType);
        Assert.Equal("stationid", Address(stationPeer.Sink.Packets[1]).IdType);
        Assert.Equal("corpid", Address(corporationPeer.Sink.Packets[1]).IdType);

        var itemArgs = Arguments(stationPeer.Sink.Packets[0]);
        var expectedRow = InventoryWire.PackedRow(
            new ItemRow(71001, 27, 9001, 10, 6, -1, 321, 654), PyNone.Instance);
        AssertPackedRow(expectedRow, Assert.Single(Assert.IsType<PyList>(itemArgs.Items[0]).Items));
        Assert.Equal(new PyDict([
            new(new PyInt(3), new PyInt(61001)),
            new(new PyInt(4), new PyInt(2)),
        ]), itemArgs.Items[1]);
        Assert.Equal(PyNone.Instance, itemArgs.Items[2]);
        AssertOfficeRentalArgs(stationPeer.Sink.Packets[1], 9001, 71001);
        AssertOfficeRentalArgs(corporationPeer.Sink.Packets[1], 9001, 71001);
    }

    [Fact]
    public void Unrent_with_assets_sends_only_the_office_rental_change()
    {
        var sessions = new CharacterSessions();
        var both = NewSession(sessions, 301, 701, 9001, 62001, structure: true);
        var officeType = new ItemType(CorporationOfficeNotifications.OfficeTypeId, 321, 654);

        CorporationOfficeNotifications.Unrented(sessions, 9001, 62001, 72001, officeType, sendItemChange: false);

        Assert.Equal(new[] { "OnOfficeRentalChange", "OnOfficeRentalChange" }, Names(both.Sink));
        Assert.Equal("stationid", Address(both.Sink.Packets[0]).IdType);
        Assert.Equal("corpid", Address(both.Sink.Packets[1]).IdType);
        AssertOfficeRentalArgs(both.Sink.Packets[0], 9001, 72001);
        AssertOfficeRentalArgs(both.Sink.Packets[1], 9001, 72001);
    }

    private static (ClientSession Session, PacketSink Sink) NewSession(
        CharacterSessions sessions, long characterId, long clientId, long corporationId, long locationId, bool structure = false)
    {
        var sink = new PacketSink();
        var session = new ClientSession(sink) { CharacterId = characterId, ClientId = clientId };
        session.Set("corpid", new PyInt(corporationId));
        session.Set(structure ? "structureid" : "stationid", new PyLong(locationId));
        sessions.Enter(characterId, session);
        return (session, sink);
    }

    private static string?[] Names(PacketSink sink) => [.. sink.Packets.Select(Name)];

    private static string? Name(PyValue packet)
    {
        if (packet is not PyObject { Name: PyStr name, Args: PyTuple args } ||
            !name.Value.EndsWith("Notification", StringComparison.Ordinal)) return null;
        return MachoAddress.Decode(args.Items[2]).BroadcastId;
    }

    private static MachoAddress Address(PyValue packet) =>
        MachoAddress.Decode(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items[2]);

    private static PyTuple Arguments(PyValue packet)
    {
        var state = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args);
        var payload = Assert.IsType<PyTuple>(state.Items[4]);
        var envelope = Assert.IsType<PyTuple>(payload.Items[0]);
        var buffer = Assert.IsType<PyBuffer>(envelope.Items[1]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(buffer.Value));
        var call = Assert.IsType<PyTuple>(decoded.Items[1]);
        return Assert.IsType<PyTuple>(call.Items[1]);
    }

    private static void AssertPackedRow(PyValue expectedValue, PyValue actual)
    {
        var expected = Assert.IsType<PyPackedRow>(MarshalCodec.DecodeExact(
            MarshalCodec.Encode(Assert.IsType<PyPackedRow>(expectedValue))));
        var packed = Assert.IsType<PyPackedRow>(actual);
        Assert.Equal(expected.Columns.Select(column => (column.Name, column.DbType)),
            packed.Columns.Select(column => (column.Name, column.DbType)));
        Assert.Equal(expected.Values, packed.Values);
    }

    private static void AssertOfficeRentalArgs(PyValue packet, long corporationId, long officeFolderId) =>
        Assert.Equal(new PyTuple([new PyInt(corporationId), new PyLong(officeFolderId)]), Arguments(packet));
}
