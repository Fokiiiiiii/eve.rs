using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed record AgentMissionAgentData(
    long AgentId,
    long AgentTypeId,
    long DivisionId,
    long Level,
    long? StationId,
    long CorporationId,
    bool IsLocatorAgent,
    bool IsInSpace,
    long? CareerId,
    bool ImportantMission,
    string MissionKind,
    string MissionTypeLabel,
    long? SpecialityId,
    long? StationTypeId,
    long? BloodlineId,
    long? SolarSystemId,
    long? DungeonId,
    long? SpawnPointId,
    long? TypeId);

public sealed record AgentMissionRpcCallbacks(
    Func<CancellationToken, ValueTask<IReadOnlyList<AgentMissionAgentData>>>? ReadAgents = null,
    Func<CancellationToken, ValueTask<IReadOnlyList<long>>>? ReadDisabledMissions = null,
    Func<long, CancellationToken, ValueTask<IReadOnlyList<long>>>? ReadPlausibleMissionIds = null,
    Func<CancellationToken, ValueTask<JsonObject?>>? ReadEpicArcMessages = null,
    Func<long, CancellationToken, ValueTask<long?>>? ReadSolarSystemOfAgent = null,
    Func<long, long, CancellationToken, ValueTask<HeldMissionView?>>? ReadHeldMission = null);

