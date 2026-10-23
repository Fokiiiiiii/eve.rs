using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed record AchievementTrackerPersistedState(PyDict? EventCounts, PyDict? Completed);

public static class LoginInitializationGameStoreProjection
{
    public static AchievementTrackerPersistedState? ReadAchievementState(JsonObject? characterRecord)
    {
        if (characterRecord?["achievementState"] is not JsonObject state)
            return null;
        return new(
            ReadDictionary(state["eventDict"] ?? state["eventCounts"] ?? state["clientEventCounts"],
                timestamps: false, canonicalNumericKeys: true),
            ReadDictionary(state["completedDict"] ?? state["completedAchievements"],
                timestamps: true, canonicalNumericKeys: true));
    }

    public static bool? ReadHasEverWarped(JsonObject? characterRecord)
    {
        var node = (characterRecord?["achievementState"] as JsonObject)?["hasEverWarped"];
        if (node is JsonObject wrapper && wrapper["value"] is { } value)
            node = value;
        if (node is JsonValue scalar)
        {
            if (scalar.TryGetValue<bool>(out var boolean)) return boolean;
            if (scalar.TryGetValue<long>(out var integer)) return integer != 0;
            if (scalar.TryGetValue<string>(out var text))
                return text switch { "true" or "1" => true, "false" or "0" or "" => false, _ => null };
        }
        return null;
    }

    public static PyDict? ReadOperationsProgress(JsonObject? characterRecord) =>
        ReadDictionary(characterRecord?["operationProgress"] ?? characterRecord?["operationsProgress"],
            timestamps: false, canonicalNumericKeys: false);

    public static long CharacterId(ServiceCall call)
    {
        var direct = PositiveId(call.Arg(0));
        return direct > 0
            ? direct
            : call.Session.GetInt("charid") ?? call.Session.GetInt("characterID") ?? call.Session.CharacterId ?? 0;
    }

    public static long PositiveId(PyValue? value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => 0,
    };

