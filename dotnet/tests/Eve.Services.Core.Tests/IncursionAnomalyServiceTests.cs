using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public sealed class IncursionAnomalyServiceTests
{
    [Fact]
    public async Task Map_returns_active_incursion_report_and_system_rowset_from_read_state()
    {
        var active = new[]
        {
            Json("""
            {
              "taleID": 1201,
              "templateClassID": 2,
              "templateNameID": 987,
              "stagingSolarSystemID": 30000142,
              "aggressorFactionID": 500019,
              "state": 1,
              "influence": 0.4,
              "lastUpdated": 900000000,
              "decayRate": 0.03,
              "graceTime": 5,
              "hasFinalEncounter": true,
              "effects": [1, 2],
              "rewardGroupID": 12,
              "incursedSystems": [30000142, 30000143],
              "systems": [
                { "locationID": 30000142, "sceneType": 5 },
                { "solarSystemID": 30000143, "sceneType": 6 }
              ],
              "hasChat": true,
              "chatAnnouncementMessageId": 44,
              "musicState": "incursion"
            }
            """)
        };
        var service = NewService("map", active);

        var report = Assert.IsType<PyList>(await service.CallAsync(Request("GetIncursionGlobalReport").Call));
        var entry = Assert.IsType<PyObject>(Assert.Single(report.Items));
        Assert.Equal("util.KeyVal", Str(entry.Name));
        var fields = Fields(entry);
        Assert.Equal(1201L, Int(fields["taleID"]));
        Assert.Equal(30000142L, Int(fields["stagingSolarSystemID"]));
        Assert.Equal(0.4, Real(fields["influence"]));
        Assert.Equal(1L, Int(fields["state"]));
        Assert.True(Assert.IsType<PyBool>(fields["hasFinalEncounter"]).Value);

        var rowset = Assert.IsType<PyObject>(await service.CallAsync(Request("GetSystemsInIncursions").Call));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", Str(rowset.Name));
        var rowsetFields = Fields(rowset);
        Assert.Equal(["locationID", "sceneType", "templateNameID"],
            List(rowsetFields["header"]).Select(Str).ToArray());
        var rows = List(rowsetFields["lines"]);
        Assert.Equal(2, rows.Count);
        Assert.Equal([30000142L, 5L, 987L], List(rows[0]).Select(Int).ToArray());
        Assert.Equal([30000143L, 6L, 987L], List(rows[1]).Select(Int).ToArray());
    }

    [Fact]
    public async Task Tale_manager_sends_current_system_incursion_tales_in_client_wire_shape()
    {
        var active = new[]
        {
            Json("""
            {
              "taleID": 1201,
              "templateNameID": 987,
              "stagingSolarSystemID": 30000142,
              "systems": [{ "locationID": 30000142, "sceneType": 5 }],
              "incursedSystems": [30000142],
              "state": 2,
              "influence": 0.25,
              "lastUpdated": 1000,
              "decayRate": 0.02,
              "graceTime": 30,
              "hasFinalEncounter": false,
              "effects": [9],
              "rewardGroupID": 4,
              "hasChat": false
            }
            """)
        };
        var service = NewService("taleMgr", active);
        var (call, sink) = Request("GetGlobalWorldEventTales", 30000142);

        var result = Assert.IsType<PyObject>(await service.CallAsync(call));
        Assert.Equal(ObjectCaching.ResultClass, Str(result.Name));
        var packet = Assert.IsType<PyObject>(Assert.Single(sink.Packets));
        Assert.Equal(ClientNotification.ClassName, Str(packet.Name));
        var packetItems = Assert.IsType<PyTuple>(packet.Args).Items;
        var destination = Assert.IsType<PyObject>(packetItems[2]);
        var destinationItems = Assert.IsType<PyTuple>(destination.Args).Items;
        Assert.Equal("OnTaleData", Str(destinationItems[1]));
        Assert.Equal("solarsystemid2", Str(destinationItems[3]));

        var notificationEnvelope = Assert.IsType<PyTuple>(packetItems[4]).Items[0];
        var notificationPayload = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(notificationEnvelope).Items[1]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(notificationPayload.Value));
        var eventCall = Assert.IsType<PyTuple>(decoded.Items[1]);
        Assert.Equal(1L, Int(eventCall.Items[0]));
        var eventArgs = Assert.IsType<PyTuple>(eventCall.Items[1]);
        Assert.Equal(30000142L, Int(eventArgs.Items[0]));
        var tales = Assert.IsType<PyDict>(eventArgs.Items[1]);
        var taleEntry = Assert.Single(tales.Entries);
        Assert.Equal(1201L, Int(taleEntry.Key));
        var tale = Fields(Assert.IsType<PyObject>(taleEntry.Value));
        Assert.Equal(30000142L, Int(tale["locationID"]));
        Assert.Equal(5L, Int(tale["severity"]));
        Assert.Equal(0.25, Real(tale["influence"]));
        Assert.False(Assert.IsType<PyBool>(tale["hasChat"]).Value);
    }

    [Fact]
    public async Task Scan_manager_full_state_returns_saved_anomalies_only_for_the_requested_system()
    {
        const long systemId = 30000142;
        long requestedSystem = 0;
        var sites = new[]
        {
            Json("""
            {
              "siteID": 7001,
              "instanceID": 7001,
              "targetID": "scene-anomaly:30000142:7001",
              "position": [1.25, 2.5, -4.0],
              "difficulty": 2,
              "dungeonID": 5164,
              "archetypeID": 3,
              "dungeonNameID": 444,
              "factionID": 500019,
              "scanStrengthAttribute": 100,
              "allowedTypes": [12, 13],
              "entryObjectTypeID": 1234,
              "solarSystemID": 30000142
            }
            """),
            Json("""
            {
              "siteID": 7002,
              "instanceID": 7002,
              "targetID": "scene-anomaly:30000143:7002",
              "solarSystemID": 30000143
            }
            """)
        };
        var service = new IncursionAnomalyService(
            "scanMgr",
            _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            (requested, _) =>
            {
                requestedSystem = requested;
                return ValueTask.FromResult<IReadOnlyList<JsonObject>>(sites);
            });
        var (call, _) = Request("GetFullState", systemId);

        var state = Assert.IsType<PyList>(await service.CallAsync(call));
        Assert.Equal(systemId, requestedSystem);
        Assert.Equal(4, state.Items.Count);
        var anomalies = Assert.IsType<PyDict>(state.Items[0]);
        var (siteKey, siteValue) = Assert.Single(anomalies.Entries);
        Assert.Equal(7001L, Int(siteKey));
        Assert.Equal("util.KeyVal", Str(Assert.IsType<PyObject>(siteValue).Name));
        var fields = Fields(Assert.IsType<PyObject>(siteValue));
        Assert.Equal("scene-anomaly:30000142:7001", Str(fields["targetID"]));
        Assert.Equal(5164L, Int(fields["dungeonID"]));
        Assert.Equal(30000142L, Int(fields["solarSystemID"]));
        Assert.Equal([1.25, 2.5, -4.0], List(fields["position"]).Select(Real).ToArray());
        Assert.Equal([12L, 13L], List(fields["allowedTypes"]).Select(Int).ToArray());
        Assert.All(state.Items.Skip(1), dict => Assert.Empty(Assert.IsType<PyDict>(dict).Entries));
    }

    [Fact]
    public async Task Missing_incursion_and_anomaly_state_returns_exact_empty_read_shapes()
    {
        var empty = NewService("map");
        var report = Assert.IsType<PyList>(await empty.CallAsync(Request("GetIncursionGlobalReport").Call));
        Assert.Empty(report.Items);
        var rowset = Assert.IsType<PyObject>(await empty.CallAsync(Request("GetSystemsInIncursions").Call));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", Str(rowset.Name));
        Assert.Empty(List(Fields(rowset)["lines"]));

        var scanner = NewService("scanMgr");
        var (call, _) = Request("GetFullState", 30000142);
        var state = Assert.IsType<PyList>(await scanner.CallAsync(call));
        Assert.Equal(4, state.Items.Count);
        Assert.All(state.Items, dict => Assert.Empty(Assert.IsType<PyDict>(dict).Entries));
    }

    [Fact]
    public async Task Scan_manager_binds_one_system_scan_object_to_the_selected_character_session()
    {
        var service = NewService("scanMgr");
        var (call, _) = Request("GetSystemScanMgr", 30000142);
        call.Session.CharacterId = 42;

        var bound = Assert.IsType<PySubStruct>(await service.CallAsync(call));
        var reference = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(bound.Value).Decoded);
        var objectId = Str(reference.Items[0]);
        Assert.StartsWith("N=", objectId);
        Assert.IsType<PyLong>(reference.Items[1]);
        Assert.Equal(objectId, call.Session.BoundObjectIds["scanMgr"]);
        Assert.Equal(objectId, call.Session.BoundObjectStates["scanMgr"].ObjectId);

        var second = Assert.IsType<PySubStruct>(await service.CallAsync(call));
        var secondReference = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(second.Value).Decoded);
        Assert.Equal(objectId, Str(secondReference.Items[0]));
    }

    [Fact]
    public async Task Scan_manager_signal_tracker_register_returns_none_and_calls_a_wired_backend()
    {
        var empty = NewService("scanMgr");
        var (emptyCall, _) = Request("SignalTrackerRegister", 30000142);
        emptyCall.Session.CharacterId = 42;
        Assert.Equal(PyNone.Instance, await empty.CallAsync(emptyCall));

        var calls = 0;
        var wired = NewService("scanMgr", signalTrackerRegister: _ =>
        {
            calls++;
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        });
        var (wiredCall, _) = Request("SignalTrackerRegister", 30000142);
        wiredCall.Session.CharacterId = 42;

        Assert.Equal(PyNone.Instance, await wired.CallAsync(wiredCall));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Map_read_service_delegates_unhandled_methods_to_existing_map_service()
    {
        var fallback = new FallbackMapService();
        var service = NewService("map", fallback: fallback);

        var value = await service.CallAsync(Request("GetStationCount").Call);

        Assert.True(service.HasMethod("GetStationCount"));
        Assert.Equal("existing-map", Str(value));
    }

    private static IncursionAnomalyService NewService(
        string name,
        IReadOnlyList<JsonObject>? incursions = null,
        IService? fallback = null,
        Func<ServiceCall, ValueTask<PyValue>>? signalTrackerRegister = null) =>
        new(
            name,
            _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>(incursions ?? Array.Empty<JsonObject>()),
            (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>(Array.Empty<JsonObject>()),
            fallback,
            () => 1_700_000_000_000,
            signalTrackerRegister);

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static (ServiceCall Call, Sink Sink) Request(string method, long systemId = 0, params PyValue[] args)
    {
        var sink = new Sink();
        var session = new ClientSession(sink);
        if (systemId > 0)
            session.Set("solarsystemid2", new PyInt(systemId));
        return (new ServiceCall { Method = method, Args = args, Session = session }, sink);
    }

    private static Dictionary<string, PyValue> Fields(PyObject value) =>
        Assert.IsType<PyDict>(value.Args).Entries.ToDictionary(entry => Str(entry.Key), entry => entry.Value);

    private static PyValue Field(PyDict value, string key) =>
        value.Entries.Single(entry => Str(entry.Key) == key).Value;

    private static List<PyValue> List(PyValue value) => Assert.IsType<PyList>(value).Items.ToList();

    private static string Str(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
        _ => throw new InvalidOperationException($"Expected a marshaled string, got {value.GetType().Name}."),
    };

    private static long Int(PyValue value) => Assert.IsType<PyInt>(value).Value;

    private static double Real(PyValue value) => Assert.IsType<PyReal>(value).Value;

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];

        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class FallbackMapService : IService
    {
        public string Name => "map";

        public bool HasMethod(string method) => method == "GetStationCount";

        public ValueTask<PyValue> CallAsync(ServiceCall call) =>
            ValueTask.FromResult<PyValue>(new PyStr("existing-map"));
    }
}
