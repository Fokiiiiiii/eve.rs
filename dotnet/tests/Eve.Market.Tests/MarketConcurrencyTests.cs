namespace Eve.Market.Tests;

/// <summary>
/// Concurrent control of one order (M8): a modification or a cancellation racing a fill of the same order. Whichever commits first, the
/// order, the escrow and the goods agree, and the reconciliation holds afterwards.
/// </summary>
public class MarketConcurrencyTests
{
    [PgFact]
    public async Task A_modification_racing_a_fill_of_the_same_bid_leaves_one_consistent_outcome()
    {
        await using var r = await TradeRig.Create();
        var seller = await r.Actor("Race Seller");
        var bidder = await r.Actor("Race Bidder");
        var stock = await r.Stock(seller, 5);
        var bid = await r.Trading.PlaceAsync(r.Buy(bidder, 50, 5, 30), "race-bid");
        r.Now += 301_000; // the modification delay has passed

        var results = await Task.WhenAll(
            Attempt(() => r.Trading.ModifyAsync(bidder, bid.OrderId!.Value, 45m, "race-modify")),
            Attempt(() => r.Trading.PlaceAsync(r.Sell(seller, stock, 40, 5), "race-sell")));

        var sell = Assert.IsType<MarketReceipt>(results[1]);
        Assert.Equal(5, sell.FilledQuantity);
        Assert.IsType<MarketReceipt>(results[0]); // a modification of a bid that the fill took is a no-op, not an error
        Assert.Equal(0, Assert.Single(await r.Book.OwnOrdersAsync(bidder, bidder, true)).Order.VolumeRemaining);
        // the money agrees with the escrow identity (checked below); the exact fee is the rule's, not the test's
        Assert.Empty(await r.Reconciliation.RunAsync());
    }

    [PgFact]
    public async Task A_cancellation_racing_a_fill_of_the_same_bid_leaves_one_consistent_outcome()
    {
        await using var r = await TradeRig.Create();
        var seller = await r.Actor("Cancel Seller");
        var bidder = await r.Actor("Cancel Bidder");
        var stock = await r.Stock(seller, 5);
        var bid = await r.Trading.PlaceAsync(r.Buy(bidder, 50, 5, 30), "cancel-race-bid");

        var results = await Task.WhenAll(
            Attempt(() => r.Trading.CancelAsync(bidder, bid.OrderId!.Value, "cancel-race")),
            Attempt(() => r.Trading.PlaceAsync(r.Sell(seller, stock, 40, 5), "cancel-race-sell")));

        var sell = Assert.IsType<MarketReceipt>(results[1]);
        var state = (await r.Trading.ExpiryStateAsync(bid.OrderId!.Value))!.Value.State;
        if (state == 3)
        {
            // cancelled first: the sell found no bid, and the escrow came back once
            Assert.Equal(0, sell.FilledQuantity);
            Assert.Equal(3, state);
        }
        else
        {
            // filled first: the cancellation is refused, the escrow was consumed by the fill
            Assert.Equal(1, state);
            Assert.Equal(5, sell.FilledQuantity);
            Assert.Equal("MARKET_ORDER_NOT_ACTIVE", Assert.IsType<string>(results[0]));
        }
        Assert.Empty(await r.Reconciliation.RunAsync());
    }

    /// <summary>The receipt, or the market error's code: a refused call is a result here, not a failure of the test.</summary>
    private static async Task<object> Attempt(Func<Task<MarketReceipt>> call)
    {
        try { return await call(); }
        catch (MarketError error) { return error.Code; }
    }
}
