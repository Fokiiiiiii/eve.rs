namespace Eve.GameStore.Tests;

public class OutboxTests
{
    private static RowUpsert Up(string key, string json) => new(key, json);

    private static async Task<T> Fails<T>(Task task) where T : Exception => await Assert.ThrowsAsync<T>(() => task);

    private static async Task<OwnerFence> World(PgScope pg, string instance = "a") =>
        await pg.Store.AcquireOwnerAsync("world", instance, new AcquireOptions(LeaseMs: 600_000));

    [PgFact]
    public async Task A_batch_is_journaled_then_applied_then_acknowledged()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        var operation = await pg.Store.EnqueueAsync("characters", [Up("1", """{"name":"a"}""")], ["gone"], fence);
        Assert.Equal(OperationState.Pending, operation.State);
        Assert.Equal("world", operation.OwnerRole);
        Assert.Equal(fence.Epoch, operation.OwnerEpoch);
        Assert.Equal(0, await pg.Store.RowCountAsync("characters"));

        var applied = await pg.Store.ApplyOperationAsync(operation.OperationId, "characters", fence);
        Assert.Equal(OperationState.Applied, applied!.State);
        Assert.NotNull(applied.AppliedAt);
        Assert.Equal(1, await pg.Store.RowCountAsync("characters"));
        Assert.Single(await pg.Store.ListOperationsAsync(fence));

