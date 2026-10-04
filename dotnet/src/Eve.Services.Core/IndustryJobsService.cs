using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>
/// Read-only industry job RPCs backed only by the persisted <c>industryJobs.jobs</c> table.
/// Completed history is exposed from stored rows only when requested; no jobs or history are synthesized.
/// Starbase visibility/lifecycle and legacy inventory-flag resolution are supplied by Host callbacks.
/// Mirrors EVE.js <c>industryManagerService</c>/<c>industryRuntimeState</c>; the client Job Browser
/// requests owner-scoped active and completed job lists.
/// </summary>

public sealed class IndustryJobsService : IService
{
    private const long FactoryManagerRole = 1024;
    private const long Installed = 1;
    private const long Paused = 2;
    private const long Ready = 3;
    private const long Completed = 100;
    private const long FileTimeEpochOffset = 116444736000000000;
    private const long FileTimeTicksPerMillisecond = 10000;
    private const long LegacyUnixMillisecondsMax = 9999999999999;
    private static readonly int[] Activities = [1, 3, 4, 5, 8, 9];
    private static readonly int[] ScienceActivities = [3, 4, 5, 8];

    private readonly Func<CancellationToken, ValueTask<JsonObject?>> readJobs;
    private readonly Func<long> currentFileTime;
    private readonly Func<ServiceCall, JsonObject, bool>? canViewStarbaseJob;
    private readonly Func<long, bool>? isStarbaseFacilityOnline;
    private readonly Func<long, long, long, long>? resolveBlueprintLocationFlagID;

    public IndustryJobsService(
        Func<CancellationToken, ValueTask<JsonObject?>> readJobs,
        Func<long>? currentFileTime = null,
        Func<ServiceCall, JsonObject, bool>? canViewStarbaseJob = null,
        Func<long, bool>? isStarbaseFacilityOnline = null,
        Func<long, long, long, long>? resolveBlueprintLocationFlagID = null)
    {
        ArgumentNullException.ThrowIfNull(readJobs);
        this.readJobs = readJobs;
        this.currentFileTime = currentFileTime ?? (() => DateTime.UtcNow.ToFileTimeUtc());
        this.canViewStarbaseJob = canViewStarbaseJob;
        this.isStarbaseFacilityOnline = isStarbaseFacilityOnline;
        this.resolveBlueprintLocationFlagID = resolveBlueprintLocationFlagID;
    }

    public string Name => "industryManager";

    public bool HasMethod(string method) => method is "GetJob" or "GetJobsByOwner" or "GetJobCounts";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();

        if (call.Method == "GetJobCounts")
        {
            var characterId = SessionId(call, "charid");
            var installerId = call.Args.Count == 0 ? characterId : Positive(call.Arg(0));
            if (characterId == 0 || installerId != characterId) return new PyDict([]);
            return Counts(await readJobs(call.Cancellation), characterId);
        }

        var jobId = Positive(call.Arg(0));
        if (call.Method == "GetJob")
        {
            var table = await readJobs(call.Cancellation);
            var job = Jobs(table)?[Key(jobId)] as JsonObject;
            var visible = job is not null && !Pending(job) && CanView(call, job) &&
                CanAccessOwner(call, Integer(job["ownerID"]));
            return visible ? Job(job, Status(job!)) : Job(null);
        }

