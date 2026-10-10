using Eve.Wallet;
using Npgsql;

namespace Eve.Market.Tests;

public class TradingTests
{
    [PgFact]
    public async Task Player_ask_purchase_custody_tax_and_reconstructed_retry_commit_once()
    {
        await using var r=await TradeRig.Create();
        var seller=await r.Actor("Seller One");
        var buyer=await r.Actor("Buyer One");
        var item=await r.Stock(seller,10);
        var ask=await r.Trading.PlaceAsync(r.Sell(seller,item,10,10,90),"ask");
        Assert.NotNull(ask.OrderId);
        Assert.Equal(9900m,(await r.WalletStore.GetAsync(seller))!.Isk); // minimum broker fee
        Assert.Empty(await r.Items.InLocationAsync(r.Station.Id,seller));
        var trade=await r.Trading.PlaceAsync(r.Buy(buyer,10,4),"buy");
        Assert.Equal(4,trade.FilledQuantity);
        Assert.Equal(40m,trade.GrossAmount);
        Assert.Equal(9960m,(await r.WalletStore.GetAsync(buyer))!.Isk);
        Assert.Equal(9937.00m,(await r.WalletStore.GetAsync(seller))!.Isk); // 40 less the 7.5% sales tax (3.00)
        Assert.Equal(4,Assert.Single(await r.Items.InLocationAsync(r.Station.Id,buyer)).Quantity);
        Assert.Equal(6,(await r.Book.BookAsync(r.Station.RegionId,34,r.Now)).Asks.Single().VolumeRemaining);
        await r.Reopen().PlaceAsync(r.Buy(buyer,10.00m,4),"buy"); // equivalent decimal formatting is a valid retry
        Assert.Equal(9960m,(await r.WalletStore.GetAsync(buyer))!.Isk);
        Assert.Single(await r.Items.InLocationAsync(r.Station.Id,buyer));
        await Assert.ThrowsAsync<MarketError>(() => r.Trading.PlaceAsync(r.Buy(buyer,10,5),"buy"));
    }

    [PgFact]
    public async Task Bid_escrow_pays_the_seller_and_delivers_to_the_acting_buyer()
    {
        await using var r=await TradeRig.Create();
        var buyer=await r.Actor("Buyer One");
        var seller=await r.Actor("Seller One");
        var item=await r.Stock(seller,10);
        var bid=await r.Trading.PlaceAsync(r.Buy(buyer,12,10,90,8),"bid");
        Assert.Equal(9780m,(await r.WalletStore.GetAsync(buyer))!.Isk);
        Assert.NotNull(bid.OrderId);
        var tooSmall=await r.Trading.PlaceAsync(r.Sell(seller,item,10,7),"small");
        Assert.Equal(0,tooSmall.FilledQuantity);
        var trade=await r.Trading.PlaceAsync(r.Sell(seller,item,10,8),"sell");
        Assert.Equal(8,trade.FilledQuantity);
        Assert.Equal(10088.80m,(await r.WalletStore.GetAsync(seller))!.Isk); // 80 less the 7.5% sales tax (6.00)
        Assert.Equal(9780m,(await r.WalletStore.GetAsync(buyer))!.Isk); // already paid escrow
        var last=await r.Trading.PlaceAsync(r.Sell(seller,item,10,2),"last");
        Assert.Equal(2,last.FilledQuantity); // remainder smaller than the original minimum may fill in full
        Assert.Empty((await r.Book.BookAsync(r.Station.RegionId,34,r.Now)).Bids);
        Assert.Equal(10,(await r.Items.InLocationAsync(r.Station.Id,buyer)).Sum(i => i.Quantity));
    }

