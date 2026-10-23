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
        Assert.Contains("OnNotificationReceived", Strings(Assert.Single(sink.Packets)));
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
