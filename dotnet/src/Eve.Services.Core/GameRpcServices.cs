using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>The answer of a stance set: the stance it had, and whether the stance changed (a changed stance is announced and the ship is sent again).</summary>
public sealed record ShipStanceChange(bool Success, int OldStanceId = 0, string? ErrorCode = null, bool Changed = false);

/// <summary>Optional host data paths for RPCs whose reference services depend on game state.</summary>
public sealed record GameRpcCallbacks(
    Func<int?, CancellationToken, ValueTask<int?>>? SeedRafflesAsync = null,
    Action<bool, PyValue, PyValue>? SetHijackMode = null,
    Func<bool>? IsHijackModeEnabled = null,
    Func<JsonObject?>? ReadSharedSettings = null,
    Func<JsonObject, CancellationToken, ValueTask<bool>>? WriteSharedSettingsAsync = null,
    Func<long, int, long, CancellationToken, ValueTask<ShipStanceChange?>>? SetShipStanceAsync = null,
    Func<CancellationToken, ValueTask<JsonObject?>>? ReadInterferenceTableAsync = null,
    Func<CancellationToken, ValueTask<int?>>? ReadActiveSurveyIdAsync = null,
    Func<string, ServiceCall, CancellationToken, ValueTask>? RecordSurveyEventAsync = null,
    Func<long, CancellationToken, ValueTask<JsonObject?>>? ReadKillmailAsync = null,
    Func<JsonObject?>? ReadOverviewPresets = null,
    Func<JsonObject, bool>? WriteOverviewPresets = null,
    ReadMiningScanResultsCallback? ReadMiningScanResultsAsync = null,
    AbyssalSpawnReportCallback? AbyssalSpawnReportAsync = null,
    ReadPeaceTreatiesCallback? ReadPeaceTreaties = null,
    PublicQaToolsRpcCallbacks? PublicQaTools = null);

[Service("raffleMgr")]
public sealed partial class RaffleMgrService(Func<int?, CancellationToken, ValueTask<int?>>? seedRafflesAsync = null)
{
    [Method("QA_SeedRaffles")]
    public async ValueTask<PyValue> QaSeedRaffles(ServiceCall call)
    {
        var seeded = seedRafflesAsync is null
            ? 0
            : await seedRafflesAsync(NullableInteger(call.Arg(0)), call.Cancellation).ConfigureAwait(false) ?? 0;
        return new PyInt(Math.Max(0, seeded));
    }

    private static int? NullableInteger(PyValue value) => value switch
    {
        PyInt number => (int)Math.Clamp(number.Value, int.MinValue, int.MaxValue),
        PyLong number => (int)Math.Clamp(number.Value, int.MinValue, int.MaxValue),
        PyReal number when double.IsFinite(number.Value) =>
            (int)Math.Clamp(Math.Truncate(number.Value), int.MinValue, int.MaxValue),
        _ => null,
    };
}


[Service("raffleProxy")]
public sealed partial class RaffleProxyService
{
    [Method("AmIBanned")]
    public PyValue AmIBanned(ServiceCall call)
    {
        // shortcut: HyperNet account bans are not persisted yet; replace when raffle moderation is modeled.
        return PyBool.False;
    }
}


[Service("expertSystemMgr")]
public sealed partial class ExpertSystemMgrService
{
    // shortcut: expert-system ownership is not persisted; read per-character records when that store exists.
    [Method("GetMyExpertSystems")]
    public PyValue GetMyExpertSystems(ServiceCall _) => new PyDict([]);
}

[Service("contractProxy")]
public sealed partial class ContractProxyService
{
    // shortcut: contract records are not persisted; calculate escrow from the contract store once it exists.
    [Method("GetMyContractEscrow")]
    public PyValue GetMyContractEscrow(ServiceCall _) =>
        Eve.Services.Py.KeyVal(
            ("iskEscrow", Eve.Services.Py.Number(0d)),
            ("itemsEscrow", new PyInt(0)));
}

[Service("jumpCloneSvc")]
public sealed partial class JumpCloneService
{
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall _) =>
        new PyInt(Eve.Macho.MachoMessage.ProxyNodeId);
}

