namespace Eve.GameStore.Tests;

public class OwnerTests
{
    private static async Task<PersistenceException> Fails(Task task) => await Assert.ThrowsAsync<PersistenceException>(() => task);

    [PgFact]
    public async Task The_first_acquire_gets_epoch_one_with_a_lease()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await pg.Store.AcquireOwnerAsync("world", "a", new AcquireOptions(LeaseMs: 5_000));
        Assert.Equal(1, fence.Epoch);
        Assert.Equal(pg.Now + 5_000, fence.LeaseExpiresAt);
        Assert.Equal(fence with { LeaseExpiresAt = null }, (await pg.Store.AssertOwnerAsync(fence)) with { LeaseExpiresAt = null });
    }

    [PgFact]
    public async Task The_same_live_instance_keeps_its_epoch_when_it_acquires_again()
    {
        await using var pg = await PgScope.CreateAsync();
        var first = await pg.Store.AcquireOwnerAsync("world", "a");
        var second = await pg.Store.AcquireOwnerAsync("world", "a");
        Assert.Equal(first.Epoch, second.Epoch);
    }

    [PgFact]
    public async Task Another_instance_is_refused_while_the_lease_is_live()
    {
        await using var pg = await PgScope.CreateAsync();
        await pg.Store.AcquireOwnerAsync("world", "a", new AcquireOptions(LeaseMs: 5_000));
        var error = await Fails(pg.Store.AcquireOwnerAsync("world", "b"));
        Assert.Equal(PersistenceCodes.OwnerActive, error.Code);
        Assert.Equal("a", error.Details["ownerInstanceId"]);
    }

    [PgFact]
    public async Task Taking_over_an_expired_lease_advances_the_epoch_and_fences_the_old_owner_out()
    {
        await using var pg = await PgScope.CreateAsync();
        var old = await pg.Store.AcquireOwnerAsync("world", "a", new AcquireOptions(LeaseMs: 1_000));
        pg.Advance(1_001);
        var next = await pg.Store.AcquireOwnerAsync("world", "b");
        Assert.Equal(2, next.Epoch);

        var stale = await Fails(pg.Store.AssertOwnerAsync(old));
        Assert.Equal(PersistenceCodes.OwnerStale, stale.Code);

        var enqueue = await Fails(pg.Store.EnqueueAsync("characters", [], [], old));
        Assert.Equal(PersistenceCodes.OwnerStale, enqueue.Code);
        Assert.False(await pg.Store.TableExistsAsync("characters"));
    }

    [PgFact]
    public async Task A_lease_that_ran_out_is_stale_even_if_nobody_took_over()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await pg.Store.AcquireOwnerAsync("world", "a", new AcquireOptions(LeaseMs: 1_000));
        pg.Advance(1_000);
        Assert.Equal(PersistenceCodes.OwnerStale, (await Fails(pg.Store.AssertOwnerAsync(fence))).Code);
    }

    [PgFact]
    public async Task Force_takeover_fences_out_a_live_owner()
    {
        await using var pg = await PgScope.CreateAsync();
        var old = await pg.Store.AcquireOwnerAsync("world", "a", new AcquireOptions(LeaseMs: 60_000));
        var next = await pg.Store.AcquireOwnerAsync("world", "b", new AcquireOptions(ForceTakeover: true));
        Assert.Equal(2, next.Epoch);
        Assert.Equal(PersistenceCodes.OwnerStale, (await Fails(pg.Store.AssertOwnerAsync(old))).Code);
    }

    [PgFact]
    public async Task Renewing_extends_the_lease_and_only_for_a_valid_fence()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await pg.Store.AcquireOwnerAsync("world", "a", new AcquireOptions(LeaseMs: 1_000));
        pg.Advance(600);
        var renewed = await pg.Store.RenewOwnerAsync(fence, 1_000);
        Assert.Equal(pg.Now + 1_000, renewed.LeaseExpiresAt);
        pg.Advance(600);
        await pg.Store.AssertOwnerAsync(fence); // still live thanks to the renewal

        pg.Advance(500);
        Assert.Equal(PersistenceCodes.OwnerStale, (await Fails(pg.Store.RenewOwnerAsync(fence, 1_000))).Code);
    }

    [PgFact]
    public async Task Releasing_lets_another_instance_take_the_role_at_once()
    {
        await using var pg = await PgScope.CreateAsync();
        var first = await pg.Store.AcquireOwnerAsync("world", "a", new AcquireOptions(LeaseMs: 60_000));
        await pg.Store.ReleaseOwnerAsync(first);
        Assert.Equal(PersistenceCodes.OwnerStale, (await Fails(pg.Store.AssertOwnerAsync(first))).Code);
        var second = await pg.Store.AcquireOwnerAsync("world", "b");
        Assert.Equal(2, second.Epoch);
    }

    [PgFact]
    public async Task Independent_roles_do_not_interfere()
    {
        await using var pg = await PgScope.CreateAsync();
        var world = await pg.Store.AcquireOwnerAsync("world", "a");
        var wallet = await pg.Store.AcquireOwnerAsync("wallet", "b");
        await pg.Store.AssertOwnerAsync(world);
        await pg.Store.AssertOwnerAsync(wallet);
    }

    [PgFact]
    public async Task An_exclusive_owner_conflicts_with_every_other_live_owner()
    {
        await using var pg = await PgScope.CreateAsync();
        await pg.Store.AcquireOwnerAsync("world", "a", new AcquireOptions(LeaseMs: 60_000));
        var error = await Fails(pg.Store.AcquireOwnerAsync("maintenance", "m"));
        Assert.Equal(PersistenceCodes.OwnerConflict, error.Code);
    }

    [PgFact]
    public async Task Other_owners_are_refused_while_an_exclusive_owner_is_live()
    {
        await using var pg = await PgScope.CreateAsync();
        await pg.Store.AcquireOwnerAsync("maintenance", "m", new AcquireOptions(LeaseMs: 60_000));
        var error = await Fails(pg.Store.AcquireOwnerAsync("world", "a"));
        Assert.Equal(PersistenceCodes.OwnerConflict, error.Code);
    }

    [PgFact]
    public async Task Force_exclusive_deactivates_the_conflicting_owners()
    {
        await using var pg = await PgScope.CreateAsync();
        var world = await pg.Store.AcquireOwnerAsync("world", "a", new AcquireOptions(LeaseMs: 60_000));
        var wallet = await pg.Store.AcquireOwnerAsync("wallet", "b", new AcquireOptions(LeaseMs: 60_000));
        var maintenance = await pg.Store.AcquireOwnerAsync("maintenance", "m", new AcquireOptions(ForceExclusive: true));
        await pg.Store.AssertOwnerAsync(maintenance);
        Assert.Equal(PersistenceCodes.OwnerStale, (await Fails(pg.Store.AssertOwnerAsync(world))).Code);
        Assert.Equal(PersistenceCodes.OwnerStale, (await Fails(pg.Store.AssertOwnerAsync(wallet))).Code);
    }

    [PgFact]
    public async Task Bad_arguments_are_rejected_with_their_codes()
    {
        await using var pg = await PgScope.CreateAsync();
        Assert.Equal(PersistenceCodes.OwnerRoleInvalid, (await Fails(pg.Store.AcquireOwnerAsync("", "a"))).Code);
        Assert.Equal(PersistenceCodes.OwnerRoleInvalid, (await Fails(pg.Store.AcquireOwnerAsync("bad role", "a"))).Code);
        Assert.Equal(PersistenceCodes.OwnerRoleInvalid, (await Fails(pg.Store.AcquireOwnerAsync(new string('r', 129), "a"))).Code);
        Assert.Equal(PersistenceCodes.OwnerInstanceInvalid, (await Fails(pg.Store.AcquireOwnerAsync("world", " "))).Code);
        Assert.Equal(PersistenceCodes.OwnerInstanceInvalid, (await Fails(pg.Store.AcquireOwnerAsync("world", new string('i', 321)))).Code);
        Assert.Equal(PersistenceCodes.OwnerLeaseInvalid, (await Fails(pg.Store.AcquireOwnerAsync("world", "a", new AcquireOptions(LeaseMs: 999)))).Code);
        Assert.Equal(PersistenceCodes.OwnerEpochInvalid, (await Fails(pg.Store.AssertOwnerAsync(new OwnerFence("world", 0, "a")))).Code);
        Assert.Equal(PersistenceCodes.OwnerFenceRequired, (await Fails(pg.Store.AssertOwnerAsync(null!))).Code);
    }

    [PgFact]
    public async Task A_fence_with_the_wrong_epoch_or_instance_is_stale()
    {
        await using var pg = await PgScope.CreateAsync();
        var fence = await pg.Store.AcquireOwnerAsync("world", "a");
        Assert.Equal(PersistenceCodes.OwnerStale, (await Fails(pg.Store.AssertOwnerAsync(fence with { Epoch = 2 }))).Code);
        Assert.Equal(PersistenceCodes.OwnerStale, (await Fails(pg.Store.AssertOwnerAsync(fence with { InstanceId = "b" }))).Code);
        Assert.Equal(PersistenceCodes.OwnerStale, (await Fails(pg.Store.AssertOwnerAsync(fence with { OwnerRole = "wallet" }))).Code);
    }

    [PgFact]
    public async Task Of_many_concurrent_acquirers_exactly_one_wins()
    {
        await using var pg = await PgScope.CreateAsync();
        var attempts = Enumerable.Range(0, 12).Select(async i =>
        {
            try
            {
                var fence = await pg.Store.AcquireOwnerAsync("world", $"instance-{i}", new AcquireOptions(LeaseMs: 60_000));
                return (Fence: (OwnerFence?)fence, Error: (PersistenceException?)null);
            }
            catch (PersistenceException ex)
            {
                return (Fence: (OwnerFence?)null, Error: (PersistenceException?)ex);
            }
        });
        var results = await Task.WhenAll(attempts);
        Assert.Single(results, r => r.Fence is not null);
        Assert.All(results.Where(r => r.Fence is null), r => Assert.Equal(PersistenceCodes.OwnerActive, r.Error!.Code));
    }

    [PgFact]
    public async Task A_takeover_waits_for_an_in_flight_publish_and_then_fences_out_later_ones()
    {
        await using var pg = await PgScope.CreateAsync();
        var old = await pg.Store.AcquireOwnerAsync("world", "a", new AcquireOptions(LeaseMs: 60_000));
        var writers = Enumerable.Range(0, 20).Select(async i =>
        {
            try
            {
                await pg.Store.ApplyChangesAsync("characters", [new RowUpsert($"k{i}", "1")], [], old);
                return true;
            }
            catch (PersistenceException ex) when (ex.Code == PersistenceCodes.OwnerStale)
            {
                return false;
            }
        }).ToList();
        var takeover = pg.SecondStore().AcquireOwnerAsync("world", "b", new AcquireOptions(ForceTakeover: true));
        await Task.WhenAll(writers.Append(Task.FromResult(true)));
        var next = await takeover;

        var after = await Fails(pg.Store.ApplyChangesAsync("characters", [new RowUpsert("late", "1")], [], old));
        Assert.Equal(PersistenceCodes.OwnerStale, after.Code);
        Assert.Equal(2, next.Epoch);
        Assert.Equal(0, await pg.ScalarAsync<long>($"SELECT count(*) FROM {pg.Q("characters")} WHERE key = 'late'"));
    }
}
