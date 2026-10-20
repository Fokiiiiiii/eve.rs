using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public sealed class IndustryJobsServiceTests
{
    private const long Now = 133000000000000000;
    private const long Start = 134000000000000001;
    private const long End = 134000000000000999;

    [Fact]
    public async Task Jobs_by_owner_use_persisted_rows_and_the_reference_keyval_contract()
    {
        var persistedJob = Job(42001);
        persistedJob["pauseDate"] = 0;
        persistedJob["optionalTypeID"] = 0;
        var table = Table((42001, persistedJob));
        var service = Service(table);

        var row = Assert.Single(Assert.IsType<PyList>(
            await service.CallAsync(Call("GetJobsByOwner", [new PyInt(1001), PyValue.Of(false)]))).Items);

        Assert.Equal([
            "activityID", "jobID", "blueprintID", "blueprintTypeID", "blueprintCopy",
            "blueprintLocationID", "blueprintLocationFlagID", "facilityID", "ownerID", "status",
            "installerID", "completedCharacterID", "solarSystemID", "stationID", "startDate",
            "endDate", "pauseDate", "runs", "licensedRuns", "successfulRuns", "cost",
            "timeInSeconds", "probability", "productTypeID", "optionalTypeID", "optionalTypeID2",
            "outputLocationID", "outputFlagID",
        ], Fields(row));
        Assert.Equal(new PyStr("util.KeyVal"), Assert.IsType<PyObject>(row).Name);
        Assert.Equal(new PyInt(42001), Field(row, "jobID"));
        Assert.Equal(new PyInt(1), Field(row, "activityID"));
        Assert.Equal(new PyInt(1001), Field(row, "ownerID"));
        Assert.Equal(new PyInt(1), Field(row, "status"));
        Assert.Equal(new PyLong(Start), Field(row, "startDate"));
        Assert.Equal(new PyLong(End), Field(row, "endDate"));
        Assert.Same(PyNone.Instance, Field(row, "pauseDate"));
        Assert.Equal(new PyInt(1), Field(row, "probability"));
        Assert.Same(PyNone.Instance, Field(row, "optionalTypeID"));
        Assert.Equal(new PyInt(123456), Field(row, "cost"));
        Assert.Equal(28, Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args).Entries.Count);
    }

    [Fact]
    public async Task Missing_store_data_returns_reference_empty_list_dict_and_default_job()
    {
        var service = Service(null);

        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)]))).Items);
        var counts = Assert.IsType<PyDict>(await service.CallAsync(Call("GetJobCounts")));
        Assert.Equal([1, 3, 4, 5, 8, 9], counts.Entries.Select(pair => Assert.IsType<PyInt>(pair.Key).Value));
        Assert.All(counts.Entries, entry => Assert.Equal(new PyInt(0), entry.Value));

        var missing = await service.CallAsync(Call("GetJob", [new PyInt(99999)]));
        Assert.Equal(new PyInt(0), Field(missing, "jobID"));
        Assert.Equal(new PyInt(0), Field(missing, "status"));
        Assert.Equal(new PyLong(0), Field(missing, "startDate"));
        Assert.Same(PyNone.Instance, Field(missing, "pauseDate"));
        Assert.Equal(new PyInt(1), Field(missing, "probability"));
    }

    [Fact]
    public async Task Include_completed_returns_persisted_history_in_job_id_order()
    {
        var service = Service(Table(
            (42003, Job(42003, status: 102)),
            (42001, Job(42001)),
            (42002, Job(42002, status: 100)),
            (42004, Job(42004, ownerId: 2002))));

        var active = await service.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)]));
        var history = await service.CallAsync(Call("GetJobsByOwner", [new PyInt(1001), PyValue.Of(true)]));

        Assert.Equal([42001L], JobIds(active));
        Assert.Equal([42001L, 42002L, 42003L], JobIds(history));
        Assert.Equal(new PyInt(100), Field(Assert.IsType<PyList>(history).Items[1], "status"));
        Assert.Equal(new PyInt(102), Field(Assert.IsType<PyList>(history).Items[2], "status"));
    }

    [Fact]
    public async Task Expired_installed_jobs_become_ready_and_pending_installs_are_not_readable()
    {
        var service = Service(Table(
            (42001, Job(42001, endDate: Now - 1)),
            (42002, Job(42002, installationPending: true))));

        var jobs = Assert.IsType<PyList>(await service.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)])));

        Assert.Equal([42001L], JobIds(jobs));
        Assert.Equal(new PyInt(3), Field(jobs.Items[0], "status"));
    }

    [Fact]
    public async Task Legacy_filetimes_and_blueprint_location_flags_are_normalized()
    {
        var legacy = Job(42001);
        legacy.Remove("blueprintInventoryFlagID");
        legacy["blueprintInventoryLocationID"] = 60002;
        legacy["startDate"] = "1000";
        legacy["endDate"] = "2000";
        var resolverCalls = 0;
        long ResolveFlag(long locationId, long ownerId, long fallback)
        {
            resolverCalls++;
            Assert.Equal(60002, locationId);
            Assert.Equal(1001, ownerId);
            Assert.Equal(4, fallback);
            return 104;
        }
        var service = Service(Table((42001, legacy)), resolveBlueprintLocationFlagID: ResolveFlag);

        var row = Assert.Single(Assert.IsType<PyList>(
            await service.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)]))).Items);

        Assert.Equal(new PyLong(116444736010000000), Field(row, "startDate"));
        Assert.Equal(new PyLong(116444736020000000), Field(row, "endDate"));
        Assert.Equal(new PyInt(104), Field(row, "blueprintLocationFlagID"));
        Assert.Equal(new PyInt(3), Field(row, "status"));
        Assert.Equal(1, resolverCalls);
    }

    [Fact]
    public async Task Corporation_jobs_require_factory_manager_role_and_job_owner_is_not_leaked()
    {
        var service = Service(Table((42001, Job(42001, ownerId: 2002))));
        var pendingService = Service(Table((42002, Job(42002, ownerId: 2002, installationPending: true))));

        var denied = await service.CallAsync(Call("GetJob", [new PyInt(42001)], corporationId: 2002));
        var allowed = await service.CallAsync(Call("GetJob", [new PyInt(42001)], corporationId: 2002, corporationRole: 1024));
        var pending = await pendingService.CallAsync(Call("GetJob", [new PyInt(42002)], corporationId: 2002, corporationRole: 1024));

        Assert.Equal(new PyInt(0), Field(denied, "jobID"));
        Assert.Equal(new PyInt(0), Field(denied, "status"));
        Assert.Equal(new PyInt(2002), Field(allowed, "ownerID"));
        Assert.Equal(new PyInt(0), Field(pending, "jobID"));
        Assert.Equal(new PyInt(0), Field(pending, "status"));
        Assert.Equal([42001L], JobIds(await service.CallAsync(Call("GetJobsByOwner", [new PyInt(2002)], corporationId: 2002, corporationRole: 1024))));
    }

    [Fact]
    public async Task Job_counts_use_only_the_requesting_installer_and_aggregate_science_slots()
    {
        var service = Service(Table(
            (1, Job(1, activityId: 3)),
            (2, Job(2, activityId: 4, status: 2)),
            (3, Job(3, ownerId: 2002, activityId: 1)),
            (4, Job(4, activityId: 8, status: 100)),
            (5, Job(5, activityId: 5, installationPending: true)),
            (6, Job(6, activityId: 9, status: 101))));

        var counts = Assert.IsType<PyDict>(await service.CallAsync(Call("GetJobCounts", [new PyInt(1001)])));

        Assert.Equal(new PyInt(1), DictValue(counts, 1));
        foreach (var activity in new[] { 3, 4, 5, 8 }) Assert.Equal(new PyInt(2), DictValue(counts, activity));
        Assert.Equal(new PyInt(0), DictValue(counts, 9));
        var denied = Assert.IsType<PyDict>(await service.CallAsync(Call("GetJobCounts", [new PyInt(1002)])));
        Assert.Empty(denied.Entries);
    }

    [Fact]
    public async Task Starbase_jobs_require_access_and_offline_facilities_remain_paused()
    {
        var table = Table((42001, Job(42001, endDate: Now - 1, starbaseTowerId: 9001)));
        var hidden = Service(table);
        var missingLifecyclePolicy = Service(table, canViewStarbaseJob: (_, _) => true);
        var visibleOffline = Service(table, canViewStarbaseJob: (_, _) => true, isStarbaseFacilityOnline: _ => false);
        var visibleOnline = Service(table, canViewStarbaseJob: (_, _) => true, isStarbaseFacilityOnline: _ => true);

        Assert.Empty(Assert.IsType<PyList>(await hidden.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)]))).Items);
        Assert.Empty(Assert.IsType<PyList>(await missingLifecyclePolicy.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)]))).Items);
        var paused = Assert.Single(Assert.IsType<PyList>(await visibleOffline.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)]))).Items);
        var ready = Assert.Single(Assert.IsType<PyList>(await visibleOnline.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)]))).Items);
        Assert.Equal(new PyInt(2), Field(paused, "status"));
        Assert.Equal(new PyInt(3), Field(ready, "status"));
    }

    [Fact]
    public async Task Each_call_reads_current_persisted_snapshot_without_mutating_it()
    {
        var table = Table();
        var reads = 0;
        var service = new IndustryJobsService(_ =>
        {
            reads++;
            return ValueTask.FromResult<JsonObject?>(table);
        }, new RecordingIndustryJobOperations(), () => Now);

        var initial = table.ToJsonString();
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)]))).Items);
        Assert.Equal(initial, table.ToJsonString());
        ((JsonObject)table["jobs"]!)["42001"] = Job(42001);
        var persisted = table.ToJsonString();
        Assert.Equal([42001L], JobIds(await service.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)]))));

        Assert.Equal(2, reads);
        Assert.Equal(persisted, table.ToJsonString());
    }

    [Fact]
    public async Task Reads_propagate_cancellation_and_storage_failures()
    {
        var reads = 0;
        var service = new IndustryJobsService(_ =>
        {
            reads++;
            return ValueTask.FromException<JsonObject?>(new InvalidOperationException("storage failed"));
        }, new RecordingIndustryJobOperations(), () => Now);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)], cancellation: source.Token)));
        Assert.Equal(0, reads);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)])));
    }

