using Npgsql;

namespace Eve.Identity.Tests;

/// <summary>The purchase rules of an account's Omega end time and its extra training queues, and their writes inside a transaction.</summary>
public class EntitlementRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Horizon = SubscriptionPolicy.PurchaseHorizon;

    [Fact]
    public void A_purchase_counts_from_the_later_of_the_stored_end_and_now()
    {
        var lapsed = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(Now.AddDays(30), SubscriptionPolicy.Extend(lapsed, Now, TimeSpan.FromDays(30)));

        var running = Now.AddDays(10);
        Assert.Equal(running.AddDays(90), SubscriptionPolicy.Extend(running, Now, TimeSpan.FromDays(90)));
    }

    [Fact]
    public void A_purchase_is_capped_at_the_horizon_and_never_moves_an_end_time_earlier()
    {
        var nearHorizon = Horizon.AddDays(-3);
        Assert.Equal(Horizon, SubscriptionPolicy.Extend(nearHorizon, Now, TimeSpan.FromDays(365)));

        // an end at or past the horizon is kept exactly as it is
        Assert.Equal(Horizon, SubscriptionPolicy.Extend(Horizon, Now, TimeSpan.FromDays(30)));
        var beyond = Horizon.AddYears(5);
        Assert.Equal(beyond, SubscriptionPolicy.Extend(beyond, Now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void A_purchase_needs_a_positive_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SubscriptionPolicy.Extend(Now, Now, TimeSpan.Zero));
    }

    [Fact]
    public void A_free_queue_is_opened_from_now_before_a_running_one_is_extended()
    {
        var stored = new Dictionary<int, DateTimeOffset> { [2] = Now.AddDays(5) };
        var (slot, endsAt) = MultipleTrainingRules.Plan(stored, null, Now, TimeSpan.FromDays(30));
        Assert.Equal(3, slot);
        Assert.Equal(Now.AddDays(30), endsAt);

        // a queue whose end time has passed is free again
        var lapsed = new Dictionary<int, DateTimeOffset> { [2] = Now.AddDays(-1), [3] = Now.AddDays(5) };
        Assert.Equal((2, Now.AddDays(30)), MultipleTrainingRules.Plan(lapsed, null, Now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void With_both_queues_running_the_one_that_ends_first_is_extended()
    {
        var stored = new Dictionary<int, DateTimeOffset> { [2] = Now.AddDays(20), [3] = Now.AddDays(7) };
        Assert.Equal((3, Now.AddDays(37)), MultipleTrainingRules.Plan(stored, null, Now, TimeSpan.FromDays(30)));

        var tied = new Dictionary<int, DateTimeOffset> { [2] = Now.AddDays(7), [3] = Now.AddDays(7) };
        Assert.Equal((2, Now.AddDays(37)), MultipleTrainingRules.Plan(tied, null, Now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void A_queue_the_client_names_is_extended_or_opened_from_now()
    {
        var stored = new Dictionary<int, DateTimeOffset> { [2] = Now.AddDays(20) };
        Assert.Equal((2, Now.AddDays(50)), MultipleTrainingRules.Plan(stored, 2, Now, TimeSpan.FromDays(30)));
        Assert.Equal((3, Now.AddDays(30)), MultipleTrainingRules.Plan(stored, 3, Now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void There_are_only_two_extra_queues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MultipleTrainingRules.Plan(new Dictionary<int, DateTimeOffset>(), 4, Now, TimeSpan.FromDays(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MultipleTrainingRules.Plan(new Dictionary<int, DateTimeOffset>(), 1, Now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void A_queue_already_at_the_horizon_is_refused_rather_than_granted_as_nothing()
    {
        var stored = new Dictionary<int, DateTimeOffset> { [2] = Horizon, [3] = Horizon };
        Assert.Throws<InvalidOperationException>(() => MultipleTrainingRules.Plan(stored, null, Now, TimeSpan.FromDays(30)));
    }

    [PgFact]
    public async Task A_rolled_back_purchase_leaves_the_end_time_and_the_queues_as_they_were()
    {
        await using var schema = await PgSchema.CreateAsync();
        var accounts = new PgAccountDirectory(schema.Source, new AccountOptions { AutoCreate = true }, schema.Name);
        var login = Assert.IsType<LoggedIn>(await accounts.LoginAsync(
            new LoginAttempt("rollback-buyer", new byte[] { 4, 2 }, "en", "127.0.0.1"), CancellationToken.None));
        var accountId = login.AccountId;
        accounts.SetSubscriptionEndsAt(accountId, Now.AddDays(10));
        var stored = await accounts.SubscriptionEndsAtAsync(accountId, CancellationToken.None);

        await using (var connection = await schema.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await accounts.ExtendSubscriptionAsync(connection, transaction, accountId, TimeSpan.FromDays(30), Now, CancellationToken.None);
            await accounts.ExtendTrainingSlotAsync(connection, transaction, accountId, null, TimeSpan.FromDays(30), Now, CancellationToken.None);
            await transaction.RollbackAsync();
        }

        Assert.Equal(stored, await accounts.SubscriptionEndsAtAsync(accountId, CancellationToken.None));
        Assert.Empty(await accounts.TrainingSlotsAsync(accountId, CancellationToken.None));
    }

    [PgFact]
    public async Task A_committed_purchase_extends_the_end_time_and_fills_the_queues_in_order()
    {
        await using var schema = await PgSchema.CreateAsync();
        var accounts = new PgAccountDirectory(schema.Source, new AccountOptions { AutoCreate = true }, schema.Name);
        var accountId = Assert.IsType<LoggedIn>(await accounts.LoginAsync(
            new LoginAttempt("committed-buyer", new byte[] { 5, 5 }, "en", "127.0.0.1"), CancellationToken.None)).AccountId;
        accounts.SetSubscriptionEndsAt(accountId, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await using (var connection = await schema.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var endsAt = await accounts.ExtendSubscriptionAsync(connection, transaction, accountId, TimeSpan.FromDays(90), Now, CancellationToken.None);
            Assert.Equal(Now.AddDays(90), endsAt);
            Assert.Equal(2, (await accounts.ExtendTrainingSlotAsync(connection, transaction, accountId, null, TimeSpan.FromDays(30), Now, CancellationToken.None)).Slot);
            Assert.Equal(3, (await accounts.ExtendTrainingSlotAsync(connection, transaction, accountId, null, TimeSpan.FromDays(30), Now, CancellationToken.None)).Slot);
            await transaction.CommitAsync();
        }

        Assert.Equal(Now.AddDays(90), await accounts.SubscriptionEndsAtAsync(accountId, CancellationToken.None));
        var slots = await accounts.TrainingSlotsAsync(accountId, CancellationToken.None);
        Assert.Equal(Now.AddDays(30), slots[2]);
        Assert.Equal(Now.AddDays(30), slots[3]);
        Assert.Equal(2, accounts.ActiveTrainingSlotCount(accountId, Now.AddDays(1)));
        Assert.Equal(0, accounts.ActiveTrainingSlotCount(accountId, Now.AddDays(31)));
    }
}
