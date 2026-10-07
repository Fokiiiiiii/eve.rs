namespace Eve.Wallet.Tests;

public class TransactionTests
{
    [PgFact]
    public async Task A_transfer_and_its_retry_move_money_once_and_record_both_sides()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1, 100m, 7);
        await rig.OpenAsync(2, 10m, 3);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await rig.Transfers.TransferAsync(connection, tx, 1, 2, 12.34m, 2, "gift", 100, "gift-1");
            await tx.CommitAsync();
        }
        Assert.Equal(87.66m, (await rig.Store.GetAsync(1))!.Isk);
        Assert.Equal(22.34m, (await rig.Store.GetAsync(2))!.Isk);
        Assert.Equal(7, (await rig.Store.GetAsync(1))!.Plex);
        Assert.Equal(3, (await rig.Store.GetAsync(2))!.Plex);
        var rows = await rig.Transfers.JournalAsync(1);
        Assert.Single(rows);
        Assert.Equal(-12.34m, rows[0].Amount);
        Assert.Equal(87.66m, rows[0].Balance);
        Assert.Equal(12.34m, (await rig.Transfers.JournalAsync(2)).Single().Amount);
    }


    [PgFact]
    public async Task PLEX_adjustment_uses_the_caller_transaction()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1, 100m, 7);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
            Assert.Equal(4, await rig.Store.AdjustPlexAsync(connection, tx, 1, -3));
        Assert.Equal(7, (await rig.Store.GetAsync(1))!.Plex);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            Assert.Equal(2, await rig.Store.AdjustPlexAsync(connection, tx, 1, -5));
            await tx.CommitAsync();
        }
        Assert.Equal(2, (await rig.Store.GetAsync(1))!.Plex);
    }

    [PgFact]
    public async Task PLEX_adjustment_refuses_an_overdraft()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1, 100m, 7);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => rig.Store.AdjustPlexAsync(connection, tx, 1, -8));
        Assert.Equal(7, (await rig.Store.GetAsync(1))!.Plex);
    }

    [PgFact]
    public async Task An_uncommitted_transfer_and_its_journal_are_rolled_back()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1, 100m, 0);
        await rig.OpenAsync(2, 0m, 0);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await rig.Transfers.TransferAsync(connection, tx, 1, 2, 10m, 2, "gift", 100, "rollback");
        }
        Assert.Equal(100m, (await rig.Store.GetAsync(1))!.Isk);
        Assert.Equal(0m, (await rig.Store.GetAsync(2))!.Isk);
        Assert.Empty(await rig.Transfers.JournalAsync(1));
        Assert.Empty(await rig.Transfers.JournalAsync(2));
    }

    [PgFact]
    public async Task Concurrent_debits_cannot_spend_the_same_money_twice()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1, 10m, 0);
        await rig.OpenAsync(2, 0m, 0);
        async Task<bool> Buy(int index)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var tx = await connection.BeginTransactionAsync();
            try
            {
                await rig.Transfers.TransferAsync(connection, tx, 1, 2, 8m, 2, "purchase", 100, $"buy-{index}");
                await tx.CommitAsync();
                return true;
            }
            catch (InsufficientFundsException) { return false; }
        }
        Assert.Equal(1, (await Task.WhenAll(Buy(1), Buy(2))).Count(success => success));
        Assert.Equal(2m, (await rig.Store.GetAsync(1))!.Isk);
        Assert.Equal(8m, (await rig.Store.GetAsync(2))!.Isk);
        Assert.Single(await rig.Transfers.JournalAsync(1));
    }

    [PgFact]
    public async Task Unknown_wallets_and_invalid_amounts_are_refused_before_any_debit()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1, 100m, 0);
        foreach (var amount in new[] { 0m, -1m, 0.001m })
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Transfers.TransferAsync(connection, tx, 1, 2, amount, 2, "gift", 100, "invalid"));
            await tx.CommitAsync();
        }
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<WalletNotFoundException>(() => rig.Transfers.TransferAsync(connection, tx, 1, 2, 10m, 2, "gift", 100, "missing"));
            await tx.CommitAsync();
        }
        Assert.Equal(100m, (await rig.Store.GetAsync(1))!.Isk);
        Assert.Empty(await rig.Transfers.JournalAsync(1));
    }

    [PgFact]
    public async Task Reusing_an_operation_key_with_other_terms_cannot_change_the_balances()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1, 100m, 0);
        await rig.OpenAsync(2, 0m, 0);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await rig.Transfers.TransferAsync(connection, tx, 1, 2, 10m, 2, "gift", 100, "same-key");
            await tx.CommitAsync();
        }
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<WalletOperationConflictException>(() => rig.Transfers.TransferAsync(connection, tx, 1, 2, 11m, 2, "gift", 100, "same-key"));
            await tx.CommitAsync();
        }
        Assert.Equal(90m, (await rig.Store.GetAsync(1))!.Isk);
        Assert.Equal(10m, (await rig.Store.GetAsync(2))!.Isk);
        Assert.Single(await rig.Transfers.JournalAsync(1));
    }

    [PgFact]
    public async Task Opposite_transfers_lock_wallets_in_the_same_order_and_conserve_money()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1, 100m, 0);
        await rig.OpenAsync(2, 100m, 0);
        async Task Move(long from, long to)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await rig.Transfers.TransferAsync(connection, tx, from, to, 10m, 2, "gift", 100, $"opposite-{from}");
            await tx.CommitAsync();
        }
        await Task.WhenAll(Move(1, 2), Move(2, 1));
        Assert.Equal(100m, (await rig.Store.GetAsync(1))!.Isk);
        Assert.Equal(100m, (await rig.Store.GetAsync(2))!.Isk);
        Assert.Equal(2, (await rig.Transfers.JournalAsync(1)).Count);
    }
}
