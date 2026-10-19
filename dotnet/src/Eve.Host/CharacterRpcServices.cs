using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

public sealed class CharacterEnergyMgrRpcService : IService
{
    private const double DefaultCapacity = 100;
    private const double DefaultRechargeSeconds = 792;
    private readonly Eve.GameStore.GameStoreCache? _store;
    private readonly Func<long> _now;

    public CharacterEnergyMgrRpcService(Eve.GameStore.GameStoreCache? store = null, Func<long>? nowUnixMs = null)
        => (_store, _now) = (store, nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    public string Name => "characterEnergyMgr";
    public bool HasMethod(string method) => method == "GetMyEnergyState";

    public ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return ValueTask.FromResult<PyValue>(PyNone.Instance);
        call.Cancellation.ThrowIfCancellationRequested();
        var characterId = call.Session.CharacterId ?? call.Session.UserId ?? 0;
        var raw = characterId > 0
            ? _store?.Read("characterEnergyState", $"/characters/{characterId}").Data as System.Text.Json.Nodes.JsonObject
            : null;
        var nowFileTime = Py.FileTimeFromUnixMs(_now());
        var min = Read(raw, "minEnergyLevel", "_minEnergyLevel", 0);
        var capacity = Read(raw, "quiescentEnergyLevel", "_quiescentEnergyLevel", DefaultCapacity);
        if (capacity <= min) capacity = min + 1;
        var increase = Math.Max(double.Epsilon, Read(raw, "energyIncreasePerRechargeTick", "_energyIncreasePerRechargeTick", 1));
        var period = Math.Max(double.Epsilon, Read(raw, "rechargeTickPeriod", "_rechargeTickPeriod", DefaultRechargeSeconds));
        var energy = Math.Clamp(Read(raw, "energyLevel", "_energyLevel", capacity), min, capacity);
        var last = ReadLong(raw, "lastRechargeTickTimestamp", "_lastRechargeTickTimestamp", nowFileTime);
        if (nowFileTime >= last)
        {
            energy = Math.Min(capacity, energy + ((nowFileTime - last) / 10_000_000d / period) * increase);
            last = nowFileTime;
        }
        return ValueTask.FromResult<PyValue>(new PyObjectEx(ObjectExKind.Ex1,
            Py.Tuple(new PyToken("characterenergy.characterEnergyState.CharacterEnergyState"),
                Py.List(Py.Number(energy), new PyLong(last), Py.Number(increase), Py.Number(period), Py.Number(min), Py.Number(capacity))), [], []));
    }