[Service("serviceGatewayMgr")]
public sealed partial class ServiceGatewayMgrService(
    Action<bool, PyValue, PyValue>? setHijackMode = null,
    Func<bool>? isHijackModeEnabled = null)
{
    private bool _enabled;

    [Method("qa_enable_hijack_mode")]
    public PyValue QaEnableHijackMode(ServiceCall call)
    {
        _enabled = true;
        setHijackMode?.Invoke(true, call.Arg(0), call.Arg(1));
        return PyNone.Instance;
    }

    [Method("qa_disable_hijack_mode")]
    public PyValue QaDisableHijackMode(ServiceCall call)
    {
        _enabled = false;
        setHijackMode?.Invoke(false, PyNone.Instance, PyNone.Instance);
        return PyNone.Instance;
    }

    [Method("qa_is_hijack_mode_enabled")]
    public PyValue QaIsHijackModeEnabled(ServiceCall call) =>
        (isHijackModeEnabled?.Invoke() ?? _enabled) ? PyBool.True : PyBool.False;
}

[Service("sharedSettingsMgr")]
public sealed partial class SharedSettingsMgrService(
    Func<JsonObject?>? readState = null,
    Func<JsonObject, CancellationToken, ValueTask<bool>>? writeStateAsync = null,
    Func<long>? nowUnixMs = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JsonObject _memoryState = EmptyState();

    [Method("StoreSettingLinkAndGetID")]
    public async ValueTask<PyValue> StoreSettingLinkAndGetId(ServiceCall call)
    {
        var settingTypeId = SettingType(call.Arg(0));
        var payload = BuildPayload(call.Arg(1));
        if (settingTypeId is null || payload is null)
            return PyNone.Instance;

        await _gate.WaitAsync(call.Cancellation).ConfigureAwait(false);
        try
        {
            var state = ReadState();
            var entries = EnsureObject(state, "entries");
            var hash = Hash(payload);
            foreach (var entry in entries.Select(pair => pair.Value as JsonObject).Where(entry => entry is not null))
            {
                if (Integer(entry!["settingTypeID"]) == settingTypeId &&
                    Text(entry["hashvalue"]) == hash && Text(entry["payload"]) == payload &&
                    Integer(entry["sqID"]) is > 0 and var existingId)
                {
                    return SettingKey(hash, existingId, settingTypeId.Value);
                }
            }

            var sqId = Math.Max(1, Integer(state["nextSqID"], 1));
            var key = EntryKey(settingTypeId.Value, hash, sqId);
            entries[key] = new JsonObject
            {
                ["settingTypeID"] = settingTypeId.Value,
                ["hashvalue"] = hash,
                ["sqID"] = sqId,
                ["payload"] = payload,
                ["ownerID"] = Math.Max(0, call.Session.GetInt("charid") ?? call.Session.GetInt("userid") ?? 0),
                ["createdAt"] = nowUnixMs?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            var hashIndex = EnsureObject(state, "hashIndex");
            var hashKey = HashKey(settingTypeId.Value, hash);
            var ids = hashIndex[hashKey] as JsonArray ?? new JsonArray();
            var sortedIds = ids.Select(id => Integer(id)).Append(sqId).Distinct().Order().ToArray();
            hashIndex[hashKey] = new JsonArray(sortedIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            state["nextSqID"] = JsonValue.Create(sqId + 1);

            if (!await WriteStateAsync(state, call.Cancellation).ConfigureAwait(false))
                return PyNone.Instance;
            return SettingKey(hash, sqId, settingTypeId.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    [Method("GetStoredSharedSetting")]
    public async ValueTask<PyValue> GetStoredSharedSetting(ServiceCall call)
    {
        var key = ParseSettingKey(call.Arg(0));
        if (key is null)
            return PyNone.Instance;

        await _gate.WaitAsync(call.Cancellation).ConfigureAwait(false);
        try
        {
            var entries = ReadState()["entries"] as JsonObject;
            var entry = entries?[EntryKey(key.Value.SettingTypeId, key.Value.Hash, key.Value.SqId)] as JsonObject;
            return entry is not null && Text(entry["hashvalue"]) == key.Value.Hash &&
                   !string.IsNullOrEmpty(Text(entry["payload"]))
                ? new PyStr(Text(entry["payload"]))
                : PyNone.Instance;
        }
        finally
        {
            _gate.Release();
        }
    }

    private JsonObject ReadState()
    {
        var state = readState is not null && writeStateAsync is not null
            ? readState()?.DeepClone() as JsonObject
            : _memoryState.DeepClone() as JsonObject;
        state ??= EmptyState();
        state["nextSqID"] = JsonValue.Create(Math.Max(1, Integer(state["nextSqID"], 1)));
        _ = EnsureObject(state, "entries");
        _ = EnsureObject(state, "hashIndex");
        return state;
    }

    private async ValueTask<bool> WriteStateAsync(JsonObject state, CancellationToken cancellationToken)
    {
        if (readState is not null && writeStateAsync is not null)
            return await writeStateAsync(state, cancellationToken).ConfigureAwait(false);
        _memoryState = state.DeepClone() as JsonObject ?? EmptyState();
        return true;
    }

    private static JsonObject EmptyState() => new()
    {
        ["nextSqID"] = 1,
        ["entries"] = new JsonObject(),
        ["hashIndex"] = new JsonObject(),
    };

    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject value)
            return value;
        value = new JsonObject();
        parent[key] = value;
        return value;
    }

    private static PyValue SettingKey(string hash, long sqId, int settingTypeId) =>
        Py.KeyVal(
            ("hashvalue", new PyStr(hash)),
            ("sqID", Py.Number(sqId)),
            ("settingTypeID", new PyInt(settingTypeId)));

    private static string EntryKey(int settingTypeId, string hash, long sqId) =>
        $"{settingTypeId}::{hash}::{sqId.ToString(CultureInfo.InvariantCulture)}";

    private static string HashKey(int settingTypeId, string hash) => $"{settingTypeId}::{hash}";

    private static string Hash(string payload) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    private static int? SettingType(PyValue? value)
    {
        var id = Integer(value);
        return id is 2 or 3 ? (int)id : null;
    }

    private static (string Hash, long SqId, int SettingTypeId)? ParseSettingKey(PyValue value)
    {
        if (value is PyObject { Args: PyDict objectArgs })
        {
            var hash = Text(Field(objectArgs, "hashvalue"));
            var sqId = Integer(Field(objectArgs, "sqID"));
            var settingTypeId = SettingType(Field(objectArgs, "settingTypeID"));
            return !string.IsNullOrWhiteSpace(hash) && sqId > 0 && settingTypeId is { } type
                ? (hash.Trim(), sqId, type)
                : null;
        }

        var items = value switch { PyTuple tuple => tuple.Items, PyList list => list.Items, _ => [] };
        if (items.Count < 3)
            return null;
        var listHash = Text(items[0]).Trim();
        var listSqId = Integer(items[1]);
        var listTypeId = SettingType(items[2]);
        return !string.IsNullOrWhiteSpace(listHash) && listSqId > 0 && listTypeId is { } parsedType
            ? (listHash, listSqId, parsedType)
            : null;
    }

    private static PyValue? Field(PyDict dict, string name) => dict.Entries
        .FirstOrDefault(entry => Text(entry.Key) == name).Value;

    private static string? BuildPayload(PyValue value)
    {
        if (value is PyStr or PyWString or PyBytes)
        {
            var text = Text(value);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        var entries = DictionaryEntries(value);
        if (entries is null || entries.Count == 0)
            return null;
        var payload = new JsonArray();
        foreach (var (key, entryValue) in entries.OrderBy(entry => Text(entry.Key), StringComparer.Ordinal))
            payload.Add(new JsonArray(JsonValue.Create(Text(key)), ToJson(entryValue)));
        return payload.ToJsonString(JsonOptions);
    }

    private static IReadOnlyList<KeyValuePair<PyValue, PyValue>>? DictionaryEntries(PyValue value) => value switch
    {
        PyDict dict => dict.Entries,
        PyObject { Args: PyDict dict } => dict.Entries,
        PyObjectEx objectEx => objectEx.Dict,
        _ => null,
    };

    private static JsonNode? ToJson(PyValue value) => value switch
    {
        PyNone => null,
        PyBool boolean => JsonValue.Create(boolean.Value),
        PyInt integer => JsonValue.Create(integer.Value),
        PyLong integer => JsonValue.Create(integer.Value),
        PyReal number when double.IsFinite(number.Value) => JsonValue.Create(number.Value),
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => JsonValue.Create(Eve.Macho.JsSemantics.StringValue(text)!),
        PyBytes bytes => JsonValue.Create(Encoding.UTF8.GetString(bytes.Value.Span)),
        PyTuple tuple => Array(tuple.Items),
        PyList list => Array(list.Items),
        PyDict dict => Object(dict.Entries),
        PyObject obj => ToJson(obj.Args),
        PyObjectEx objectEx => Object(objectEx.Dict),
        _ => null,
    };

    private static JsonArray Array(IReadOnlyList<PyValue> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
            array.Add(ToJson(item));
        return array;
    }

    private static JsonObject Object(IReadOnlyList<KeyValuePair<PyValue, PyValue>> entries)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in entries.OrderBy(entry => Text(entry.Key), StringComparer.Ordinal))
            obj[Text(key)] = ToJson(value);
        return obj;
    }

    private static long Integer(JsonNode? value, long fallback = 0) =>
        long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;

    private static long Integer(PyValue? value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal number when double.IsFinite(number.Value) => (long)Math.Clamp(Math.Truncate(number.Value), long.MinValue, long.MaxValue),
        PyStr text when long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        _ => 0,
    };

    private static string Text(JsonNode? value) => value is JsonValue json && json.TryGetValue<string>(out var text)
        ? text ?? string.Empty
        : value?.ToString() ?? string.Empty;

    private static string Text(PyValue? value) => value switch
    {
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        PyToken token => token.Value,
        PyInt integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        PyLong integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        _ => string.Empty,
    };
}

[Service("shipStanceMgr")]
public sealed partial class ShipStanceMgrService(
    Func<long, int, long, CancellationToken, ValueTask<ShipStanceChange?>>? setShipStanceAsync = null,
    Func<ClientSession, CancellationToken, Task>? refreshShipInSpaceAsync = null,
    Func<ClientSession, bool>? isInSpace = null)
{
    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> ModifiersByShipType =
        new Dictionary<int, IReadOnlyDictionary<int, int>>
        {
            [34317] = new Dictionary<int, int> { [1] = 34319, [2] = 34323, [3] = 34321 },
            [34562] = new Dictionary<int, int> { [1] = 34564, [2] = 34566, [3] = 34570 },
            [34828] = new Dictionary<int, int> { [1] = 35676, [2] = 35677, [3] = 35678 },
            [35683] = new Dictionary<int, int> { [1] = 35686, [2] = 35687, [3] = 35688 },
            [89808] = new Dictionary<int, int> { [1] = 90060, [2] = 90062, [3] = 90064 },
            [89807] = new Dictionary<int, int> { [1] = 90061, [2] = 90065, [3] = 90063 },
        };

    [Method("SetShipStance")]
    public async ValueTask<PyValue> SetShipStance(ServiceCall call)
    {
        var characterId = call.Session.GetInt("charid") ?? 0;
        var shipId = Integer(call.Arg(0));
        var stanceId = Integer(call.Arg(1));
        if (characterId <= 0)
            throw Notify("CHARACTER_NOT_FOUND");
        if (shipId <= 0)
            throw Notify("SHIP_NOT_FOUND");
        if (setShipStanceAsync is null)
            throw Notify("ITEM_CREATE_FAILED");
        // the wiki's rule: a tactical mode is switched in space (Tactical Destroyers, Mobile Depot aside); the client offers the buttons in station too
        if (isInSpace is not null && !isInSpace(call.Session))
            throw Notify("SHIP_STANCE_NOT_IN_SPACE");

        var result = await setShipStanceAsync(shipId, (int)Math.Clamp(stanceId, int.MinValue, int.MaxValue), characterId, call.Cancellation)
            .ConfigureAwait(false);
        if (result is not { Success: true } success)
            throw Notify(result?.ErrorCode ?? "ITEM_CREATE_FAILED");
        if (success.Changed)
        {
            // after the commit, as the reference does: the client's stance button follows OnStanceActive (shipID, stanceID), and a ship in space is sent again
            call.Session.SendNotification("OnStanceActive", "charid", InventoryWire.Id(shipId), new PyInt(stanceId));
            if (refreshShipInSpaceAsync is not null)
                await refreshShipInSpaceAsync(call.Session, call.Cancellation).ConfigureAwait(false);
        }
        return new PyInt(success.OldStanceId);
    }

    public static IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> StanceModifiersByShipType => ModifiersByShipType;

    private static MachoWrappedException Notify(string errorCode)
    {
        var message = errorCode switch
        {
            "CHARACTER_NOT_FOUND" => "Select a character before changing ship stance.",
            "SHIP_NOT_FOUND" => "Ship stance can only be changed for a valid ship.",
            "SHIP_NOT_OWNED" => "You can only change stance on your own ship.",
            "SHIP_STANCE_UNSUPPORTED" => "That ship does not support tactical modes.",
            "SHIP_STANCE_INVALID" => "That tactical mode is not available for this ship.",
            "SHIP_STANCE_COOLDOWN" => "That tactical mode is still reconfiguring. Try again shortly.",
            "SHIP_STANCE_COOLDOWN_UNAVAILABLE" => "That ship's tactical mode cooldown could not be verified.",
            "SHIP_STANCE_NOT_IN_SPACE" => "Tactical modes can only be switched in space.",
            _ => "Could not update ship stance state.",
        };
        return new MachoWrappedException(MachoErrors.Wrapped("CustomNotify", [],
        [new KeyValuePair<PyValue, PyValue>(new PyStr("notify"), new PyStr(message))]));
    }

    private static long Integer(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal number when double.IsFinite(number.Value) => (long)Math.Clamp(Math.Truncate(number.Value), long.MinValue, long.MaxValue),
        _ => 0,
    };
}

[Service("solarsystemInterferenceMgr")]
public sealed partial class SolarSystemInterferenceMgrService(
    Func<CancellationToken, ValueTask<JsonObject?>>? readTableAsync = null,
    Func<long>? nowUnixMs = null)
{
    private const string StateClassName = "solarsysteminterference.solarsystemInterferenceState.SolarsystemInterferenceState";
    private const double DefaultDecrease = 2800;
    private const double DefaultPeriodSeconds = 25200;
    private const double DefaultMaximum = 10000;
    private const double NumberEpsilon = 2.2204460492503131e-16;
    private const double MediumThreshold = 0.33;
    private const double HighThreshold = 0.66;
    private const double FiletimeTicksPerSecond = 10_000_000;

    [Method("GetLocalInterferenceState")]
    public async ValueTask<PyValue> GetLocalInterferenceState(ServiceCall call)
    {
        var systemId = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 0;
        var table = await ReadTableAsync(call.Cancellation).ConfigureAwait(false);
        var raw = systemId > 0
            ? (table?["systems"] as JsonObject)?[systemId.ToString(CultureInfo.InvariantCulture)] as JsonObject
            : null;
        return ObjectState(Calculate(raw, CurrentFiletime()));
    }

    [Method("GetAllInterferenceBands")]
    public async ValueTask<PyValue> GetAllInterferenceBands(ServiceCall call)
    {
        var table = await ReadTableAsync(call.Cancellation).ConfigureAwait(false);
        var now = CurrentFiletime();
        var rows = new List<KeyValuePair<PyValue, PyValue>>();
        if (table?["systems"] is JsonObject systems)
        {
            foreach (var (key, value) in systems)
            {
                if (!long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var systemId) || systemId <= 0)
                    continue;
                var band = Band(Calculate(value as JsonObject, now));
                if (band != "N")
                    rows.Add(new(new PyInt(systemId), new PyStr(band)));
            }
        }
        return new PyDict(rows);
    }

    private ValueTask<JsonObject?> ReadTableAsync(CancellationToken cancellationToken) =>
        readTableAsync is null ? ValueTask.FromResult<JsonObject?>(null) : readTableAsync(cancellationToken);

    private long CurrentFiletime() => Py.FileTimeFromUnixMs(
        nowUnixMs?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static PyValue ObjectState(InterferenceState state) => new PyObjectEx(
        ObjectExKind.Ex1,
        Py.Tuple(new PyToken(StateClassName), Py.Tuple(
            Py.Number(state.Level),
            Py.FileTimeLong(state.LastDecayFiletime),
            Py.Number(state.DecreasePerTick),
            Py.Number(state.TickPeriodSeconds),
            Py.Number(state.Maximum),
            Py.Number(state.Quiescent))),
        [],
        []);

    private static InterferenceState Calculate(JsonObject? source, long now)
    {
        var quiescent = Number(source?["quiescentInterferenceLevel"] ?? source?["_quiescentInterferenceLevel"], 0);
        var maximum = Number(source?["maxInterferenceLevel"] ?? source?["_maxInterferenceLevel"], DefaultMaximum);
        if (maximum <= quiescent)
            maximum = quiescent + 1;
        var decrease = Math.Max(NumberEpsilon,
            Number(source?["interferenceDecreasePerDecayTick"] ?? source?["_interferenceDecreasePerDecayTick"], DefaultDecrease));
        var period = Math.Max(NumberEpsilon,
            Number(source?["decayTickPeriod"] ?? source?["_decayTickPeriod"], DefaultPeriodSeconds));
        var level = Math.Clamp(
            Number(source?["interferenceLevel"] ?? source?["_interferenceLevel"], quiescent),
            quiescent,
            maximum);
        var last = Long(source?["lastDecayTickTimestamp"] ?? source?["_lastDecayTickTimestamp"], now);
        if (level > quiescent && now >= last)
        {
            var elapsedSeconds = (now - last) / FiletimeTicksPerSecond;
            level = Math.Max(quiescent, level - (elapsedSeconds / period * decrease));
            last = now;
        }
        return new(level, last, decrease, period, maximum, quiescent);
    }

    private static string Band(InterferenceState state)
    {
        if (state.Level <= state.Quiescent)
            return "N";
        var ratio = (state.Level - state.Quiescent) / (state.Maximum - state.Quiescent);
        return ratio > HighThreshold ? "H" : ratio > MediumThreshold ? "M" : "L";
    }

    private static double Number(JsonNode? value, double fallback) =>
        double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
        double.IsFinite(number) ? number : fallback;

    private static long Long(JsonNode? value, long fallback) =>
        long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number : fallback;

    private sealed record InterferenceState(
        double Level, long LastDecayFiletime, double DecreasePerTick, double TickPeriodSeconds, double Maximum, double Quiescent);
}

[Service("survey")]
public sealed partial class SurveyService(
    Func<CancellationToken, ValueTask<int?>>? readActiveSurveyIdAsync = null,
    Func<string, ServiceCall, CancellationToken, ValueTask>? recordEventAsync = null)
{
    [Method("GetActiveSurveyID")]
    public async ValueTask<PyValue> GetActiveSurveyId(ServiceCall call)
    {
        await RecordAsync("get_active_survey_id", call).ConfigureAwait(false);
        var surveyId = readActiveSurveyIdAsync is null
            ? null
            : await readActiveSurveyIdAsync(call.Cancellation).ConfigureAwait(false);
        return surveyId is > 0 ? new PyInt(surveyId.Value) : PyNone.Instance;
    }

    [Method("PerformSurveyChecks")]
    public async ValueTask<PyValue> PerformSurveyChecks(ServiceCall call)
    {
        await RecordAsync("perform_checks", call).ConfigureAwait(false);
        return PyNone.Instance;
    }

    [Method("ClaimSurveyRewards")]
    public async ValueTask<PyValue> ClaimSurveyRewards(ServiceCall call)
    {
        await RecordAsync("claim_rewards", call).ConfigureAwait(false);
        return PyNone.Instance;
    }

    private ValueTask RecordAsync(string eventName, ServiceCall call) =>
        recordEventAsync is null
            ? ValueTask.CompletedTask
            : recordEventAsync(eventName, call, call.Cancellation);
}

[Service("warStatisticMgr")]
public sealed partial class WarStatisticMgrService(
    Func<long, CancellationToken, ValueTask<JsonObject?>>? readKillmailAsync = null,
    Func<long>? nowUnixMs = null)
{
    [Method("GetKillMail")]
    public async ValueTask<PyValue> GetKillMail(ServiceCall call)
    {
        var killId = PositiveInteger(call.Arg(0));
        if (killId <= 0 || readKillmailAsync is null)
            return PyNone.Instance;

        var record = await readKillmailAsync(killId, call.Cancellation).ConfigureAwait(false);
        if (record is null)
            return PyNone.Instance;
        var hashValue = Text(call.Arg(1)).Trim();
        if (hashValue.Length > 0 && !string.Equals(hashValue, KillmailHash(record), StringComparison.Ordinal))
            return PyNone.Instance;
        return BuildKillmailPayload(record, CurrentFiletime());
    }

    private long CurrentFiletime() => Py.FileTimeFromUnixMs(
        nowUnixMs?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static PyValue BuildKillmailPayload(JsonObject record, long nowFiletime)
    {
        var rawTime = Long(record["killTime"], 0);
        return Py.KeyVal(
            ("killID", Py.Number(PositiveInteger(record["killID"]))),
            ("killTime", Py.FileTimeLong(rawTime > 0 ? rawTime : nowFiletime)),
            ("solarSystemID", NullablePositive(record["solarSystemID"])),
            ("moonID", NullablePositive(record["moonID"])),
            ("victimCharacterID", NullablePositive(record["victimCharacterID"])),
            ("victimCorporationID", NullablePositive(record["victimCorporationID"])),
            ("victimAllianceID", NullablePositive(record["victimAllianceID"])),
            ("victimFactionID", NullablePositive(record["victimFactionID"])),
            ("victimShipTypeID", NullablePositive(record["victimShipTypeID"])),
            ("victimDamageTaken", Py.Number(Math.Max(0, Number(record["victimDamageTaken"], 0)))),
            ("finalCharacterID", NullablePositive(record["finalCharacterID"])),
            ("finalCorporationID", NullablePositive(record["finalCorporationID"])),
            ("finalAllianceID", NullablePositive(record["finalAllianceID"])),
            ("finalFactionID", NullablePositive(record["finalFactionID"])),
            ("finalShipTypeID", NullablePositive(record["finalShipTypeID"])),
            ("finalWeaponTypeID", NullablePositive(record["finalWeaponTypeID"])),
            ("finalSecurityStatus", NullableNumber(record["finalSecurityStatus"])),
            ("finalDamageDone", Py.Number(Math.Max(0, Number(record["finalDamageDone"], 0)))),
            ("warID", NullablePositive(record["warID"])),
            ("iskLost", NullableNumber(record["iskLost"])),
            ("iskDestroyed", NullableNumber(record["iskDestroyed"])),
            ("bountyClaimed", NullableNumber(record["bountyClaimed"])),
            ("loyaltyPoints", NullableNumber(record["loyaltyPoints"])),
            ("killRightSupplied", NullablePositive(record["killRightSupplied"])),
            ("killBlob", new PyStr(Text(record["killBlob"]))));
    }

    private static string KillmailHash(JsonObject record)
    {
        var input = string.Concat(
            PythonString(record["victimCharacterID"]),
            PythonString(record["finalCharacterID"]),
            PythonString(record["victimShipTypeID"]),
            PythonString(record["killTime"]));
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static string PythonString(JsonNode? value)
    {
        if (value is null)
            return "None";
        if (value is JsonValue json && json.TryGetValue<bool>(out var boolean))
            return boolean ? "True" : "False";
        return Text(value);
    }

    private static long PositiveInteger(PyValue value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        PyStr text when long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0 => number,
        _ => 0,
    };

    private static long PositiveInteger(JsonNode? value)
    {
        var number = Long(value, 0);
        return number > 0 ? number : 0;
    }

    private static PyValue NullablePositive(JsonNode? value)
    {
        var number = PositiveInteger(value);
        return number > 0 ? Py.Number(number) : PyNone.Instance;
    }

    private static PyValue NullableNumber(JsonNode? value) =>
        value is null || !double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)
            ? PyNone.Instance
            : Py.Number(number);

    private static double Number(JsonNode? value, double fallback) =>
        double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : fallback;

    private static long Long(JsonNode? value, long fallback) =>
        long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : fallback;

    private static string Text(PyValue value) => value switch
    {
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        PyInt integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        PyLong integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private static string Text(JsonNode? value) => value is JsonValue json && json.TryGetValue<string>(out var text)
        ? text ?? string.Empty
        : value?.ToString() ?? string.Empty;
}
