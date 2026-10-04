using System.Globalization;
using System.Text.Json.Nodes;
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
        }, () => Now);

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
        }, () => Now);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)], cancellation: source.Token)));
        Assert.Equal(0, reads);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.CallAsync(Call("GetJobsByOwner", [new PyInt(1001)])));
    }

    [Fact]
    public void Only_read_rpc_methods_are_exposed()
    {
        var service = Service(null);

        Assert.Equal("industryManager", service.Name);
        Assert.True(service.HasMethod("GetJob"));
        Assert.True(service.HasMethod("GetJobsByOwner"));
        Assert.True(service.HasMethod("GetJobCounts"));
        Assert.False(service.HasMethod("InstallJob"));
        Assert.False(service.HasMethod("CompleteJob"));
        Assert.False(service.HasMethod("CancelJob"));
    }

    private static IndustryJobsService Service(
        JsonObject? table,
        long now = Now,
        Func<ServiceCall, JsonObject, bool>? canViewStarbaseJob = null,
        Func<long, bool>? isStarbaseFacilityOnline = null,
        Func<long, long, long, long>? resolveBlueprintLocationFlagID = null) =>
        new(_ => ValueTask.FromResult(table), () => now, canViewStarbaseJob, isStarbaseFacilityOnline, resolveBlueprintLocationFlagID);

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
        CancellationToken cancellation = default)
    {
        var session = new ClientSession();
        session.Set("charid", new PyInt(characterId));
        session.Set("corpid", new PyInt(corporationId));
        session.Set("corprole", new PyLong(corporationRole));
        return new ServiceCall
        {
            Method = method,
            Args = args ?? [],
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
}