    internal static PyValue ToPyValue(JsonNode? node, bool timestamps = false)
    {
        if (node is null) return PyNone.Instance;
        if (node is JsonObject obj)
        {
            var kind = Text(obj["type"]);
            if (obj["items"] is JsonArray items && kind == "tuple")
                return new PyTuple([.. items.Select(item => ToPyValue(item, timestamps))]);
            if (obj["items"] is JsonArray listItems && kind == "list")
                return new PyList([.. listItems.Select(item => ToPyValue(item, timestamps))]);
            if (obj["entries"] is JsonArray entries && kind == "dict")
                return ReadEntryArray(entries, timestamps, canonicalNumericKeys: true);
            return ReadDictionary(obj, timestamps, canonicalNumericKeys: true) ?? Py.Dict();
        }
        if (node is JsonArray array)
            return new PyList([.. array.Select(item => ToPyValue(item, timestamps))]);
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean)) return boolean ? PyBool.True : PyBool.False;
            if (value.TryGetValue<long>(out var integer))
                return timestamps ? new PyLong(integer) : new PyInt(integer);
            if (value.TryGetValue<double>(out var real)) return new PyReal(real);
            if (value.TryGetValue<string>(out var text))
            {
                if (timestamps && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
                    return new PyLong(integer);
                return new PyStr(text);
            }
        }
        return PyNone.Instance;
    }

    private static PyDict? ReadDictionary(JsonNode? node, bool timestamps, bool canonicalNumericKeys)
    {
        if (node is JsonObject obj)
        {
            if (Text(obj["type"]) == "dict" && obj["entries"] is JsonArray entries)
                return ReadEntryArray(entries, timestamps, canonicalNumericKeys);
            return new PyDict([.. obj.Select(pair => new KeyValuePair<PyValue, PyValue>(
                DictionaryKey(pair.Key, canonicalNumericKeys), ToPyValue(pair.Value, timestamps)))]);
        }
        if (node is JsonArray rows)
            return ReadEntryArray(rows, timestamps, canonicalNumericKeys);
        return null;
    }

    private static PyDict ReadEntryArray(JsonArray rows, bool timestamps, bool canonicalNumericKeys)
    {
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var row in rows.OfType<JsonArray>())
        {
            if (row.Count < 2) continue;
            var key = row[0] is JsonValue keyValue && keyValue.TryGetValue<string>(out var text)
                ? DictionaryKey(text, canonicalNumericKeys)
                : ToPyValue(row[0]);
            entries.Add(new(key, ToPyValue(row[1], timestamps)));
        }
        return new PyDict(entries);
    }

    private static PyValue DictionaryKey(string key, bool canonicalNumericKeys) =>
        long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) &&
        (!canonicalNumericKeys || string.Equals(key, integer.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            ? new PyInt(integer)
            : new PyStr(key);

    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}

public sealed class AchievementTrackerMgrService(
    Func<long, CancellationToken, ValueTask<AchievementTrackerPersistedState?>>? readState = null,
    Func<long, CancellationToken, ValueTask<bool>>? hasEverWarped = null,
    Func<ServiceCall, CancellationToken, ValueTask<PyValue?>>? rpcHandler = null,
    Func<long, PyDict, PyDict, CancellationToken, ValueTask>? writeState = null) : IService
{
    public AchievementTrackerMgrService(Func<ServiceCall, ValueTask<PyValue?>> stateHandler)
        : this(rpcHandler: (call, _) => stateHandler(call))
    {
    }

    public string Name => "achievementTrackerMgr";

    public bool HasMethod(string method) => method is
        "HasEverWarped" or
        "GetCompletedAchievementsAndClientEventCount" or
        "UpdateClientAchievmentsAndCounters";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        if (rpcHandler is not null && await rpcHandler(call, call.Cancellation) is { } handled)
            return handled;

        if (call.Method == "HasEverWarped")
        {
            var characterId = call.Session.GetInt("charid") ?? 0;
            if (call.Session.GetInt("hasEverWarped") == 1)
                return PyBool.True;
            var hasEverWarpedForCharacter = characterId > 0 && hasEverWarped is not null
                && await hasEverWarped(characterId, call.Cancellation);
            if (hasEverWarpedForCharacter)
                call.Session.Set("hasEverWarped", new PyInt(1));
            return hasEverWarpedForCharacter ? PyBool.True : PyBool.False;
        }

        // the client sends both maps whole when it stores them (its achievements and counters at logoff, and as they change)
        if (call.Method == "UpdateClientAchievmentsAndCounters")
        {
            if (writeState is not null && call.Session.GetInt("charid") is { } writer && writer > 0
                && call.Arg(0) is PyDict completed && call.Arg(1) is PyDict eventCounts)
                await writeState(writer, completed, eventCounts, call.Cancellation);
            return PyNone.Instance;
        }

        // a character with no record has empty maps (the client's own default), not a fixture
        var state = readState is not null && call.Session.GetInt("charid") is { } id && id > 0
            ? await readState(id, call.Cancellation)
            : null;
        return new PyDict([
            Entry("eventDict", state?.EventCounts ?? new PyDict([])),
            Entry("completedDict", state?.Completed ?? new PyDict([])),
        ]);
    }

    private static KeyValuePair<PyValue, PyValue> Entry(string key, PyValue value) =>
        new(new PyStr(key), value);

    private static KeyValuePair<PyValue, PyValue> Entry(PyValue key, PyValue value) => new(key, value);
}

