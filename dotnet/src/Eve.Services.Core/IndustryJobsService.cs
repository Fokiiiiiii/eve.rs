using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>
/// Industry manager protocol adapter. Job reads are snapshots; mutations cross the typed lifecycle port
/// and must commit authoritative inventory, wallet, job, and outbox changes before reporting success.
/// Starbase visibility/lifecycle and legacy inventory-flag resolution are supplied by Host callbacks.

/// </summary>

/// <summary>The authenticated session facts needed by the industry domain; never copied from the job payload.</summary>
public sealed record IndustryJobActor(long CharacterId, long CorporationId, long CorporationRoles, int CorporationAccountKey);

public sealed record IndustryJobLocation(long LocationId, int FlagId);

public sealed record IndustryJobWallet(long OwnerId, int AccountKey);

/// <summary>Client-selected job inputs. Costs, duration, materials, outcomes, and resulting status are always recomputed from authoritative data.</summary>
public sealed record IndustryJobInstallSubmission(
    int ActivityId,
    long BlueprintId,
    int BlueprintTypeId,
    bool BlueprintCopy,
    long BlueprintLocationId,
    int BlueprintLocationFlagId,
    long FacilityId,
    long OwnerId,
    long SolarSystemId,
    long StationId,
    IndustryJobLocation InputLocation,
    IndustryJobLocation OutputLocation,
    int Runs,
    int LicensedRuns,
    int? ProductTypeId,
    int? OptionalTypeId,
    int? OptionalTypeId2,
    IndustryJobWallet Wallet,
    IReadOnlyList<int>? MaterialTypeIds = null,
    bool IsAlphaClone = false);

/// <summary>A retry key scoped to the authenticated character, RPC and Macho call id.</summary>
public sealed record IndustryJobOperationContext(IndustryJobActor Actor, string OperationId);

public sealed record IndustryJobInstallRequest(IndustryJobOperationContext Context, IndustryJobInstallSubmission Job);
public sealed record IndustryJobAction(IndustryJobOperationContext Context, long JobId, long SolarSystemId);
public sealed record IndustryJobBatch(IndustryJobOperationContext Context, IReadOnlyList<IndustryJobAction> Jobs);

public sealed record IndustryJobChangedEvent(
    long JobId,
    long OwnerId,
    long BlueprintId,
    long InstallerId,
    int Status,
    long? SuccessfulRuns,
    string OperationId);

public static class IndustryJobEvents
{
    public const string Changed = "industry.job.changed";
}

/// <summary>A localized, client-visible industry error returned by an authoritative domain operation.</summary>
public sealed class IndustryJobUserErrorException : Exception
{
    public IndustryJobUserErrorException(string messageKey) : base(messageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);
        MessageKey = messageKey;
    }

    public string MessageKey { get; }
}

/// <summary>
/// Typed use-case port implemented by Host composition. Each operation must commit job rows, custody,
/// wallet settlement and the matching outbox event atomically before returning. OperationId makes safe
/// retries explicit; CompleteManyJobsAsync is all-or-nothing.
/// </summary>
public interface IIndustryJobOperations
{
    ValueTask<long> InstallJobAsync(IndustryJobInstallRequest request, CancellationToken cancellationToken);
    ValueTask CompleteJobAsync(IndustryJobAction request, CancellationToken cancellationToken);
    ValueTask CompleteManyJobsAsync(IndustryJobBatch request, CancellationToken cancellationToken);
    ValueTask CancelJobAsync(IndustryJobAction request, CancellationToken cancellationToken);
}

public sealed class IndustryJobsService : IService
{
    private const long FactoryManagerRole = Eve.Corporations.CorporationRoles.FactoryManager;
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
    private readonly Func<PyValue?, bool>? isAlphaClone;

private readonly IIndustryJobOperations operations;

