using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed class IncursionAnomalyService : IService
{
    private const string MapName = "map";
    private const string TaleManagerName = "taleMgr";
    private const string ScanManagerName = "scanMgr";
    private static readonly string[] IncursionSystemColumns = ["locationID", "sceneType", "templateNameID"];

    private readonly string _serviceName;
    private readonly Func<CancellationToken, ValueTask<IReadOnlyList<JsonObject>>> _readIncursions;
    private readonly Func<long, CancellationToken, ValueTask<IReadOnlyList<JsonObject>>> _readAnomalies;
    private readonly IService? _fallback;
    private readonly Func<long> _nowUnixMs;

    private readonly Func<ServiceCall, ValueTask<PyValue>>? _signalTrackerRegister;

    /// <summary>The recall of a probe (scanMgr <c>DestroyProbe</c>): the probe leaves space and its pilot is told.</summary>
    private readonly Func<ClientSession, long, CancellationToken, ValueTask>? _recallProbe;

    /// <summary>The probe scan's answers on the scan manager (<c>RequestScans</c>, <c>SetActivityState</c>): given the method's name and the call.</summary>
    private readonly Func<string, ServiceCall, ValueTask<PyValue>>? _probeScans;

    public IncursionAnomalyService(
        string serviceName,
        Func<CancellationToken, ValueTask<IReadOnlyList<JsonObject>>> readIncursions,
        Func<long, CancellationToken, ValueTask<IReadOnlyList<JsonObject>>> readAnomalies,
        IService? fallback = null,
        Func<long>? nowUnixMs = null,
        Func<ServiceCall, ValueTask<PyValue>>? signalTrackerRegister = null,
        Func<ClientSession, long, CancellationToken, ValueTask>? recallProbe = null,
        Func<string, ServiceCall, ValueTask<PyValue>>? probeScans = null)
    {
        _recallProbe = recallProbe;
        _probeScans = probeScans;
        if (serviceName is not (MapName or TaleManagerName or ScanManagerName))
            throw new ArgumentOutOfRangeException(nameof(serviceName), serviceName, "Unsupported incursion/anomaly service.");
        ArgumentNullException.ThrowIfNull(readIncursions);
        ArgumentNullException.ThrowIfNull(readAnomalies);
        if (fallback is not null && fallback.Name != serviceName)
            throw new ArgumentException("Fallback service must have the same service name.", nameof(fallback));

        _serviceName = serviceName;
        _readIncursions = readIncursions;
        _readAnomalies = readAnomalies;
        _fallback = fallback;
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _signalTrackerRegister = signalTrackerRegister;
    }

    public string Name => _serviceName;

    public bool HasMethod(string method) =>
        OwnsMethod(method) || _fallback?.HasMethod(method) == true;

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (!OwnsMethod(call.Method))
            return _fallback is not null && _fallback.HasMethod(call.Method)
                ? await _fallback.CallAsync(call)
                : PyNone.Instance;

        return (_serviceName, call.Method) switch
        {
            (MapName, "GetIncursionGlobalReport") => await GetIncursionGlobalReportAsync(call.Cancellation),
            (MapName, "GetSystemsInIncursions") => await GetSystemsInIncursionsAsync(call.Cancellation),
            (TaleManagerName, "GetGlobalWorldEventTales") => await GetGlobalWorldEventTalesAsync(call),
            (TaleManagerName, "get_active_tales_by_template") => await GetActiveTalesByTemplateAsync(call),
            (ScanManagerName, "GetFullState") => await GetFullStateAsync(call),
            (ScanManagerName, "GetSystemScanMgr") => GetSystemScanMgr(call),
            (ScanManagerName, "SignalTrackerRegister") => await SignalTrackerRegisterAsync(call),
            (ScanManagerName, "DestroyProbe") => await DestroyProbeAsync(call),
            (ScanManagerName, "RequestScans" or "SetActivityState" or "RecoverProbes" or "ReconnectToLostProbes" or "ConeScan") =>
                _probeScans is null ? PyNone.Instance : await _probeScans(call.Method, call),
            _ => PyNone.Instance,
        };
    }

    private bool OwnsMethod(string method) => _serviceName switch
    {
        MapName => method is "GetIncursionGlobalReport" or "GetSystemsInIncursions",
        TaleManagerName => method is "GetGlobalWorldEventTales" or "get_active_tales_by_template",
        ScanManagerName => method is "GetFullState" or "GetSystemScanMgr" or "SignalTrackerRegister" or "DestroyProbe" or "RequestScans" or "SetActivityState" or "RecoverProbes" or "ReconnectToLostProbes" or "ConeScan",
        _ => false,
    };

    private async ValueTask<PyValue> GetIncursionGlobalReportAsync(CancellationToken cancellationToken)
    {
        var incursions = await _readIncursions(cancellationToken);
        return new PyList(incursions
            .Where(HasStagingSystem)
            .Select(BuildIncursionReport)
            .ToArray());
    }

    private async ValueTask<PyValue> GetSystemsInIncursionsAsync(CancellationToken cancellationToken)
    {
        var incursions = await _readIncursions(cancellationToken);
        var rows = new List<PyValue>();
        foreach (var incursion in incursions)
        {
            var defaultTemplateNameId = Positive(incursion["templateNameID"]);
            var systems = incursion["systems"] as JsonArray ?? incursion["systemRows"] as JsonArray;
            if (systems is null)
                continue;

            foreach (var system in systems)
            {
                var row = SystemRow(system, defaultTemplateNameId);
                if (row is not null)
                    rows.Add(row);
            }
        }
        return Py.Rowset(IncursionSystemColumns, rows);
    }

    private async ValueTask<PyValue> GetGlobalWorldEventTalesAsync(ServiceCall call)
    {
        var incursions = await _readIncursions(call.Cancellation);
        PublishTaleData(call.Session, ResolveSystemId(call.Session), incursions);
        return ObjectCaching.MethodResult(new PyList([]), _nowUnixMs(), "15 minutes");
    }

    private async ValueTask<PyValue> GetActiveTalesByTemplateAsync(ServiceCall call)
    {
        if (Positive(Number(call.Arg(0))) <= 0)
            return new PyList([]);

        var incursions = await _readIncursions(call.Cancellation);
        PublishTaleData(call.Session, ResolveSystemId(call.Session), incursions);
        return new PyList([]);
    }

    private async ValueTask<PyValue> GetFullStateAsync(ServiceCall call)
    {
        var systemId = ResolveSystemId(call.Session);
        var sites = systemId > 0
            ? await _readAnomalies(systemId, call.Cancellation)
            : Array.Empty<JsonObject>();

        var anomalies = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var site in sites)
        {
            var siteSystemId = Positive(site["solarSystemID"]);
            if (siteSystemId > 0 && siteSystemId != systemId)
                continue;

            var siteId = Positive(site["siteID"]);
            if (siteId <= 0)
                continue;

            var overlayId = site["instanceID"] is null
                ? siteId
                : (long)Math.Truncate(Number(site["instanceID"]));
            anomalies.Add(new(Py.Number(siteId), BuildAnomalyInfo(site, systemId, overlayId)));
        }

        return new PyList([
            new PyDict(anomalies),
            new PyDict([]),
            new PyDict([]),
            new PyDict([]),
        ]);
    }

    private PyValue GetSystemScanMgr(ServiceCall call)
    {
        _ = ResolveSystemId(call.Session);
        if (!call.Session.BoundObjectIds.TryGetValue(ScanManagerName, out var objectId))
        {
            objectId = $"N={Eve.Macho.MachoMessage.ProxyNodeId}:{Eve.Services.BoundObjectIds.Next()}";
            call.Session.BoundObjectIds[ScanManagerName] = objectId;
            call.Session.BoundObjectStates[ScanManagerName] = new BoundObjectState(objectId);
        }

        call.Session.LastBoundObjectId = objectId;
        return new PySubStruct(new PySubStream(Py.Tuple(
            new PyStr(objectId),
            new PyLong(Py.FileTimeFromUnixMs(_nowUnixMs())))));
    }

    /// <summary><c>DestroyProbe(probeID)</c> on the scan manager: the probe is recalled (see <see cref="_recallProbe"/>); a call with no probe does nothing.</summary>
    private async ValueTask<PyValue> DestroyProbeAsync(ServiceCall call)
    {
        if (_recallProbe is null || call.Session.CharacterId is null || call.Arg(0) is not (PyInt or PyLong))
        {
            return PyNone.Instance;
        }
        var probeId = call.Arg(0) is PyLong number ? number.Value : ((PyInt)call.Arg(0)).Value;
        await _recallProbe(call.Session, probeId, call.Cancellation);
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> SignalTrackerRegisterAsync(ServiceCall call) =>
        _signalTrackerRegister is null
            ? PyNone.Instance
            : await _signalTrackerRegister(call);

    private static PyValue BuildIncursionReport(JsonObject incursion)
    {
        var severity = Positive(incursion["severity"]);
        if (severity == 0)
            severity = Positive(incursion["sceneType"]);
        if (severity == 0)
            severity = 4;

        return Py.KeyVal(
            ("taleID", Py.Number(Positive(incursion["taleID"]))),
            ("templateClassID", Py.Number(Positive(incursion["templateClassID"], 2))),
            ("templateNameID", Py.Number(Positive(incursion["templateNameID"]))),
            ("stagingSolarSystemID", Py.Number(StagingSystemId(incursion))),
            ("aggressorFactionID", Py.Number(Positive(incursion["aggressorFactionID"], 500019))),
            ("state", Py.Number(IncursionState(incursion["state"]))),
            ("influence", Py.Number(Influence(incursion["influence"]))),
            ("lastUpdated", Py.FileTimeLong(Positive(incursion["lastUpdated"]))),  // a FILETIME is a long on the wire (Py.Number would write it as a real)
            ("decayRate", Py.Number(Math.Max(0, Number(incursion["decayRate"])))),
            ("graceTime", Py.Number(Math.Max(0, Number(incursion["graceTime"])))),
            ("hasFinalEncounter", PyValue.Of(Truthy(incursion["hasFinalEncounter"]))),
            ("effects", PyListFrom(incursion["effects"])),
            ("rewardGroupID", Py.Number(Positive(incursion["rewardGroupID"]))),
            ("incursedSystems", PyListFrom(incursion["incursedSystems"])),
            ("severity", Py.Number(severity)),
            ("hasChat", PyValue.Of(Truthy(incursion["hasChat"]))),
            ("chatAnnouncementMessageId", Py.Number(Positive(incursion["chatAnnouncementMessageId"]))),
            ("musicState", Truthy(incursion["musicState"]) ? ToPyValue(incursion["musicState"]) : PyNone.Instance));
    }

    private static PyValue BuildTaleData(long systemId, JsonObject incursion)
    {
        var influenceData = incursion["influenceData"] as JsonObject ?? incursion;
        var incursedSystems = ResolveIncursedSystems(incursion);
        var hasChat = incursion["hasChat"] is not JsonValue hasChatValue ||
                      !hasChatValue.TryGetValue<bool>(out var enabled) || enabled;

        return Py.KeyVal(
            ("templateClassID", Py.Number(Positive(incursion["templateClassID"], 2))),
            ("locationID", Py.Number(systemId)),
            ("taleID", Py.Number(Positive(incursion["taleID"]))),
            ("templateNameID", Py.Number(Positive(incursion["templateNameID"]))),
            ("stagingSolarSystemID", Py.Number(Positive(incursion["stagingSolarSystemID"]))),
            ("aggressorFactionID", Py.Number(Positive(incursion["aggressorFactionID"], 500019))),
            ("state", Py.Number(IncursionState(incursion["state"]))),
            ("severity", Py.Number(IncursionSeverityForSystem(incursion, systemId))),
            ("influence", Py.Number(Number(incursion["influence"]))),
            ("lastUpdated", Py.Number(Number(incursion["lastUpdated"]))),
            ("decayRate", Py.Number(Number(incursion["decayRate"]))),
            ("graceTime", Py.Number(Number(incursion["graceTime"]))),
            ("influenceData", Py.KeyVal(
                ("influence", Py.Number(Number(influenceData["influence"]))),
                ("lastUpdated", Py.Number(Number(influenceData["lastUpdated"]))),
                ("decayRate", Py.Number(Number(influenceData["decayRate"]))),
                ("graceTime", Py.Number(Number(influenceData["graceTime"]))))),
            ("hasFinalEncounter", PyValue.Of(Truthy(incursion["hasFinalEncounter"]))),
            ("effects", PyListFrom(incursion["effects"])),
            ("rewardGroupID", Py.Number(Positive(incursion["rewardGroupID"]))),
            ("incursedSystems", new PyList(incursedSystems.Select(system => Py.Number(system)).ToArray())),
            ("hasChat", PyValue.Of(hasChat)),
            ("chatAnnouncementMessageId", Py.Number(Positive(incursion["chatAnnouncementMessageId"]))),
            ("musicState", Truthy(incursion["musicState"]) ? ToPyValue(incursion["musicState"]) : PyNone.Instance));
    }

    private static void PublishTaleData(ClientSession session, long systemId, IReadOnlyList<JsonObject> incursions)
    {
        if (systemId <= 0)
            return;

        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var incursion in incursions)
        {
            var taleId = Positive(incursion["taleID"]);
            if (taleId <= 0 || !ResolveIncursedSystems(incursion).Contains(systemId))
                continue;
            entries.Add(new(Py.Number(taleId), BuildTaleData(systemId, incursion)));
        }

        session.SendNotification("OnTaleData", "solarsystemid2", Py.Number(systemId), new PyDict(entries));
    }

    private static PyValue BuildAnomalyInfo(JsonObject site, long systemId, long overlayId)
    {
        return Py.KeyVal(
            ("position", MarshalRealVector(site["position"])),
            ("targetID", new PyStr(StringValue(site["targetID"]))),
            ("difficulty", Py.Number(Number(site["difficulty"], 1))),
            ("dungeonID", site["dungeonID"] is null ? Py.Number(0) : ToPyValue(site["dungeonID"])),
            ("archetypeID", ToPyNullableValue(site["archetypeID"])),
            ("siteID", Py.Number(overlayId)),
            ("instanceID", Py.Number(overlayId)),
            ("dungeonNameID", ToPyNullableValue(site["dungeonNameID"])),
            ("factionID", ToPyNullableValue(site["factionID"])),
            ("scanStrengthAttribute", ToPyNullableValue(site["scanStrengthAttribute"])),
            ("allowedTypes", PyListFrom(site["allowedTypes"])),
            ("entryObjectTypeID", ToPyNullableValue(site["entryObjectTypeID"])),
            ("solarSystemID", Py.Number(Number(site["solarSystemID"], systemId))));
    }

    private static PyValue? SystemRow(JsonNode? system, long defaultTemplateNameId)
    {
        long systemId;
        long sceneType;
        long templateNameId;
        if (system is JsonValue)
        {
            systemId = Positive(system);
            sceneType = 4;
            templateNameId = defaultTemplateNameId;
        }
        else if (system is JsonObject row)
        {
            systemId = Positive(row["locationID"]);
            if (systemId == 0)
                systemId = Positive(row["solarSystemID"]);
            sceneType = Positive(row["sceneType"], 4);
            templateNameId = Positive(row["templateNameID"], defaultTemplateNameId);
        }
        else
        {
            return null;
        }

        return systemId > 0
            ? new PyList([Py.Number(systemId), Py.Number(sceneType), Py.Number(templateNameId)])
            : null;
    }

    private static PyValue MarshalRealVector(JsonNode? position)
    {
        JsonNode? x = null;
        JsonNode? y = null;
        JsonNode? z = null;
        if (position is JsonArray vector)
        {
            x = vector.ElementAtOrDefault(0);
            y = vector.ElementAtOrDefault(1);
            z = vector.ElementAtOrDefault(2);
        }
        else if (position is JsonObject coordinates)
        {
            x = coordinates["x"];
            y = coordinates["y"];
            z = coordinates["z"];
        }

        return new PyList([
            new PyReal(Number(x)),
            new PyReal(Number(y)),
            new PyReal(Number(z)),
        ]);
    }

    private static List<long> ResolveIncursedSystems(JsonObject incursion)
    {
        var systems = new List<long>();
        if (incursion["incursedSystems"] is JsonArray direct)
        {
            systems.AddRange(direct.Select(node => Positive(node)).Where(id => id > 0));
        }
        else if (incursion["systems"] is JsonArray rows)
        {
            foreach (var row in rows.OfType<JsonObject>())
            {
                var systemId = Positive(row["locationID"]);
                if (systemId == 0)
                    systemId = Positive(row["solarSystemID"]);
                if (systemId > 0)
                    systems.Add(systemId);
            }
        }
        return systems.Distinct().ToList();
    }

    private static long IncursionSeverityForSystem(JsonObject incursion, long systemId)
    {
        if (incursion["systems"] is JsonArray systems)
        {
            foreach (var system in systems.OfType<JsonObject>())
            {
                var id = Positive(system["locationID"]);
                if (id == 0)
                    id = Positive(system["solarSystemID"]);
                if (id == systemId)
                    return Positive(system["sceneType"], 4);
            }
        }
        return 4;
    }

    private static bool HasStagingSystem(JsonObject incursion) => StagingSystemId(incursion) > 0;

    private static long StagingSystemId(JsonObject incursion)
    {
        var staging = Positive(incursion["stagingSolarSystemID"]);
        return staging > 0 ? staging : Positive(incursion["solarSystemID"]);
    }

    private static long ResolveSystemId(ClientSession session) =>
        session.GetInt("solarsystemid2") ??
        session.GetInt("solarsystemid") ??
        session.GetInt("locationid") ??
        0;

    private static long IncursionState(JsonNode? state)
    {
        var value = (long)Math.Truncate(Number(state, 2));
        return value is 0 or 1 or 2 ? value : 2;
    }

    private static double Influence(JsonNode? value) => Math.Clamp(Number(value), 0, 1);

    private static long Positive(JsonNode? value, long fallback = 0)
    {
        var number = Number(value, fallback);
        if (!double.IsFinite(number) || number <= 0)
            return fallback;
        return (long)Math.Min(long.MaxValue, Math.Truncate(number));
    }

    private static double Number(JsonNode? value, double fallback = 0)
    {
        if (value is not JsonValue json)
            return fallback;
        if (json.TryGetValue<double>(out var number))
            return double.IsFinite(number) ? number : fallback;
        if (json.TryGetValue<long>(out var integer))
            return integer;
        if (json.TryGetValue<string>(out var text) &&
            double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number))
            return double.IsFinite(number) ? number : fallback;
        return fallback;
    }

    private static double Number(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real => real.Value,
        PyBool boolean => boolean.Value ? 1 : 0,
        PyStr text when double.TryParse(text.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };

    private static bool Truthy(JsonNode? value)
    {
        if (value is null)
            return false;
        if (value is JsonArray or JsonObject)
            return true;
        if (value is JsonValue json)
        {
            if (json.TryGetValue<bool>(out var boolean))
                return boolean;
            if (json.TryGetValue<string>(out var text))
                return text.Length > 0;
            return Number(value) != 0;
        }
        return false;
    }

    private static string StringValue(JsonNode? value)
    {
        if (value is null || !Truthy(value))
            return "";
        if (value is JsonValue json && json.TryGetValue<string>(out var text))
            return text;
        if (value is JsonValue number)
            return Number(value).ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        return value.ToJsonString();
    }

    private static PyValue PyListFrom(JsonNode? value) =>
        value is JsonArray array
            ? new PyList(array.Select(ToPyValue).ToArray())
            : new PyList([]);

    private static PyValue ToPyNullableValue(JsonNode? value) =>
        value is null ? PyNone.Instance : ToPyValue(value);

    private static PyValue ToPyValue(JsonNode? value)
    {
        if (value is null)
            return PyNone.Instance;
        if (value is JsonArray array)
            return new PyList(array.Select(ToPyValue).ToArray());
        if (value is JsonObject obj)
            return new PyDict(obj.Select(entry =>
                new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Key), ToPyValue(entry.Value))).ToArray());
        if (value is JsonValue json)
        {
            if (json.TryGetValue<bool>(out var boolean))
                return PyValue.Of(boolean);
            if (json.TryGetValue<string>(out var text))
                return new PyStr(text);
            if (json.TryGetValue<long>(out var integer))
                return Py.Number(integer);
            if (json.TryGetValue<double>(out var number))
                return Py.Number(number);
        }
        return PyNone.Instance;
    }
}