    private static double Read(System.Text.Json.Nodes.JsonObject? source, string first, string second, double fallback) =>
        double.TryParse((source?[first] ?? source?[second])?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;
    private static long ReadLong(System.Text.Json.Nodes.JsonObject? source, string first, string second, long fallback) =>
        long.TryParse((source?[first] ?? source?[second])?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
}

public sealed class PaperDollServerRpcService : IService
{
    private static readonly string[] Methods = ["GetMyPaperDollData", "GetPaperDollData", "GetPaperDollDataBatch", "GetPaperDollPortraitDataFor", "UpdateExistingCharacterFull", "UpdateExistingCharacterLimited"];
    private static readonly string[] PortraitColumns = [
        "cameraX", "cameraY", "cameraZ", "cameraFieldOfView", "cameraPoiX", "cameraPoiY", "cameraPoiZ", "backgroundID", "lightID", "lightColorID", "lightIntensity", "portraitPoseNumber", "headLookTargetX", "headLookTargetY", "headLookTargetZ", "headTilt", "orientChar", "browLeftCurl", "browLeftTighten", "browLeftUpDown", "browRightCurl", "browRightTighten", "browRightUpDown", "eyeClose", "eyesLookVertical", "eyesLookHorizontal", "squintLeft", "squintRight", "jawSideways", "jawUp", "puckerLips", "frownLeft", "frownRight", "smileLeft", "smileRight", "renderStatus", "paperdollState"];
    private static readonly HashSet<string> IntegerPortraitFields = ["backgroundID", "lightColorID", "lightID", "renderStatus", "paperdollState", "portraitPoseNumber"];
    private static readonly Descriptor PortraitDescriptor = Descriptor.FromHeader(new PyObjectEx(ObjectExKind.Ex1,
        Py.Tuple(new PyToken("blue.DBRowDescriptor"), Py.Tuple(Py.Tuple([.. PortraitColumns.Select(name =>
            (PyValue)Py.Tuple(new PyStr(name), new PyInt(IntegerPortraitFields.Contains(name) ? 3 : 5)))]))), [], []));
    private readonly PgCharacterStore? _characters;

    public PaperDollServerRpcService(PgCharacterStore? characters = null) => _characters = characters;
    public string Name => "paperDollServer";
    public bool HasMethod(string method) => Methods.Contains(method, StringComparer.Ordinal);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        return call.Method switch
        {
            "GetMyPaperDollData" => await GetMyPaperDollDataAsync(call),
            "GetPaperDollData" => await GetPaperDollDataAsync(call),
            "GetPaperDollDataBatch" => await GetPaperDollDataBatchAsync(call),
            "GetPaperDollPortraitDataFor" => await GetPaperDollPortraitDataForAsync(call),
            "UpdateExistingCharacterFull" or "UpdateExistingCharacterLimited" => await UpdateAppearanceAsync(call),
            _ => PyNone.Instance,
        };
    }

    private async ValueTask<PyValue> GetMyPaperDollDataAsync(ServiceCall call)
    {
        var id = call.Session.CharacterId ?? 0;
        if (id <= 0 || _characters is null) return PyNone.Instance;
        var (doll, _) = await _characters.AppearanceAsync(id, call.Cancellation);
        return Decode(doll);
    }

    private async ValueTask<PyValue> GetPaperDollDataAsync(ServiceCall call)
    {
        if (_characters is null) return PyNone.Instance;
        var id = Long(call.Arg(0));
        if (id <= 0) id = call.Session.CharacterId ?? 0;
        var (doll, _) = await _characters.AppearanceAsync(id, call.Cancellation);
        return Decode(doll);
    }

    private async ValueTask<PyValue> GetPaperDollDataBatchAsync(ServiceCall call)
    {
        var ids = Ids(call.Arg(0));
        if (ids.Count == 0) return new PyDict([]);
        var appearances = _characters is null
            ? new Dictionary<long, (byte[]? Doll, byte[]? Portrait)>()
            : await _characters.AppearanceManyAsync(ids, call.Cancellation);
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var id in ids) entries.Add(new(new PyInt(id), Decode(appearances.GetValueOrDefault(id).Doll)));
        return new PyDict(entries);
    }

    private async ValueTask<PyValue> GetPaperDollPortraitDataForAsync(ServiceCall call)
    {
        if (_characters is null) return Py.Tuple(Py.List(), PyNone.Instance);
        var id = Long(call.Arg(0));
        if (id <= 0) id = call.Session.CharacterId ?? 0;
        var (_, portrait) = await _characters.AppearanceAsync(id, call.Cancellation);
        if (portrait is null) return Py.Tuple(Py.List(), PyNone.Instance);
        var decoded = Decode(portrait);
        if (decoded is PyNone) return Py.Tuple(Py.List(), PyNone.Instance);
        var fields = NormalizePortrait(decoded);
        var values = PortraitColumns.Select(name => PortraitValue(name, fields[name])).ToArray();
        return Py.Tuple(new PyList([new PyPackedRow(PortraitDescriptor, values)]), PyNone.Instance);
    }

    private async ValueTask<PyValue> UpdateAppearanceAsync(ServiceCall call)
    {
        if (_characters is null) return PyNone.Instance;
        var id = Long(call.Arg(0));
        if (id <= 0) return PyNone.Instance;
        var character = await _characters.GetAsync(id, call.Cancellation);
        if (character is null || (character.AccountId != call.Session.UserId && call.Session.CharacterId != id)) return PyNone.Instance;
        var dollExists = call.Args.Count <= 3 || BooleanOrDefault(call.Arg(3), true);
        var hasDoll = !dollExists || call.Arg(1) is not PyNone;
        var hasPortrait = !dollExists || call.Arg(2) is not PyNone;
        var doll = call.Arg(1) is not PyNone ? MarshalCodec.Encode(NormalizePaperDollPayload(call.Arg(1))) : null;
        var portrait = call.Arg(2) is not PyNone ? MarshalCodec.Encode(call.Arg(2)) : null;
        if (hasDoll || hasPortrait)
            await _characters.SetAppearanceAsync(id, doll, hasDoll, portrait, hasPortrait, call.Cancellation);
        return PyNone.Instance;
    }

    private static readonly string[] SculptWeightFields = ["weightUpDown", "weightLeftRight", "weightForwardBack"];

    private static PyValue Decode(byte[]? bytes) =>
        bytes is null ? PyNone.Instance : NormalizePaperDollPayload(MarshalCodec.DecodeExact(bytes));