    public IndustryJobsService(
        Func<CancellationToken, ValueTask<JsonObject?>> readJobs,
        IIndustryJobOperations operations,
        Func<long>? currentFileTime = null,
        Func<ServiceCall, JsonObject, bool>? canViewStarbaseJob = null,
        Func<long, bool>? isStarbaseFacilityOnline = null,
        Func<long, long, long, long>? resolveBlueprintLocationFlagID = null,
        Func<PyValue?, bool>? isAlphaClone = null)
    {
        ArgumentNullException.ThrowIfNull(readJobs);
        ArgumentNullException.ThrowIfNull(operations);
        this.readJobs = readJobs;
        this.operations = operations;
        this.currentFileTime = currentFileTime ?? (() => DateTime.UtcNow.ToFileTimeUtc());
        this.canViewStarbaseJob = canViewStarbaseJob;
        this.isStarbaseFacilityOnline = isStarbaseFacilityOnline;
        this.resolveBlueprintLocationFlagID = resolveBlueprintLocationFlagID;
        this.isAlphaClone = isAlphaClone;
    }

    public string Name => "industryManager";

    public bool HasMethod(string method) => method is "GetJob" or "GetJobsByOwner" or "GetJobCounts" or "InstallJob" or "CompleteJob" or "CompleteManyJobs" or "CancelJob";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();

        if (call.Method is "InstallJob" or "CompleteJob" or "CompleteManyJobs" or "CancelJob")
            return await MutateAsync(call);

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

private async ValueTask<PyValue> MutateAsync(ServiceCall call)
    {
        var context = OperationContext(call);
        try
        {
            switch (call.Method)
            {
                case "InstallJob":
                {
                    if (call.Args.Count != 1) throw InvalidIndustryJob();
                    var jobId = await operations.InstallJobAsync(
                        new IndustryJobInstallRequest(context, InstallSubmission(call.Arg(0), context.Actor) with
                        { IsAlphaClone = isAlphaClone?.Invoke(call.Session.Get("userid")) ?? false }),
                        call.Cancellation);
                    if (jobId <= 0) throw new InvalidOperationException("Industry install returned no committed job ID.");
                    return new PyInt(jobId);
                }
                case "CompleteJob":
                {
                    if (call.Args.Count != 2) throw InvalidIndustryJob();
                    await operations.CompleteJobAsync(ActionRequest(context, call.Arg(0), call.Arg(1)), call.Cancellation);
                    return PyNone.Instance;
                }
                case "CompleteManyJobs":
                {
                    if (call.Args.Count != 1 || call.Arg(0) is not PyList rows) throw InvalidIndustryJob();
                    var jobs = new List<IndustryJobAction>(rows.Items.Count);
                    var seen = new HashSet<long>();
                    foreach (var row in rows.Items)
                    {
                        if (row is not PyTuple { Items.Count: 2 } pair) throw InvalidIndustryJob();
                        var jobId = RequiredPositive(pair.Items[0]);
                        var systemId = RequiredPositive(pair.Items[1]);
                        if (!seen.Add(jobId)) throw InvalidIndustryJob();
                        jobs.Add(new IndustryJobAction(context, jobId, systemId));
                    }
                    if (jobs.Count > 0)
                        await operations.CompleteManyJobsAsync(new IndustryJobBatch(context, jobs), call.Cancellation);
                    return PyNone.Instance;
                }
                case "CancelJob":
                {
                    if (call.Args.Count != 2) throw InvalidIndustryJob();
                    await operations.CancelJobAsync(ActionRequest(context, call.Arg(0), call.Arg(1)), call.Cancellation);
                    return PyNone.Instance;
                }
                default:
                    return PyNone.Instance;
            }
        }
        catch (IndustryJobUserErrorException error)
        {
            throw MachoErrors.ThrowUserError(error.MessageKey);
        }
    }

    private static IndustryJobAction ActionRequest(IndustryJobOperationContext context, PyValue jobId, PyValue solarSystemId) =>
        new(context, RequiredPositive(jobId), RequiredPositive(solarSystemId));