        await pg.Store.AcknowledgeOperationAsync(operation.OperationId, "characters", fence);
        Assert.Empty(await pg.Store.ListOperationsAsync(fence));
    }

    [PgFact]
    public async Task Reconcile_applies_and_removes_in_one_step()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        var operation = await pg.Store.EnqueueAsync("items", [Up("1", "1"), Up("2", "2")], [], fence);
        await pg.Store.ReconcileOperationAsync(operation.OperationId, "items", fence);
        Assert.Equal(2, await pg.Store.RowCountAsync("items"));
        Assert.Empty(await pg.Store.ListOperationsAsync(fence));
        // Already gone: a harmless no-op, as for a late worker.
        Assert.Null(await pg.Store.ReconcileOperationAsync(operation.OperationId, "items", fence));
        Assert.Null(await pg.Store.ApplyOperationAsync(operation.OperationId, "items", fence));
    }

    [PgFact]
    public async Task Only_one_batch_per_table_may_be_open()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        var first = await pg.Store.EnqueueAsync("items", [Up("1", "1")], [], fence);
        var error = await Fails<PersistenceException>(pg.Store.EnqueueAsync("items", [Up("2", "2")], [], fence));
        Assert.Equal(PersistenceCodes.OperationPending, error.Code);
        await pg.Store.EnqueueAsync("skills", [Up("1", "1")], [], fence); // another table is fine
        await pg.Store.ReconcileOperationAsync(first.OperationId, "items", fence);
        await pg.Store.EnqueueAsync("items", [Up("2", "2")], [], fence);
    }

    [PgFact]
    public async Task Operations_belong_to_their_owner_role()
    {
        await using var pg = await PgScope.CreateAsync();
        var world = await World(pg);
        var wallet = await pg.Store.AcquireOwnerAsync("wallet", "w", new AcquireOptions(LeaseMs: 600_000));
        var operation = await pg.Store.EnqueueAsync("items", [Up("1", "1")], [], world);
        var error = await Fails<PersistenceException>(pg.Store.ApplyOperationAsync(operation.OperationId, "items", wallet));
        Assert.Equal(PersistenceCodes.OperationOwnerMismatch, error.Code);
        Assert.Empty(await pg.Store.ListOperationsAsync(wallet));
        Assert.Single(await pg.Store.ListAllOperationsAsync());
    }

    [PgFact]
    public async Task The_expected_table_is_checked()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        var operation = await pg.Store.EnqueueAsync("items", [Up("1", "1")], [], fence);
        await Fails<InvalidOperationException>(pg.Store.ApplyOperationAsync(operation.OperationId, "skills", fence));
        await Fails<InvalidOperationException>(pg.Store.ReconcileOperationAsync(operation.OperationId, "skills", fence));
    }

    [PgFact]
    public async Task A_batch_cannot_be_acknowledged_before_it_is_applied_or_when_missing()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        var operation = await pg.Store.EnqueueAsync("items", [Up("1", "1")], [], fence);
        await Fails<InvalidOperationException>(pg.Store.AcknowledgeOperationAsync(operation.OperationId, "items", fence));
        var missing = await Fails<PersistenceException>(pg.Store.AcknowledgeOperationAsync(999_999, "items", fence));
        Assert.Equal(PersistenceCodes.OperationNotFound, missing.Code);
    }

    [PgFact]
    public async Task Recovery_replays_this_owners_journal_in_order_and_keeps_it_until_acknowledged()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        await pg.Store.EnqueueAsync("items", [Up("1", """{"v":1}""")], [], fence);
        await pg.Store.EnqueueAsync("skills", [Up("1", """{"v":2}""")], [], fence);

        var recovered = await pg.Store.RecoverOperationsAsync(fence);
        Assert.Equal(["items", "skills"], recovered.Select(o => o.Table));
        Assert.All(recovered, o => Assert.Equal(OperationState.Applied, o.State));
        Assert.Equal(1, await pg.Store.RowCountAsync("items"));
        Assert.Equal(2, (await pg.Store.ListOperationsAsync(fence)).Count);

        var acknowledged = await pg.Store.AcknowledgeRecoveredAsync(recovered.Select(o => o.OperationId), fence);
        Assert.Equal(2, acknowledged.Count);
        Assert.Empty(await pg.Store.ListOperationsAsync(fence));
    }

    [PgFact]
    public async Task Recovery_ignores_other_owners_and_acknowledging_needs_applied_batches()
    {
        await using var pg = await PgScope.CreateAsync();
        var world = await World(pg);
        var wallet = await pg.Store.AcquireOwnerAsync("wallet", "w", new AcquireOptions(LeaseMs: 600_000));
        var mine = await pg.Store.EnqueueAsync("items", [Up("1", "1")], [], world);
        await pg.Store.EnqueueAsync("walletAuthorityState", [Up("1", "1")], [], wallet);

        Assert.Single(await pg.Store.RecoverOperationsAsync(world));
        Assert.Equal(0, await pg.Store.RowCountAsync("walletAuthorityState"));
        var missing = await Fails<PersistenceException>(pg.Store.AcknowledgeRecoveredAsync([12345], world));
        Assert.Equal(PersistenceCodes.OperationNotFound, missing.Code);
        var pending = (await pg.Store.ListAllOperationsAsync()).Single(o => o.OwnerRole == "wallet");
        var foreign = await Fails<PersistenceException>(pg.Store.AcknowledgeRecoveredAsync([pending.OperationId], world));
        Assert.Equal(PersistenceCodes.OperationOwnerMismatch, foreign.Code);
        await pg.Store.AcknowledgeRecoveredAsync([mine.OperationId], world);
    }

    [PgFact]
    public async Task Maintenance_is_blocked_by_another_owners_journal()
    {
        await using var pg = await PgScope.CreateAsync();
        var world = await World(pg);
        await pg.Store.EnqueueAsync("items", [Up("1", "1")], [], world);
        var maintenance = await pg.Store.AcquireOwnerAsync("maintenance", "m", new AcquireOptions(ForceExclusive: true));

        var blocked = await Fails<PersistenceException>(pg.Store.AssertNoForeignOperationsAsync(maintenance));
        Assert.Equal(PersistenceCodes.ForeignOperationsPending, blocked.Code);
        Assert.Contains("world:items#", blocked.Message);

        var foreign = (await pg.Store.ListAllOperationsAsync()).Single();
        await pg.ExecuteAsync($"DELETE FROM {pg.Q("_persistence_outbox")} WHERE operation_id = {foreign.OperationId}");
        await pg.Store.AssertNoForeignOperationsAsync(maintenance);
    }

    [PgFact]
    public async Task Foreign_inspection_requires_the_exclusive_maintenance_role()
    {
        await using var pg = await PgScope.CreateAsync();
        var world = await World(pg);
        var error = await Fails<PersistenceException>(pg.Store.AssertNoForeignOperationsAsync(world));
        Assert.Equal(PersistenceCodes.MaintenanceFenceRequired, error.Code);
    }

    [PgFact]
    public async Task Bad_input_never_reaches_the_journal()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        await Fails<ArgumentException>(pg.Store.EnqueueAsync("items", [Up("1", "{not json")], [], fence));
        await Fails<ArgumentException>(pg.Store.EnqueueAsync("items", [Up("1", "")], [], fence));
        await Fails<ArgumentException>(pg.Store.EnqueueAsync("_persistence_outbox", [], [], fence));
        await Fails<ArgumentException>(pg.Store.EnqueueAsync("bad-name", [], [], fence));
        await Fails<ArgumentException>(pg.Store.EnqueueAsync("items", [], [null!], fence));
        Assert.Empty(await pg.Store.ListAllOperationsAsync());
    }

    [PgFact]
    public async Task Documents_survive_the_journal_unicode_big_numbers_and_depth()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        var deep = string.Concat(Enumerable.Repeat("[", 100)) + "1" + string.Concat(Enumerable.Repeat("]", 100));
        var docs = new[]
        {
            Up("unicode", "\"日本語 \\ud83d\\ude00 \\u001f end\""),
            Up("big", """{"n":9007199254740993,"f":0.1,"e":1e300,"neg":-0}"""),
            Up("deep", deep),
            Up("key\u001fsep", "null"),
            Up("empty", "{}"),
        };
        var operation = await pg.Store.EnqueueAsync("items", docs, [], fence);
        var stored = (await pg.Store.GetOperationAsync(operation.OperationId, fence))!;
        Assert.Equal(docs.Select(d => d.Key), stored.Upserts.Select(u => u.Key));
        Assert.Contains("9007199254740993", stored.Upserts[1].Json);
        Assert.Equal(deep, stored.Upserts[2].Json);
        await pg.Store.ReconcileOperationAsync(operation.OperationId, "items", fence);
        var rows = (await pg.Store.LoadRowsAsync("items")).ToDictionary(r => r.Key, r => r.Json);
        Assert.Contains("9007199254740993", rows["big"]);
        Assert.Contains("key\u001fsep", rows.Keys);
    }

    [PgFact]
    public async Task A_json_string_with_a_nul_escape_is_reported_as_unsupported()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await World(pg);
        var error = await Fails<PersistenceException>(pg.Store.EnqueueAsync("items", [Up("1", "\"a\\u0000b\"")], [], fence));
        Assert.Equal(PersistenceCodes.UnsupportedJson, error.Code);
        Assert.Empty(await pg.Store.ListAllOperationsAsync());
    }
}