    internal static PyValue NormalizePaperDollPayload(PyValue value) => value switch
    {
        PyList list => new PyList(list.Items.Select(NormalizePaperDollPayload).ToArray()),
        PyTuple tuple => Py.Tuple(tuple.Items.Select(NormalizePaperDollPayload).ToArray()),
        PyDict dictionary => NormalizeSculptingFields(dictionary.Entries),
        PyObject instance => new PyObject(instance.Name, NormalizePaperDollPayload(instance.Args)),
        PyObjectEx instance => new PyObjectEx(
            instance.Kind,
            NormalizeSculptingHeader(instance.Header),
            instance.List.Select(NormalizePaperDollPayload).ToArray(),
            NormalizeSculptingFields(instance.Dict).Entries),
        PyPackedRow row => NormalizeSculptingRow(row),
        _ => value,
    };

    private static PyDict NormalizeSculptingFields(IEnumerable<KeyValuePair<PyValue, PyValue>> source)
    {
        var entries = source
            .Select(entry => new KeyValuePair<PyValue, PyValue>(entry.Key, NormalizePaperDollPayload(entry.Value)))
            .ToList();
        bool HasName(string name) => entries.Any(entry => PaperDollName(entry.Key) == name);
        if (!HasName("sculptLocationID") || !SculptWeightFields.Any(HasName))
            return new PyDict(entries);

        foreach (var name in SculptWeightFields)
        {
            var index = entries.FindLastIndex(entry => PaperDollName(entry.Key) == name);
            if (index < 0)
                entries.Add(new(new PyStr(name), new PyReal(0d)));
            else if (entries[index].Value is PyNone)
                entries[index] = new(entries[index].Key, new PyReal(0d));
        }
        return new PyDict(entries);
    }

    private static PyValue NormalizeSculptingHeader(PyValue source)
    {
        var header = NormalizePaperDollPayload(source);
        if (header is not PyTuple { Items.Count: > 0 } tuple)
            return header;
        var rowHeader = tuple.Items[0] switch
        {
            PyTuple nested => nested.Items,
            PyList nested => nested.Items,
            _ => Array.Empty<PyValue>(),
        };
        if (rowHeader.Count == 0 || PaperDollName(rowHeader[0])?.EndsWith(".SculptingRow", StringComparison.Ordinal) != true)
            return header;

        var columns = rowHeader.ToList();
        while (columns.Count < 5)
            columns.Add(new PyInt(0));
        for (var index = 2; index <= 4; index++)
            if (columns[index] is PyNone)
                columns[index] = new PyInt(0);
        var items = tuple.Items.ToArray();
        items[0] = tuple.Items[0] is PyList ? new PyList(columns) : Py.Tuple(columns.ToArray());
        return Py.Tuple(items);
    }

    private static PyValue NormalizeSculptingRow(PyPackedRow row)
    {
        var columns = row.Columns;
        var values = row.Values.Select(NormalizePaperDollPayload).ToList();
        bool IsWeight(string name) => SculptWeightFields.Contains(name, StringComparer.Ordinal);
        if (!columns.Any(column => column.Name == "sculptLocationID") || !columns.Any(column => IsWeight(column.Name)))
            return new PyPackedRow(row.Descriptor, values);
        while (values.Count < columns.Count)
            values.Add(PyNone.Instance);
        for (var index = 0; index < columns.Count; index++)
            if (IsWeight(columns[index].Name) && values[index] is PyNone)
                values[index] = new PyReal(0d);
        return new PyPackedRow(row.Descriptor, values);
    }

    private static string? PaperDollName(PyValue value) => value switch
    {
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        PyToken token => token.Value,
        _ => null,
    };

