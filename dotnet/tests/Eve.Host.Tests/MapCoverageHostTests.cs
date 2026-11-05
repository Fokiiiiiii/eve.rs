using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Station;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Map_incursion_and_map_read_replies_keep_client_consumed_shapes_through_host()
    {
        await using var source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1");
        var map = new MapService(new Eve.ReadSide.ReadSide(source), () => FixedNowMs);
        ValueTask<IReadOnlyList<System.Text.Json.Nodes.JsonObject>> ReadIncursions(CancellationToken _) =>
            ValueTask.FromResult<IReadOnlyList<System.Text.Json.Nodes.JsonObject>>(
            [
                System.Text.Json.Nodes.JsonNode.Parse(
                    """{"templateNameID":5001,"systems":[{"solarSystemID":30000142,"sceneType":1}]}""")!.AsObject(),
            ]);
        ValueTask<IReadOnlyList<System.Text.Json.Nodes.JsonObject>> ReadAnomalies(long _, CancellationToken __) =>
            ValueTask.FromResult<IReadOnlyList<System.Text.Json.Nodes.JsonObject>>([]);
        var mapWithIncursions = new Eve.Services.Core.IncursionAnomalyService(
            "map", ReadIncursions, ReadAnomalies, fallback: map);
        Start(configure: registry => registry.Register(mapWithIncursions));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> CallMap(int callId, string method, PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                global::Eve.Host.Tests.Packets.Call(open, callId, "map", method, args))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload))
                ?? throw new Xunit.Sdk.XunitException($"The map.{method} RPC returned no value.");
        }

        async Task<Dictionary<string, PyValue>> RowsetFields(
            int callId, string method, PyValue[] args, string[] expectedColumns)
        {
            var rowset = Assert.IsType<PyObject>(await CallMap(callId, method, args));
            Assert.Equal("eve.common.script.sys.rowset.Rowset", T(rowset.Name));
            var fields = Assert.IsType<PyDict>(rowset.Args).Entries
                .ToDictionary(entry => T(entry.Key)!, entry => entry.Value);
            Assert.Equal(expectedColumns,
                Assert.IsType<PyList>(fields["header"]).Items.Select(value => T(value)!).ToArray());
            return fields;
        }

        var incursionFields = await RowsetFields(1, "GetSystemsInIncursions", [],
            ["locationID", "sceneType", "templateNameID"]);
        var incursionRow = Assert.IsType<PyList>(
            Assert.Single(Assert.IsType<PyList>(incursionFields["lines"]).Items));
        Assert.Equal(new PyInt(30000142), incursionRow.Items[0]);
        Assert.Equal(new PyInt(1), incursionRow.Items[1]);
        Assert.Equal(new PyInt(5001), incursionRow.Items[2]);

        var sovFields = await RowsetFields(2, "GetCurrentSovData", [new PyInt(30000142)],
            ["locationID", "solarSystemID", "constellationID", "regionID", "ownerID", "allianceID",
                "corporationID", "claimStructureID", "infrastructureHubID", "stationID", "claimTime"]);
        Assert.Empty(Assert.IsType<PyList>(sovFields["lines"]).Items);

        var lpFields = await RowsetFields(3, "GetConstellationLPData", [new PyInt(20000001)],
            ["solarSystemID", "loyaltyPoints"]);
        Assert.Empty(Assert.IsType<PyList>(lpFields["lines"]).Items);

        var memberFields = await RowsetFields(4, "GetMyExtraMapInfo", [],
            ["characterID", "locationID"]);
        Assert.Empty(Assert.IsType<PyList>(memberFields["lines"]).Items);

        var agentFields = await RowsetFields(5, "GetMyExtraMapInfoAgents", [],
            ["fromID", "rank"]);
        Assert.Empty(Assert.IsType<PyList>(agentFields["lines"]).Items);

        Assert.Empty(Assert.IsType<PyDict>(await CallMap(6, "GetBeaconCount", [])).Entries);

        var triglavianSystems = Assert.IsType<PyList>(
            await CallMap(7, "GetTriglavianMinorVictorySystems", []));
        Assert.Equal(28, triglavianSystems.Items.Count);
        Assert.Contains(new PyInt(30003073), triglavianSystems.Items);
    }
}