    [PgFact]
    public async Task Recipient_cap_failure_rolls_back_the_buyer_book_item_and_receipt()
    {
        await using var r=await TradeRig.Create();
        var seller=await r.Actor("Seller One",WalletLimits.MaximumIsk);
        var buyer=await r.Actor("Buyer One");
        var item=await r.Stock(seller,1);
        await r.Trading.PlaceAsync(r.Sell(seller,item,1000,1,90),"ask");
        await using (var c=await r.Source.OpenConnectionAsync())
        await using (var tx=await c.BeginTransactionAsync())
        {
            await r.Wallets.AdjustAsync(c,tx,seller,100m,1,1,"Refill",r.Now,"refill");
            await tx.CommitAsync();
        }
        await Assert.ThrowsAsync<MoneyLimitException>(() => r.Trading.PlaceAsync(r.Buy(buyer,1000,1),"blocked"));
        Assert.Equal(10000m,(await r.WalletStore.GetAsync(buyer))!.Isk);
        Assert.Equal(WalletLimits.MaximumIsk,(await r.WalletStore.GetAsync(seller))!.Isk);
        Assert.Empty(await r.Items.InLocationAsync(r.Station.Id,buyer));
        Assert.Single((await r.Book.BookAsync(r.Station.RegionId,34,r.Now)).Asks);
        await using var count=r.Source.CreateCommand($"SELECT count(*) FROM {r.MarketSchema}.commands WHERE operation_key='blocked'");
        Assert.Equal(0L,(long)(await count.ExecuteScalarAsync())!);
    }

    [PgFact]
    public async Task Npc_supply_and_demand_use_real_custody_and_only_the_intended_isk_source_or_sink()
    {
        await using var r=await TradeRig.Create();
        var actor=await r.Actor("Trader One");
        var location=MarketLocation.From(r.Station);
        await r.Book.SeedNpcAsync([new("ask",34,false,10,100,32767,location,r.Now)]);
        await r.Trading.PlaceAsync(r.Buy(actor,10,5),"npc-buy");
        Assert.Equal(9950m,(await r.WalletStore.GetAsync(actor))!.Isk);
        var bought=Assert.Single(await r.Items.InLocationAsync(r.Station.Id,actor));
        await r.Book.SeedNpcAsync([new("bid",34,true,8,100,32767,location,r.Now)]);
        await r.Trading.PlaceAsync(r.Sell(actor,bought.Id,8,5),"npc-sell");
        Assert.Equal(9987.00m,(await r.WalletStore.GetAsync(actor))!.Isk); // 40 less the 7.5% sales tax (3.00)
        Assert.Empty(await r.Items.InLocationAsync(r.Station.Id,actor));
    }

    [PgFact]
    public async Task A_late_fill_persistence_failure_rolls_back_all_money_and_goods()
    {
        await using var r=await TradeRig.Create();
        var seller=await r.Actor("Seller One");
        var buyer=await r.Actor("Buyer One");
        var item=await r.Stock(seller,10);
        await r.Trading.PlaceAsync(r.Sell(seller,item,10,10,90),"ask");
        await using (var fail=r.Source.CreateCommand($"ALTER TABLE {r.MarketSchema}.fills ADD CONSTRAINT no_fills CHECK(quantity=0)"))
            await fail.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<PostgresException>(() => r.Trading.PlaceAsync(r.Buy(buyer,10,4),"fail"));
        Assert.Equal(10000m,(await r.WalletStore.GetAsync(buyer))!.Isk);
        Assert.Equal(9900m,(await r.WalletStore.GetAsync(seller))!.Isk);
        Assert.Empty(await r.Items.InLocationAsync(r.Station.Id,buyer));
        Assert.Equal(10,(await r.Book.BookAsync(r.Station.RegionId,34,r.Now)).Asks.Single().VolumeRemaining);
    }

