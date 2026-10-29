using Eve.Characters;
using Eve.Corporations;
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
        const long allianceId = 3_000_842;
        const long characterId = 3_000_843;
        const long structureId = 1_100_000_142_000;

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
        var corporation = new Corporation(ownerId, "Test Corporation", "TST", agentId, agentId, null,
            stationId, systemId, 0m, 0m, "", "", 100, false, false, 0, default);
        var alliance = new Alliance(allianceId, "Test Alliance", "TAL", "", "", null, ownerId, agentId, 0, false);
        var character = new Character(characterId, 7, "Test Character", 1, 1, 1, 1, 1, 1373,
            ownerId, null, allianceId, stationId, stationId, systemId, 20_000_001, 10_000_001, null,
            DateTimeOffset.UnixEpoch, null, null);
        var ownerIds = ids.Append(characterId).Append(ownerId).Append(allianceId).ToArray();
        var service = new ConfigService(
            null!, null!, stations, solarSystems,
            agentOwners: agentOwners,
            readCharacters: (requested, _) => Task.FromResult<IReadOnlyList<Character>>(
                requested.Contains(characterId) ? [character] : []),
            readStructureNames: (requested, _) => Task.FromResult<IReadOnlyDictionary<long, string>>(
                requested.Contains(structureId) ? new Dictionary<long, string> { [structureId] = "Test Structure" } : new Dictionary<long, string>()),
            readCorporations: (requested, _) => Task.FromResult<IReadOnlyList<Corporation>>(
                requested.Contains(ownerId) ? [corporation] : []),
            readAlliances: (requested, _) => Task.FromResult<IReadOnlyList<Alliance>>(
                requested.Contains(allianceId) ? [alliance] : []));
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
        var owner = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyTuple>(owners.Items[1]).Items));
        Assert.Equal(new PyInt(agentId), owner.Items[0]);
        Assert.Equal($"Agent {agentId}", WireText(owner.Items[1]));
        Assert.Equal(new PyInt(1373), owner.Items[2]);
        Assert.Equal(new PyInt((int)(agentId % 2)), owner.Items[3]);
        Assert.Same(PyNone.Instance, owner.Items[4]);
        var multiOwners = Rowset(await Call(2, "GetMultiOwnersEx",
                new PyList(ownerIds.Select(id => (PyValue)new PyLong(id)).ToArray())),
            "ownerID", "ownerName", "typeID", "gender", "ownerNameID");
        var ownerRows = Assert.IsType<PyTuple>(multiOwners.Items[1]).Items.Select(value => Assert.IsType<PyTuple>(value)).ToArray();
        Assert.Equal(ownerIds.Length, ownerRows.Length);
        var characterRow = Assert.Single(ownerRows, row => row.Items[0] == new PyInt(characterId));
        Assert.Equal("Test Character", WireText(characterRow.Items[1]));
        Assert.Equal(new PyInt(1373), characterRow.Items[2]);
        Assert.Equal(new PyInt(1), characterRow.Items[3]);
        Assert.Same(PyNone.Instance, characterRow.Items[4]);
        var corporationRow = Assert.Single(ownerRows, row => row.Items[0] == new PyInt(ownerId));
        Assert.Equal("Test Corporation", WireText(corporationRow.Items[1]));
        Assert.Equal(new PyInt(2), corporationRow.Items[2]);
        var allianceOwnerRow = Assert.Single(ownerRows, row => row.Items[0] == new PyInt(allianceId));
        Assert.Equal("Test Alliance", WireText(allianceOwnerRow.Items[1]));
        Assert.Equal(new PyInt(16159), allianceOwnerRow.Items[2]);

        var tickers = Rowset(await Call(3, "GetMultiCorpTickerNamesEx", new PyList([new PyLong(ownerId)])),
            "corporationID", "tickerName", "shape1", "shape2", "shape3", "color1", "color2", "color3");
        var tickerRow = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyTuple>(tickers.Items[1]).Items));
        Assert.Equal(new PyInt(ownerId), tickerRow.Items[0]);
        Assert.Equal("TST", WireText(tickerRow.Items[1]));
        Assert.All(tickerRow.Items.Skip(2), value => Assert.Same(PyNone.Instance, value));

        var allianceNames = Rowset(await Call(4, "GetMultiAllianceShortNamesEx", new PyList([new PyInt(allianceId)])),
            "allianceID", "shortName");
        var allianceRow = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyTuple>(allianceNames.Items[1]).Items));
        Assert.Equal(new PyInt(allianceId), allianceRow.Items[0]);
        Assert.Equal("TAL", WireText(allianceRow.Items[1]));

        var locations = Rowset(await Call(5, "GetMultiLocationsEx",
                new PyList([new PyInt(0), new PyLong(systemId), new PyLong(stationId), new PyLong(structureId), new PyLong(9_999_999)])),
            "locationID", "locationName", "solarSystemID", "x", "y", "z", "locationNameID");
        var locationRows = Assert.IsType<PyTuple>(locations.Items[1]).Items.Select(value => Assert.IsType<PyTuple>(value)).ToArray();
        Assert.Equal(4, locationRows.Length);
        Assert.Equal("(none)", WireText(locationRows[0].Items[1]));
        Assert.Same(PyNone.Instance, locationRows[0].Items[2]);
        Assert.Equal(new PyInt(systemId), locationRows[1].Items[2]);
        Assert.Equal("Test Station", WireText(locationRows[2].Items[1]));
        Assert.Equal(11d, Assert.IsType<PyReal>(locationRows[2].Items[3]).Value);
        Assert.Same(PyNone.Instance, locationRows[2].Items[6]);
        Assert.Equal(new PyLong(structureId), locationRows[3].Items[0]);
        Assert.Equal("Test Structure", WireText(locationRows[3].Items[1]));
        Assert.Same(PyNone.Instance, locationRows[3].Items[2]);
        Assert.All(locationRows[3].Items.Skip(3).Take(3), value => Assert.Equal(0d, Assert.IsType<PyReal>(value).Value));
        Assert.Same(PyNone.Instance, locationRows[3].Items[6]);
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

        var stationsByOwner = Assert.IsType<PyList>(await Call(10, "GetStationSolarSystemsByOwner", new PyInt(ownerId)));
        var stationSystems = Assert.IsType<PyObject>(Assert.Single(stationsByOwner.Items));
        Assert.Equal("util.KeyVal", WireText(stationSystems.Name));
        var stationFields = Assert.IsType<PyDict>(stationSystems.Args).Entries
            .ToDictionary(entry => WireText(entry.Key), entry => entry.Value);
        Assert.Equal(new PyInt(ownerId), stationFields["ownerID"]);
        Assert.Equal(new PyInt(systemId), stationFields["solarSystemID"]);
        Assert.Equal(new PyInt(1), stationFields["stationCount"]);

        var tickerAlias = Rowset(await Call(11, "GetCorpTickerNamesEx", new PyLong(ownerId)),
            "corporationID", "tickerName", "shape1", "shape2", "shape3", "color1", "color2", "color3");
        Assert.Equal("TST", WireText(Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyTuple>(tickerAlias.Items[1]).Items)).Items[1]));
        var allianceAlias = Rowset(await Call(12, "GetAllianceShortNamesEx", new PyLong(allianceId)),
            "allianceID", "shortName");
        Assert.Equal("TAL", WireText(Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyTuple>(allianceAlias.Items[1]).Items)).Items[1]));
        var stationsByLongOwner = Assert.IsType<PyList>(await Call(13, "GetStationSolarSystemsByOwner", new PyLong(ownerId)));
        Assert.Single(stationsByLongOwner.Items);
    }

    [Fact]
    public async Task Config_direct_read_methods_return_the_client_wire_values_through_host()
    {
        IReadOnlyDictionary<long, decimal> sales = new Dictionary<long, decimal> { [34] = 13.2m, [36] = 10m };
        var config = new ConfigService(
            null!, null!, new StationCatalog([]), new SolarSystemCatalog([]),
            latestAverageSales: _ => Task.FromResult(sales));
        Start(strictRpc: true, configure: registry => registry.Register(config));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        async Task<PyValue> Call(long callId, string method)
        {
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(open, callId, "config", method, new PyTuple([]), kwargs))));
            var response = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(response.Payload);
            Assert.Equal(MachoMessage.CallRsp, MachoPacket.Decode(decoded)!.Type);
            return Result(decoded)!;
        }

        var prices = Assert.IsType<PyDict>(await Call(1, "GetAverageMarketPrices"));
        Assert.Equal([34, 36], prices.Entries.Select(entry => Assert.IsType<PyInt>(entry.Key).Value).ToArray());
        var priceFields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(prices.Entries[0].Value).Args)
            .Entries.ToDictionary(entry => WireText(entry.Key), entry => entry.Value);
        Assert.Equal(13.2d, Assert.IsType<PyReal>(priceFields["averagePrice"]).Value);
        Assert.Equal(13.2d, Assert.IsType<PyReal>(priceFields["adjustedPrice"]).Value);
        Assert.Empty(Assert.IsType<PyList>(await Call(2, "GetBlackListedPlanets")).Items);
        Assert.Empty(Assert.IsType<PyList>(await Call(3, "GetOldStationData")).Items);
    }
}