public sealed class OperationsManagerService(
    Func<ServiceCall, CancellationToken, ValueTask<PyDict?>>? readCharacterProgress = null,
    Func<ServiceCall, CancellationToken, ValueTask<PyValue>>? readActiveCategoryId = null,
    Func<ServiceCall, CancellationToken, ValueTask<PyList>>? readCompletedOperations = null,
    Func<ServiceCall, ValueTask>? startSite = null,
    Func<ServiceCall, ValueTask>? unblockTask = null,
    Func<ServiceCall, CancellationToken, ValueTask<PyValue?>>? rpcHandler = null,
    Func<string>? newCharacterExperienceMode = null,
    Func<long, CancellationToken, Task<AirNpeCharacterState?>>? readTutorialState = null) : IService
{
    private static readonly PyDict GoldenCharacterProgress = new([
        Category(32, Operation(158, 347, 348)),
        Category(2113,
            Operation(2401, 3792, 3794, 3795, 3796),
            Operation(2397, 3812, 3798, 3799, 3711, 3712, 3713, 3814, 3716, 3815, 3717, 3718),
            Operation(2395, 3705, 3706),
            Operation(2396, 3816, 3707, 3708)),
        Category(2115,
            Operation(2402, 3817, 3733, 3734, 3735, 3736, 3737, 3738, 3739, 3807, 3740, 3741, 3742, 3818, 3743),
            Operation(2403, 3744, 3745, 3746, 3747, 3748, 3749, 3750, 3751, 3752, 3753, 3754),
            Operation(2404, 3801, 3755, 3756, 3757, 3758, 3800, 3759),
            Operation(2405, 3802, 3760, 3761, 3762, 3763, 3764, 3765, 3766, 3767),
            Operation(2406, 3768, 3769, 3770, 3771, 3772, 3773, 3774, 3775, 3776),
            Operation(2407, 3804, 3822, 3823, 3824, 3825, 3777, 3778),
            Operation(2408, 3780, 3781, 3782, 3783, 3784, 3785, 3786, 3787, 3788, 3779, 3820, 3821, 3791)),
        Category(2122,
            Operation(2423, 3912, 4014, 4015, 4031),
            Operation(2433, 3908, 4179, 3950, 3943, 3946, 3947, 3948, 4178),
            Operation(2594, 4140, 4086, 4047, 4139),
            Operation(2674, 4176, 4177)),
        Category(2126,
            Operation(2663), Operation(2664), Operation(2666), Operation(2667), Operation(2668),
            Operation(2669), Operation(2670), Operation(2671), Operation(2672), Operation(2673),
            Operation(2460, 3973), Operation(2462, 3974), Operation(2463, 3975), Operation(2464, 3976),
            Operation(2465, 3977), Operation(2466, 3978), Operation(2467, 3979), Operation(2468, 3980),
            Operation(2469, 3981), Operation(2470, 3982), Operation(2471, 3983), Operation(2472, 3984),
            Operation(2473, 3985), Operation(2474, 3986), Operation(2475, 3987), Operation(2476, 3988),
            Operation(2477, 3989), Operation(2478, 3990), Operation(2479, 3991)),
    ]);

    public string Name => "operationsManager";

    public bool HasMethod(string method) => method is
        "can_character_play_the_tutorial" or
        "get_tutorial_state" or
        "is_main_tutorial_finished" or
        "has_skipped_tutorial" or
        "get_character_progress" or
        "get_active_category_id" or
        "start_site" or
        "process_client_event" or
        "activate_operation_in_solar_system" or
        "block_task" or
        "complete_category_for_character" or
        "get_category_state" or
        "get_mission_avoidance_systems" or
        "get_operations_completed_at_least_once" or
        "is_category_complete" or
        "skip_task" or
        "unblock_task" or
        "cancel_current_treatment_operation";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        if (rpcHandler is not null && await rpcHandler(call, call.Cancellation) is { } handled)
            return handled;

        switch (call.Method)
        {
            case "get_character_progress":
                return readCharacterProgress is null
                    ? GoldenCharacterProgress
                    : await readCharacterProgress(call, call.Cancellation) ?? GoldenCharacterProgress;
            case "get_active_category_id":
                return readActiveCategoryId is null
                    ? PyNone.Instance
                    : await readActiveCategoryId(call, call.Cancellation);
            case "get_operations_completed_at_least_once":
                return readCompletedOperations is null
                    ? new PyList([])
                    : await readCompletedOperations(call, call.Cancellation);
            case "can_character_play_the_tutorial":
            case "get_tutorial_state":
            case "is_main_tutorial_finished":
            case "has_skipped_tutorial":
                return await TutorialStateAsync(call);
            case "get_mission_avoidance_systems":
                return new PyList([]);
            case "get_category_state":
                return new PyInt(20);
            case "activate_operation_in_solar_system":
            case "complete_category_for_character":
            case "is_category_complete":
            case "skip_task":
                return PyBool.False;
            case "start_site" when startSite is not null:
                await startSite(call);
                return PyNone.Instance;
            case "unblock_task" when unblockTask is not null:
                await unblockTask(call);
                return PyNone.Instance;
            default:
                return PyNone.Instance;
        }
    }

    private static KeyValuePair<PyValue, PyValue> Category(long id, params PyValue[] operations) =>
        new(new PyInt(id), new PyList(operations));

    private static PyValue Operation(long id, params long[] taskIds) =>
        new PyList([
            new PyInt(id),
            new PyInt(0),
            new PyList(taskIds.Select(Task).ToArray()),
        ]);

    private static PyValue Task(long id) =>
        new PyList([new PyInt(id), new PyInt(10), new PyInt(0)]);

    private async ValueTask<PyValue> TutorialStateAsync(ServiceCall call)
    {
        var characterId = Positive(call.Arg(0));
        if (characterId == 0) characterId = call.Session.GetInt("charid") ?? 0;
        var state = characterId > 0 && readTutorialState is not null
            ? await readTutorialState(characterId, call.Cancellation)
            : null;
        var airNpeState = state?.EffectiveState(newCharacterExperienceMode?.Invoke() ?? "disabled") ?? 2;
        return call.Method switch
        {
            "can_character_play_the_tutorial" => airNpeState == 1 ? PyBool.True : PyBool.False,
            "get_tutorial_state" => new PyInt(airNpeState == 1 ? 21 : airNpeState == 3 ? 23 : 22),
            "is_main_tutorial_finished" => airNpeState == 1 ? PyBool.False : PyBool.True,
            "has_skipped_tutorial" => airNpeState == 3 ? PyBool.True : PyBool.False,
            _ => PyNone.Instance,
        };
    }

    private static long Positive(PyValue? value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => 0,
    };
}

