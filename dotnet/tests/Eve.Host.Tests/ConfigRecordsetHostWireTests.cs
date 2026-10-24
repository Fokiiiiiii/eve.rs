using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;
using Eve.Universe;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Config_recordset_routes_return_client_rows_through_host_with_transport_kwargs()
    {
        const long ownerId = 3_000_841;
        const int systemId = 30_000_142;
        const long stationId = 60_000_142;
        const long agentId = 3_008_416;

        var solarSystems = new SolarSystemCatalog([
            new SolarSystemInfo(systemId, "Test System", 20_000_001, 10_000_001, 0.5, X: 4, Y: 5, Z: 6),
        ]);
        var stations = new StationCatalog([
            new Station(stationId, 1531, "Test Station", ownerId, "Test Owner", null, "", 0,
                systemId, (int)systemId, "Test System", 20_000_001, "Test Constellation",
                10_000_001, "Test Region", 0.5, 0, 0, 11, 12, 13),
        ]);
        var ids = Enumerable.Range(0, 501).Select(index => 3_000_000L + index).ToArray();
        var agentOwners = ids.Append(agentId).ToDictionary(id => id, id => new AgentOwnerInfo($"Agent {id}", (int)(id % 2)));
        var service = new ConfigService(null!, null!, stations, solarSystems, agentOwners: agentOwners);
        string[] methods =
        [
            "GetAllianceShortNamesEx", "GetCorpTickerNamesEx", "GetLocationsEx", "GetLocationsLocal",
            "GetMultiAllianceShortNamesEx", "GetMultiCorpTickerNamesEx", "GetMultiLocationsEx", "GetMultiOwnersEx",
            "GetOwnersEx", "GetPositionEx", "GetPositionsEx", "GetStationSolarSystemsByOwner",
        ];
        foreach (var method in methods)
            Assert.True(((IService)service).HasMethod(method), method);

        Start(strictRpc: true, configure: registry => registry.Register(service));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(long callId, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, callId, "config", method, new PyTuple(args), kwargs))));
            var response = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(response.Payload))!;
        }

        string WireText(PyValue value) => value switch
        {
            PyStr text => text.Value,
            PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
            _ => throw new InvalidCastException($"Unexpected wire string type: {value.GetType().Name}"),
        };

        PyTuple Rowset(PyValue value, params string[] columns)
        {
            var result = Assert.IsType<PyTuple>(value);
            Assert.Equal(columns, Assert.IsType<PyTuple>(result.Items[0]).Items.Select(WireText));
            return result;
        }

        var owners = Rowset(await Call(1, "GetOwnersEx", new PyInt(agentId)),
            "ownerID", "ownerName", "typeID", "gender", "ownerNameID");
        Assert.Single(Assert.IsType<PyTuple>(owners.Items[1]).Items);
        var multiOwners = Rowset(await Call(2, "GetMultiOwnersEx", new PyList(ids.Select(id => (PyValue)new PyLong(id)).ToArray())),
            "ownerID", "ownerName", "typeID", "gender", "ownerNameID");
        Assert.Equal(ids.Length, Assert.IsType<PyTuple>(multiOwners.Items[1]).Items.Count);

        Rowset(await Call(3, "GetMultiCorpTickerNamesEx", new PyList([])),
            "corporationID", "tickerName", "shape1", "shape2", "shape3", "color1", "color2", "color3");
        Rowset(await Call(4, "GetMultiAllianceShortNamesEx", new PyList([])), "allianceID", "shortName");

        var locations = Rowset(await Call(5, "GetMultiLocationsEx",
                new PyList([new PyInt(0), new PyLong(systemId), new PyLong(stationId)])),
            "locationID", "locationName", "solarSystemID", "x", "y", "z", "locationNameID");
        Assert.Equal(3, Assert.IsType<PyTuple>(locations.Items[1]).Items.Count);
        Rowset(await Call(6, "GetLocationsEx", new PyLong(stationId)),
            "locationID", "locationName", "solarSystemID", "x", "y", "z", "locationNameID");
        Rowset(await Call(7, "GetLocationsLocal", new PyList([new PyLong(systemId), new PyLong(stationId)])),
            "locationID", "locationName", "solarSystemID", "x", "y", "z", "locationNameID");

        var position = Rowset(await Call(8, "GetPositionEx", new PyLong(systemId)),
            "itemID", "x", "y", "z", "yaw", "pitch", "roll");
        var positionRow = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyTuple>(position.Items[1]).Items));
        Assert.Equal(4d, Assert.IsType<PyReal>(positionRow.Items[1]).Value);
        Assert.Equal(5d, Assert.IsType<PyReal>(positionRow.Items[2]).Value);
        Assert.Equal(6d, Assert.IsType<PyReal>(positionRow.Items[3]).Value);
        Assert.All(positionRow.Items.Skip(4), value => Assert.Same(PyNone.Instance, value));
        var positions = Rowset(await Call(9, "GetPositionsEx", new PyList([new PyLong(systemId), new PyLong(stationId)])),
            "itemID", "x", "y", "z", "yaw", "pitch", "roll");
        Assert.Equal(2, Assert.IsType<PyTuple>(positions.Items[1]).Items.Count);

        var stationsByOwner = Assert.IsType<PyList>(await Call(10, "GetStationSolarSystemsByOwner", new PyLong(ownerId)));
        var stationSystems = Assert.IsType<PyObject>(Assert.Single(stationsByOwner.Items));
        Assert.Equal("util.KeyVal", WireText(stationSystems.Name));
        var stationFields = Assert.IsType<PyDict>(stationSystems.Args).Entries
            .ToDictionary(entry => WireText(entry.Key), entry => entry.Value);
        Assert.Equal(new PyInt(systemId), stationFields["solarSystemID"]);
        Assert.Equal(new PyInt(1), stationFields["stationCount"]);
    }
}
