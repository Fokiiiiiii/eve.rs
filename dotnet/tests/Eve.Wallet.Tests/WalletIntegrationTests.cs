namespace Eve.Wallet.Tests;

public class WalletIntegrationTests
{
    [PgFact]
    public async Task References_round_trip_and_reusing_a_key_with_another_reference_is_refused()
    {
        await using var rig=await Rig.CreateAsync();
        await rig.OpenAsync(1,0m,0);
        await using var connection=await rig.Source.OpenConnectionAsync();
        await using(var tx=await connection.BeginTransactionAsync())
        {
            await rig.Transfers.AdjustAsync(connection,tx,1,100m,1000125,17,"Bounty",100,"bounty-ref",referenceId:30000001,referenceType:5);
            await tx.CommitAsync();
        }
        var journal=(await rig.Transfers.JournalAsync(1)).Single();
        Assert.Equal(30000001,journal.ReferenceId);
        Assert.Equal(5,journal.ReferenceType);
        await using(var tx=await connection.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<WalletOperationConflictException>(()=>rig.Transfers.AdjustAsync(connection,tx,1,100m,1000125,17,"Bounty",101,"bounty-ref",referenceId:30000002,referenceType:5));
            await tx.CommitAsync();
        }
        Assert.Equal(100m,(await rig.Store.GetAsync(1))!.Isk);
    }
    [PgFact]
    public async Task An_announcement_is_explicit_after_commit_and_the_domain_emits_no_early_event()
    {
        await using var rig=await Rig.CreateAsync();
        await rig.OpenAsync(1,0m,0);
        var transactions=rig.Transfers;
        var changes=new List<WalletBalanceChange>();
        transactions.BalanceCommitted += changes.Add;
        await using(var connection=await rig.Source.OpenConnectionAsync())
        await using(var tx=await connection.BeginTransactionAsync())
        {
            var balance=await transactions.AdjustAsync(connection,tx,1,100m,1000125,17,"Bounty",100,"announce");
            Assert.Empty(changes);
            await tx.CommitAsync();
            transactions.AnnounceCommitted(new WalletBalanceChange(1,1000,balance));
        }
        Assert.Equal(new WalletBalanceChange(1,1000,100m),Assert.Single(changes));
    }
}