public sealed class MissionTrackerMgrService(
    Func<long, long, ClientSession, CancellationToken, ValueTask<PyValue?>>? readMissionInfoItems = null,
    Func<ServiceCall, CancellationToken, ValueTask<PyValue?>>? rpcHandler = null,
    Func<long, long, ClientSession, CancellationToken, ValueTask<PyValue?>>? readAllMissionObjectives = null,
    Func<ServiceCall, CancellationToken, ValueTask<bool?>>? isInActiveDungeon = null) : IService
{
    public MissionTrackerMgrService(Func<long, long, ValueTask<PyValue?>> readMissionInfoItems)
        : this(readMissionInfoItems: (long characterId, long agentId, ClientSession session, CancellationToken cancellation) => readMissionInfoItems(characterId, agentId))
    {
    }

    public string Name => "missionTrackerMgr";

    public bool HasMethod(string method) => method is
        "UpdateAllMissions" or "GetMissionInfoItems" or "GetAllMissionObjectives" or "IsInActiveDungeonID";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        if (rpcHandler is not null && await rpcHandler(call, call.Cancellation) is { } handled)
            return handled;
        return call.Method switch
        {
            "UpdateAllMissions" => await UpdateAllMissionsAsync(call),
            "GetMissionInfoItems" => await GetMissionInfoItemsAsync(call),
            "GetAllMissionObjectives" => await GetAllMissionObjectivesAsync(call),
            "IsInActiveDungeonID" => await IsInActiveDungeonAsync(call),
            _ => PyNone.Instance,
        };
    }

    private async ValueTask<PyValue> GetAllMissionObjectivesAsync(ServiceCall call) =>
        readAllMissionObjectives is null
            ? new PyList([])
            : AsList(await readAllMissionObjectives(
                Positive(call.Session.Get("charid")), Positive(call.Arg(0)), call.Session, call.Cancellation));

    private async ValueTask<PyValue> IsInActiveDungeonAsync(ServiceCall call)
    {
        var active = isInActiveDungeon is not null
            ? await isInActiveDungeon(call, call.Cancellation)
            : false;
        return active switch
        {
            true => PyBool.True,
            false => PyBool.False,
            null => PyNone.Instance,
        };
    }

    private async ValueTask<PyValue> UpdateAllMissionsAsync(ServiceCall call)
    {
        var characterId = Positive(call.Session.Get("charid"));
        var updates = new List<PyValue>();
        foreach (var agentId in AgentIds(call.Arg(0)))
        {
            var info = await ReadMissionInfoItemsAsync(characterId, agentId, call.Session, call.Cancellation);
            updates.Add(new PyDict([
                Entry("info", AsList(info)),
                Entry("agentID", new PyInt(agentId)),
            ]));
        }

        call.Session.SendNotification("OnMissionsUpdated", "charid", new PyList(updates));
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> GetMissionInfoItemsAsync(ServiceCall call)
    {
        var info = await ReadMissionInfoItemsAsync(
            Positive(call.Session.Get("charid")), Positive(call.Arg(0)), call.Session, call.Cancellation);
        return AsList(info);
    }

    private ValueTask<PyValue?> ReadMissionInfoItemsAsync(long characterId, long agentId, ClientSession session, CancellationToken cancellation) =>
        readMissionInfoItems is null
            ? ValueTask.FromResult<PyValue?>(null)
            : readMissionInfoItems(characterId, agentId, session, cancellation);

    private static PyValue AsList(PyValue? value) => value switch
    {
        null => PyNone.Instance,
        PyTuple tuple => new PyList(tuple.Items),
        _ => value,
    };

    private static IEnumerable<long> AgentIds(PyValue value)
    {
        var seen = new HashSet<long>();
        var source = value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => [],
        };
        foreach (var entry in source)
        {
            var id = Positive(entry);
            if (id > 0 && seen.Add(id)) yield return id;
        }
    }

    private static long Positive(PyValue? value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => 0,
    };

    private static KeyValuePair<PyValue, PyValue> Entry(string key, PyValue value) =>
        new(new PyStr(key), value);
}

