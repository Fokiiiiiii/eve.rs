using Npgsql;

namespace Eve.Wallet.Tests;

public class CorporateSettlementTests
{
    private static async Task Corporation(Rig rig,long id,decimal balance)
    {
        await rig.OpenAsync(id,balance,0);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await rig.Transfers.OpenCorporationDivisionsAsync(connection,tx,id);
        await tx.CommitAsync();
    }
    [PgFact]
    public async Task An_over_limit_dividend_never_debits_the_company_or_any_recipient()
    {
        await using var rig = await Rig.CreateAsync();
        await Corporation(rig,98000000,1000m);
        await rig.OpenAsync(1,WalletLimits.MaximumIsk-100m,0);
        await rig.OpenAsync(2,0m,0);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<MoneyLimitException>(() => rig.Transfers.DistributeAsync(connection,tx,new(98000000),
            [new(new(1),500m),new(new(2),500m)],37,"Dividend",100,"dividend-over","share-vote-1"));
        await tx.CommitAsync();
        Assert.Equal(1000m,(await rig.Store.GetAsync(98000000))!.Isk);
        Assert.Equal(WalletLimits.MaximumIsk-100m,(await rig.Store.GetAsync(1))!.Isk);
        Assert.Equal(0m,(await rig.Store.GetAsync(2))!.Isk);
        Assert.Empty(await rig.Transfers.JournalAsync(98000000));
        Assert.Empty(await rig.Transfers.JournalAsync(1));
        Assert.Empty(await rig.Transfers.JournalAsync(2));
    }
    [PgFact]
    public async Task Duplicate_recipient_allocations_are_checked_together()
    {
        await using var rig = await Rig.CreateAsync();
        await Corporation(rig,98000000,1000m);
        await rig.OpenAsync(1,WalletLimits.MaximumIsk-600m,0);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<MoneyLimitException>(() => rig.Transfers.DistributeAsync(connection,tx,new(98000000),
            [new(new(1),500m),new(new(1),500m)],37,"Dividend",100,"duplicate-recipient","share-vote-2"));
        await tx.CommitAsync();
        Assert.Equal(1000m,(await rig.Store.GetAsync(98000000))!.Isk);
        Assert.Equal(WalletLimits.MaximumIsk-600m,(await rig.Store.GetAsync(1))!.Isk);
    }
    [PgFact]
    public async Task A_liquidation_that_cannot_fit_leaves_every_division_and_the_ceo_unchanged()
    {
        await using var rig = await Rig.CreateAsync();
        await Corporation(rig,98000000,5000000000000m);
        await rig.OpenAsync(1,WalletLimits.MaximumIsk-2000000000000m,0);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<MoneyLimitException>(() => rig.Transfers.LiquidateAsync(connection,tx,98000000,1,10,"Liquidation",100,"liquidate-over","closure-1"));
        await tx.CommitAsync();
        Assert.Equal(5000000000000m,(await rig.Store.GetAsync(98000000))!.Isk);
        Assert.Equal(WalletLimits.MaximumIsk-2000000000000m,(await rig.Store.GetAsync(1))!.Isk);
        for(var key=1001;key<=1006;key++) Assert.Equal(0m,await rig.Transfers.AccountBalanceAsync(new(98000000,key)));
    }
    [PgFact]
    public async Task Valid_dividends_replay_once_and_changed_references_or_amounts_fail()
    {
        await using var rig = await Rig.CreateAsync();
        await Corporation(rig,98000000,1000m);
        await rig.OpenAsync(1,0m,0);
        await rig.OpenAsync(2,0m,0);
        await using var connection = await rig.Source.OpenConnectionAsync();
        for(var i=0;i<2;i++)
        {
            await using var tx=await connection.BeginTransactionAsync();
            await rig.Transfers.DistributeAsync(connection,tx,new(98000000),[new(new(1),500m),new(new(2),500m)],37,"Dividend",100+i,"dividend","vote-1");
            await tx.CommitAsync();
        }
        await using(var tx=await connection.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<WalletOperationConflictException>(()=>rig.Transfers.DistributeAsync(connection,tx,new(98000000),
                [new(new(1),400m),new(new(2),600m)],37,"Dividend",102,"dividend","vote-1"));
            await tx.CommitAsync();
        }
        Assert.Equal(0m,(await rig.Store.GetAsync(98000000))!.Isk);
        Assert.Equal(500m,(await rig.Store.GetAsync(1))!.Isk);
        Assert.Equal(500m,(await rig.Store.GetAsync(2))!.Isk);
        Assert.Single(await rig.Transfers.JournalAsync(1));
    }
    [PgFact]
    public async Task A_settlement_receipt_is_not_evicted_after_1024_newer_operations()
    {
        await using var rig = await Rig.CreateAsync();
        await Corporation(rig,98000000,2000m);
        await rig.OpenAsync(1,0m,0);
        await using var connection = await rig.Source.OpenConnectionAsync();
        for(var i=0;i<1026;i++)
        {
            await using var tx=await connection.BeginTransactionAsync();
            await rig.Transfers.TransferAsync(connection,tx,new WalletAccount(98000000),new WalletAccount(1),1m,10,"Gift",100+i,"transfer-"+i,"gift-"+i);
            await tx.CommitAsync();
        }
        await using(var tx=await connection.BeginTransactionAsync())
        {
            await rig.Transfers.TransferAsync(connection,tx,new WalletAccount(98000000),new WalletAccount(1),1m,10,"Gift",2000,"transfer-0","gift-0");
            await tx.CommitAsync();
        }
        Assert.Equal(974m,(await rig.Store.GetAsync(98000000))!.Isk);
        Assert.Equal(1026m,(await rig.Store.GetAsync(1))!.Isk);
        Assert.Equal(1000,(await rig.Transfers.JournalAsync(1)).Count);
    }
    [PgFact]
    public async Task A_transfer_between_divisions_conserves_money_and_does_not_change_plex()
    {
        await using var rig = await Rig.CreateAsync();
        await Corporation(rig,98000000,1000m);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var tx=await connection.BeginTransactionAsync();
        await rig.Transfers.TransferAsync(connection,tx,new WalletAccount(98000000),new WalletAccount(98000000,1006),100m,10,"Division transfer",100,"division-1","division-transfer-1");
        await tx.CommitAsync();
        Assert.Equal(900m,(await rig.Store.GetAsync(98000000))!.Isk);
        Assert.Equal(100m,await rig.Transfers.AccountBalanceAsync(new(98000000,1006)));
        Assert.Equal(0,(await rig.Store.GetAsync(98000000))!.Plex);
    }