    private static IndustryJobOperationContext OperationContext(ServiceCall call)
    {
        var actor = new IndustryJobActor(
            SessionId(call, "charid"),
            SessionId(call, "corpid"),
            RoleValue(call.Session.Get("corprole")),
            checked((int)Math.Min(Positive(call.Session.Get("corpAccountKey")), int.MaxValue)));
        var callId = Positive(call.CallId);
        if (actor.CharacterId <= 0 || callId <= 0) throw InvalidIndustryJob();
        return new IndustryJobOperationContext(actor, $"industry:{actor.CharacterId}:{call.Method}:{callId}");
    }

    private static IndustryJobInstallSubmission InstallSubmission(PyValue payload, IndustryJobActor actor)
    {
        var activityId = RequiredInt(payload, "activityID");
        var blueprintId = RequiredLong(payload, "blueprintID");
        var blueprintTypeId = RequiredInt(payload, "blueprintTypeID");

        // corporation when the account is the corporation's, and the character otherwise. The blueprint's own location is read by the host.
        var accountOwner = AccountOwnerId(payload);
        var ownerId = OptionalPositiveField(payload, "ownerID")
            ?? (accountOwner is { } corporation && corporation == actor.CorporationId ? corporation : actor.CharacterId);
        if (ownerId != actor.CharacterId && (actor.CorporationId <= 0 || ownerId != actor.CorporationId))
            throw MachoErrors.ThrowUserError("IndustryInvalidOwner");

        var blueprintLocationId = OptionalPositiveField(payload, "blueprintLocationID") ?? 0;
        var blueprintLocationFlagId = NonNegativeInt(payload, "blueprintLocationFlagID", 0);
        var input = Location(payload, "inputLocation", "inputLocationID", "inputLocationFlagID",
            blueprintLocationId, blueprintLocationFlagId);
        // A corporation job without a chosen output goes to the facility's deliveries (industry/job.py MatchLocation's default).
        var corporate = ownerId != actor.CharacterId;
        var output = Location(payload, "outputLocation", "outputLocationID", "outputFlagID",
            corporate ? RequiredLong(payload, "facilityID") : input.LocationId,
            corporate ? IndustryRules.CorporationDeliveriesFlag : input.FlagId);
        var wallet = Wallet(payload, ownerId, actor);

        return new IndustryJobInstallSubmission(
            activityId,
            blueprintId,
            blueprintTypeId,
            JsSemantics.Truthy(WireField(payload, "blueprintCopy")),
            blueprintLocationId,
            blueprintLocationFlagId,
            RequiredLong(payload, "facilityID"),
            ownerId,
            RequiredLong(payload, "solarSystemID"),
            NonNegativeLong(payload, "stationID", 0),
            input,
            output,
            RequiredInt(payload, "runs"),
            NonNegativeInt(payload, "licensedRuns", 0),
            OptionalInt(payload, "productTypeID"),
            OptionalInt(payload, "optionalTypeID"),
            OptionalInt(payload, "optionalTypeID2"),
            wallet,
            MaterialTypeIds(payload));
    }

    /// <summary>The item types of the job's materials dictionary: the client lists the chosen decryptor among them.</summary>
    private static IReadOnlyList<int> MaterialTypeIds(PyValue payload) =>
        WireField(payload, "materials") is PyDict materials
            ? materials.Entries.Select(entry => (int)Integer(entry.Key)).Where(id => id > 0).ToArray()
            : [];


    private static long? AccountOwnerId(PyValue payload)
    {
        var owner = WireField(payload, "accountOwnerID");
        if (owner is null && WireField(payload, "account") is PyTuple tuple && tuple.Items.Count == 2)
            owner = tuple.Items[0];
        else if (owner is null && WireField(payload, "account") is PyList list && list.Items.Count == 2)
            owner = list.Items[0];
        var id = Positive(owner);
        return id > 0 ? id : null;
    }

    private static IndustryJobLocation Location(
        PyValue payload,
        string nestedName,
        string idName,
        string flagName,
        long defaultId,
        int defaultFlag)
    {
        var nested = WireField(payload, nestedName);
        var locationId = OptionalPositiveField(payload, idName)
            ?? OptionalPositiveField(nested, "itemID")
            ?? defaultId;
        var flagId = OptionalIntField(payload, flagName)
            ?? OptionalIntField(nested, "flagID")
            ?? defaultFlag;
        return new IndustryJobLocation(locationId, flagId);
    }

