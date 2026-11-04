namespace Eve.Market.Tests;

/// <summary>
/// The batched candidate read (M7): a match over more rows than one batch fills in book order (price, issue time, id) across the batch
/// boundaries, and locks no more rows than the batches it reads.
/// </summary>
public class OrderBookMatchTests
{
    [PgFact]
    public async Task A_match_across_batches_fills_in_price_then_issue_order_and_locks_only_the_batches_it_reads()
    {
        await using var r = await TradeRig.Create();
        var buyer = await r.Actor("Batch Buyer", 10000m);
        var location = MarketLocation.From(r.Station);
        // 130 asks of one unit: ten prices of thirteen orders each, so ties run across the batch boundaries at 50 and 100.
        // Issue time and insertion order both follow i, so the book order is i.
        var seeds = Enumerable.Range(0, 130).Select(i => new MarketNpcSeed($"tie:{i}", 34, false, 10m + i / 13, 1, MarketRules.RangeRegion,
            location, 1000 + i)).ToArray();
        await r.Book.SeedNpcAsync(seeds);

        var before = r.Trading.CandidateRowsLocked;
        var receipt = await r.Trading.PlaceAsync(r.Buy(buyer, 100m, 75), "batch-buy");
        var locked = r.Trading.CandidateRowsLocked - before;

        Assert.Equal(75, receipt.FilledQuantity);
        // 75 units are the first 75 in book order: the batches read 100 rows (two of 50), not the 130 of the book
        Assert.Equal(100, locked);
        // the remaining asks are exactly the orders from position 75 on, in the same order
        var remaining = (await r.Book.BookAsync(r.Station.RegionId, 34, r.Now)).Asks.Select(a => a.IssuedMs).ToArray();
        Assert.Equal(Enumerable.Range(75, 55).Select(i => 1000L + i), remaining);
        // the money: thirteen units at each of 10, 11, 12, 13 and 14, then ten at 15
        Assert.Equal(10000m - 930m, (await r.WalletStore.GetAsync(buyer))!.Isk);
    }
}