public static class MissionTrackerGameStoreProjection
{
    private const long PlaceholderCargoTypeId = 16135;
    private const long MissionDungeonDistanceMeters = 500_000;

    public static PyList ReadAllObjectives(
        JsonObject? characterMissionState,
        long agentId,
        bool inActiveDungeon,
        JsonObject? dungeonRuntimeState = null)
    {
        var record = MissionRecord(characterMissionState, agentId);
        if (record is null || !string.Equals(Text(record["runtimeStatus"]), "accepted", StringComparison.OrdinalIgnoreCase))
            return new PyList([]);

        var dungeonInstanceId = Positive(record["dungeonInstanceID"]);
        var dungeonInstance = dungeonInstanceId > 0
            ? (dungeonRuntimeState?["instancesByID"] as JsonObject)?[dungeonInstanceId.ToString(CultureInfo.InvariantCulture)] as JsonObject
            : null;

        var storedRows = record["objectiveRows"] ?? record["objectives"] ??
            (record["objectiveState"] as JsonObject)?["objectiveRows"] ??
            (record["clientMissionRecord"] as JsonObject)?["objectives"] ??
            dungeonInstance?["objectiveRows"] ?? dungeonInstance?["objectives"];
        if (storedRows is JsonObject wrapper && wrapper["items"] is JsonArray wrappedItems)
            storedRows = wrappedItems;
        if (storedRows is JsonArray rows)
            return new PyList([.. rows.Select<JsonNode?, PyValue>(row => LoginInitializationGameStoreProjection.ToPyValue(row))]);

        var missionId = Positive(record["agentID"]) is > 0 and var savedAgentId ? savedAgentId : agentId;
        var complete = List("AllObjectivesComplete", new PyInt(missionId));
        var mode = Text(record["objectiveMode"]).Trim().ToLowerInvariant();
        var cargo = record["cargo"] as JsonObject;
        var typeId = Positive(cargo?["typeID"]) is > 0 and var savedTypeId ? savedTypeId : PlaceholderCargoTypeId;
        var quantity = Positive(cargo?["quantity"]) is > 0 and var savedQuantity ? savedQuantity : 1;
        var destinationId = Positive((record["dropoffLocation"] as JsonObject)?["locationID"]);
        var systemId = Positive(record["missionSystemID"]);
        var completed = Boolean(record["objectiveCompleted"]);

        if (mode is "agent" or "conversation")
        {
            var targetAgentId = Positive(record["conversationTargetAgentID"] ?? record["targetAgentID"]) is > 0 and var target
                ? target
                : missionId;
            return new PyList([Marshal("TalkToAgent", new PyInt(targetAgentId)), complete]);
        }

        if (mode is "transport" or "courier")
        {
            return new PyList([
                List("TransportItemsMissing", new PyInt(typeId), new PyInt(quantity)),
                List("TransportItemsPresent", new PyInt(typeId), destinationId > 0 ? new PyInt(destinationId) : PyNone.Instance, new PyInt(quantity)),
                complete,
            ]);
        }

        if (mode is "mining" or "mine")
        {
            PyValue current = completed
                ? List("FetchObjectAcquiredDungeonDone", new PyInt(typeId), new PyInt(missionId),
                    destinationId > 0 ? new PyInt(destinationId) : new PyInt(0), new PyInt(quantity))
                : inActiveDungeon
                    ? Marshal("MissionFetchMineTrigger", new PyInt(typeId))
                    : Marshal("TravelTo", new PyInt(systemId));
            return new PyList([current, complete]);
        }

        if (completed)
            return new PyList([complete]);

        var nextGateId = Positive(record["nextGateEntityID"] ?? dungeonInstance?["nextGateEntityID"]);
        var activeObjective = nextGateId > 0
            ? Marshal("GoToGate", new PyInt(nextGateId))
            : inActiveDungeon
                ? Marshal("DestroyAll", new PyInt(0))
                : Marshal("TravelTo", new PyInt(systemId));
        return new PyList([activeObjective, complete]);
    }

