namespace Eve.Market.Tests;

/// <summary>
/// The money and custody invariants (M8): a history of placements, fills, modifications, cancellations and expiry leaves the escrow,
/// item custody and fill journal consistent, and a planted break is reported by the reconciliation the expiry job runs.
/// </summary>
public class MarketInvariantTests
{
    [PgFact]
    public async Task A_mixed_history_keeps_every_invariant_while_orders_rest_and_after_they_expire()
    {
        await using var r = await TradeRig.Create();
        var seller = await r.Actor("Seller Inv");
        var buyer = await r.Actor("Buyer Inv");
        var bidder = await r.Actor("Bidder Inv");
        var stock = await r.Stock(seller, 20);
        var quick = await r.Stock(seller, 4);

        await r.Trading.PlaceAsync(r.Sell(seller, stock, 10, 10, 90), "inv-ask");
        var bid = await r.Trading.PlaceAsync(r.Buy(bidder, 9, 4, 30), "inv-bid");
        // a seller hits the bid: the bid's escrow is consumed, the seller is credited, the buyer receives the goods
        await r.Trading.PlaceAsync(r.Sell(seller, quick, 8, 4), "inv-hit");
        // a buyer takes part of the ask: a transfer leg pair per fill, the seller's tax
        await r.Trading.PlaceAsync(r.Buy(buyer, 10, 3), "inv-take");
        // a bid that rests, is modified after the delay, and is partly filled by a sell
        var rest = await r.Trading.PlaceAsync(r.Buy(bidder, 8, 6, 30), "inv-rest");
        await Reconciled(r, "open escrow, partly filled ask");

        r.Now += 301_000; // the modification delay (five minutes)
        await r.Trading.ModifyAsync(bidder, rest.OrderId!.Value, 8.5m, "inv-modify");
        await Reconciled(r, "after a modification of a resting bid");

        // a cancellation returns the escrow once; the broker fee stays
        var cancelled = await r.Trading.PlaceAsync(r.Buy(buyer, 7, 2, 30), "inv-cancel-me");
        await r.Trading.CancelAsync(buyer, cancelled.OrderId!.Value, "inv-cancel");
        await Reconciled(r, "after a cancellation");

        // every resting order runs out: the expiry jobs close them, the escrow goes back once, the goods return
        r.Now += 91L * 86_400_000;
        await r.Expiry.SweepAsync();
        Assert.Empty(r.Mismatches);
        await Reconciled(r, "after expiry");
        Assert.Empty((await r.Book.BookAsync(r.Station.RegionId, 34, r.Now)).Bids);
        Assert.Empty((await r.Book.BookAsync(r.Station.RegionId, 34, r.Now)).Asks);
    }

    [PgFact]
    public async Task A_planted_escrow_drift_is_reported_and_the_expiry_sweep_logs_it()
    {
        await using var r = await TradeRig.Create();
        var bidder = await r.Actor("Drift Bidder");
        var bid = await r.Trading.PlaceAsync(r.Buy(bidder, 9, 4, 30), "drift-bid");
        Assert.Empty(await r.Reconciliation.RunAsync());

        await using (var drift = r.Source.CreateCommand($"UPDATE {r.MarketSchema}.orders SET escrow_isk=escrow_isk+1 WHERE id=$1"))
        {
            drift.Parameters.Add(new Npgsql.NpgsqlParameter { Value = bid.OrderId!.Value });
            await drift.ExecuteNonQueryAsync();
        }
        var problems = await r.Reconciliation.RunAsync();
        Assert.Contains(problems, p => p.StartsWith("escrow.wallet:"));

        // the minute sweep runs the reconciliation and logs each mismatch
        await r.Expiry.SweepAsync();
        Assert.Contains(r.Mismatches, m => m.StartsWith("market reconciliation: escrow.wallet:"));
    }

    [PgFact]
    public async Task A_planted_item_shortfall_in_an_escrowed_ask_is_reported()
    {
        await using var r = await TradeRig.Create();
        var seller = await r.Actor("Short Seller");
        var stock = await r.Stock(seller, 10);
        var ask = await r.Trading.PlaceAsync(r.Sell(seller, stock, 10, 10, 90), "short-ask");
        Assert.Empty(await r.Reconciliation.RunAsync());

        await using (var short_ = r.Source.CreateCommand($"UPDATE {r.ItemSchema}.items SET quantity=quantity-1 WHERE location_id=$1"))
        {
            short_.Parameters.Add(new Npgsql.NpgsqlParameter { Value = 9200000000L + ask.OrderId!.Value });
            await short_.ExecuteNonQueryAsync();
        }
        var problems = await r.Reconciliation.RunAsync();
        Assert.Contains(problems, p => p.StartsWith("escrow.items:") && p.Contains("escrows 9 of 10"));
    }

    private static async Task Reconciled(TradeRig r, string moment)
    {
        var problems = await r.Reconciliation.RunAsync();
        Assert.True(problems.Count == 0, $"{moment}: {string.Join("; ", problems)}");
    }
}