    private static Dictionary<string, double?> NormalizePortrait(PyValue source)
    {
        var fields = new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            ["browLeftCurl"] = 0, ["browLeftTighten"] = 0, ["browLeftUpDown"] = 0.5,
            ["browRightCurl"] = 0.5, ["browRightTighten"] = 0, ["browRightUpDown"] = 0.5,
            ["eyeClose"] = 0, ["eyesLookHorizontal"] = 0.5, ["eyesLookVertical"] = 0.5,
            ["frownLeft"] = 0, ["frownRight"] = 0, ["headLookTargetX"] = 0,
            ["headLookTargetY"] = 1.5, ["headLookTargetZ"] = 1, ["headTilt"] = 0,
            ["jawSideways"] = 0.5, ["jawUp"] = 0.5, ["orientChar"] = 0, ["puckerLips"] = 0,
            ["smileLeft"] = 0, ["smileRight"] = 0, ["squintLeft"] = 0, ["squintRight"] = 0,
            ["cameraFieldOfView"] = 0.3, ["cameraPoiX"] = 0, ["cameraPoiY"] = 1.5, ["cameraPoiZ"] = 0,
            ["cameraX"] = 0, ["cameraY"] = 1.5, ["cameraZ"] = 1.5,
            ["backgroundID"] = 1, ["lightColorID"] = null, ["lightID"] = null, ["lightIntensity"] = null,
            ["renderStatus"] = 0, ["paperdollState"] = 0, ["portraitPoseNumber"] = 0,
        };
        foreach (var key in fields.Keys.ToArray())
            if (TryField(source, key, out var direct) && Numeric(direct, out var number)) fields[key] = number;
        SetVector(source, "cameraPosition", ["cameraX", "cameraY", "cameraZ"], fields);
        SetVector(source, "cameraPoi", ["cameraPoiX", "cameraPoiY", "cameraPoiZ"], fields);
        if (TryField(source, "poseData", out var pose))
        {
            foreach (var field in fields.Keys.ToArray())
            {
                var key = string.Concat(field.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
                if (TryField(pose, key, out var value) && Numeric(value, out var number)) fields[field] = number;
            }
            if (TryField(pose, "HeadLookTarget", out var target)) SetVectorValue(target, ["headLookTargetX", "headLookTargetY", "headLookTargetZ"], fields);
        }
        return fields;
    }

    private static void SetVector(PyValue source, string name, string[] fields, Dictionary<string, double?> values)
    {
        if (TryField(source, name, out var vector)) SetVectorValue(vector, fields, values);
    }
    private static void SetVectorValue(PyValue vector, string[] fields, Dictionary<string, double?> values)
    {
        var components = vector switch { PyList list => list.Items, PyTuple tuple => tuple.Items, _ => Array.Empty<PyValue>() };
        for (var i = 0; i < Math.Min(3, components.Count); i++) if (Numeric(components[i], out var number)) values[fields[i]] = number;
        var axes = new[] { "x", "y", "z" };
        for (var i = 0; i < 3; i++) if (TryField(vector, axes[i], out var value) && Numeric(value, out var number)) values[fields[i]] = number;
    }
    private static PyValue PortraitValue(string name, double? value) => value is null ? PyNone.Instance : IntegerPortraitFields.Contains(name) ? new PyInt((long)value.Value) : new PyReal(value.Value);
    private static bool TryField(PyValue? source, string name, out PyValue value)
    {
        IEnumerable<KeyValuePair<PyValue, PyValue>> entries = source switch { PyDict d => d.Entries, PyObject { Args: PyDict args } => args.Entries, PyObjectEx ex => ex.Dict, _ => Array.Empty<KeyValuePair<PyValue, PyValue>>() };
        foreach (var (key, entry) in entries) if (JsSemantics.StringValue(key) == name) { value = entry; return true; }
        value = PyNone.Instance; return false;
    }
    private static bool Numeric(PyValue value, out double number)
    {
        number = value switch { PyInt i => i.Value, PyLong l => l.Value, PyReal r => r.Value, _ => double.NaN };
        return double.IsFinite(number);
    }
    private static List<long> Ids(PyValue value) => value switch
    {
        PyList list => list.Items.Select(Long).Where(id => id > 0).Distinct().ToList(),
        PyTuple tuple => tuple.Items.Select(Long).Where(id => id > 0).Distinct().ToList(),
        _ => [],
    };
    private static long Long(PyValue? value) => value switch { PyInt i => i.Value, PyLong l => l.Value, PyReal r when double.IsFinite(r.Value) => (long)r.Value, _ => 0 };
    private static bool BooleanOrDefault(PyValue value, bool fallback) => value is PyNone ? fallback : value switch { PyBool b => b == PyBool.True, PyInt i => i.Value != 0, PyLong l => l.Value != 0, _ => fallback };
}

public sealed class ClientStatLoggerRpcService : IService
{
    private readonly Action<string> _log;

    public ClientStatLoggerRpcService(Action<string>? log = null)
        => _log = log ?? (message => Console.Error.WriteLine($"[ClientStatLogger] LogString {System.Text.Json.JsonSerializer.Serialize(message)}"));

    public string Name => "clientStatLogger";
    public bool HasMethod(string method) => method == "LogString";

    public ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return ValueTask.FromResult<PyValue>(PyNone.Instance);
        call.Cancellation.ThrowIfCancellationRequested();
        var message = call.Arg(0) is PyNone ? "" : JsSemantics.StringValue(call.Arg(0)) ?? "";
        _log(message);
        return ValueTask.FromResult<PyValue>(PyNone.Instance);
    }
}