    public static bool? IsInActiveMissionDungeon(
        JsonObject? missionRecord,
        JsonObject? dungeonRuntimeState,
        long characterId,
        long currentSystemId,
        long shipId,
        double? shipX,
        double? shipY,
        double? shipZ,
        bool isWarping)
    {
        if (missionRecord is null) return false;
        var missionSystemId = Positive(missionRecord["missionSystemID"]);
        var missionPosition = missionRecord["missionPosition"] as JsonObject;
        if (missionSystemId <= 0 || missionPosition is null) return false;
        if (currentSystemId <= 0 || currentSystemId != missionSystemId) return false;
        if (isWarping || shipId <= 0 || shipX is null || shipY is null || shipZ is null) return null;

        var instanceId = Positive(missionRecord["dungeonInstanceID"]);
        var instances = dungeonRuntimeState?["instancesByID"] as JsonObject;
        var instance = instanceId > 0
            ? instances?[instanceId.ToString(CultureInfo.InvariantCulture)] as JsonObject
            : null;
        if (instance is not null)
        {
            var lifecycle = Text(instance["lifecycleState"] ?? instance["state"]).ToLowerInvariant();
            if (lifecycle.Length > 0 && lifecycle is not ("active" or "seeded" or "paused")) return false;
            var instanceSystemId = Positive(instance["solarSystemID"] ?? instance["systemID"]);
            if (instanceSystemId > 0 && instanceSystemId != currentSystemId) return false;
            var ownerCharacterId = Positive(instance["characterID"] ?? instance["ownerCharacterID"]);
            if (ownerCharacterId > 0 && characterId > 0 && ownerCharacterId != characterId) return false;
            var ownerShipId = Positive(instance["shipID"] ?? instance["shipId"]);
            if (ownerShipId > 0 && ownerShipId != shipId) return false;
            if (!ContainsParticipant(instance["participantCharacterIDs"] ?? instance["participantsByCharacterID"], characterId))
                return false;
        }

        var x = Number(missionPosition["x"]);
        var y = Number(missionPosition["y"]);
        var z = Number(missionPosition["z"]);
        if (x is null || y is null || z is null) return false;
        var dx = shipX.Value - x.Value;
        var dy = shipY.Value - y.Value;
        var dz = shipZ.Value - z.Value;
        return dx * dx + dy * dy + dz * dz <= MissionDungeonDistanceMeters * MissionDungeonDistanceMeters;
    }

