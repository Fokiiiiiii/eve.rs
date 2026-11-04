using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Station;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Station_count_and_station_info_keep_the_shapes_consumed_by_map_views_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Timeout=1");
        var map = new MapService(new Eve.ReadSide.ReadSide(source), () => FixedNowMs)
        {
            ReadDataHook = call => ValueTask.FromResult<PyValue?>(call.Method switch
            {
                "GetStationCount" => new PyList([
                    Py.Tuple(new PyInt(30_000_142), new PyInt(2)),
                    Py.Tuple(new PyInt(30_000_143), new PyInt(0)),
                ]),
                "GetStationInfo" => ObjectCaching.MethodResult(
                    Py.Rowset(
                        ["stationID", "solarSystemID", "operationID", "stationTypeID", "ownerID", "serviceMask", "constellationID", "regionID"],
                        [Py.Tuple(new PyInt(60_000_001), new PyInt(30_000_142), new PyInt(5), new PyInt(1529),
                            new PyInt(98_000_001), new PyInt(StationService.AllServicesMask), new PyInt(20_000_020), new PyInt(10_000_002))]),
                    FixedNowMs),
                _ => null,
            }),
        };
        Start(configure: registry => registry.Register(map), strictRpc: true);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var counts = Assert.IsType<PyList>(await CallOverHost(
            front, open, 1, "map", "GetStationCount", Py.Tuple([])));
        Assert.Equal(2, counts.Items.Count);
        Assert.All(counts.Items, item =>
        {
            var pair = Assert.IsType<PyTuple>(item).Items;
            Assert.Equal(2, pair.Count);
            Assert.IsType<PyInt>(pair[0]);
            Assert.IsType<PyInt>(pair[1]);
        });
        Assert.Equal(new PyInt(30_000_142), Assert.IsType<PyTuple>(counts.Items[0]).Items[0]);
        Assert.Equal(new PyInt(2), Assert.IsType<PyTuple>(counts.Items[0]).Items[1]);

        var cached = Assert.IsType<PyObject>(await CallOverHost(
            front, open, 2, "map", "GetStationInfo", Py.Tuple([])));
        Assert.Equal(ObjectCaching.ResultClass, JsSemantics.StringValue(cached.Name));
        var args = Assert.IsType<PyTuple>(cached.Args).Items;
        var rowset = Assert.IsType<PyObject>(Assert.IsType<PySubStream>(args[1]).Decoded);
        var fields = Assert.IsType<PyDict>(rowset.Args).Entries
            .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        Assert.Equal(
            new[] { "stationID", "solarSystemID", "operationID", "stationTypeID", "ownerID", "serviceMask", "constellationID", "regionID" },
            Assert.IsType<PyList>(fields["header"]).Items.Select(value => JsSemantics.StringValue(value)!).ToArray());
        Assert.Equal(new PyToken("util.Row"), fields["RowClass"]);
        var line = Assert.IsType<PyTuple>(
            Assert.Single(Assert.IsType<PyList>(fields["lines"]).Items)).Items;
        Assert.Equal(8, line.Count);
        Assert.Equal(new PyInt(60_000_001), line[0]);
        Assert.Equal(new PyInt(30_000_142), line[1]);
        Assert.Equal(new PyInt(98_000_001), line[4]);
    }
}