    [PgFact]
    public async Task A_missing_market_receipt_cannot_reapply_goods_when_the_wallet_receipt_survived()
    {
        await using var r=await TradeRig.Create();
        var seller=await r.Actor("Seller One");
        var buyer=await r.Actor("Buyer One");
        var item=await r.Stock(seller,10);
        await r.Trading.PlaceAsync(r.Sell(seller,item,10,10,90),"ask");
        await r.Trading.PlaceAsync(r.Buy(buyer,10,4),"buy");
        await using (var restore=r.Source.CreateCommand($"DELETE FROM {r.MarketSchema}.commands WHERE operation_key='buy'"))
            await restore.ExecuteNonQueryAsync();
        var error=await Assert.ThrowsAsync<MarketError>(() => r.Reopen().PlaceAsync(r.Buy(buyer,10,4),"buy"));
        Assert.Equal("MARKET_SETTLEMENT_RECONCILIATION_REQUIRED",error.Code);
        Assert.Equal(9960m,(await r.WalletStore.GetAsync(buyer))!.Isk);
        Assert.Equal(4,Assert.Single(await r.Items.InLocationAsync(r.Station.Id,buyer)).Quantity);
        Assert.Equal(6,(await r.Book.BookAsync(r.Station.RegionId,34,r.Now)).Asks.Single().VolumeRemaining);
    }

    [PgFact]
    public async Task Partial_immediate_purchase_can_leave_a_bid_smaller_than_its_original_minimum()
    {
        await using var r=await TradeRig.Create();
        var actor=await r.Actor("Trader One");
        await r.Book.SeedNpcAsync([new("ask",34,false,10,7,32767,MarketLocation.From(r.Station),r.Now)]);
        var result=await r.Trading.PlaceAsync(r.Buy(actor,10,10,90,8),"mixed");
        Assert.Equal(7,result.FilledQuantity);
        var bid=(await r.Book.BookAsync(r.Station.RegionId,34,r.Now)).Bids.Single();
        Assert.Equal(3,bid.VolumeRemaining);
        Assert.Equal(8,bid.MinimumVolume);
        Assert.Equal(9800m,(await r.WalletStore.GetAsync(actor))!.Isk);
    }