[Fact]
    public async Task Install_job_uses_the_typed_port_and_returns_only_the_committed_job_id()
    {
        var operations = new RecordingIndustryJobOperations { InstallResult = 52001 };
        var service = Service(null, operations: operations);
        var job = Py.KeyVal(
            ("activityID", new PyInt(1)),
            ("blueprintID", new PyInt(50001)),
            ("blueprintTypeID", new PyInt(34)),
            ("blueprintCopy", PyValue.Of(true)),
            ("blueprintLocationID", new PyInt(60001)),
            ("blueprintLocationFlagID", new PyInt(4)),
            ("facilityID", new PyInt(70001)),
            ("ownerID", new PyInt(2002)),
            ("solarSystemID", new PyInt(30000142)),
            ("stationID", new PyInt(70001)),
            ("inputLocationID", new PyInt(60002)),
            ("inputLocationFlagID", new PyInt(5)),
            ("outputLocationID", new PyInt(60003)),
            ("outputFlagID", new PyInt(6)),
            ("runs", new PyInt(3)),
            ("licensedRuns", new PyInt(3)),
            ("productTypeID", new PyInt(34)),
            ("optionalTypeID", PyNone.Instance),
            ("optionalTypeID2", new PyInt(35)),
            ("account", PyTuple.Of(new PyInt(1001), new PyInt(1000))),
            ("cost", new PyInt(1)),
            ("timeInSeconds", new PyInt(1)));

        var result = await service.CallAsync(Call(
            "InstallJob", [job], corporationId: 2002, corporationRole: 1024, corporationAccountKey: 1004, callId: 41));

        Assert.Equal(new PyInt(52001), result);
        var request = Assert.IsType<IndustryJobInstallRequest>(operations.InstallRequest);
        Assert.Equal("industry:1001:InstallJob:41", request.Context.OperationId);
        Assert.Equal(new IndustryJobActor(1001, 2002, 1024, 1004), request.Context.Actor);
        Assert.Equal(1, request.Job.ActivityId);
        Assert.Equal(50001, request.Job.BlueprintId);
        Assert.Equal(34, request.Job.BlueprintTypeId);
        Assert.True(request.Job.BlueprintCopy);
        Assert.Equal(new IndustryJobLocation(60002, 5), request.Job.InputLocation);
        Assert.Equal(new IndustryJobLocation(60003, 6), request.Job.OutputLocation);
        Assert.Equal(3, request.Job.Runs);
        Assert.Equal(3, request.Job.LicensedRuns);
        Assert.Equal(new IndustryJobWallet(1001, 1000), request.Job.Wallet);
    }

    [Fact]
    public async Task Complete_and_cancel_use_two_client_arguments_and_return_none_after_commit()
    {
        var operations = new RecordingIndustryJobOperations();
        var service = Service(null, operations: operations);

        var completed = await service.CallAsync(Call("CompleteJob", [new PyInt(52001), new PyInt(30000142)], callId: 42));
        var cancelled = await service.CallAsync(Call("CancelJob", [new PyInt(52002), new PyInt(30000142)], callId: 43));

        Assert.Same(PyNone.Instance, completed);
        Assert.Same(PyNone.Instance, cancelled);
        Assert.Equal(new IndustryJobAction(
            new IndustryJobOperationContext(new IndustryJobActor(1001, 0, 0, 1000), "industry:1001:CompleteJob:42"),
            52001,
            30000142), operations.Completed);
        Assert.Equal(new IndustryJobAction(
            new IndustryJobOperationContext(new IndustryJobActor(1001, 0, 0, 1000), "industry:1001:CancelJob:43"),
            52002,
            30000142), operations.Cancelled);
    }

    [Fact]
    public async Task Complete_many_preserves_client_pairs_and_rejects_duplicate_jobs_before_domain_dispatch()
    {
        var operations = new RecordingIndustryJobOperations();
        var service = Service(null, operations: operations);
        var pairs = new PyList([
            PyTuple.Of(new PyInt(52001), new PyInt(30000142)),
            PyTuple.Of(new PyInt(52002), new PyInt(30000143)),
        ]);

        var result = await service.CallAsync(Call("CompleteManyJobs", [pairs], callId: 44));

        Assert.Same(PyNone.Instance, result);
        var batch = Assert.IsType<IndustryJobBatch>(operations.CompletedBatch);
        Assert.Equal([52001L, 52002L], batch.Jobs.Select(job => job.JobId));
        Assert.Equal([30000142L, 30000143L], batch.Jobs.Select(job => job.SolarSystemId));
        Assert.All(batch.Jobs, job => Assert.Same(batch.Context, job.Context));

        var duplicate = new PyList([
            PyTuple.Of(new PyInt(52001), new PyInt(30000142)),
            PyTuple.Of(new PyInt(52001), new PyInt(30000142)),
        ]);
        await Assert.ThrowsAsync<MachoWrappedException>(() =>
            service.CallAsync(Call("CompleteManyJobs", [duplicate], callId: 45)).AsTask());
        Assert.Equal(1, operations.CompleteManyCalls);
    }

    [Fact]
    public async Task Domain_user_errors_keep_the_macho_user_error_envelope_and_failed_writes_do_not_return_success()
    {
        var operations = new RecordingIndustryJobOperations
        {
            NextError = new IndustryJobUserErrorException("IndustryNotEnoughMaterials"),
        };
        var service = Service(null, operations: operations);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            service.CallAsync(Call("CancelJob", [new PyInt(52001), new PyInt(30000142)], callId: 46)).AsTask());

        Assert.True(error.Error.IsUserError);
        var cancelled = Assert.IsType<IndustryJobAction>(operations.Cancelled);
        Assert.Equal((52001L, 30000142L), (cancelled.JobId, cancelled.SolarSystemId));
    }

    [Fact]
    public void Lifecycle_rpc_methods_are_exposed()
    {
        var service = Service(null);

        Assert.Equal("industryManager", service.Name);
        Assert.True(service.HasMethod("GetJob"));
        Assert.True(service.HasMethod("GetJobsByOwner"));
        Assert.True(service.HasMethod("GetJobCounts"));
        Assert.True(service.HasMethod("InstallJob"));
        Assert.True(service.HasMethod("CompleteJob"));
        Assert.True(service.HasMethod("CompleteManyJobs"));
        Assert.True(service.HasMethod("CancelJob"));
    }

    private static IndustryJobsService Service(
        JsonObject? table,
        long now = Now,
        Func<ServiceCall, JsonObject, bool>? canViewStarbaseJob = null,
        Func<long, bool>? isStarbaseFacilityOnline = null,
        Func<long, long, long, long>? resolveBlueprintLocationFlagID = null,
        IIndustryJobOperations? operations = null) =>
        new(_ => ValueTask.FromResult(table), operations ?? new RecordingIndustryJobOperations(), () => now, canViewStarbaseJob, isStarbaseFacilityOnline, resolveBlueprintLocationFlagID);

    private static JsonObject Table(params (long Id, JsonObject Record)[] records)
    {
        var jobs = new JsonObject();
        foreach (var (id, record) in records)
            jobs[id.ToString(CultureInfo.InvariantCulture)] = record;
        return new JsonObject { ["jobs"] = jobs };
    }

    private static JsonObject Job(
        long id,
        long ownerId = 1001,
        long activityId = 1,
        long status = 1,
        long installerId = 1001,
        long endDate = End,
        bool installationPending = false,
        long starbaseTowerId = 0)
    {
        var job = new JsonObject
        {
            ["jobID"] = id,
            ["activityID"] = activityId,
            ["blueprintID"] = 50001,
            ["blueprintTypeID"] = 34,
            ["blueprintCopy"] = false,
            ["blueprintLocationID"] = 60001,
            ["blueprintLocationFlagID"] = 4,
            ["blueprintInventoryLocationID"] = 60001,
            ["blueprintInventoryFlagID"] = 4,
            ["facilityID"] = 70001,
            ["ownerID"] = ownerId,
            ["status"] = status,
            ["installerID"] = installerId,
            ["completedCharacterID"] = 0,
            ["solarSystemID"] = 30000142,
            ["stationID"] = 70001,
            ["startDate"] = Start.ToString(CultureInfo.InvariantCulture),
            ["endDate"] = endDate.ToString(CultureInfo.InvariantCulture),
            ["pauseDate"] = null,
            ["runs"] = 2,
            ["licensedRuns"] = 2,
            ["successfulRuns"] = 0,
            ["cost"] = 123456,
            ["timeInSeconds"] = 3600,
            ["probability"] = 1,
            ["productTypeID"] = 34,
            ["outputLocationID"] = 70001,
            ["outputFlagID"] = 4,
        };
        if (installationPending) job["installationPending"] = true;
        if (starbaseTowerId > 0) job["starbaseTowerID"] = starbaseTowerId;
        return job;
    }

    private static ServiceCall Call(
        string method,
        IReadOnlyList<PyValue>? args = null,
        long characterId = 1001,
        long corporationId = 0,
        long corporationRole = 0,
        int corporationAccountKey = 1000,
        long callId = 77,
        CancellationToken cancellation = default)
    {
        var session = new ClientSession();
        session.Set("charid", new PyInt(characterId));
        session.Set("corpid", new PyInt(corporationId));
        session.Set("corprole", new PyLong(corporationRole));
        session.Set("corpAccountKey", new PyInt(corporationAccountKey));
        return new ServiceCall
        {
            Method = method,
            Args = args ?? [],
            CallId = new PyInt(callId),
            Session = session,
            Cancellation = cancellation,
        };
    }

    private static IReadOnlyList<long> JobIds(PyValue value) =>
        Assert.IsType<PyList>(value).Items.Select(row => Assert.IsType<PyInt>(Field(row, "jobID")).Value).ToArray();

    private static PyValue Field(PyValue row, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args).Entries
            .Single(pair => pair.Key is PyStr key && key.Value == name).Value;

    private static PyValue DictValue(PyDict dict, int key) =>
        dict.Entries.Single(pair => pair.Key is PyInt value && value.Value == key).Value;

    private static string[] Fields(PyValue row) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(row).Args).Entries
            .Select(pair => Assert.IsType<PyStr>(pair.Key).Value).ToArray();

