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
    public async Task Map_history_incursion_and_industry_cost_replies_follow_registered_host_routes()
    {
        const long solarSystemId = 30000142;
        await using var source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=eve;Username=postgres;Timeout=1;Pooling=false");
        var historyCalls = new List<(long StatId, long Hours)>();
        var map = new MapService(new Eve.ReadSide.ReadSide(source), () => FixedNowMs)
        {
            ReadDataHook = call =>
            {
                if (call.Method == "GetHistory")
                {
                    historyCalls.Add((((PyInt)call.Arg(0)).Value, ((PyInt)call.Arg(1)).Value));
                    return ValueTask.FromResult<PyValue?>(Py.Rowset(
                        ["solarSystemID", "value1", "value2", "value3"],
                        [Py.Tuple(new PyInt(solarSystemId), new PyInt(7), new PyInt(2), new PyInt(1))]));
                }
                return call.Method == "GetIndustryCostModifier"
                    ? ValueTask.FromResult<PyValue?>(new PyDict([
                        new(new PyInt(solarSystemId), new PyReal(0.125)),
                    ]))
                    : ValueTask.FromResult<PyValue?>(null);
            },
        };
        var activeIncursions = new[]
        {
            JsonNode.Parse("""
                {"taleID":1201,"templateClassID":2,"templateNameID":987,"stagingSolarSystemID":30000142,
                 "aggressorFactionID":500019,"state":1,"influence":0.4,"lastUpdated":900000000,
                 "decayRate":0.03,"graceTime":5,"hasFinalEncounter":true,"effects":[1,2],
                 "rewardGroupID":12,"incursedSystems":[30000142]}
                """)!.AsObject(),
        };
        var mapRoute = new IncursionAnomalyService(
            "map",
            _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>(activeIncursions),
            (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            map,
            () => FixedNowMs);

        Start(configure: registry => registry.Register(mapRoute));
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

        foreach (var (statId, hours) in new (long, long)[] { (1, 1), (3, 1), (3, 24), (5, 24) })
        {
            var cached = Assert.IsType<PyObject>(await Call("GetHistory", new PyInt(statId), new PyInt(hours)));
            Assert.Equal(Eve.Services.ObjectCaching.ResultClass, Eve.Macho.JsSemantics.StringValue(cached.Name));
            var parts = Assert.IsType<PyTuple>(cached.Args).Items;
            var metadata = Assert.IsType<PyDict>(parts[0]).Entries;
            Assert.Equal("1 minute", Eve.Macho.JsSemantics.StringValue(
                Assert.Single(metadata, entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == "versionCheck").Value));
            var rowset = Assert.IsType<PyObject>(Assert.IsType<PySubStream>(parts[1]).Decoded);
            var fields = ((PyDict)rowset.Args).Entries
                .ToDictionary(entry => Eve.Macho.JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
            Assert.Equal(["solarSystemID", "value1", "value2", "value3"],
                Assert.IsType<PyList>(fields["header"]).Items
                    .Select(value => Eve.Macho.JsSemantics.StringValue(value)!).ToArray());
            var row = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(fields["lines"]).Items));
            Assert.Equal([solarSystemId, 7L, 2L, 1L], row.Items.Cast<PyInt>().Select(value => value.Value).ToArray());
        }

        var report = Assert.IsType<PyList>(await Call("GetIncursionGlobalReport"));
        var incursion = Assert.IsType<PyObject>(Assert.Single(report.Items));
        var reportFields = ((PyDict)incursion.Args).Entries
            .ToDictionary(entry => Eve.Macho.JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        Assert.Equal(new PyInt(solarSystemId), reportFields["stagingSolarSystemID"]);
        Assert.Equal(new PyInt(500019), reportFields["aggressorFactionID"]);
        Assert.Equal(new PyInt(12), reportFields["rewardGroupID"]);
        Assert.Equal(new PyReal(0.4), reportFields["influence"]);

        var costs = Assert.IsType<PyDict>(await Call("GetIndustryCostModifier", new PyInt(1)));
        Assert.Equal(new PyReal(0.125), Assert.Single(costs.Entries).Value);
        Assert.Equal([(1L, 1L), (3L, 1L), (3L, 24L), (5L, 24L)], historyCalls);
    }
}