    private static JsonObject? MissionRecord(JsonObject? state, long agentId) =>
        state?["missionsByAgentID"]?[agentId.ToString(CultureInfo.InvariantCulture)] as JsonObject ??
        (Positive(state?["agentID"]) == agentId ? state : null);

    private static bool ContainsParticipant(JsonNode? participants, long characterId)
    {
        if (characterId <= 0 || participants is null) return true;
        var key = characterId.ToString(CultureInfo.InvariantCulture);
        if (participants is JsonObject byId)
            return byId.ContainsKey(key);
        if (participants is JsonArray entries)
            return entries.Any(entry => Positive(entry) == characterId ||
                Positive((entry as JsonObject)?["characterID"]) == characterId);
        return true;
    }

    private static PyList List(string name, params PyValue[] values) =>
        new([new PyStr(name), .. values]);

    private static PyTuple Marshal(string name, params PyValue[] values) =>
        new([new PyStr(name), .. values]);

    private static long Positive(JsonNode? node)
    {
        if (node is JsonObject wrapper && wrapper["value"] is { } value) node = value;
        if (node is JsonValue scalar)
        {
            if (scalar.TryGetValue<long>(out var integer)) return Math.Max(0, integer);
            if (scalar.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
                return Math.Max(0, integer);
        }
        return 0;
    }

    private static double? Number(JsonNode? node)
    {
        if (node is JsonObject wrapper && wrapper["value"] is { } value) node = value;
        if (node is not JsonValue scalar) return null;
        if (scalar.TryGetValue<double>(out var number) && double.IsFinite(number)) return number;
        if (scalar.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number))
            return number;
        return null;
    }

    private static bool Boolean(JsonNode? node) =>
        node is JsonValue scalar && scalar.TryGetValue<bool>(out var value) && value;

    private static string Text(JsonNode? node) =>
        node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
}

public sealed class AbyssalMgrService : IService
{
    private readonly Func<ServiceCall, CancellationToken, ValueTask<PyValue>> _rpcHandler;

    public AbyssalMgrService(Func<ServiceCall, CancellationToken, ValueTask<PyValue>> rpcHandler)
    {
        _rpcHandler = rpcHandler ?? throw new ArgumentNullException(nameof(rpcHandler));
    }

    public string Name => "abyssalMgr";

    public bool HasMethod(string method) => method is
        "ClientIsReady" or
        "AbyssalEntranceDeployment" or
        "AbyssalEntranceGateActivation" or
        "AbyssalGateActivation" or
        "AbyssalEndGateActivation";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        return await _rpcHandler(call, call.Cancellation).ConfigureAwait(false);
    }
}

public sealed class VoidSpaceMgrService(
    Func<ServiceCall, ValueTask>? clientIsReady = null,
    Func<ServiceCall, CancellationToken, ValueTask<PyValue?>>? rpcHandler = null) : IService
{
    public string Name => "voidSpaceMgr";

    public bool HasMethod(string method) => method is
        "ClientIsReady" or "VoidSpaceEndGateActivation" or "VoidSpacePlayerJump";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        if (rpcHandler is not null && await rpcHandler(call, call.Cancellation) is { } handled)
            return handled;
        if (call.Method == "ClientIsReady" && clientIsReady is not null)
            await clientIsReady(call);

        return call.Method switch
        {
            "VoidSpaceEndGateActivation" => throw MachoErrors.ThrowUserError("DeniedTargetAttemptFailed"),
            "VoidSpacePlayerJump" => throw MachoErrors.ThrowUserError(
                "VoidSpaceJumpError",
                ("errors", new PyList([new PyList([new PyInt(22), new PyList([])])]))),
            _ => PyNone.Instance
        };
    }
}