private sealed class RecordingIndustryJobOperations : IIndustryJobOperations
    {
        public long InstallResult { get; init; } = 42050;
        public Exception? NextError { get; init; }
        public IndustryJobInstallRequest? InstallRequest { get; private set; }
        public IndustryJobAction? Completed { get; private set; }
        public IndustryJobBatch? CompletedBatch { get; private set; }
        public IndustryJobAction? Cancelled { get; private set; }
        public int CompleteManyCalls { get; private set; }

        public ValueTask<long> InstallJobAsync(IndustryJobInstallRequest request, CancellationToken cancellationToken)
        {
            InstallRequest = request;
            return NextError is { } error
                ? ValueTask.FromException<long>(error)
                : ValueTask.FromResult(InstallResult);
        }

        public ValueTask CompleteJobAsync(IndustryJobAction request, CancellationToken cancellationToken)
        {
            Completed = request;
            return NextError is { } error ? ValueTask.FromException(error) : ValueTask.CompletedTask;
        }

        public ValueTask CompleteManyJobsAsync(IndustryJobBatch request, CancellationToken cancellationToken)
        {
            CompletedBatch = request;
            CompleteManyCalls++;
            return NextError is { } error ? ValueTask.FromException(error) : ValueTask.CompletedTask;
        }

        public ValueTask CancelJobAsync(IndustryJobAction request, CancellationToken cancellationToken)
        {
            Cancelled = request;
            return NextError is { } error ? ValueTask.FromException(error) : ValueTask.CompletedTask;
        }
    }
}
