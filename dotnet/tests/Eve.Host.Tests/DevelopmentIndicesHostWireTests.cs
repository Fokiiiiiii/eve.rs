using System.Text;
using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Development_indices_are_returned_from_persisted_state_through_registered_rpc_routes()
    {
        var state = JsonNode.Parse("""
            {
              "systems": {
                "30000142": {
                  "solarSystemID": 30000142,
                  "devIndices": {
                    "militaryPoints": 600,
                    "militaryIncreasing": true,
                    "industrialPoints": 1200,
                    "industrialIncreasing": false,
                    "claimedForDays": 9
                  }
                }
              }
            }
            """)!.AsObject();
        var readModel = new PersistenceSovFwReadModel(
            (_, _) => Task.FromResult(state),
            (_, _) => throw new InvalidOperationException("affiliation is not used for this read"));

        Start(configure: registry => registry.Register(new DevIndexManagerRpcService(
            readModel.ReadAllDevelopmentIndicesAsync,
            readModel.ReadDevelopmentIndicesForSystemAsync)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.Call(open, 1, "devIndexManager", "GetAllDevelopmentIndices"))));
        var allReply = Assert.IsType<Send>(await front.ReceiveAsync());
        var all = Assert.IsType<PyList>(Result(MarshalCodec.Decode(allReply.Payload)));
        var systemRow = Assert.IsType<PyObject>(Assert.Single(all.Items));
        Assert.Equal("util.KeyVal", Encoding.UTF8.GetString(Assert.IsType<PyBytes>(systemRow.Name).Value.Span));
        var system = Assert.IsType<PyDict>(systemRow.Args);
        Assert.Contains(system.Entries, entry => Text(entry.Key) == "solarSystemID" && entry.Value == new PyInt(30000142));
        Assert.Contains(system.Entries, entry => Text(entry.Key) == "militaryPoints" && entry.Value == new PyInt(600));
        Assert.Contains(system.Entries, entry => Text(entry.Key) == "industrialPoints" && entry.Value == new PyInt(1200));
        Assert.Contains(system.Entries, entry => Text(entry.Key) == "claimedFor" && entry.Value == new PyInt(9));

        static string? Text(PyValue value) => value switch
        {
            PyStr text => text.Value,
            PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
            _ => null,
        };

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.Call(open, 2, "devIndexManager", "GetDevelopmentIndicesForSystem", new PyInt(30000142)))));
        var systemReply = Assert.IsType<Send>(await front.ReceiveAsync());
        var actual = Assert.IsType<PyDict>(Result(MarshalCodec.Decode(systemReply.Payload)));
        Assert.Equal(3, actual.Entries.Count);
        AssertIndex(1583, 600, true);
        AssertIndex(1584, 1200, false);
        AssertIndex(1615, 777600, false);

        void AssertIndex(long attributeId, long points, bool increasing)
        {
            var row = Assert.IsType<PyObject>(
                actual.Entries.Single(entry => entry.Key == new PyInt(attributeId)).Value);
            var values = Assert.IsType<PyDict>(row.Args);
            Assert.Contains(values.Entries, entry =>
                Text(entry.Key) == "points" && entry.Value == new PyInt(points));
            Assert.Contains(values.Entries, entry =>
                Text(entry.Key) == "increasing" && entry.Value == (increasing ? PyBool.True : PyBool.False));
        }
    }
}
