using Eve.Corporations;
using Eve.Services.Market;
using Eve.Wallet;

namespace Eve.Market.Tests;

/// <summary>
/// M3 (market-decisions Q7): expiry as a durable job on a fake clock (TradeRig.Now). The job refunds and returns the items once and announces
/// once; a second run or sweep does nothing; a restart resumes the expiry; a modification moves it; a cancel removes it. The minute sweep
/// is a job too, so the assertions are on the effects (balances, items, announcements), not on how many jobs ran.
/// </summary>
public class ExpiryTests
{
    private const long Day = 86_400_000;
    private const long Grace = MarketExpiry.GraceMs;

    [PgFact]
    public async Task An_expired_buy_is_refunded_once_by_the_job_and_announced_once()
    {
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Expiring Buyer");
        var placedAt = r.Now;
        await r.Trading.PlaceAsync(r.Buy(actor, 10, 5, days: 1), "expiry-buy");
        var escrowed = (await r.WalletStore.GetAsync(actor))!.Isk;
        r.Now = placedAt + Day + Grace - 1;
        await r.Scheduler.RunDueAsync();   // not yet: the expiry takes effect one second after the expiry instant
        Assert.Equal(escrowed, (await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Empty(r.Announcements);
        r.Now = placedAt + Day + Grace;
        await r.Scheduler.RunDueAsync();
        Assert.Equal(escrowed + 50m, (await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Equal("Expired", Assert.Single(r.Announcements).ChangedOrderReason);
        await r.Scheduler.RunDueAsync();
        Assert.Empty(await r.Expiry.SweepAsync());
        Assert.Equal(escrowed + 50m, (await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Single(r.Announcements);
    }

    [PgFact]
    public async Task An_expired_sell_returns_its_stack_once()
    {
        await using var r = await TradeRig.Create();
        var seller = await r.Actor("Expiring Seller");
        var item = await r.Stock(seller, 5);
        var placedAt = r.Now;
        await r.Trading.PlaceAsync(r.Sell(seller, item, 10, 5, days: 1), "expiry-sell");
        Assert.Empty(await r.Items.InLocationAsync(r.Station.Id, seller));
        r.Now = placedAt + Day + Grace;
        await r.Scheduler.RunDueAsync();
        Assert.Equal(5, Assert.Single(await r.Items.InLocationAsync(r.Station.Id, seller)).Quantity);
        await r.Scheduler.RunDueAsync();
        Assert.Empty(await r.Expiry.SweepAsync());
        Assert.Equal(5, Assert.Single(await r.Items.InLocationAsync(r.Station.Id, seller)).Quantity);
        Assert.Equal("Expired", Assert.Single(r.Announcements).ChangedOrderReason);
    }

    [PgFact]
    public async Task A_sweep_closes_an_order_whose_job_was_lost_and_a_second_sweep_does_nothing()
    {
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Lost Job Buyer");
        var placedAt = r.Now;
        await r.Trading.PlaceAsync(r.Buy(actor, 10, 5, days: 1), "lost-job");
        var orderId = (await r.Trading.OpenRestingExpiriesAsync()).Single().OrderId;
        Assert.True(await r.Scheduler.CancelAsync(MarketExpiryWorker.OrderKind, MarketExpiryWorker.OrderKey(orderId)));
        var escrowed = (await r.WalletStore.GetAsync(actor))!.Isk;
        r.Now = placedAt + Day + Grace;
        Assert.Equal(orderId, Assert.Single(await r.Expiry.SweepAsync()).OrderId);
        Assert.Equal(escrowed + 50m, (await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Empty(await r.Expiry.SweepAsync());
        Assert.Equal(escrowed + 50m, (await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Single(r.Announcements);
    }

    [PgFact]
    public async Task A_restart_gives_the_open_order_its_job_back_and_the_expiry_resumes()
    {
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Restarted Buyer");
        var placedAt = r.Now;
        await r.Trading.PlaceAsync(r.Buy(actor, 10, 5, days: 1), "restart-place");
        var escrowed = (await r.WalletStore.GetAsync(actor))!.Isk;
        // the host goes down with the job still waiting; the restart schedules the order again
        var orderId = (await r.Trading.OpenRestingExpiriesAsync()).Single().OrderId;
        Assert.True(await r.Scheduler.CancelAsync(MarketExpiryWorker.OrderKind, MarketExpiryWorker.OrderKey(orderId)));
        r.Now = placedAt + Day / 2;
        await r.Restart();
        Assert.NotNull(await r.Scheduler.FindAsync(MarketExpiryWorker.OrderKind, MarketExpiryWorker.OrderKey(orderId)));
        await r.Scheduler.RunDueAsync();   // before the expiry nothing closes
        Assert.Equal(escrowed, (await r.WalletStore.GetAsync(actor))!.Isk);
        r.Now = placedAt + Day + Grace;
        await r.Scheduler.RunDueAsync();
        Assert.Equal(escrowed + 50m, (await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Equal("Expired", Assert.Single(r.Announcements).ChangedOrderReason);
    }

    [PgFact]
    public async Task An_overdue_order_expires_at_once_after_a_restart()
    {
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Overdue Buyer");
        var placedAt = r.Now;
        await r.Trading.PlaceAsync(r.Buy(actor, 10, 5, days: 1), "overdue-place");
        var escrowed = (await r.WalletStore.GetAsync(actor))!.Isk;
        r.Now = placedAt + 3 * Day;   // the host was down for two days after the expiry
        await r.Restart();
        await r.Scheduler.RunDueAsync();
        Assert.Equal(escrowed + 50m, (await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Single(r.Announcements);
        await r.Restart();
        await r.Scheduler.RunDueAsync();
        Assert.Equal(escrowed + 50m, (await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Single(r.Announcements);
    }

    [PgFact]
    public async Task A_modification_moves_the_expiry_to_the_restarted_duration()
    {
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Renewing Buyer");
        var placedAt = r.Now;
        await r.Trading.PlaceAsync(r.Buy(actor, 10, 5, days: 1), "move-place");
        var orderId = (await r.Trading.OpenRestingExpiriesAsync()).Single().OrderId;
        r.Now = placedAt + 20 * 3_600_000L;
        await r.Trading.ModifyAsync(actor, orderId, 11m, "move-modify");
        var modifiedAt = r.Now;
        var escrowed = (await r.WalletStore.GetAsync(actor))!.Isk;
        r.Now = placedAt + Day + Grace;   // the original expiry passes: the renewed order is still open
        await r.Scheduler.RunDueAsync();
        Assert.Equal(escrowed, (await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.DoesNotContain(r.Announcements, a => a.ChangedOrderReason == "Expired");
        r.Now = modifiedAt + Day + Grace;
        await r.Scheduler.RunDueAsync();
        Assert.Equal(escrowed + 55m, (await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Single(r.Announcements, a => a.ChangedOrderReason == "Expired");
    }

    [PgFact]
    public async Task A_cancel_removes_the_expiry_job()
    {
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Cancelling Buyer");
        var placedAt = r.Now;
        var placed = await r.Trading.PlaceAsync(r.Buy(actor, 10, 5, days: 1), "cancel-place");
        var orderId = Assert.IsType<long>(placed.OrderId);
        await r.Trading.CancelAsync(actor, orderId, "cancel-now");
        var refunded = (await r.WalletStore.GetAsync(actor))!.Isk;
        Assert.Null(await r.Scheduler.FindAsync(MarketExpiryWorker.OrderKind, MarketExpiryWorker.OrderKey(orderId)));
        r.Now = placedAt + Day + Grace;
        await r.Scheduler.RunDueAsync();
        Assert.Empty(await r.Expiry.SweepAsync());
        Assert.Equal(refunded, (await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Empty(r.Announcements); // a cancel is announced by its own call, never by the expiry
    }

    [PgFact]
    public async Task A_read_triggered_sweep_runs_at_most_once_a_second()
    {
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Reading Buyer");
        var placedAt = r.Now;
        await r.Trading.PlaceAsync(r.Buy(actor, 10, 5, days: 1), "throttle-place");
        r.Now = placedAt + Day + 500;   // a sweep that runs: nothing is due before the grace
        Assert.Empty(await r.Expiry.SweepAsync(readTriggered: true));
        r.Now = placedAt + Day + 1_200;   // due, but the last sweep ran 700 ms ago: refused
        Assert.Empty(await r.Expiry.SweepAsync(readTriggered: true));
        Assert.Empty(r.Announcements);
        r.Now = placedAt + Day + 1_600;   // 1.1 s after the last sweep that ran
        Assert.Single(await r.Expiry.SweepAsync(readTriggered: true));
        Assert.Single(r.Announcements);
    }

    [PgFact]
    public async Task A_corporate_order_that_expires_is_announced_to_the_corporation_rights_holders()
    {
        await using var r = await TradeRig.Create();
        var founder = await r.Actor("Corp Founder", 10_000_000m);
        var corp = await r.Corporations.CreateAsync(founder, new("Expiry Works", "EXPWK", "", "", 0), "expiry-corp");
        await using (var c = await r.Source.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await r.Wallets.TransferAsync(c, tx, new WalletAccount(founder), new WalletAccount(corp.Id, 1006), 1000m, 10, "Deposit", r.Now, "deposit", "test");
            await tx.CommitAsync();
        }
        await r.Corporations.SetAccountKeyAsync(founder, 1006);
        // a second member with the Trader role: the client's corporate order rights (buyThisTypeWindow.py:324)
        var trader = await r.Actor("Trading Member");
        var application = await r.Corporations.ApplyAsync(trader, corp.Id, "Please accept.");
        await r.Corporations.RespondAsync(founder, application, 6, "Please accept.", "Welcome");
        await r.Corporations.RespondAsync(trader, application, 2, "Please accept.", "Accepted");
        var joined = (await r.Corporations.MemberAsync(trader))!;
        await r.Corporations.UpdateMemberAsync(founder, joined with { Masks = new RoleMasks(CorporationRoles.Trader) });
        // a member with no market role, and a character outside the corporation
        var bystander = await r.Actor("Bystander");
        var application2 = await r.Corporations.ApplyAsync(bystander, corp.Id, "Please accept.");
        await r.Corporations.RespondAsync(founder, application2, 6, "Please accept.", "Welcome");
        await r.Corporations.RespondAsync(bystander, application2, 2, "Please accept.", "Accepted");
        var outsider = await r.Actor("Outsider");
        var placedAt = r.Now;
        await r.Trading.PlaceAsync(r.Buy(founder, 10, 5, days: 1, corp: true), "corp-expiry");
        IReadOnlyList<long> holders;
        await using (var c = await r.Source.OpenConnectionAsync())
            holders = await r.Corporations.MarketRightsHoldersAsync(c, null, corp.Id, r.Station.Id);
        Assert.Contains(founder, holders);
        Assert.Contains(trader, holders);
        Assert.DoesNotContain(bystander, holders);
        Assert.DoesNotContain(outsider, holders);
        r.Now = placedAt + Day + Grace;
        await r.Scheduler.RunDueAsync();
        var recipients = Assert.Single(r.Announcements).ChangedOrders.Single().RecipientCharacterIds;
        Assert.Contains(founder, recipients);
        Assert.Contains(trader, recipients);
        Assert.DoesNotContain(bystander, recipients);
        Assert.DoesNotContain(outsider, recipients);
    }
}
