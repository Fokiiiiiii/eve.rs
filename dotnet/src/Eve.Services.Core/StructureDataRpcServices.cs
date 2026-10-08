using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed class MoonExtractionsRpcService : IService
{
    private const long MinimumDurationSeconds = 518460;
    private const long MaximumDurationSeconds = 4838400;
    private const long DefaultFractureDelaySeconds = 10800;
    private readonly Func<JsonObject?> _readState;
    private readonly Func<ServiceCall, ValueTask<PyValue>>? _mutationBackend;

    public MoonExtractionsRpcService(
        Func<JsonObject?>? readState = null,
        Func<ServiceCall, ValueTask<PyValue>>? mutationBackend = null)
    {
        _readState = readState ?? (() => null);
        _mutationBackend = mutationBackend;
    }

    public string Name => "moonExtractions";

    public bool HasMethod(string method) => method is
        "GetExtractionAndEventIDFromStructureID" or
        "GetExtractionsForCorp" or
        "GetMoonResourcesForStructure" or
        "StartNewExtraction" or
        "FractureChunkWithStructure" or
        "CancelExtraction";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        if (call.Method == "StartNewExtraction")
            return _mutationBackend is null ? PyNone.Instance : await _mutationBackend(call).ConfigureAwait(false);
        if (call.Method is "FractureChunkWithStructure" or "CancelExtraction")
        {
            if (_mutationBackend is not null) await _mutationBackend(call).ConfigureAwait(false);
            return PyNone.Instance;
        }

        var state = _readState() ?? new JsonObject();
        return call.Method switch
        {
            "GetExtractionAndEventIDFromStructureID" => ExtractionAndEvent(call, state),
            "GetExtractionsForCorp" => ExtractionsForCorporation(call, state),
            "GetMoonResourcesForStructure" => MoonResources(call, state),
            _ => PyNone.Instance,
        };
    }

    private static PyValue ExtractionAndEvent(ServiceCall call, JsonObject state)
    {
        var structureId = PositiveId(call.Arg(0), SessionStructureId(call.Session));
        var extraction = structureId > 0
            ? ReadExtractions(state).Select(NormalizeExtraction)
                .FirstOrDefault(row => row is { State: "active" or "fracturing" } && row.StructureId == structureId)
            : null;
        return Py.Tuple(
            extraction is null ? PyNone.Instance : ExtractionKeyVal(extraction),
            extraction is null ? PyNone.Instance : new PyInt(extraction.CalendarEventId));
    }

    private static PyValue ExtractionsForCorporation(ServiceCall call, JsonObject state)
    {
        var corporationId = PositiveId(call.Arg(0), SessionCorporationId(call.Session));
        var rows = ReadExtractions(state)
            .Select(NormalizeExtraction)
            .OfType<Extraction>()
            .Where(row => (row.State is "active" or "fracturing") &&
                (corporationId == 0 || row.OwnerId == corporationId))
            .OrderBy(row => row.ChunkAvailableTimeMs)
            .Select(ExtractionKeyVal)
            .ToArray();
        return new PyList(rows);
    }

    private static PyValue MoonResources(ServiceCall call, JsonObject state)
    {
        var structureId = PositiveId(call.Arg(0), SessionStructureId(call.Session));
        if (structureId <= 0 || state["resourcesByStructureID"] is not JsonObject profiles ||
            profiles[structureId.ToString(CultureInfo.InvariantCulture)] is not JsonObject profile ||
            profile["resources"] is not JsonObject resources)
            return Py.Dict();

        var entries = resources
            .Select(pair => (TypeId: PositiveId(pair.Key), Abundance: Number(pair.Value, 0)))
            .Where(row => row.TypeId > 0 && row.Abundance > 0 && double.IsFinite(row.Abundance))
            .OrderBy(row => row.TypeId)
            .Select(row => new KeyValuePair<PyValue, PyValue>(new PyInt(row.TypeId), Py.Number(row.Abundance)))
            .ToArray();
        return new PyDict(entries);
    }

    private static IEnumerable<JsonObject> ReadExtractions(JsonObject state) =>
        state["extractions"] is JsonObject extractions
            ? extractions.Select(pair => pair.Value).OfType<JsonObject>()
            : [];

    private static Extraction? NormalizeExtraction(JsonObject source)
    {
        var extractionId = PositiveId(source["extractionID"]);
        var structureId = PositiveId(source["structureID"]);
        var startMoveTimeMs = PositiveId(source["startMoveTimeMs"]);
        var chunkAvailableTimeMs = PositiveId(source["chunkAvailableTimeMs"]);
        if (extractionId <= 0 || structureId <= 0 || startMoveTimeMs <= 0 || chunkAvailableTimeMs <= 0)
            return null;

        var duration = Number(source["durationSeconds"], 0);
        var durationSeconds = duration <= 0
            ? 0
            : Math.Clamp((long)Math.Truncate(duration), MinimumDurationSeconds, MaximumDurationSeconds);
        var yieldMultiplier = Math.Max(0, Number(source["yieldMultiplier"], 1));
        if (yieldMultiplier == 0) yieldMultiplier = 1;
        var state = Text(source["state"], "active").Trim();
        var ownerId = PositiveId(source["ownerID"], PositiveId(source["ownerCorpID"]));
        var decayFallback = chunkAvailableTimeMs + DefaultFractureDelaySeconds * 1000;

        return new Extraction(
            extractionId,
            structureId,
            PositiveId(source["moonID"]),
            PositiveId(source["solarSystemID"]),
            ownerId,
            startMoveTimeMs,
            chunkAvailableTimeMs,
            PositiveId(source["naturalDecayTimeMs"], decayFallback),
            durationSeconds,
            yieldMultiplier,
            Math.Max(0, PositiveId(source["calendarEventID"])),
            state);
    }

    private static PyValue ExtractionKeyVal(Extraction row) => Py.KeyVal(
        ("extractionID", new PyInt(row.ExtractionId)),
        ("structureID", new PyInt(row.StructureId)),
        ("moonID", new PyInt(row.MoonId)),
        ("solarSystemID", new PyInt(row.SolarSystemId)),
        ("ownerID", new PyInt(row.OwnerId)),
        ("startMoveTime", Py.FileTimeLong(Py.FileTimeFromUnixMs(row.StartMoveTimeMs))),
        ("chunkAvailableTime", Py.FileTimeLong(Py.FileTimeFromUnixMs(row.ChunkAvailableTimeMs))),
        ("naturalDecayTime", Py.FileTimeLong(Py.FileTimeFromUnixMs(row.NaturalDecayTimeMs))),
        ("durationSeconds", new PyInt(row.DurationSeconds)),
        ("yieldMultiplier", Py.Number(row.YieldMultiplier)));

    private static long SessionStructureId(ClientSession session)
    {
        var id = PositiveId(session.Get("structureid"));
        if (id > 0) return id;
        id = PositiveId(session.Get("structureID"));
        return id > 0 ? id : PositiveId(session.Get("shipid"));
    }

    private static long SessionCorporationId(ClientSession session)
    {
        var id = PositiveId(session.Get("corpid"));
        if (id > 0) return id;
        id = PositiveId(session.Get("corporationID"));
        return id > 0 ? id : PositiveId(session.Get("corpID"));
    }

    private static long PositiveId(PyValue? value, long fallback = 0)
    {
        var number = value switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            PyReal real when double.IsFinite(real.Value) && real.Value > 0 && real.Value < long.MaxValue => (long)Math.Truncate(real.Value),
            PyStr text when long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            PyWString text when long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
        return number > 0 ? number : fallback;
    }

    private static long PositiveId(JsonNode? value, long fallback = 0)
    {
        var number = Number(value, 0);
        return number > 0 && number < long.MaxValue ? (long)Math.Truncate(number) : fallback;
    }

    private static long PositiveId(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 0;

    private static double Number(JsonNode? value, double fallback) =>
        double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : fallback;

    private static string Text(JsonNode? value, string fallback) =>
        value is null ? fallback : value.ToString();

    private sealed record Extraction(
        long ExtractionId,
        long StructureId,
        long MoonId,
        long SolarSystemId,
        long OwnerId,
        long StartMoveTimeMs,
        long ChunkAvailableTimeMs,
        long NaturalDecayTimeMs,
        long DurationSeconds,
        double YieldMultiplier,
        long CalendarEventId,
        string State);
}

