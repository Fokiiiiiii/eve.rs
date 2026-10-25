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
    public async Task Map_history_and_faction_reads_follow_the_registered_map_route_with_client_kwargs()
    {
        const long factionId = 500_001;
        const long solarSystemId = 30_000_142;
        await using var source = NpgsqlDataSource.Create("Host=localhost;Timeout=1");
        var map = new MapService(new Eve.ReadSide.ReadSide(source), () => FixedNowMs)
        {
            ReadDataHook = call => ValueTask.FromResult<PyValue?>(call.Method switch
            {
                "GetFacWarZoneInfo" when call.Arg(0) is PyInt { Value: factionId } =>
                    Py.KeyVal(
                        ("factionID", new PyInt(factionId)),
                        ("factionPoints", new PyInt(1)),
                        ("maxWarZonePoints", new PyInt(50)),
                        ("systemUpgradeLevel", new PyDict([
                            new KeyValuePair<PyValue, PyValue>(new PyInt(solarSystemId), new PyInt(3)),
                        ]))),
                "GetHistory" when call.Arg(0) is PyInt { Value: 1 } && call.Arg(1) is PyInt { Value: 24 } =>
                    Py.Rowset(
                        ["solarSystemID", "value1", "value2", "value3"],
                        [Py.Tuple(new PyInt(solarSystemId), new PyInt(7), PyNone.Instance, PyNone.Instance)]),
                _ => null,
            }),
        };
        var registeredMap = new IncursionAnomalyService(
            "map",
            _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            fallback: map,
            nowUnixMs: () => FixedNowMs);
        Start(configure: registry => registry.Register(registeredMap), strictRpc: true);
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 1, "map", "GetDeadspaceComplexMap", new PyStr("en")));
        Assert.Same(PyNone.Instance,
            await HostRpcAsync(front, open, 2, "map", "GetDeadspaceComplexMap", new PyStr("de")));

        var fortresses = Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 3, "map", "GetEdencomFortressSystems"));
        Assert.Equal(53, fortresses.Items.Count);
        Assert.Contains(new PyInt(30_004_992), fortresses.Items);
        var minorVictories = Assert.IsType<PyList>(
            await HostRpcAsync(front, open, 4, "map", "GetEdencomMinorVictorySystems"));
        Assert.Equal(84, minorVictories.Items.Count);
        Assert.Contains(new PyInt(30_002_048), minorVictories.Items);

        var zone = Assert.IsType<PyObject>(
            await HostRpcAsync(front, open, 5, "map", "GetFacWarZoneInfo", new PyInt(factionId)));
        Assert.Equal(new PyInt(factionId), WireField(zone, "factionID"));
        Assert.Equal(new PyInt(1), WireField(zone, "factionPoints"));
        Assert.Equal(new PyInt(50), WireField(zone, "maxWarZonePoints"));
        var upgradeLevels = Assert.IsType<PyDict>(WireField(zone, "systemUpgradeLevel"));
        Assert.Equal(new PyInt(3), upgradeLevels.Entries.Single(entry => entry.Key.Equals(new PyInt(solarSystemId))).Value);

        var populatedHistory = Assert.IsType<PyObject>(
            await HostRpcAsync(front, open, 6, "map", "GetHistory", new PyInt(1), new PyInt(24)));
        Assert.Equal(ObjectCaching.ResultClass, WireText(populatedHistory.Name));
        var cachedFields = Assert.IsType<PyTuple>(populatedHistory.Args).Items;
        var details = Assert.IsType<PyDict>(cachedFields[0]);
        Assert.Equal("1 minute", WireText(Assert.Single(details.Entries,
            entry => WireText(entry.Key) == "versionCheck").Value));
        var populatedRowset = Assert.IsType<PyObject>(
            Assert.IsType<PySubStream>(cachedFields[1]).Decoded);
        Assert.Equal(
            ["solarSystemID", "value1", "value2", "value3"],
            Assert.IsType<PyList>(WireField(populatedRowset, "header")).Items.Select(WireText).ToArray());
        var rows = Assert.IsType<PyList>(WireField(populatedRowset, "lines"));
        Assert.Equal(
            Py.Tuple(new PyInt(solarSystemId), new PyInt(7), PyNone.Instance, PyNone.Instance),
            Assert.Single(rows.Items));

        var emptyHistory = Assert.IsType<PyObject>(
            await HostRpcAsync(front, open, 7, "map", "GetHistory", new PyInt(5), new PyInt(1)));
        var emptyRowset = Assert.IsType<PyObject>(
            Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(emptyHistory.Args).Items[1]).Decoded);
        Assert.Empty(Assert.IsType<PyList>(WireField(emptyRowset, "lines")).Items);
    }
}