    [PgFact]
    public async Task A_late_journal_failure_rolls_the_whole_settlement_back_even_if_the_caller_commits()
    {
        await using var rig=await Rig.CreateAsync();
        await Corporation(rig,98000000,1000m);
        await rig.OpenAsync(1,0m,0);
        await rig.OpenAsync(2,0m,0);
        await using(var fault=rig.Source.CreateCommand($"ALTER TABLE {rig.Schema}.journal ADD CONSTRAINT injected_failure CHECK(owner_id<>2)"))
            await fault.ExecuteNonQueryAsync();
        await using(var connection=await rig.Source.OpenConnectionAsync())
        await using(var tx=await connection.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<PostgresException>(()=>rig.Transfers.DistributeAsync(connection,tx,new(98000000),
                [new(new(1),500m),new(new(2),500m)],37,"Dividend",100,"late-failure","vote-fault"));
            await tx.CommitAsync();
        }
        Assert.Equal(1000m,(await rig.Store.GetAsync(98000000))!.Isk);
        Assert.Equal(0m,(await rig.Store.GetAsync(1))!.Isk);
        Assert.Equal(0m,(await rig.Store.GetAsync(2))!.Isk);
        Assert.Empty(await rig.Transfers.JournalAsync(1));
    }

    [PgFact]
    public async Task All_divisions_liquidate_once_and_a_reconstructed_store_replays_the_receipt()
    {
        await using var rig=await Rig.CreateAsync();
        await Corporation(rig,98000000,700m);
        await rig.OpenAsync(1,0m,0);
        await using var connection=await rig.Source.OpenConnectionAsync();
        for(var key=1001;key<=1006;key++)
        {
            await using var tx=await connection.BeginTransactionAsync();
            await rig.Transfers.TransferAsync(connection,tx,new WalletAccount(98000000),new WalletAccount(98000000,key),100m,10,"Allocate",100,"allocate-"+key,"allocation-"+key);
            await tx.CommitAsync();
        }
        for(var attempt=0;attempt<2;attempt++)
        {
            await using var tx=await connection.BeginTransactionAsync();
            var result=await rig.Transfers.LiquidateAsync(connection,tx,98000000,1,10,"Liquidation",200+attempt,"close-corp","corporation-generation-1");
            Assert.Equal(700m,result.Entries.Where(e=>e.OwnerId==1).Sum(e=>e.Amount));
            await tx.CommitAsync();
        }
        for(var key=1000;key<=1006;key++) Assert.Equal(0m,await rig.Transfers.AccountBalanceAsync(new(98000000,key)));
        Assert.Equal(700m,(await rig.Store.GetAsync(1))!.Isk);
        Assert.Single(await rig.Transfers.JournalAsync(1));
    }

    [PgFact]
    public async Task A_credit_primitive_refuses_overflow_for_an_auxiliary_division_before_any_write()
    {
        await using var rig=await Rig.CreateAsync();
        await Corporation(rig,98000000,0m);
        await using var connection=await rig.Source.OpenConnectionAsync();
        await using(var tx=await connection.BeginTransactionAsync())
        {
            await rig.Transfers.AdjustAsync(connection,tx,new WalletAccount(98000000,1006),WalletLimits.MaximumIsk,1000125,10,"Fund division",100,"fund","funding-1");
            await tx.CommitAsync();
        }
        await using(var tx=await connection.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<MoneyLimitException>(()=>rig.Transfers.AdjustAsync(connection,tx,new WalletAccount(98000000,1006),0.01m,1000125,10,"Fund division",101,"overflow","funding-2"));
            await tx.CommitAsync();
        }
        Assert.Equal(WalletLimits.MaximumIsk,await rig.Transfers.AccountBalanceAsync(new(98000000,1006)));
        Assert.Equal(0m,(await rig.Store.GetAsync(98000000))!.Isk);
    }
    [PgFact]
    public async Task Concurrent_company_distributions_cannot_spend_the_same_division_twice()
    {
        await using var rig=await Rig.CreateAsync();
        await Corporation(rig,98000000,1000m);
        await rig.OpenAsync(1,0m,0);
        async Task<bool> Pay(int i)
        {
            await using var connection=await rig.Source.OpenConnectionAsync();
            await using var tx=await connection.BeginTransactionAsync();
            try
            {
                await rig.Transfers.DistributeAsync(connection,tx,new(98000000),[new(new(1),800m)],37,"Dividend",100,"concurrent-"+i,"vote-"+i);
                await tx.CommitAsync();
                return true;
            }
            catch(InsufficientFundsException) { return false; }
        }
        Assert.Equal(1,(await Task.WhenAll(Pay(1),Pay(2))).Count(x=>x));
        Assert.Equal(200m,(await rig.Store.GetAsync(98000000))!.Isk);
        Assert.Equal(800m,(await rig.Store.GetAsync(1))!.Isk);
    }
}
