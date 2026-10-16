using Eve.Characters;
using Eve.Wallet;
using Npgsql;

namespace Eve.Characters.Tests;

public sealed class LoginMilestoneStoreTests
{
    private const long Account = 77_010;
    private const long Pilot = 900_010;

    [PgFact]
    public async Task A_new_account_has_no_progress_and_what_is_written_survives_a_restart()
    {
        await using var s = await PgSchemas.CreateAsync();
        var store = new LoginMilestoneStore(s.Source, s.CharactersSchema, new WalletTransactions(s.Source, s.WalletSchema));
        Assert.Equal(new LoginMilestoneStore.Progress(0, false, false), await store.ReadAsync(Account));

        await store.WriteAsync(Account, new LoginMilestoneStore.Progress(1_234_000, false, false), 10);
        var restarted = new LoginMilestoneStore(s.Source, s.CharactersSchema, new WalletTransactions(s.Source, s.WalletSchema));
        Assert.Equal(new LoginMilestoneStore.Progress(1_234_000, false, false), await restarted.ReadAsync(Account));
    }

    [PgFact]
    public async Task The_reward_is_paid_once_after_the_milestone_is_reached_and_the_claim_survives_a_restart()
    {
        await using var s = await PgSchemas.CreateAsync();
        var wallets = new WalletTransactions(s.Source, s.WalletSchema);
        await using (var connection = await s.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await wallets.EnsureAsync(connection, transaction, Pilot);
            await transaction.CommitAsync();
        }
        var store = new LoginMilestoneStore(s.Source, s.CharactersSchema, wallets);

        Assert.False(await store.ClaimAsync(Account, Pilot, 1, 500_000m, 20));
        Assert.Equal(0m, await Isk(s));

        await store.WriteAsync(Account, new LoginMilestoneStore.Progress(LoginMilestoneStore.DurationMs, true, false), 30);
        Assert.True(await store.ClaimAsync(Account, Pilot, 1, 500_000m, 40));
        Assert.False(await store.ClaimAsync(Account, Pilot, 1, 500_000m, 50));
        Assert.Equal(500_000m, await Isk(s));

        // a late write with the claim not set does not undo it
        await store.WriteAsync(Account, new LoginMilestoneStore.Progress(LoginMilestoneStore.DurationMs, false, false), 60);
        var restarted = new LoginMilestoneStore(s.Source, s.CharactersSchema, wallets);
        Assert.Equal(new LoginMilestoneStore.Progress(LoginMilestoneStore.DurationMs, true, true), await restarted.ReadAsync(Account));
        Assert.False(await restarted.ClaimAsync(Account, Pilot, 1, 500_000m, 70));
        Assert.Equal(500_000m, await Isk(s));
    }

    private static async Task<decimal> Isk(PgSchemas s)
    {
        await using var command = s.Source.CreateCommand($"SELECT isk FROM {s.WalletSchema}.balances WHERE owner_id = $1 AND account_key = 1000");
        command.Parameters.Add(new NpgsqlParameter { Value = Pilot });
        return (decimal)(await command.ExecuteScalarAsync() ?? 0m);
    }
}