// Readers return persisted character mission state, character-owned bookmark records,
// and the character's already-resolved seven-field research rows. They never write.
public sealed class AgentMissionService(
    Func<long, CancellationToken, ValueTask<JsonObject?>> readMissionState,
    Func<long, IReadOnlyList<long>, CancellationToken, ValueTask<IReadOnlyDictionary<long, JsonObject>>> readBookmarks,
    Func<long, CancellationToken, ValueTask<JsonArray?>> readResearchRows,
    Func<long, long, ClientSession, bool>? isAtAgentLocation = null,
    AgentMissionRpcCallbacks? rpcCallbacks = null,
    AgentDialogueSources? dialogue = null) : IService, IBoundObjectReleaseHandler
{
    /// <summary>Methods a bound agent answers: the dialogue (A4). The mission journal is read through the other methods.</summary>
    private static readonly HashSet<string> BoundMethods = ["DoAction"];

    private readonly AgentDialogueMachine? _dialogue = dialogue is null ? null : new AgentDialogueMachine(dialogue);

    // The agent each bound object was made for (MachoBindObject(agentID)). The moniker is Moniker('agentMgr', agentID).
    private readonly ConcurrentDictionary<string, long> _agentByBoundObject = new(StringComparer.Ordinal);

    private static readonly string[] AgentColumns =
    [
        "agentID", "agentTypeID", "divisionID", "level", "stationID", "corporationID",
        "isLocatorAgent", "isInSpace", "careerID", "importantMission", "missionKind",
        "missionTypeLabel", "specialityID", "stationTypeID", "bloodlineID", "solarSystemID",
        "dungeonID", "spawnPointID", "typeID",
    ];

    public string Name => "agentMgr";

    public bool HasMethod(string method) => method is
        "MachoBindObject" or
        "GetMyJournalDetails" or
        "GetAgents" or
        "GetCompletedCareerAgentIDs" or
        "GetAgentLocationWrap" or
        "GetDisabledMissions" or
        "GetMessagesForEpicArcMissions" or
        "GetMyEpicArcStatus" or
        "GetPlausibleMissionIDs" or
        "GetSolarSystemOfAgent";

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _agentByBoundObject.TryRemove(oid, out _);

    /// <summary>The agent of a bound object of this session, or 0 when the call is not on a bound agent.</summary>
    public long BoundAgentId(ServiceCall call) =>
        call.BoundObjectId is { } oid && _agentByBoundObject.TryGetValue(oid, out var agentId) ? agentId : 0;

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "MachoBindObject" => BindAsync(call),
        "DoAction" => DoActionAsync(call),
        "GetMyJournalDetails" => ReadAsync(call),
        "GetAgents" => ReadAgentsAsync(call),
        "GetCompletedCareerAgentIDs" => ReadCompletedCareerAgentIdsAsync(call),
        "GetDisabledMissions" => ReadDisabledMissionsAsync(call),
        "GetMessagesForEpicArcMissions" => ReadEpicArcMessagesAsync(call),
        "GetMyEpicArcStatus" => ReadMyEpicArcStatusAsync(call),
        "GetPlausibleMissionIDs" => ReadPlausibleMissionIdsAsync(call),
        "GetSolarSystemOfAgent" => ReadSolarSystemOfAgentAsync(call),
        "GetAgentLocationWrap" => ReadAgentLocationWrapAsync(call),
        "GetMissionBriefingInfo" => ReadHeldMissionAsync(call, MissionViews.Briefing),
        "GetMissionObjectiveInfo" => ReadHeldMissionAsync(call, MissionViews.Objective),
        _ => throw MachoErrors.ThrowUserError("CustomInfo",
            ("info", new PyStr($"The agent service method {call.Method} is not available on this server yet."))),
    };

    /// <summary>
    /// <c>GetAgentLocationWrap</c> on a bound agent: where the agent is (EveJS <c>buildAgentLocationWrap</c>). A station agent is its station
    /// (type, system and station ID); an agent in space is the system with the spawn data of its agentsInSpace row; the solar system otherwise.
    /// </summary>
    private async ValueTask<PyValue> ReadAgentLocationWrapAsync(ServiceCall call)
    {
        var agent = await BoundAgentAsync(call);
        return AgentLocationWrap(agent);
    }

    /// <summary>The briefing or objective of the mission the character holds with the bound agent; None when there is none (the reference's answer).</summary>
    private async ValueTask<PyValue> ReadHeldMissionAsync(ServiceCall call, Func<HeldMissionView, PyDict> answer)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var characterId = Positive(call.Session.Get("charid"));
        var agentId = BoundAgentId(call);
        if (characterId == 0 || agentId == 0 || rpcCallbacks?.ReadHeldMission is not { } readHeld) return PyNone.Instance;
        return await readHeld(characterId, agentId, call.Cancellation) is { } view ? answer(view) : PyNone.Instance;
    }

    private async ValueTask<AgentMissionAgentData> BoundAgentAsync(ServiceCall call)
    {
        var agentId = BoundAgentId(call);
        if (agentId == 0)
        {
            throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("This call is not on a bound agent.")));
        }
        var agents = rpcCallbacks?.ReadAgents is { } readAgents
            ? await readAgents(call.Cancellation)
            : Array.Empty<AgentMissionAgentData>();
        return agents.FirstOrDefault(agent => agent.AgentId == agentId)
            ?? throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("That agent is not known.")));
    }

    /// <summary>The location wrap of an agent (the reference's shape: locationID, typeID, solarsystemID, and the locationType of an in-space agent).</summary>
    public static PyDict AgentLocationWrap(AgentMissionAgentData agent)
    {
        static KeyValuePair<PyValue, PyValue> Field(string name, PyValue value) => new(new PyStr(name), value);
        static PyValue Id(long? value) => value is > 0 ? new PyInt(value.Value) : PyNone.Instance;
        if (agent.IsInSpace && agent.SolarSystemId is { } system)
        {
            return new PyDict(new List<KeyValuePair<PyValue, PyValue>>
            {
                Field("locationID", new PyInt(system)),
                Field("typeID", new PyInt(5)),
                Field("solarsystemID", new PyInt(system)),
                Field("locationType", new PyStr("agentinspace")),
                Field("agentInSpaceTypeID", Id(agent.TypeId)),
                Field("dungeonID", Id(agent.DungeonId)),
                Field("spawnPointID", Id(agent.SpawnPointId)),
                Field("source", new PyStr("sde:agentsInSpace")),
            });
        }
        if (agent.StationId is { } station)
        {
            return new PyDict(new List<KeyValuePair<PyValue, PyValue>>
            {
                Field("typeID", new PyInt(agent.StationTypeId is > 0 ? agent.StationTypeId.Value : 1531)),
                Field("solarsystemID", new PyInt(agent.SolarSystemId ?? 0)),
                Field("locationID", new PyInt(station)),
            });
        }
        return new PyDict(new List<KeyValuePair<PyValue, PyValue>>
        {
            Field("locationID", new PyInt(agent.SolarSystemId ?? 0)),
            Field("typeID", new PyInt(5)),
            Field("solarsystemID", new PyInt(agent.SolarSystemId ?? 0)),
            Field("locationType", new PyStr("solarsystem")),
        });
    }

    /// <summary>
    /// <c>MachoBindObject(agentID, (method, args, kwargs))</c>: binds the agent, then makes the nested call on the bound object.
    /// An agent the catalog does not hold is refused; the bound object answers only the methods that exist for a bound agent.
    /// </summary>
    private async ValueTask<PyValue> BindAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var agentId = Positive(call.Args.Count > 0 ? call.Args[0] : null);
        var agents = rpcCallbacks?.ReadAgents is { } readAgents
            ? await readAgents(call.Cancellation)
            : Array.Empty<AgentMissionAgentData>();
        if (agentId == 0 || agents.All(agent => agent.AgentId != agentId))
        {
            throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("That agent is not known.")));
        }

        var nestedMethod = call.Arg(1) is PyTuple { Items.Count: > 0 } pending ? JsSemantics.StringValue(pending.Items[0]) : null;
        if (nestedMethod is not null && !BoundMethods.Contains(nestedMethod))
        {
            throw new InvalidOperationException($"agentMgr has no bound method {nestedMethod}");
        }

        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _agentByBoundObject[oid] = agentId;
        var bound = new PySubStruct(new PySubStream(Py.Tuple(
            new PyStr(oid),
            new PyLong(Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            var args = nested.Items.Count > 1
                ? nested.Items[1] switch { PyTuple tuple => tuple.Items, PyList list => list.Items, _ => [] }
                : [];
            result = await CallAsync(new ServiceCall
            {
                Method = method,
                Args = args,
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : PyNone.Instance,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                BoundObjectParameters = call.Arg(0),
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(bound, result);
    }

    /// <summary>
    /// <c>DoAction(actionID)</c> on a bound agent: the dialogue's reply, <c>((agentSays, actions), lastActionInfo)</c>. Refused when the
    /// agent is not known or the call is not on a bound agent (eve-logic agentDialogueWindow.py:427-428).
    /// </summary>
    private async ValueTask<PyValue> DoActionAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var agentId = BoundAgentId(call);
        var characterId = Positive(call.Session.Get("charid"));
        if (_dialogue is null || agentId == 0 || characterId == 0)
        {
            throw new InvalidOperationException("DoAction needs a bound agent, a character and the dialogue sources");
        }
        var agents = rpcCallbacks?.ReadAgents is { } readAgents
            ? await readAgents(call.Cancellation)
            : Array.Empty<AgentMissionAgentData>();
        var agent = agents.FirstOrDefault(candidate => candidate.AgentId == agentId)
            ?? throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("That agent is not known.")));
        var atLocation = isAtAgentLocation?.Invoke(characterId, agentId, call.Session) == true;
        var reply = await _dialogue.DoActionAsync(
            characterId, agent, call.Args.Count > 0 ? call.Args[0] : PyNone.Instance, atLocation, call.Cancellation);
        return reply.ToWire();
    }

    private async ValueTask<PyValue> ReadAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var characterId = Positive(call.Session.Get("charid"));
        if (characterId == 0) return EmptyJournal();

        var state = await readMissionState(characterId, call.Cancellation);
        call.Cancellation.ThrowIfCancellationRequested();
        var missions = state?["missionsByAgentID"] as JsonObject;
        var entries = missions is null
            ? []
            : missions
                .Select(pair => (AgentId: PositiveString(pair.Key), Record: pair.Value as JsonObject))
                .Where(entry => entry.AgentId > 0 && entry.Record is not null)
                .OrderBy(entry => entry.AgentId)
                .ToArray();

        var bookmarkIds = entries
            .SelectMany(entry => BookmarkIds(entry.Record!))
            .ToArray();
        var bookmarkRecords = bookmarkIds.Length == 0
            ? new Dictionary<long, JsonObject>()
            : await readBookmarks(characterId, bookmarkIds.Distinct().ToArray(), call.Cancellation);
        call.Cancellation.ThrowIfCancellationRequested();

        var missionRows = new List<PyValue>(entries.Length);
        foreach (var entry in entries)
        {
            if (MissionRow(entry.AgentId, entry.Record!, call.Session, characterId, bookmarkRecords) is { } row)
                missionRows.Add(row);
        }

        var research = await readResearchRows(characterId, call.Cancellation);
        call.Cancellation.ThrowIfCancellationRequested();
        var researchRows = research is null
            ? []
            : research.Select(row => (PyValue)Tuple(row as JsonArray)).ToArray();
        return new PyTuple([new PyList(missionRows), new PyList(researchRows)]);
    }

    public async ValueTask<PyValue?> ReadMissionInfoItemsAsync(
        long characterId,
        long agentId,
        ClientSession session,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0 || agentId <= 0) return null;
        var state = await readMissionState(characterId, cancellationToken);
        var record = state?["missionsByAgentID"]?[agentId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
        if (record is null || !string.Equals(Text(record["runtimeStatus"]), "accepted", StringComparison.OrdinalIgnoreCase))
            return null;

        var bookmarkIds = BookmarkIds(record).Distinct().ToArray();
        var bookmarkRecords = bookmarkIds.Length == 0
            ? new Dictionary<long, JsonObject>()
            : await readBookmarks(characterId, bookmarkIds, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return MissionRow(agentId, record, session, characterId, bookmarkRecords) is PyTuple row
            ? new PyList(row.Items)
            : null;
    }

    public async ValueTask<JsonObject?> ReadMissionRecordAsync(
        long characterId,
        long agentId,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0 || agentId <= 0) return null;
        var state = await readMissionState(characterId, cancellationToken);
        return state?["missionsByAgentID"]?[agentId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
    }
    private async ValueTask<PyValue> ReadAgentsAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var agents = rpcCallbacks?.ReadAgents is { } readAgents
            ? await readAgents(call.Cancellation)
            : Array.Empty<AgentMissionAgentData>();
        call.Cancellation.ThrowIfCancellationRequested();
        return Py.Rowset(AgentColumns, agents.OrderBy(agent => agent.AgentId).Select(AgentRow));
    }

    private async ValueTask<PyValue> ReadCompletedCareerAgentIdsAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var characterId = Positive(call.Session.Get("charid"));
        var state = characterId > 0 ? await readMissionState(characterId, call.Cancellation) : null;
        call.Cancellation.ThrowIfCancellationRequested();
        var completed = state?["completedCareerAgentIDs"] as JsonObject;
        return new PyDict(
        [
            .. PositiveIds(call.Args.Count > 0 ? call.Args[0] : null).Select(agentId =>
                new KeyValuePair<PyValue, PyValue>(
                    new PyInt(agentId),
                    IsTrue(completed?[agentId.ToString(System.Globalization.CultureInfo.InvariantCulture)])
                        ? PyBool.True
                        : PyBool.False)),
        ]);
    }

    private async ValueTask<PyValue> ReadDisabledMissionsAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        IReadOnlyList<long> missionIds = rpcCallbacks?.ReadDisabledMissions is { } readDisabled
            ? await readDisabled(call.Cancellation)
            : Array.Empty<long>();
        call.Cancellation.ThrowIfCancellationRequested();
        return new PyList([.. missionIds.Where(id => id > 0).Select(id => (PyValue)new PyInt(id))]);
    }

    private async ValueTask<PyValue> ReadEpicArcMessagesAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var messages = rpcCallbacks?.ReadEpicArcMessages is { } readMessages
            ? await readMessages(call.Cancellation)
            : null;
        call.Cancellation.ThrowIfCancellationRequested();
        return new PyDict(
        [
            .. (messages ?? new JsonObject()).Select(pair =>
                new KeyValuePair<PyValue, PyValue>(
                    new PyStr(pair.Key),
                    NumericJsonDictionary(pair.Value as JsonObject))),
        ]);
    }

    private async ValueTask<PyValue> ReadMyEpicArcStatusAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var characterId = Positive(call.Session.Get("charid"));
        var state = characterId > 0 ? await readMissionState(characterId, call.Cancellation) : null;
        call.Cancellation.ThrowIfCancellationRequested();
        return EpicArcStatusPayload(state);
    }

    private async ValueTask<PyValue> ReadPlausibleMissionIdsAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var agentId = Positive(call.Args.Count > 0 ? call.Args[0] : null);
        IReadOnlyList<long> missionIds = agentId > 0 && rpcCallbacks?.ReadPlausibleMissionIds is { } readMissions
            ? await readMissions(agentId, call.Cancellation)
            : Array.Empty<long>();
        call.Cancellation.ThrowIfCancellationRequested();
        return new PyList([.. missionIds.Where(id => id > 0).Select(id => (PyValue)new PyInt(id))]);
    }

    private async ValueTask<PyValue> ReadSolarSystemOfAgentAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var agentId = Positive(call.Args.Count > 0 ? call.Args[0] : null);
        long? solarSystemId = null;
        if (agentId > 0 && rpcCallbacks?.ReadSolarSystemOfAgent is { } readSolarSystem)
        {
            solarSystemId = await readSolarSystem(agentId, call.Cancellation);
        }
        else if (agentId > 0 && rpcCallbacks?.ReadAgents is { } readAgents)
        {
            solarSystemId = (await readAgents(call.Cancellation))
                .FirstOrDefault(agent => agent.AgentId == agentId)?.SolarSystemId;
        }
        call.Cancellation.ThrowIfCancellationRequested();
        return solarSystemId is > 0 and var id ? new PyInt(id) : PyNone.Instance;
    }

    private static PyTuple AgentRow(AgentMissionAgentData agent) => new(
    [
        new PyInt(agent.AgentId),
        new PyInt(agent.AgentTypeId),
        new PyInt(agent.DivisionId),
        new PyInt(agent.Level > 0 ? agent.Level : 1),
        OptionalPositive(agent.StationId),
        new PyInt(agent.CorporationId),
        agent.IsLocatorAgent ? PyBool.True : PyBool.False,
        agent.IsInSpace ? PyBool.True : PyBool.False,
        OptionalPositive(agent.CareerId),
        agent.ImportantMission ? PyBool.True : PyBool.False,
        new PyStr(agent.MissionKind ?? ""),
        new PyStr(agent.MissionTypeLabel ?? ""),
        OptionalPositive(agent.SpecialityId),
        OptionalPositive(agent.StationTypeId),
        OptionalPositive(agent.BloodlineId),
        OptionalPositive(agent.SolarSystemId),
        OptionalPositive(agent.DungeonId),
        OptionalPositive(agent.SpawnPointId),
        OptionalPositive(agent.TypeId),
    ]);

    private static IEnumerable<long> PositiveIds(PyValue? value)
    {
        var values = value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => [],
        };
        var seen = new HashSet<long>();
        foreach (var item in values)
        {
            var id = Positive(item);
            if (id > 0 && seen.Add(id)) yield return id;
        }
    }

    private static PyValue OptionalPositive(long? value) =>
        value is > 0 and var id ? new PyInt(id) : PyNone.Instance;

    private static PyDict NumericJsonDictionary(JsonObject? value)
    {
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        if (value is not null)
        {
            foreach (var pair in value)
            {
                var id = PositiveString(pair.Key);
                if (id > 0)
                    entries.Add(new KeyValuePair<PyValue, PyValue>(new PyInt(id), Payload(pair.Value)));
            }
        }
        return new PyDict(entries);
    }

    private static PyValue EpicArcStatusPayload(JsonObject? state)
    {
        var statusByArcId = new SortedDictionary<long, SortedDictionary<long, JsonObject>>();
        var progress = state?["epicArcProgress"] as JsonObject;
        if (progress?["missionStatusByArcID"] is JsonObject missionStatusByArcId)
        {
            foreach (var arc in missionStatusByArcId)
            {
                var arcId = PositiveString(arc.Key);
                if (arcId == 0 || arc.Value is not JsonObject missions) continue;
                foreach (var mission in missions)
                {
                    var missionId = PositiveString(mission.Key);
                    if (missionId > 0 && mission.Value is JsonObject status)
                        AddEpicArcStatus(statusByArcId, arcId, missionId, status);
                }
            }
        }

        if (progress?["completedArcsByID"] is JsonObject completedArcs)
        {
            foreach (var completion in completedArcs.Select(pair => pair.Value).OfType<JsonObject>())
            {
                var arcId = Positive(completion["epicArcID"]);
                var missionId = Positive(completion["completedMissionID"]);
                if (arcId == 0 || missionId == 0) continue;
                var status = new JsonObject
                {
                    ["completedDate"] = completion["completedAtFileTime"]?.DeepClone(),
                };
                AddEpicArcStatus(statusByArcId, arcId, missionId, status);
            }
        }

        if (state?["missionsByAgentID"] is JsonObject missionRecords)
        {
            foreach (var missionRecord in missionRecords.Select(pair => pair.Value).OfType<JsonObject>())
            {
                var clientMission = missionRecord["clientMissionRecord"] as JsonObject;
                var isEpicArc = BooleanValue(clientMission?["isEpicArc"]) == true ||
                    string.Equals(Text(clientMission?["missionFlavor"]), "epicArc", StringComparison.OrdinalIgnoreCase) ||
                    Positive(clientMission?["epicArcID"]) > 0;
                if (!isEpicArc) continue;
                var arcId = Positive(clientMission?["epicArcID"]);
                var missionId = Positive(clientMission?["missionID"]);
                if (missionId == 0) missionId = Positive(missionRecord["missionID"]);
                if (missionId == 0) missionId = Positive(missionRecord["contentID"]);
                if (arcId == 0 || missionId == 0) continue;

                var runtimeStatus = Text(missionRecord["runtimeStatus"]);
                var status = new JsonObject
                {
                    ["acceptedDate"] = string.Equals(runtimeStatus, "accepted", StringComparison.OrdinalIgnoreCase)
                        ? missionRecord["acceptedAtFileTime"]?.DeepClone()
                        : JsonValue.Create(""),
                    ["completedDate"] = string.Equals(runtimeStatus, "completed", StringComparison.OrdinalIgnoreCase)
                        ? missionRecord["completedAtFileTime"]?.DeepClone()
                        : JsonValue.Create(""),
                    ["quitDate"] = missionRecord["quitDate"]?.DeepClone() ?? JsonValue.Create(""),
                    ["nameID"] = Positive(missionRecord["missionNameID"]) is > 0 and var nameId
                        ? JsonValue.Create(nameId)
                        : clientMission?["nameID"]?.DeepClone(),
                };
                AddEpicArcStatus(statusByArcId, arcId, missionId, status);
            }
        }

        return new PyDict(
        [
            .. statusByArcId.Select(arc =>
                new KeyValuePair<PyValue, PyValue>(
                    new PyInt(arc.Key),
                    new PyDict(
                    [
                        .. arc.Value.Select(mission =>
                            new KeyValuePair<PyValue, PyValue>(
                                new PyInt(mission.Key),
                                EpicArcMissionStatus(mission.Value))),
                    ]))),
        ]);
    }

    private static void AddEpicArcStatus(
        SortedDictionary<long, SortedDictionary<long, JsonObject>> statusByArcId,
        long arcId,
        long missionId,
        JsonObject status)
    {
        if (!statusByArcId.TryGetValue(arcId, out var missions))
            statusByArcId[arcId] = missions = [];
        var merged = missions.TryGetValue(missionId, out var current)
            ? (JsonObject)current.DeepClone()
            : new JsonObject();
        foreach (var field in status)
            merged[field.Key] = field.Value?.DeepClone();
        missions[missionId] = merged;
    }

    private static PyValue EpicArcMissionStatus(JsonObject status) => Py.KeyVal(
        ("acceptedDate", FileTimeValue(status["acceptedDate"])),
        ("completedDate", FileTimeValue(status["completedDate"])),
        ("quitDate", FileTimeValue(status["quitDate"])),
        ("nameID", OptionalPositive(status["nameID"])));

    private static PyValue FileTimeValue(JsonNode? value) =>
        Integer(value) is > 0 and var fileTime ? Py.FileTimeLong(fileTime) : PyNone.Instance;

    private static PyTuple EmptyJournal() => new([new PyList([]), new PyList([])]);

    private PyValue? MissionRow(
        long agentId,
        JsonObject record,
        ClientSession session,
        long characterId,
        IReadOnlyDictionary<long, JsonObject> bookmarkRecords)
    {
        var status = Text(record["runtimeStatus"]).ToLowerInvariant() switch
        {
            "accepted" => 2,
            "completed" => 4,
            _ => 1,
        };
        var missionName = Positive(record["missionNameID"]) is > 0 and var nameId
            ? (PyValue)new PyInt(nameId)
            : Text(record["missionTitle"]) is { Length: > 0 } title
                ? new PyStr(title)
                : null;
        var missionType = Text(record["missionTypeLabel"]);
        if (missionName is null || missionType.Length == 0) return null;

        var clientMission = record["clientMissionRecord"] as JsonObject;
        var objectiveMode = Text(record["objectiveMode"]);
        var explicitOfferable = BooleanValue(clientMission?["remoteOfferable"]);
        var remoteOfferable = explicitOfferable ??
            (objectiveMode != "agent" && Positive(clientMission?["initialAgentGiftTypeID"]) == 0 && objectiveMode != "transport");
        var explicitCompletable = BooleanValue(record["remoteCompletable"]) ?? BooleanValue(clientMission?["remoteCompletable"]) ?? false;
        var remoteCompletable = explicitCompletable &&
            (isAtAgentLocation?.Invoke(characterId, agentId, session) != true);
        var bookmarks = BookmarkIds(record)
            .Where(bookmarkRecords.ContainsKey)
            .Select(id => (PyValue)Bookmark(bookmarkRecords[id]))
            .ToArray();

        return Tuple(
            new PyInt(status),
            new PyInt(BooleanValue(record["importantMission"]) == true ? 1 : 0),
            new PyStr(missionType),
            missionName,
            new PyInt(agentId),
            Py.FileTimeLong(Integer(record["expiresAtFileTime"])),
            new PyList(bookmarks),
            new PyInt(remoteOfferable ? 1 : 0),
            new PyInt(remoteCompletable ? 1 : 0),
            MissionContentId(record["contentID"]));
    }

    private static PyValue Bookmark(JsonObject record)
    {
        var metadata = record["metadata"] as JsonObject;
        var role = Text(metadata?["role"]).ToLowerInvariant();
        var x = 0d;
        var y = 0d;
        var z = 0d;
        var coordinates = TryNumber(record["x"], out x) &&
            TryNumber(record["y"], out y) &&
            TryNumber(record["z"], out z);
        if (!coordinates) (x, y, z) = (0, 0, 0);
        var fields = new List<(string, PyValue)>
        {
            ("itemID", OptionalPositive(record["itemID"])),
            ("typeID", new PyInt(Positive(record["typeID"]) is > 0 and var typeId ? typeId : 5)),
            ("agentID", OptionalPositive(metadata?["agentID"])),
            ("hint", new PyStr(Text(metadata?["hint"]))),
            ("locationType", new PyStr(Text(metadata?["locationType"]))),
            ("memo", new PyStr("")),
            ("isAgentBase", new PyInt(BooleanValue(metadata?["isAgentBase"]) == true ? 1 : 0)),
            ("locationNumber", new PyInt(role == "agenthomebase" ? -1 : 0)),
        };
        if (BooleanValue(metadata?["deadspace"]) == true) fields.Add(("deadspace", PyBool.True));
        fields.Add(("created", Py.FileTimeLong(Integer(record["created"]))));
        fields.Add(("flag", PyNone.Instance));
        fields.Add(("locationID", new PyInt(Positive(record["locationID"]))));
        fields.Add(("ownerID", OptionalPositive(record["creatorID"])));
        fields.Add(("y", coordinates ? new PyReal(y) : new PyInt(0)));
        fields.Add(("x", coordinates ? new PyReal(x) : new PyInt(0)));
        fields.Add(("solarsystemID", new PyInt(Positive(metadata?["solarsystemID"]) is > 0 and var systemId
            ? systemId : Positive(record["locationID"]))));
        fields.Add(("z", coordinates ? new PyReal(z) : new PyInt(0)));
        return Py.KeyVal([.. fields]);
    }

    private static IEnumerable<long> BookmarkIds(JsonObject record) =>
        record["bookmarkIDsByRole"] is JsonObject roles
            ? roles.Select(pair => Positive(pair.Value)).Where(id => id > 0)
            : [];

    private static PyTuple Tuple(JsonArray? row) =>
        new(row is null ? [] : [.. row.Select(Payload)]);

    private static PyTuple Tuple(params PyValue[] values) => new(values);

    private static PyValue Payload(JsonNode? value) => value switch
    {
        null => PyNone.Instance,
        JsonObject obj => new PyDict([.. obj.Select(pair => new KeyValuePair<PyValue, PyValue>(new PyStr(pair.Key), Payload(pair.Value)))]),
        JsonArray array => new PyList([.. array.Select(Payload)]),
        _ => value.GetValueKind() switch
        {
            JsonValueKind.String => new PyStr(value.GetValue<string>()),
            JsonValueKind.True => PyBool.True,
            JsonValueKind.False => PyBool.False,
            JsonValueKind.Number => TryInteger(value, out var number) ? new PyInt(number) : new PyReal(Number(value)),
            _ => PyNone.Instance,
        },
    };

    private static PyValue MissionContentId(JsonNode? value) =>
        value is JsonValue json && json.GetValueKind() == JsonValueKind.String &&
        long.TryParse(json.GetValue<string>(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? new PyInt(id)
            : Payload(value);

    private static PyValue OptionalPositive(JsonNode? value) =>
        Positive(value) is > 0 and var id ? new PyInt(id) : PyNone.Instance;

    private static long Positive(PyValue? value) =>
        value switch
        {
            PyInt integer when integer.Value > 0 => integer.Value,
            PyLong integer when integer.Value > 0 => integer.Value,
            _ => 0,
        };

    private static long Positive(JsonNode? value) =>
        Integer(value) is > 0 and var number ? number : 0;

    private static long PositiveString(string value) =>
        long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
            ? id : 0;

    private static long Integer(JsonNode? value) => TryInteger(value, out var number) ? number : 0;

    private static bool TryInteger(JsonNode? value, out long number) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number);

    private static bool TryNumber(JsonNode? value, out double number)
    {
        if (double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) && double.IsFinite(number))
            return true;
        number = 0;
        return false;
    }

    private static double Number(JsonNode? value) => TryNumber(value, out var number) ? number : 0;

    private static bool IsTrue(JsonNode? value) =>
        value is JsonValue json && json.GetValueKind() == JsonValueKind.True;

    private static bool? BooleanValue(JsonNode? value) => value?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when Integer(value) is 1 => true,
        JsonValueKind.Number when Integer(value) is 0 => false,
        _ => null,
    };

    private static string Text(JsonNode? value) =>
        value is JsonValue json && json.GetValueKind() == JsonValueKind.String ? json.GetValue<string>().Trim() : "";

}