public sealed class OverviewPresetMgrService : IService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<JsonObject?> _readState;
    private readonly Func<JsonObject, bool>? _writeState;
    private readonly object _gate = new();

    public OverviewPresetMgrService(Func<JsonObject?>? readState = null, Func<JsonObject, bool>? writeState = null)
    {
        _readState = readState ?? (() => null);
        _writeState = writeState;
    }

    public string Name => "overviewPresetMgr";

    public bool HasMethod(string method) => method is "StoreLinkAndGetID" or "GetStoredPreset";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => ValueTask.FromResult(
        !HasMethod(call.Method) ? PyNone.Instance :
        call.Method == "StoreLinkAndGetID" ? StoreLink(call) : GetStoredPreset(call.Arg(0)));

    private PyValue StoreLink(ServiceCall call)
    {
        if (ToJson(call.Arg(0)) is not JsonObject payload) return PyNone.Instance;
        var payloadString = CanonicalPayload(payload);
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(payloadString)));

        lock (_gate)
        {
            var state = _readState() ?? new JsonObject();
            var changed = EnsureStateShape(state);
            var entries = (JsonObject)state["entries"]!;
            var existing = entries.Select(pair => pair.Value).OfType<JsonObject>()
                .FirstOrDefault(entry => Text(entry["payload"]) == payloadString);
            if (existing is not null)
            {
                if (Text(existing["source"]) != "client_drag")
                {
                    existing["source"] = "client_drag";
                    changed = true;
                }
                if (changed && _writeState?.Invoke(state) == false) return PyNone.Instance;
                return PresetKey(existing);
            }

            var sqId = Math.Max(1, Integer(state["nextSqID"], 1));
            var ownerId = call.Session.CharacterId.GetValueOrDefault();
            if (ownerId <= 0) ownerId = call.Session.UserId.GetValueOrDefault();
            entries[$"{hash}::{sqId}"] = new JsonObject
            {
                ["hashvalue"] = hash,
                ["sqID"] = sqId,
                ["payload"] = payloadString,
                ["ownerID"] = ownerId,
                ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["source"] = "client_drag",
                ["label"] = "",
                ["sourcePath"] = "",
            };
            state["nextSqID"] = checked(sqId + 1);
            EnsureStateShape(state);
            return _writeState?.Invoke(state) == true
                ? Py.KeyVal(("hashvalue", new PyStr(hash)), ("sqID", new PyInt(sqId)))
                : PyNone.Instance;
        }
    }

    private PyValue GetStoredPreset(PyValue key)
    {
        if (!TryPresetKey(key, out var hash, out var sqId)) return PyNone.Instance;
        lock (_gate)
        {
            var state = _readState() ?? new JsonObject();
            var changed = EnsureStateShape(state);
            if (changed) _writeState?.Invoke(state);
            if (state["entries"] is not JsonObject entries ||
                entries[$"{hash}::{sqId}"] is not JsonObject entry ||
                Text(entry["hashvalue"]) != hash ||
                Text(entry["payload"]) is not { Length: > 0 } payload)
                return PyNone.Instance;
            return new PyStr(payload);
        }
    }

    private static bool EnsureStateShape(JsonObject state)
    {
        var changed = false;
        if (state["entries"] is not JsonObject)
        {
            state["entries"] = new JsonObject();
            changed = true;
        }
        if (state["hashIndex"] is not JsonObject)
        {
            state["hashIndex"] = new JsonObject();
            changed = true;
        }

        var entries = (JsonObject)state["entries"]!;
        var rebuiltIndex = new JsonObject();
        long highestSqId = 0;
        foreach (var entry in entries.Select(pair => pair.Value).OfType<JsonObject>())
        {
            var hash = Text(entry["hashvalue"]);
            var sqId = Integer(entry["sqID"], 0);
            if (sqId <= 0) continue;
            highestSqId = Math.Max(highestSqId, sqId);
            if (hash.Length == 0) continue;
            if (rebuiltIndex[hash] is not JsonArray ids) rebuiltIndex[hash] = ids = new JsonArray();
            ids.Add(sqId);
        }
        foreach (var ids in rebuiltIndex.Select(pair => pair.Value).OfType<JsonArray>())
        {
            var sorted = ids.Select(id => Integer(id, 0)).Where(id => id > 0).Distinct().Order().ToArray();
            ids.Clear();
            foreach (var id in sorted) ids.Add(id);
        }
        if (state["hashIndex"] is not JsonObject currentIndex || !JsonNode.DeepEquals(currentIndex, rebuiltIndex))
        {
            state["hashIndex"] = rebuiltIndex;
            changed = true;
        }

        var nextSqId = Math.Max(1, Integer(state["nextSqID"], 1));
        if (highestSqId < long.MaxValue) nextSqId = Math.Max(nextSqId, highestSqId + 1);
        if (Integer(state["nextSqID"], 0) != nextSqId)
        {
            state["nextSqID"] = nextSqId;
            changed = true;
        }
        return changed;
    }

    private static string CanonicalPayload(JsonObject payload)
    {
        var json = new StringBuilder("[");
        var first = true;
        foreach (var (key, value) in payload.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first) json.Append(',');
            first = false;
            json.Append('[').Append(JsonSerializer.Serialize(key, JsonOptions)).Append(',');
            AppendJson(json, value);
            json.Append(']');
        }
        return json.Append(']').ToString();
    }

    private static void AppendJson(StringBuilder json, JsonNode? node)
    {
        switch (node)
        {
            case null:
                json.Append("null");
                break;
            case JsonObject obj:
                json.Append('{');
                var firstProperty = true;
                foreach (var (key, value) in JavaScriptObjectEntries(obj))
                {
                    if (!firstProperty) json.Append(',');
                    firstProperty = false;
                    json.Append(JsonSerializer.Serialize(key, JsonOptions)).Append(':');
                    AppendJson(json, value);
                }
                json.Append('}');
                break;
            case JsonArray array:
                json.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) json.Append(',');
                    AppendJson(json, array[i]);
                }
                json.Append(']');
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                json.Append(JsonSerializer.Serialize(text, JsonOptions));
                break;
            case JsonValue value when value.TryGetValue<bool>(out var boolean):
                json.Append(boolean ? "true" : "false");
                break;
            case JsonValue value when value.TryGetValue<long>(out var integer):
                json.Append(integer.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValue value when value.TryGetValue<double>(out var real):
                json.Append(JavaScriptNumber(real));
                break;
            default:
                json.Append(node!.ToJsonString(JsonOptions));
                break;
        }
    }

    private static string JavaScriptNumber(double value)
    {
        if (value == 0) return "0";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = text.IndexOfAny(['e', 'E']);
        if (exponentAt < 0) return text;

        var mantissa = text[..exponentAt];
        var exponent = int.Parse(text[(exponentAt + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (exponent is < -6 or >= 21)
            return $"{mantissa}e{(exponent >= 0 ? "+" : "")}{exponent}";

        var sign = mantissa.StartsWith('-') ? "-" : "";
        if (sign.Length > 0) mantissa = mantissa[1..];
        var point = mantissa.IndexOf('.');
        if (point < 0) point = mantissa.Length;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        var decimalPoint = point + exponent;
        if (decimalPoint <= 0) return $"{sign}0.{new string('0', -decimalPoint)}{digits}";
        if (decimalPoint >= digits.Length) return sign + digits + new string('0', decimalPoint - digits.Length);
        return sign + digits[..decimalPoint] + "." + digits[decimalPoint..];
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> JavaScriptObjectEntries(JsonObject obj)
    {
        var indices = new List<(uint Index, KeyValuePair<string, JsonNode?> Pair)>();
        var other = new List<KeyValuePair<string, JsonNode?>>();
        foreach (var pair in obj)
        {
            if (uint.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                index != uint.MaxValue && pair.Key == index.ToString(CultureInfo.InvariantCulture))
                indices.Add((index, pair));
            else
                other.Add(pair);
        }
        foreach (var entry in indices.OrderBy(entry => entry.Index)) yield return entry.Pair;
        foreach (var pair in other) yield return pair;
    }

    private static PyValue PresetKey(JsonObject entry) => Py.KeyVal(
        ("hashvalue", new PyStr(Text(entry["hashvalue"]))),
        ("sqID", new PyInt(Integer(entry["sqID"], 0))));

    private static bool TryPresetKey(PyValue raw, out string hash, out long sqId)
    {
        if (raw is PyObject objectValue) raw = objectValue.Args;
        if (raw is PyTuple or PyList)
        {
            var values = raw is PyTuple tuple ? tuple.Items : ((PyList)raw).Items;
            hash = values.Count > 0 ? StringValue(values[0]).Trim() : "";
            sqId = values.Count > 1 ? Math.Max(0, Integer(values[1], 0)) : 0;
        }
        else if (raw is PyDict dict)
        {
            hash = "";
            sqId = 0;
            foreach (var (key, value) in dict.Entries)
            {
                var name = StringValue(key);
                if (name == "hashvalue") hash = StringValue(value).Trim();
                else if (name == "sqID") sqId = Math.Max(0, Integer(value, 0));
            }
        }
        else
        {
            hash = "";
            sqId = 0;
        }
        return hash.Length > 0 && sqId > 0;
    }

    private static JsonNode? ToJson(PyValue value, int depth = 0)
    {
        if (depth > 8) return null;
        return value switch
        {
            PyNone => null,
            PyBool boolean => JsonValue.Create(boolean.Value),
            PyInt integer => JsonValue.Create(integer.Value),
            PyLong integer => JsonValue.Create(integer.Value),
            PyReal real when double.IsFinite(real.Value) => JsonValue.Create(real.Value),
            PyReal => null,
            PyStr text => JsonValue.Create(text.Value),
            PyWString text => JsonValue.Create(text.Value),
            PyBytes text => JsonValue.Create(Encoding.UTF8.GetString(text.Value.Span)),
            PyBuffer buffer => JsonValue.Create(Encoding.UTF8.GetString(buffer.Value.Span)),
            PyToken token => JsonValue.Create(token.Value),
            PyList list => new JsonArray([.. list.Items.Select(item => ToJson(item, depth + 1))]),
            PyTuple tuple => new JsonArray([.. tuple.Items.Select(item => ToJson(item, depth + 1))]),
            PyDict dict => ToJsonObject(dict.Entries, depth + 1),
            PyObject objectValue => ToJson(objectValue.Args, depth + 1),
            PyObjectEx objectEx => ToJsonObjectEx(objectEx, depth + 1),
            PySubStruct subStruct => ToJson(subStruct.Value, depth + 1),
            _ => null,
        };
    }

    private static JsonObject ToJsonObject(IEnumerable<KeyValuePair<PyValue, PyValue>> entries, int depth)
    {
        var result = new JsonObject();
        foreach (var (key, value) in entries)
        {
            var name = StringValue(key);
            if (name.Length > 0) result[name] = ToJson(value, depth + 1);
        }
        return result;
    }

    private static JsonNode ToJsonObjectEx(PyObjectEx value, int depth)
    {
        var header = Sequence(value.Header);
        var headerName = header.Count > 0 ? StringValue(header[0]).Trim() : "";
        var headerArgs = header.Count > 1 ? Sequence(header[1]) : [];
        if (headerName is "util.KeyVal" or "utillib.KeyVal" or "KeyVal")
        {
            if (header.Count > 2 && header[2] is PyDict headerDict)
                return ToJsonObject(headerDict.Entries, depth + 1);
            if (value.Dict.Count > 0) return ToJsonObject(value.Dict, depth + 1);
            return headerArgs.Count > 0 ? ToJson(headerArgs[0], depth + 1) ?? new JsonObject() : new JsonObject();
        }
        if (headerName is "__builtin__.set" or "builtins.set" or "set")
            return headerArgs.Count > 0 ? ToJson(headerArgs[0], depth + 1) ?? new JsonArray() : new JsonArray();

        return new JsonObject
        {
            ["header"] = ToJson(value.Header, depth + 1),
            ["list"] = new JsonArray([.. value.List.Select(item => ToJson(item, depth + 1))]),
            ["dict"] = ToJsonObject(value.Dict, depth + 1),
        };
    }

    private static IReadOnlyList<PyValue> Sequence(PyValue? value) => value switch
    {
        PyTuple tuple => tuple.Items,
        PyList list => list.Items,
        _ => [],
    };

    private static string StringValue(PyValue? value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        PyBytes text => Encoding.UTF8.GetString(text.Value.Span),
        PyToken token => token.Value,
        PyInt integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        PyLong integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        PyReal real when double.IsFinite(real.Value) => real.Value.ToString("R", CultureInfo.InvariantCulture),
        PyBool boolean => boolean.Value ? "true" : "false",
        _ => "",
    };

    private static string Text(JsonNode? node) => node?.ToString() ?? "";

    private static long Integer(JsonNode? node, long fallback) =>
        long.TryParse(Text(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static long Integer(PyValue? value, long fallback) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real when double.IsFinite(real.Value) && real.Value >= 0 && real.Value < long.MaxValue => (long)Math.Truncate(real.Value),
        PyStr text when long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        PyWString text when long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => fallback,
    };
}
