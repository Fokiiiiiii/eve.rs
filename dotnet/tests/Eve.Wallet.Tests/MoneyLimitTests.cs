using Npgsql;

namespace Eve.Wallet.Tests;

public class MoneyLimitTests
{
    [PgFact]
    public async Task The_exact_ceiling_is_preserved_to_the_cent()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1,WalletLimits.MaximumIsk - 0.01m,10);
        Assert.Equal(1145141919364363.99m,(await rig.Store.GetAsync(1))!.Isk);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        Assert.Equal(WalletLimits.MaximumIsk,await rig.Transfers.AdjustAsync(connection,tx,1,0.01m,1000125,17,"reward",100,"last-cent"));
        await tx.CommitAsync();
        Assert.Equal(1145141919364364m,(await rig.Store.GetAsync(1))!.Isk);
        Assert.Equal(1145141919364364m,(await rig.Transfers.JournalAsync(1)).Single().Balance);
        Assert.Equal(10,(await rig.Store.GetAsync(1))!.Plex);
    }

    [PgFact]
    public async Task A_transfer_over_the_receivers_ceiling_never_debits_the_sender()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1,100m,0);
        await rig.OpenAsync(2,WalletLimits.MaximumIsk,0);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<MoneyLimitException>(() => rig.Transfers.TransferAsync(connection,tx,1,2,0.01m,10,"gift",100,"over-limit"));
        await tx.CommitAsync();
        Assert.Equal(100m,(await rig.Store.GetAsync(1))!.Isk);
        Assert.Equal(WalletLimits.MaximumIsk,(await rig.Store.GetAsync(2))!.Isk);
        Assert.Empty(await rig.Transfers.JournalAsync(1));
        Assert.Empty(await rig.Transfers.JournalAsync(2));
    }

    [PgFact]
    public async Task Neither_the_open_api_nor_a_reward_can_exceed_the_ceiling()
    {
        await using var rig = await Rig.CreateAsync();
        await Assert.ThrowsAsync<MoneyLimitException>(() => rig.OpenAsync(1,WalletLimits.MaximumIsk + 0.01m,0));
        await rig.OpenAsync(1,WalletLimits.MaximumIsk,0);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<MoneyLimitException>(() => rig.Transfers.AdjustAsync(connection,tx,1,0.01m,1000125,17,"reward",100,"over-limit"));
        await tx.CommitAsync();
        Assert.Equal(WalletLimits.MaximumIsk,(await rig.Store.GetAsync(1))!.Isk);
        Assert.Empty(await rig.Transfers.JournalAsync(1));
    }

    [PgFact]
    public async Task The_database_also_refuses_an_over_limit_balance()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1,100m,0);
        await using (var command = rig.Source.CreateCommand($"UPDATE {rig.Schema}.balances SET isk=1145141919364364.01 WHERE owner_id=1"))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation,error.SqlState);
        }
        Assert.Equal(100m,(await rig.Store.GetAsync(1))!.Isk);
    }

    [PgFact]
    public async Task A_reward_retry_at_the_ceiling_is_a_no_op_but_changed_terms_are_refused()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1,WalletLimits.MaximumIsk - 5m,0);
        await using var connection = await rig.Source.OpenConnectionAsync();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var tx = await connection.BeginTransactionAsync();
            Assert.Equal(WalletLimits.MaximumIsk,await rig.Transfers.AdjustAsync(connection,tx,1,5m,1000125,17,"reward",100 + attempt,"reward-once"));
            await tx.CommitAsync();
        }
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<WalletOperationConflictException>(() => rig.Transfers.AdjustAsync(connection,tx,1,6m,1000125,17,"reward",102,"reward-once"));
            await tx.CommitAsync();
        }
        Assert.Equal(WalletLimits.MaximumIsk,(await rig.Store.GetAsync(1))!.Isk);
        Assert.Single(await rig.Transfers.JournalAsync(1));
    }
}
