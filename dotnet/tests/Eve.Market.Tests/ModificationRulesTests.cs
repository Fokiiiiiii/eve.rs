using Eve.Wallet;

namespace Eve.Market.Tests;

/// <summary>
/// The rules M2 settles (market-decisions.md Q3 to Q5, Q12 and Q15): the modification delay, the duration reset, the price ticks, the matching
/// order and the stale-view refusal. Each runs on the trading engine with the rig of the trading tests, on PostgreSQL.
/// </summary>
public class ModificationRulesTests
{
    private const long Day = 86_400_000;

    [PgFact]
    public async Task A_modification_inside_the_delay_is_refused_with_the_seconds_left()
    {
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Patient Trader");
        var placed = await r.Trading.PlaceAsync(r.Buy(actor, 10, 5, days: 90), "delay-place");
        var orderId = Assert.IsType<long>(placed.OrderId);
        r.Now += 100_000;
        var refused = await Assert.ThrowsAsync<MarketError>(() => r.Trading.ModifyAsync(actor, orderId, 11m, "delay-early"));
        Assert.Equal("MARKET_MODIFY_TOO_SOON", refused.Code);
        Assert.Equal(200L, Convert.ToInt64(Assert.Single(refused.Arguments).Value));
        Assert.Equal(10m, (await r.Book.BookAsync(r.Station.RegionId, 34, r.Now)).Bids.Single().Price);
        r.Now += MarketRules.ModificationDelayMs - 100_000;
        var changed = await r.Trading.ModifyAsync(actor, orderId, 11m, "delay-ready");
        Assert.Equal(11m, changed.ChangedOrder!.Order.Price);
    }

    [PgFact]
    public async Task A_modification_restarts_the_duration_and_the_issue_time_of_the_order()
    {
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Renewing Trader");
        var placed = await r.Trading.PlaceAsync(r.Buy(actor, 10, 5, days: 30), "reset-place");
        var orderId = Assert.IsType<long>(placed.OrderId);
        r.Now += 20 * Day;
        var modifiedAt = r.Now;
        var changed = await r.Trading.ModifyAsync(actor, orderId, 11m, "reset-modify");
        Assert.Equal(modifiedAt, changed.ChangedOrder!.Order.IssuedMs);
        Assert.Equal(modifiedAt, Assert.Single((await r.Book.BookAsync(r.Station.RegionId, 34, r.Now)).Bids).IssuedMs);
        r.Now = modifiedAt + 30 * Day - 1;
        Assert.Empty(await r.Trading.ExpireDueAsync());
        r.Now = modifiedAt + 30 * Day + MarketExpiry.GraceMs;
        Assert.Single(await r.Trading.ExpireDueAsync());
    }

    [PgFact]
    public async Task A_price_that_is_not_a_tick_is_refused_on_place_and_on_modify()
    {
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Tick Trader");
        var refused = await Assert.ThrowsAsync<MarketError>(() => r.Trading.PlaceAsync(r.Buy(actor, 1234.5m, 1, days: 1), "tick-place"));
        Assert.Equal("MARKET_PRICE_TICK", refused.Code);
        var placed = await r.Trading.PlaceAsync(r.Buy(actor, 1234m, 1, days: 1), "tick-ok");
        r.Now += MarketRules.ModificationDelayMs;
        var modifyRefused = await Assert.ThrowsAsync<MarketError>(() => r.Trading.ModifyAsync(actor, Assert.IsType<long>(placed.OrderId), 12345m, "tick-modify"));
        Assert.Equal("MARKET_PRICE_TICK", modifyRefused.Code);
    }

    [Fact]
    public void Equal_prices_fill_the_order_issued_first_whatever_its_station()
    {
        // Q5: price, then issue time, then order id. The station id is not a tie-break (no source gives one).
        var here = new MarketLocation(60_000_001, 30_000_001, 20_000_001, 10_000_002);
        var older = new MarketOrder(1, 34, false, 10m, 5, 5, 1, -1, new MarketLocation(60_000_002, 30_000_001, 20_000_001, 10_000_002), 1_000, 90);
        var newer = new MarketOrder(2, 34, false, 10m, 5, 5, 1, -1, new MarketLocation(60_000_001, 30_000_001, 20_000_001, 10_000_002), 2_000, 90);
        var fills = MarketMatching.Plan([newer, older], new MarketTopology([]), 34, true, here, 10m, 3, MarketRules.RangeRegion, 1_500);
        Assert.Equal(1, Assert.Single(fills).Order.Id);
    }

    [PgFact]
    public async Task A_modification_on_a_stale_view_is_refused_and_changes_nothing()
    {
        // Q15: the client's oldPrice and issueDate must still be the order's; a stale view is refused, and no escrow moves.
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Stale Trader");
        var placedAt = r.Now;
        var placed = await r.Trading.PlaceAsync(r.Buy(actor, 10, 5, days: 30), "stale-place");
        var orderId = Assert.IsType<long>(placed.OrderId);
        var balance = (await r.WalletStore.GetAsync(actor))!.Isk;
        r.Now += MarketRules.ModificationDelayMs;
        var stalePrice = await Assert.ThrowsAsync<MarketError>(() => r.Trading.ModifyAsync(actor, orderId, 11m, "stale-price", expectedPrice: 9m));
        Assert.Equal("MARKET_ORDER_STALE", stalePrice.Code);
        var staleIssue = await Assert.ThrowsAsync<MarketError>(() => r.Trading.ModifyAsync(actor, orderId, 11m, "stale-issue", expectedPrice: 10m, expectedIssuedMs: placedAt + 1));
        Assert.Equal("MARKET_ORDER_STALE", staleIssue.Code);
        Assert.Equal(balance, (await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Equal(10m, (await r.Book.BookAsync(r.Station.RegionId, 34, r.Now)).Bids.Single().Price);
        var current = await r.Trading.ModifyAsync(actor, orderId, 11m, "stale-ok", expectedPrice: 10m, expectedIssuedMs: placedAt);
        Assert.Equal(11m, current.ChangedOrder!.Order.Price);
    }
}