    private static IndustryJobWallet Wallet(PyValue payload, long ownerId, IndustryJobActor actor)
    {
        var account = WireField(payload, "account");
        PyValue? accountOwner = null;
        PyValue? accountKey = null;
        if (account is PyTuple tuple && tuple.Items.Count == 2)
        {
            accountOwner = tuple.Items[0];
            accountKey = tuple.Items[1];
        }
        else if (account is PyList list && list.Items.Count == 2)
        {
            accountOwner = list.Items[0];
            accountKey = list.Items[1];
        }

        var walletOwnerId = Positive(WireField(payload, "accountOwnerID") ?? accountOwner) is var suppliedOwner && suppliedOwner > 0
            ? suppliedOwner
            : ownerId;
        var defaultKey = walletOwnerId == actor.CorporationId && actor.CorporationAccountKey is >= 1000 and <= 1006
            ? actor.CorporationAccountKey
            : 1000;
        var division = OptionalIntField(payload, "accountKey")
            ?? OptionalIntValue(accountKey)
            ?? defaultKey;
        if (walletOwnerId != actor.CharacterId && walletOwnerId != actor.CorporationId ||
            walletOwnerId == actor.CharacterId && division != 1000 ||
            walletOwnerId == actor.CorporationId && division is < 1000 or > 1006)
            throw MachoErrors.ThrowUserError("IndustryInvalidAccount");
        return new IndustryJobWallet(walletOwnerId, division);
    }

    private static PyValue? WireField(PyValue? payload, string name)
    {
        var dictionary = payload switch
        {
            PyDict direct => direct,
            PyObject objectValue when objectValue.Args is PyDict args => args,
            _ => null,
        };
        if (dictionary is null) return null;
        foreach (var (key, value) in dictionary.Entries)
            if (Eve.Macho.JsSemantics.StringValue(key) == name) return value;
        return null;
    }

    private static long RequiredLong(PyValue payload, string name) =>
        RequiredPositive(WireField(payload, name));

    private static long RequiredPositive(PyValue? value)
    {
        var number = Positive(value);
        if (number <= 0) throw InvalidIndustryJob();
        return number;
    }

    private static int RequiredInt(PyValue payload, string name)
    {
        var value = RequiredLong(payload, name);
        if (value > int.MaxValue) throw InvalidIndustryJob();
        return (int)value;
    }

    private static int NonNegativeInt(PyValue payload, string name, int fallback)
    {
        var value = WireField(payload, name);
        if (value is null or PyNone) return fallback;
        var number = Integer(JsSemantics.ToNumber(value));
        if (number < 0 || number > int.MaxValue) throw InvalidIndustryJob();
        return (int)number;
    }

    private static long NonNegativeLong(PyValue payload, string name, long fallback)
    {
        var value = WireField(payload, name);
        if (value is null or PyNone) return fallback;
        var number = Integer(JsSemantics.ToNumber(value));
        if (number < 0) throw InvalidIndustryJob();
        return number;
    }

    private static int? OptionalInt(PyValue payload, string name) =>
        OptionalIntField(payload, name);

    private static int? OptionalIntField(PyValue? payload, string name)
    {
        var value = WireField(payload, name);
        return OptionalIntValue(value);
    }

    private static int? OptionalIntValue(PyValue? value)
    {
        if (value is null or PyNone) return null;
        var number = Integer(JsSemantics.ToNumber(value));
        if (number <= 0 || number > int.MaxValue) throw InvalidIndustryJob();
        return (int)number;
    }

    private static long? OptionalPositiveField(PyValue? payload, string name)
    {
        var value = WireField(payload, name);
        if (value is null or PyNone) return null;
        var number = Positive(value);
        if (number <= 0) throw InvalidIndustryJob();
        return number;
    }

    private static MachoWrappedException InvalidIndustryJob() =>
        MachoErrors.ThrowUserError("IndustryInvalidJob");



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
