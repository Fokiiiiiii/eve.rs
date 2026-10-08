using Eve.Marshal;
using Eve.Data;
using Eve.Macho;
using Eve.Services;
using Npgsql;

namespace Eve.Services.Station.Tests;

public sealed class MapServiceRpcTests
{
    [PgFact]
    public async Task Missing_map_rpcs_are_registered_and_keep_the_reference_wire_shapes()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(PgFactAttribute.Url!));
        var service = new MapService(new Eve.ReadSide.ReadSide(source), () => Data.ClockMs);
        var (session, _) = Data.Session(null, 42);

        var emptyRowsets = new Dictionary<string, (string[] Columns, PyValue[] Args)>
        {
            ["GetConstellationLPData"] = (["solarSystemID", "loyaltyPoints"], [new PyInt(20000001)]),
            ["GetCurrentSovData"] = (["locationID", "solarSystemID", "constellationID", "regionID", "ownerID", "allianceID", "corporationID", "claimStructureID", "infrastructureHubID", "stationID", "claimTime"], [new PyInt(30000142)]),
            ["GetIndustryJobsOverLast24Hours"] = (["solarSystemID", "noOfJobs"], [new PyInt(1)]),
            ["GetMyExtraMapInfo"] = (["characterID", "locationID"], []),
            ["GetMyExtraMapInfoAgents"] = (["fromID", "rank"], []),
            ["GetRecentSovActivity"] = (["solarSystemID", "ownerID", "oldOwnerID", "stationID", "changeTime"], []),
            ["GetSolarSystemVisits"] = (["lastDateTime", "solarSystemID", "visits"], []),
            ["GetAllRoamingWeatherSystems"] = (["locationID", "sceneType"], []),
        };

        foreach (var (method, expected) in emptyRowsets)
        {
            var value = await Data.CallAsync(service, session, method, expected.Args);
            var fields = Data.Fields(value);
            Assert.Equal("eve.common.script.sys.rowset.Rowset", JsSemantics.StringValue(Assert.IsType<PyObject>(value).Name));
            Assert.Equal(expected.Columns, Assert.IsType<PyList>(fields["header"]).Items.Select(JsSemantics.StringValue).ToArray());
            Assert.Empty(Assert.IsType<PyList>(fields["lines"]).Items);
        }

        foreach (var method in new[] { "GetBeaconCount", "GetIndustryCostModifier" })
        {
            var value = Assert.IsType<PyDict>(await Data.CallAsync(service, session, method, new PyInt(1)));
            Assert.Empty(value.Entries);
        }

        foreach (var method in new[] { "GetDeadspaceAgentsMap", "GetDeadspaceComplexMap" })
        {
            Assert.Equal(PyNone.Instance, await Data.CallAsync(service, session, method, new PyStr("en")));
        }

        var zone = Assert.IsType<PyObject>(await Data.CallAsync(service, session, "GetFacWarZoneInfo", new PyInt(500001)));
        var zoneFields = Data.Fields(zone);
        Assert.Equal(new PyInt(500001), zoneFields["factionID"]);
        Assert.Empty(Assert.IsType<PyDict>(zoneFields["systemUpgradeLevel"]).Entries);

        var fortressSystems = Assert.IsType<PyList>(await Data.CallAsync(service, session, "GetEdencomFortressSystems"));
        Assert.Equal(53, fortressSystems.Items.Count);
        Assert.Contains(new PyInt(30004992), fortressSystems.Items);
        var minorVictorySystems = Assert.IsType<PyList>(await Data.CallAsync(service, session, "GetEdencomMinorVictorySystems"));
        Assert.Equal(84, minorVictorySystems.Items.Count);
        Assert.Contains(new PyInt(30002048), minorVictorySystems.Items);
        var triglavianSystems = Assert.IsType<PyList>(await Data.CallAsync(service, session, "GetTriglavianMinorVictorySystems"));
        Assert.Equal(28, triglavianSystems.Items.Count);
        Assert.Contains(new PyInt(30003073), triglavianSystems.Items);

        var cachedHistory = Assert.IsType<PyObject>(
            await Data.CallAsync(service, session, "GetHistory", new PyInt(1), new PyInt(24)));
        Assert.Equal(ObjectCaching.ResultClass, JsSemantics.StringValue(cachedHistory.Name));
        var cachedFields = Assert.IsType<PyTuple>(cachedHistory.Args).Items;
        var details = Assert.IsType<PyDict>(cachedFields[0]);
        Assert.Equal("1 minute", JsSemantics.StringValue(
            Assert.Single(details.Entries, entry => JsSemantics.StringValue(entry.Key) == "versionCheck").Value));
        var history = Assert.IsType<PyObject>(Assert.IsType<PySubStream>(cachedFields[1]).Decoded);
        Assert.Equal(["solarSystemID", "value1", "value2", "value3"],
            Assert.IsType<PyList>(Data.Fields(history)["header"]).Items.Select(value => JsSemantics.StringValue(value)!).ToArray());
        Assert.Empty(Assert.IsType<PyList>(Data.Fields(history)["lines"]).Items);
    }

    [PgFact]
    public async Task Map_rpc_read_hook_can_supply_future_data()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(PgFactAttribute.Url!));
        var service = new MapService(new Eve.ReadSide.ReadSide(source), () => Data.ClockMs)
        {
            ReadDataHook = call => ValueTask.FromResult<PyValue?>(
                call.Method == "GetDeadspaceAgentsMap" ? new PyStr("wired") : null),
        };
        var (session, _) = Data.Session(null, 42);

        Assert.Equal("wired", JsSemantics.StringValue(await Data.CallAsync(service, session, "GetDeadspaceAgentsMap", new PyStr("en"))));
    }
}
