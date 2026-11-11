using System.Text;
using Eve.Host.Tests;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class ContactEditHostWireTests
{
    [PgFact]
    public async Task EditContact_persists_type_90_and_routes_the_client_notification_to_the_recipient()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var senderId = await rig.CreateCharacterAsync();
        var recipientId = await rig.CreateCharacterAsync();
        var sink = new Sink();
        var recipient = new ClientSession { CharacterId = recipientId, UserId = 1, Sink = sink };
        rig.Sessions.Enter(recipientId, recipient);
        var charMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charMgr"));

        var result = await charMgr.CallAsync(new ServiceCall
        {
            Method = "EditContact",
            Session = new ClientSession { CharacterId = senderId, UserId = 1 },
            Args = [new PyLong(recipientId), new PyInt(10), PyBool.False, PyBool.True, new PyStr("Updated relationship")],
        });

        Assert.Same(PyNone.Instance, result);
        var notification = Assert.IsType<PyObject>(Assert.Single(sink.Packets));
        Assert.Equal(new PyStr(ClientNotification.ClassName), notification.Name);
        var packet = Assert.IsType<PyTuple>(notification.Args).Items;
        Assert.Equal(new PyInt(MachoMessage.Notification), packet[0]);
        Assert.Equal(new PyInt(1), packet[3]);
        var destination = MachoAddress.Decode(packet[2]);
        Assert.Equal(AddressKind.Broadcast, destination.Kind);
        Assert.Equal("OnNotificationReceived", destination.BroadcastId);
        Assert.Equal("clientID", destination.IdType);
        Assert.Equal(14, packet.Count);
        var machoEnvelope = Assert.IsType<PyTuple>(packet[4]);
        var machoPayload = Assert.IsType<PyTuple>(Assert.Single(machoEnvelope.Items));
        Assert.Equal(new PyInt(0), machoPayload.Items[0]);
        var encodedPayload = Assert.IsType<PyBuffer>(machoPayload.Items[1]);
        var notificationCall = Assert.IsType<PyTuple>(MarshalCodec.Decode(encodedPayload.Value.ToArray()));
        Assert.Equal(new PyInt(0), notificationCall.Items[0]);
        var invocation = Assert.IsType<PyTuple>(notificationCall.Items[1]);
        Assert.Equal(new PyInt(1), invocation.Items[0]);
        var notificationArgs = Assert.IsType<PyTuple>(invocation.Items[1]).Items;
        Assert.Equal(5, notificationArgs.Count);
        Assert.IsType<PyInt>(notificationArgs[0]);
        Assert.Equal(new PyInt(90), notificationArgs[1]);
        Assert.Equal(new PyInt(senderId), notificationArgs[2]);
        Assert.IsType<PyLong>(notificationArgs[3]);
        var notificationData = Assert.IsType<PyDict>(notificationArgs[4]);
        Assert.Equal(new PyInt(10), Field(notificationData, "level"));
        Assert.Equal(new PyBytes(Encoding.UTF8.GetBytes("Updated relationship")), Field(notificationData, "message"));
        var sequence = Assert.IsType<PyDict>(packet[5]);
        Assert.Equal(new PyInt(0), Field(sequence, "sn"));
        var notificationMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("notificationMgr"));
        var rows = Assert.IsType<PyList>(await notificationMgr.CallAsync(new ServiceCall
        {
            Method = "GetAllNotifications",
            Args = [],
            Session = recipient,
        }));
        var row = Assert.Single(rows.Items);
        Assert.Equal(new PyInt(90), Field(row, "typeID"));
        Assert.Equal(new PyInt(senderId), Field(row, "senderID"));
        Assert.Equal(new PyInt(recipientId), Field(row, "receiverID"));
        var data = Assert.IsType<PyDict>(Field(row, "data"));
        Assert.Equal(new PyInt(10), Field(data, "level"));
        Assert.Equal(new PyStr("Updated relationship"), Field(data, "message"));
    }

    private static PyValue Field(PyValue keyVal, string key)
    {
        var entries = keyVal switch
        {
            PyObject obj => Assert.IsType<PyDict>(obj.Args).Entries,
            PyDict dict => dict.Entries,
            _ => throw new Xunit.Sdk.XunitException($"Expected a dictionary, got {keyVal.GetType().Name}"),
        };
        return entries.Single(entry => entry.Key switch
        {
            PyStr text => text.Value == key,
            PyBytes bytes => bytes.Value.Span.SequenceEqual(Encoding.UTF8.GetBytes(key)),
            _ => false,
        }).Value;
    }

    private static IEnumerable<string> Strings(PyValue value)
    {
        if (value is PyStr str) yield return str.Value;
        if (value is PyObject obj)
            foreach (var nestedText in Strings(obj.Args)) yield return nestedText;
        if (value is PyTuple tuple)
            foreach (var item in tuple.Items)
                foreach (var nestedText in Strings(item)) yield return nestedText;
        if (value is PyList list)
            foreach (var item in list.Items)
                foreach (var nestedText in Strings(item)) yield return nestedText;
        if (value is PyBuffer buffer)
            foreach (var nestedText in Strings(MarshalCodec.Decode(buffer.Value))) yield return nestedText;
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }
}
