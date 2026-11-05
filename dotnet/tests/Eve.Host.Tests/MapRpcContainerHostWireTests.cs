using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Station;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Map_client_container_consumers_receive_reference_shapes_through_the_registered_route()
    {
        const long systemId = 30_000_142;
        const long constellationId = 20_000_001;
        const long factionId = 500_001;
        await using var source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=eve;Username=postgres;Timeout=1;Pooling=false");
        var map = new MapService(new Eve.ReadSide.ReadSide(source), () => FixedNowMs)
        {
            ReadDataHook = call => ValueTask.FromResult<PyValue?>(call.Method switch
            {
                "GetCurrentSovData" => Py.Rowset(
                    ["locationID", "solarSystemID", "constellationID", "regionID", "ownerID",
                     "allianceID", "corporationID", "claimStructureID", "infrastructureHubID",
                     "stationID", "claimTime"],
                    [Py.Tuple(new PyInt(systemId), new PyInt(systemId), new PyInt(constellationId),
                        new PyInt(20_000_001), new PyInt(factionId), PyNone.Instance, PyNone.Instance,
                        PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance)]),
                "GetConstellationLPData" => Py.Rowset(
                    ["solarSystemID", "loyaltyPoints"],
                    [Py.Tuple(new PyInt(systemId), new PyInt(12))]),
                "GetMyExtraMapInfoAgents" => Py.Rowset(
                    ["fromID", "rank"],
                    [Py.Tuple(new PyInt(factionId), new PyInt(3))]),
                "GetBeaconCount" => new PyDict([
                    new(new PyInt(systemId), new PyInt(2)),
                ]),
                _ => null,
            }),
        };
        var activeIncursions = new[]
        {
            JsonNode.Parse("""
                {"taleID":1201,"templateNameID":987,"stagingSolarSystemID":30000142,
                 "systems":[{"locationID":30000142,"sceneType":5}]}
                """)!.AsObject(),
        };
        var mapRoute = new IncursionAnomalyService(
            "map",
            _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>(activeIncursions),
            (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            fallback: map,
            nowUnixMs: () => FixedNowMs);

        Start(configure: registry => registry.Register(mapRoute), strictRpc: true);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new(new PyStr("machoVersion"), new PyInt(1))]);
        uint callId = 0;

        async Task<PyValue?> Call(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, ++callId, "map", method, new PyTuple(args), kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload));
        }

        var incursions = Assert.IsType<PyObject>(await Call("GetSystemsInIncursions"));
        Assert.Equal(["locationID", "sceneType", "templateNameID"],
            Assert.IsType<PyList>(WireField(incursions, "header")).Items.Select(WireText).ToArray());
        Assert.Equal(
            [new PyInt(systemId), new PyInt(5), new PyInt(987)],
            Assert.IsType<PyList>(Assert.Single(Assert.IsType<PyList>(WireField(incursions, "lines")).Items)).Items);

        var victorySystems = Assert.IsType<PyList>(await Call("GetTriglavianMinorVictorySystems"));
        Assert.Equal(28, victorySystems.Items.Count);
        Assert.Contains(new PyInt(30_003_073), victorySystems.Items);

        var sov = Assert.IsType<PyObject>(await Call("GetCurrentSovData", new PyInt(constellationId)));
        Assert.Equal(
            ["locationID", "solarSystemID", "constellationID", "regionID", "ownerID", "allianceID",
             "corporationID", "claimStructureID", "infrastructureHubID", "stationID", "claimTime"],
            Assert.IsType<PyList>(WireField(sov, "header")).Items.Select(WireText).ToArray());
        Assert.Equal(
            [new PyInt(systemId), new PyInt(systemId), new PyInt(constellationId), new PyInt(20_000_001),
             new PyInt(factionId), PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
             PyNone.Instance, PyNone.Instance],
            Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(WireField(sov, "lines")).Items)).Items);

        var lp = Assert.IsType<PyObject>(await Call("GetConstellationLPData", new PyInt(constellationId)));
        Assert.Equal(["solarSystemID", "loyaltyPoints"],
            Assert.IsType<PyList>(WireField(lp, "header")).Items.Select(WireText).ToArray());

        var agents = Assert.IsType<PyObject>(await Call("GetMyExtraMapInfoAgents"));
        Assert.Equal(["fromID", "rank"],
            Assert.IsType<PyList>(WireField(agents, "header")).Items.Select(WireText).ToArray());
        Assert.Equal(
            [new PyInt(factionId), new PyInt(3)],
            Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(WireField(agents, "lines")).Items)).Items);

        var beacons = Assert.IsType<PyDict>(await Call("GetBeaconCount"));
        Assert.Equal(new PyInt(2),
            Assert.Single(beacons.Entries, entry => entry.Key.Equals(new PyInt(systemId))).Value);
    }
}