        var ownerId = jobId;
        if (ownerId == 0 || !CanAccessOwner(call, ownerId)) return new PyList([]);
        var jobs = Jobs(await readJobs(call.Cancellation));
        var includeCompleted = JsSemantics.Truthy(call.Arg(1));
        var rows = new List<(long Id, PyValue Value)>();
        if (jobs is not null)
            foreach (var (_, node) in jobs)
            {
                if (node is not JsonObject job || Pending(job) || Integer(job["ownerID"]) != ownerId || !CanView(call, job))
                    continue;
                var status = Status(job);
                if (!includeCompleted && status >= Completed) continue;
                rows.Add((Integer(job["jobID"]), Job(job, status)));
            }
        return new PyList([.. rows.OrderBy(row => row.Id).Select(row => row.Value)]);
    }

    private PyValue Counts(JsonObject? table, long installerId)
    {
        var counts = new Dictionary<int, int>();
        var jobs = Jobs(table);
        if (jobs is not null)
            foreach (var (_, node) in jobs)
            {
                if (node is not JsonObject job || Pending(job) ||
                    Integer(job["installerID"]) != installerId || Status(job) >= Completed)
                    continue;
                var activity = (int)Integer(job["activityID"]);
                counts[activity] = counts.GetValueOrDefault(activity) + 1;
            }

        return new PyDict([.. Activities.Select(activity =>
            new KeyValuePair<PyValue, PyValue>(new PyInt(activity), new PyInt(SlotUsage(activity, counts))))]);
    }

    private static int SlotUsage(int activity, IReadOnlyDictionary<int, int> counts) =>
        Array.IndexOf(ScienceActivities, activity) >= 0
            ? ScienceActivities.Sum(id => counts.GetValueOrDefault(id))
            : counts.GetValueOrDefault(activity);

    private bool CanAccessOwner(ServiceCall call, long ownerId)
    {
        if (ownerId <= 0) return false;
        if (SessionId(call, "charid") == ownerId) return true;
        if (SessionId(call, "corpid") != ownerId) return false;
        return (RoleValue(call.Session.Get("corprole")) & FactoryManagerRole) == FactoryManagerRole;
    }

    private static long RoleValue(PyValue? value) => value switch
    {
        PyInt role => role.Value,
        PyLong role => role.Value,
        PyStr text when long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var role) => role,
        _ => Integer(JsSemantics.ToNumber(value)),
    };

    private bool CanView(ServiceCall call, JsonObject job)
    {
        var towerId = Positive(job["starbaseTowerID"]);
        return towerId == 0 ||
            (canViewStarbaseJob?.Invoke(call, job) == true && isStarbaseFacilityOnline is not null);
    }

    private long BlueprintLocationFlagID(JsonObject? job)
    {
        if (job is null) return 0;
        var flag = Integer(job["blueprintLocationFlagID"]);
        if (job.ContainsKey("blueprintInventoryFlagID") || flag < 0) return flag;
        var inventoryLocationId = Positive(job["blueprintInventoryLocationID"] ?? job["blueprintLocationID"]);
        return resolveBlueprintLocationFlagID?.Invoke(inventoryLocationId, Integer(job["ownerID"]), flag) ?? flag;
    }

    private long Status(JsonObject job)
    {
        var status = Integer(job["status"]);
        if (status != Installed) return status;
        var towerId = Positive(job["starbaseTowerID"]);
        var online = towerId == 0 ? true : isStarbaseFacilityOnline?.Invoke(towerId);
        if (online == false) return Paused;
        var end = FileTime(job["endDate"]);
        return (towerId == 0 || online == true) && end > 0 && end <= currentFileTime()
            ? Ready
            : status;
    }

    private static bool Pending(JsonObject job) =>
        job["installationPending"] is JsonValue value && value.TryGetValue<bool>(out var pending) && pending;

    private static JsonObject? Jobs(JsonObject? table) => table?["jobs"] as JsonObject;
    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static long SessionId(ServiceCall call, string name) => Positive(call.Session.Get(name));
    private static long Positive(PyValue? value) => Math.Max(0, Integer(JsSemantics.ToNumber(value)));
    private static long Positive(JsonNode? value) => Math.Max(0, Integer(value));

    private static long Integer(PyValue? value) => Integer(JsSemantics.ToNumber(value));

    private static long Integer(double value) =>
        !double.IsFinite(value) || value < long.MinValue || value >= 9223372036854775808d
            ? 0
            : (long)Math.Truncate(value);

    private static long Integer(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<long>(out var integer)) return integer;
        if (value.TryGetValue<int>(out var smallInteger)) return smallInteger;
        if (value.TryGetValue<string>(out var text) &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)) return integer;
        if (value.TryGetValue<double>(out var number)) return Integer(number);
        if (value.TryGetValue<bool>(out var boolean)) return boolean ? 1 : 0;
        return 0;
    }

    private static long FileTime(JsonNode? value)
    {
        var time = Integer(value);
        if (time <= 0) return 0;
        return time <= LegacyUnixMillisecondsMax
            ? time * FileTimeTicksPerMillisecond + FileTimeEpochOffset
            : time;
    }

    private static bool Truthy(JsonNode? node)
    {
        if (node is null) return false;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag)) return flag;
            if (value.TryGetValue<long>(out var integer)) return integer != 0;
            if (value.TryGetValue<int>(out var smallInteger)) return smallInteger != 0;
            if (value.TryGetValue<double>(out var number)) return number != 0 && !double.IsNaN(number);
            if (value.TryGetValue<string>(out var text)) return text.Length > 0;
        }
        return true;
    }

    private static PyValue OptionalInteger(JsonObject? record, string name) =>
        record is not null && Truthy(record[name]) ? new PyInt(Integer(record[name])) : PyNone.Instance;

    private PyValue Job(JsonObject? record, long? normalizedStatus = null)
    {
        var data = record ?? new JsonObject();
        var probability = data.ContainsKey("probability") ? Number(data["probability"], 0) : 1;
        return Py.KeyVal(
            ("activityID", new PyInt(Integer(data["activityID"]))),
            ("jobID", new PyInt(Integer(data["jobID"]))),
            ("blueprintID", new PyInt(Integer(data["blueprintID"]))),
            ("blueprintTypeID", new PyInt(Integer(data["blueprintTypeID"]))),
            ("blueprintCopy", PyValue.Of(Truthy(data["blueprintCopy"]))),
            ("blueprintLocationID", new PyInt(Integer(data["blueprintLocationID"]))),
            ("blueprintLocationFlagID", new PyInt(BlueprintLocationFlagID(record))),
            ("facilityID", new PyInt(Integer(data["facilityID"]))),
            ("ownerID", new PyInt(Integer(data["ownerID"]))),
            ("status", new PyInt(normalizedStatus ?? Integer(data["status"]))),
            ("installerID", new PyInt(Integer(data["installerID"]))),
            ("completedCharacterID", new PyInt(Integer(data["completedCharacterID"]))),
            ("solarSystemID", new PyInt(Integer(data["solarSystemID"]))),
            ("stationID", new PyInt(Integer(data["stationID"]))),
            ("startDate", Py.FileTimeLong(FileTime(data["startDate"]))),
            ("endDate", Py.FileTimeLong(FileTime(data["endDate"]))),
            ("pauseDate", Truthy(data["pauseDate"]) ? Py.FileTimeLong(FileTime(data["pauseDate"])) : PyNone.Instance),
            ("runs", new PyInt(Integer(data["runs"]))),
            ("licensedRuns", new PyInt(Integer(data["licensedRuns"]))),
            ("successfulRuns", new PyInt(Integer(data["successfulRuns"]))),
            ("cost", new PyInt(Integer(data["totalCost"] ?? data["cost"]))),
            ("timeInSeconds", new PyInt(Integer(data["timeInSeconds"]))),
            ("probability", Py.Number(probability)),
            ("productTypeID", new PyInt(Integer(data["productTypeID"]))),
            ("optionalTypeID", OptionalInteger(record, "optionalTypeID")),
            ("optionalTypeID2", OptionalInteger(record, "optionalTypeID2")),
            ("outputLocationID", new PyInt(Integer(data["outputLocationID"]))),
            ("outputFlagID", new PyInt(Integer(data["outputFlagID"]))));
    }

    private static double Number(JsonNode? node, double fallback)
    {
        if (node is not JsonValue value) return fallback;
        if (value.TryGetValue<double>(out var number)) return number;
        if (value.TryGetValue<long>(out var integer)) return integer;
        if (value.TryGetValue<int>(out var smallInteger)) return smallInteger;
        if (value.TryGetValue<bool>(out var flag)) return flag ? 1 : 0;
        return value.TryGetValue<string>(out var text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                ? number
                : fallback;
    }
}
