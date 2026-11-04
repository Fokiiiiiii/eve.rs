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

    [Fact]
    public async Task Map_personal_and_industry_replies_follow_registered_host_routes_with_client_kwargs()
    {
        const long corporationId = 98_765;
        const long characterId = 90_001;
        const long solarSystemId = 30_000_142;
        const long ownerId = 99_001;
        const long stationId = 60_000_001;
        const long lastVisit = 1_790_646_377_000;
        await using var source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=eve;Username=postgres;Timeout=1;Pooling=false");
        var costActivities = new List<long>();
        var jobActivities = new List<long>();
        var map = new MapService(
            new Eve.ReadSide.ReadSide(source),
            () => FixedNowMs,
            _ =>
            [
                (CharacterId: characterId, LocationId: solarSystemId),
                (CharacterId: characterId, LocationId: solarSystemId),
            ])
        {
            ReadDataHook = call =>
            {
                PyValue? value = call.Method switch
                {
                    "GetIndustryCostModifier" => CostIndex(),
                    "GetIndustryJobsOverLast24Hours" => JobCounts(),
                    "GetRecentSovActivity" => Py.Rowset(
                        ["solarSystemID", "ownerID", "oldOwnerID", "stationID", "changeTime"],
                        [Py.Tuple(new PyInt(solarSystemId), new PyInt(ownerId), new PyInt(99_000),
                            new PyInt(stationId), new PyLong(lastVisit))]),
                    _ => null,
                };
                return ValueTask.FromResult(value);

                PyValue CostIndex()
                {
                    costActivities.Add(((PyInt)call.Arg(0)).Value);
                    return new PyDict([new(new PyInt(solarSystemId), new PyReal(0.125))]);
                }

                PyValue JobCounts()
                {
                    jobActivities.Add(((PyInt)call.Arg(0)).Value);
                    return Py.Rowset(
                        ["solarSystemID", "noOfJobs"],
                        [Py.Tuple(new PyInt(solarSystemId), new PyInt(4))]);
                }
            },
        };
        var mapRoute = new IncursionAnomalyService(
            "map",
            _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            fallback: map,
            nowUnixMs: () => FixedNowMs);

        Start(
            configure: registry => registry.Register(mapRoute),
            sessionOpened: session => session.Set("corpid", new PyInt(corporationId)));
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

        foreach (var activityId in new long[] { 1, 3 })
        {
            var costs = Assert.IsType<PyDict>(
                await Call("GetIndustryCostModifier", new PyInt(activityId)));
            Assert.Equal(new PyReal(0.125),
                Assert.Single(costs.Entries, entry => entry.Key.Equals(new PyInt(solarSystemId))).Value);

            var jobs = Assert.IsType<PyObject>(
                await Call("GetIndustryJobsOverLast24Hours", new PyInt(activityId)));
            Assert.Equal(["solarSystemID", "noOfJobs"],
                Assert.IsType<PyList>(WireField(jobs, "header")).Items.Select(WireText).ToArray());
            Assert.Equal(
                Py.Tuple(new PyInt(solarSystemId), new PyInt(4)),
                Assert.Single(Assert.IsType<PyList>(WireField(jobs, "lines")).Items));
        }

        var members = Assert.IsType<PyObject>(await Call("GetMyExtraMapInfo"));
        Assert.Equal(["characterID", "locationID"],
            Assert.IsType<PyList>(WireField(members, "header")).Items.Select(WireText).ToArray());
        Assert.Equal(
            Py.Tuple(new PyInt(characterId), new PyInt(solarSystemId)),
            Assert.Single(Assert.IsType<PyList>(WireField(members, "lines")).Items));

        var agents = Assert.IsType<PyObject>(await Call("GetMyExtraMapInfoAgents"));
        Assert.Equal(["fromID", "rank"],
            Assert.IsType<PyList>(WireField(agents, "header")).Items.Select(WireText).ToArray());
        Assert.Empty(Assert.IsType<PyList>(WireField(agents, "lines")).Items);

        var sov = Assert.IsType<PyObject>(await Call("GetRecentSovActivity"));
        Assert.Equal(["solarSystemID", "ownerID", "oldOwnerID", "stationID", "changeTime"],
            Assert.IsType<PyList>(WireField(sov, "header")).Items.Select(WireText).ToArray());
        Assert.Equal(
            Py.Tuple(new PyInt(solarSystemId), new PyInt(ownerId), new PyInt(99_000),
                new PyInt(stationId), new PyLong(lastVisit)),
            Assert.Single(Assert.IsType<PyList>(WireField(sov, "lines")).Items));

        var visits = Assert.IsType<PyObject>(await Call("GetSolarSystemVisits"));
        Assert.Equal(["lastDateTime", "solarSystemID", "visits"],
            Assert.IsType<PyList>(WireField(visits, "header")).Items.Select(WireText).ToArray());
        Assert.Empty(Assert.IsType<PyList>(WireField(visits, "lines")).Items);
        Assert.Equal([1L, 3L], costActivities);
        Assert.Equal([1L, 3L], jobActivities);
    }
}
