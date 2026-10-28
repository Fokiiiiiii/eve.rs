using System.Text;
using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Station;
using Eve.Ships;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Map_telemetry_and_beacons_keep_client_wire_shapes_through_registered_route()
    {
        const long characterId = 90_001;
        const long systemA = 30_000_142;
        const long systemB = 30_000_143;
        const long ownerId = 99_001;
        var now = FixedNowMs;
        var telemetry = MapTelemetryState.Empty();
        MapTelemetryState.RecordLocation(telemetry, characterId, systemA, now - 1_000);
        MapTelemetryState.RecordLocation(telemetry, characterId, systemA, now - 500);
        MapTelemetryState.RecordLocation(telemetry, characterId, 32_000_050, now - 250);
        MapTelemetryState.RecordJump(telemetry, characterId, systemA, systemB, now);
        MapTelemetryState.RecordLocation(telemetry, characterId, systemB, now);
        telemetry["jumpEvents"]!.AsArray().Add(new JsonObject
        {
            ["atMs"] = now,
            ["toSolarSystemID"] = 32_000_050,
        });

        var filetime = DateTimeOffset.FromUnixTimeMilliseconds(now).ToFileTime();
        var killmail = new JsonObject
        {
            ["killID"] = 1,
            ["killTime"] = filetime,
            ["solarSystemID"] = systemA,
            ["victimShipTypeID"] = 10,
            ["victimCharacterID"] = characterId,
            ["victimFactionID"] = 500_001,
        };
        var kills = new JsonObject
        {
            ["pending"] = new JsonObject { ["1"] = (JsonObject)killmail.DeepClone() },
            ["records"] = new JsonObject
            {
                ["1"] = killmail,
                ["2"] = new JsonObject
                {
                    ["killID"] = 2, ["killTime"] = filetime, ["solarSystemID"] = systemA,
                    ["victimShipTypeID"] = 11, ["victimFactionID"] = 500_001,
                },
                ["3"] = new JsonObject
                {
                    ["killID"] = 3, ["killTime"] = filetime, ["solarSystemID"] = systemA,
                    ["victimShipTypeID"] = 12, ["victimCharacterID"] = characterId,
                },
                ["4"] = new JsonObject
                {
                    ["killID"] = 4, ["killTime"] = filetime, ["solarSystemID"] = systemA,
                    ["victimShipTypeID"] = 13, ["victimCharacterID"] = characterId,
                },
                ["5"] = new JsonObject
                {
                    ["killID"] = 5, ["killTime"] = now - 25L * 60 * 60 * 1000,
                    ["solarSystemID"] = systemA, ["victimShipTypeID"] = 10, ["victimCharacterID"] = characterId,
                },
            },
        };
        var typeCategories = new Dictionary<long, int>
        {
            [10] = 6,
            [11] = 6,
            [12] = 6,
            [13] = 65,
        };
        var capsuleTypes = new HashSet<long> { 12 };
        var factionIds = new HashSet<long> { 500_001 };
        var fields = new[]
        {
            Deployed(1, "cyno_field", (int)systemA, ownerId, now + 5_000),
            Deployed(2, "mobile_cyno_beacon", (int)systemA, ownerId, now + 5_000, now - 1),
            Deployed(3, "mobile_cyno_beacon", (int)systemA, ownerId, now + 5_000, now + 1),
            Deployed(4, "cyno_field", (int)systemA, 88_001, now + 5_000),
            Deployed(5, "cyno_field", 32_000_050, ownerId, now + 5_000),
            Deployed(6, "cyno_field", (int)systemA, ownerId, now - 1),
        };
        var fleetOwners = new HashSet<long> { ownerId };

        await using var source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=eve;Username=postgres;Timeout=1;Pooling=false");
        var map = new MapService(new Eve.ReadSide.ReadSide(source), () => now)
        {
            ReadDataHook = call =>
            {
                PyValue? value = call.Method switch
                {
                    "GetHistory" => MapTelemetryState.History(
                        telemetry, kills, Number(call.Arg(0)), Number(call.Arg(1)), now,
                        typeCategories, capsuleTypes, factionIds),
                    "GetSolarSystemVisits" => MapTelemetryState.SolarSystemVisits(
                        telemetry, call.Session.CharacterId ?? 0),
                    "GetBeaconCount" => new PyDict(MapTelemetryState.ActiveBeaconCounts(fields, fleetOwners, now)
                        .Select(entry => new KeyValuePair<PyValue, PyValue>(
                            new PyInt(entry.Key), new PyInt(entry.Value))).ToArray()),
                    "GetFacWarZoneInfo" => Py.KeyVal(
                        ("factionID", new PyInt(Number(call.Arg(0)))),
                        ("systemUpgradeLevel", new PyDict([]))),
                    _ => null,
                };
                return ValueTask.FromResult(value);
            },
        };
        var route = new MapAccessGuardService(
            new IncursionAnomalyService(
                "map",
                _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
                (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
                fallback: map,
                nowUnixMs: () => now),
            _ => false);

        Start(
            configure: registry => registry.Register(route),
            sessionOpened: session => session.CharacterId = characterId);
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
            Assert.Equal(MachoMessage.CallRsp, Message(reply));
            return Result(MarshalCodec.Decode(reply.Payload));
        }

        static long Number(PyValue value) => value switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => 0,
        };

        var defaultHistory = CachedRows(Assert.IsType<PyObject>(await Call("GetHistory")));
        Assert.Empty(Assert.IsType<PyList>(WireField(defaultHistory, "lines")).Items);

        var jumps = CachedRows(Assert.IsType<PyObject>(
            await Call("GetHistory", new PyInt(1), new PyInt(24))));
        AssertTuple(
            Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(WireField(jumps, "lines")).Items)),
            new PyInt(systemB), new PyInt(1), new PyInt(0), new PyInt(0));

        var killsByShip = CachedRows(Assert.IsType<PyObject>(
            await Call("GetHistory", new PyLong(3), new PyLong(24))));
        AssertTuple(
            Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(WireField(killsByShip, "lines")).Items)),
            new PyInt(systemA), new PyInt(2), new PyInt(1), new PyInt(1));

        var factionKills = CachedRows(Assert.IsType<PyObject>(
            await Call("GetHistory", new PyInt(5), new PyLong(24))));
        AssertTuple(
            Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(WireField(factionKills, "lines")).Items)),
            new PyInt(systemA), new PyInt(2), new PyInt(1), new PyInt(0));

        var visits = Assert.IsType<PyObject>(await Call("GetSolarSystemVisits"));
        Assert.Equal(
            new[] { "lastDateTime", "solarSystemID", "visits" },
            Assert.IsType<PyList>(WireField(visits, "header")).Items.Select(WireText).ToArray());
        var visitRows = Assert.IsType<PyList>(WireField(visits, "lines")).Items;
        Assert.Equal(2, visitRows.Count);
        AssertTuple(
            Assert.IsType<PyTuple>(visitRows[0]),
            new PyLong(filetime - 5_000_000), new PyInt(systemA), new PyInt(1));
        AssertTuple(
            Assert.IsType<PyTuple>(visitRows[1]),
            new PyLong(filetime), new PyInt(systemB), new PyInt(1));

        var beacons = Assert.IsType<PyDict>(await Call("GetBeaconCount"));
        Assert.Equal(new PyInt(2), Assert.Single(beacons.Entries).Value);
        Assert.Equal(new PyInt(systemA), Assert.Single(beacons.Entries).Key);

        Assert.Same(PyNone.Instance, await Call("GetDeadspaceComplexMap", PyNone.Instance));
        var zone = Assert.IsType<PyObject>(await Call("GetFacWarZoneInfo", new PyLong(500_001)));
        Assert.Equal(new PyInt(500_001), WireField(zone, "factionID"));
    }

    [Fact]
    public async Task Map_inside_abyssal_space_returns_the_reference_user_error_over_host_wire()
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=eve;Username=postgres;Timeout=1;Pooling=false");
        var map = new MapService(new Eve.ReadSide.ReadSide(source), () => FixedNowMs);
        var route = new MapAccessGuardService(
            new IncursionAnomalyService(
                "map",
                _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
                (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
                fallback: map,
                nowUnixMs: () => FixedNowMs),
            session =>
            {
                var systemId = session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0;
                return systemId is >= 32_000_001 and <= 32_000_200;
            });
        Start(
            configure: registry => registry.Register(route),
            sessionOpened: session => session.Set("solarsystemid2", new PyInt(32_000_050)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new(new PyStr("machoVersion"), new PyInt(1))]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            open, 1, "map", "GetSolarSystemVisits", new PyTuple([]), kwargs))));

        var error = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.ErrorResponse, Message(error));
        var wire = Encoding.Latin1.GetString(error.Payload.Span);
        Assert.Contains("CustomInfo", wire);
        Assert.Contains("info", wire);
        Assert.Contains("The map does not work inside Abyssal Deadspace.", wire);
    }

    private static void AssertTuple(PyTuple actual, params PyValue[] expected)
    {
        Assert.Equal(expected.Length, actual.Items.Count);
        for (var index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], actual.Items[index]);
    }

    private static DeployedRecord Deployed(
        long id, string kind, int systemId, long ownerId, long expiresAtMs, long activateAtMs = 0) =>
        new(id, kind, systemId, "field", 1, 1, [], 0, ownerId, 0, 0, 0, expiresAtMs, activateAtMs);

    private static PyObject CachedRows(PyObject cached)
    {
        var result = Assert.IsType<PyTuple>(cached.Args).Items;
        return Assert.IsType<PyObject>(Assert.IsType<PySubStream>(result[1]).Decoded);
    }

    private static int Message(Send send) =>
        Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload))).Type;
}