    [PgFact]
    public async Task A_corporate_wallet_pays_while_the_acting_character_keeps_item_custody()
    {
        await using var r=await TradeRig.Create();
        var actor=await r.Actor("Trader One",10000000m);
        var corp=await r.Corporations.CreateAsync(actor,new("Orbital Works","ORBIT","","",0),"corporation");
        await using (var c=await r.Source.OpenConnectionAsync())
        await using (var tx=await c.BeginTransactionAsync())
        {
            await r.Wallets.TransferAsync(c,tx,new WalletAccount(actor),new WalletAccount(corp.Id,1006),1000m,10,"Deposit",r.Now,"deposit","test");
            await tx.CommitAsync();
        }
        await r.Corporations.SetAccountKeyAsync(actor,1006);
        var personal=(await r.WalletStore.GetAsync(actor))!.Isk;
        await r.Book.SeedNpcAsync([new("ask",34,false,10,100,32767,MarketLocation.From(r.Station),r.Now)]);
        await r.Trading.PlaceAsync(r.Buy(actor,10,5,corp:true),"corp-buy");
        Assert.Equal(personal,(await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Equal(950m,await r.Wallets.AccountBalanceAsync(new(corp.Id,1006)));
        Assert.Equal(5,Assert.Single(await r.Items.InLocationAsync(r.Station.Id,actor)).Quantity);
        Assert.Empty(await r.Items.OwnedByAsync(corp.Id));
    }

    [PgFact]
    public async Task Concurrent_buyers_cannot_consume_one_ask_twice()
    {
        await using var r=await TradeRig.Create();
        var seller=await r.Actor("Seller One");
        var a=await r.Actor("Buyer A");
        var b=await r.Actor("Buyer B");
        var item=await r.Stock(seller,1);
        await r.Trading.PlaceAsync(r.Sell(seller,item,50,1,90),"ask");
        var results=await Task.WhenAll(r.Trading.PlaceAsync(r.Buy(a,50,1),"buy-a"),r.Trading.PlaceAsync(r.Buy(b,50,1),"buy-b"));
        Assert.Equal(1,results.Sum(x => x.FilledQuantity));
        Assert.Equal(19950m,(await r.WalletStore.GetAsync(a))!.Isk+(await r.WalletStore.GetAsync(b))!.Isk);
        Assert.Equal(1,(await r.Items.InLocationAsync(r.Station.Id,a)).Sum(i => i.Quantity)+(await r.Items.InLocationAsync(r.Station.Id,b)).Sum(i => i.Quantity));
        Assert.Empty((await r.Book.BookAsync(r.Station.RegionId,34,r.Now)).Asks);
    }

    [PgFact]
    public async Task Cancel_refunds_escrow_once_and_returns_the_original_sell_stack()
    {
        await using var r=await TradeRig.Create();
        var actor=await r.Actor("Trader One");
        var foreign=await r.Actor("Other One");
        var bid=await r.Trading.PlaceAsync(r.Buy(actor,10,10,90),"bid");
        await Assert.ThrowsAsync<MarketError>(() => r.Trading.CancelAsync(foreign,bid.OrderId!.Value,"foreign"));
        Assert.Equal(9800m,(await r.WalletStore.GetAsync(actor))!.Isk);
        await r.Trading.CancelAsync(actor,bid.OrderId!.Value,"cancel-bid");
        Assert.Equal(9900m,(await r.WalletStore.GetAsync(actor))!.Isk);
        await r.Reopen().CancelAsync(actor,bid.OrderId.Value,"cancel-bid");
        Assert.Equal(9900m,(await r.WalletStore.GetAsync(actor))!.Isk);
        var item=await r.Stock(actor,10);
        var ask=await r.Trading.PlaceAsync(r.Sell(actor,item,10,10,90),"ask");
        await r.Trading.CancelAsync(actor,ask.OrderId!.Value,"cancel-ask");
        var returned=Assert.Single(await r.Items.InLocationAsync(r.Station.Id,actor));
        Assert.Equal(item,returned.Id);
        Assert.Equal(10,returned.Quantity);
        Assert.Empty((await r.Book.BookAsync(r.Station.RegionId,34,r.Now)).Asks);
        Assert.Empty((await r.Book.BookAsync(r.Station.RegionId,34,r.Now)).Bids);
    }

    [PgFact]
    public async Task An_expired_bid_that_cannot_fit_keeps_its_full_escrow_for_a_later_retry()
    {
        await using var r=await TradeRig.Create();
        var actor=await r.Actor("Trader One",WalletLimits.MaximumIsk);
        var bid=await r.Trading.PlaceAsync(r.Buy(actor,10,10,1),"bid");
        await using (var c=await r.Source.OpenConnectionAsync())
        await using (var tx=await c.BeginTransactionAsync())
        {
            await r.Wallets.AdjustAsync(c,tx,actor,200m,1,1,"Refill",r.Now,"refill");
            await tx.CommitAsync();
        }
        r.Now+=86400000;
        Assert.Empty(await r.Trading.ExpireDueAsync());
        Assert.Empty((await r.Book.BookAsync(r.Station.RegionId,34,r.Now)).Bids); // expiry hides the intact pending refund
        await using (var check=r.Source.CreateCommand($"SELECT escrow_isk FROM {r.MarketSchema}.orders WHERE id=$1"))
        {
            check.Parameters.Add(new NpgsqlParameter { Value=bid.OrderId!.Value });
            Assert.Equal(100m,(decimal)(await check.ExecuteScalarAsync())!);
        }
        await using (var c=await r.Source.OpenConnectionAsync())
        await using (var tx=await c.BeginTransactionAsync())
        {
            await r.Wallets.AdjustAsync(c,tx,actor,-100m,1,1,"Spend",r.Now,"spend");
            await tx.CommitAsync();
        }
        Assert.Single(await r.Reopen().ExpireDueAsync());
        Assert.Equal(WalletLimits.MaximumIsk,(await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Empty(await r.Trading.ExpireDueAsync());
    }

    [PgFact]
    public async Task Modifying_a_buy_order_crosses_the_book_and_records_price_history()
    {
        await using var r=await TradeRig.Create();
        var actor=await r.Actor("Trader One");
        await r.Book.SeedNpcAsync([new("cross-ask",34,false,10m,5,MarketRules.RangeRegion,MarketLocation.From(r.Station),r.Now)]);
        var placed=await r.Trading.PlaceAsync(new MarketRequest(actor,34,true,9m,5,r.Station.Id,
            Duration:90,Range:MarketRules.RangeStation),"cross-place");
        r.Now += MarketRules.ModificationDelayMs; // the five-minute modification delay (Q3)
        var changed=await r.Trading.ModifyAsync(actor,Assert.IsType<long>(placed.OrderId),12m,"cross-modify");
        Assert.Equal(5,changed.FilledQuantity);
        Assert.Equal(50m,changed.GrossAmount);
        Assert.Equal(5,Assert.Single(await r.Items.InLocationAsync(r.Station.Id,actor)).Quantity);
        var history=await r.Book.HistoryAsync(r.Station.RegionId,34,r.Now/86_400_000);
        var row=Assert.Single(history);
        Assert.Equal(5,row.Volume);
        Assert.Equal(10m,row.Average);
    }

    [PgFact]
    public async Task Corporation_office_stock_is_escrowed_and_cancelled_back_to_its_hangar_division()
    {
        await using var r=await TradeRig.Create();
        var actor=await r.Actor("Corp Trader",10_000_000m);
        var corp=await r.Corporations.CreateAsync(actor,new("Orbital Works","ORBIT","","",0),"corp-stock");
        await r.Corporations.SetAccountKeyAsync(actor,1006);
        await using (var c=await r.Source.OpenConnectionAsync())
        await using (var tx=await c.BeginTransactionAsync())
        {
            await r.Wallets.TransferAsync(c,tx,new WalletAccount(actor),new WalletAccount(corp.Id,1006),
                1000m,10,"Deposit",r.Now,"corp-stock-deposit","test");
            await tx.CommitAsync();
        }
        const int corporationHangar1=115;
        var item=await r.Stock(corp.Id,5,corporationHangar1);
        var placed=await r.Trading.PlaceAsync(r.Sell(actor,item,20m,5,90,corp:true),"corp-stock-sell");
        var escrow=Assert.Single(await r.Items.OwnedByAsync(corp.Id));
        Assert.Equal(corp.Id,escrow.OwnerId);
        Assert.Equal(Eve.Inventory.ItemFlags.Hangar,escrow.Flag);
        Assert.NotEqual(r.Station.Id,escrow.LocationId);
        await r.Trading.CancelAsync(actor,Assert.IsType<long>(placed.OrderId),"corp-stock-cancel");
        var returned=Assert.Single(await r.Items.OwnedByAsync(corp.Id));
        Assert.Equal(r.Station.Id,returned.LocationId);
        Assert.Equal(corporationHangar1,returned.Flag);
        Assert.Equal(5,returned.Quantity);
    }

    [PgFact]
    public async Task The_latest_average_sale_price_is_the_fills_average_and_an_unsold_type_has_none()
    {
        await using var r=await TradeRig.Create();
        var seller=await r.Actor("Seller One");
        var buyer=await r.Actor("Buyer One");
        var item=await r.Stock(seller,10);
        await r.Trading.PlaceAsync(r.Sell(seller,item,10,10,90),"ask");
        await r.Trading.PlaceAsync(r.Buy(buyer,10,4),"buy"); // 4 units at 10: gross 40
        var sales=await r.Book.LatestAverageSalesAsync();
        Assert.Equal(10m,sales[34]);
        Assert.False(sales.ContainsKey(35));
    }
}
