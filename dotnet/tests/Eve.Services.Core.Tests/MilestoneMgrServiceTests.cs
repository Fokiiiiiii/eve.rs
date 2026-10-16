using Eve.Characters;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Core;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Core.Tests;

public sealed class MilestoneMgrServiceTests
{
    private const long Account = 77_020;
    private const long Pilot = 900_020;
    private const int MilestoneId = 1;

    private static string? PgUrl => Environment.GetEnvironmentVariable("EVE_PG_URL");

    /// <summary>A claim pays the reward once: the milestone reached is paid when claimed, a second claim pays nothing, and the claim survives a restart.</summary>
    [Fact]
    public async Task A_reached_milestone_is_paid_once_and_the_claim_survives_a_restart()
    {
        if (PgUrl is null) return; // skipped without EVE_PG_URL (see tools/dev/pg18.sh)
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var characters = $"mc_{suffix}";
        var wallet = $"mw_{suffix}";
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(PgUrl));
        await PgCharacterStore.MigrateAsync(source, characters);
        await PgWalletStore.MigrateAsync(source, wallet);
        var wallets = new WalletTransactions(source, wallet);
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await wallets.EnsureAsync(connection, transaction, Pilot);
            await transaction.CommitAsync();
        }
        try
        {
            var store = new LoginMilestoneStore(source, characters, wallets);
            await store.WriteAsync(Account, new LoginMilestoneStore.Progress(LoginMilestoneStore.DurationMs, true, false), 1);
            var timers = new LocalTimers();
            var service = (IService)new MilestoneMgrService(store, timers, () => 2);
            var session = new ClientSession { CharacterId = Pilot, UserId = Account };

            await service.CallAsync(new ServiceCall { Method = "ClaimRewards", Args = [new PyInt(MilestoneId)], Session = session });
            Assert.Equal(500_000m, await Isk(source, wallet));
            await service.CallAsync(new ServiceCall { Method = "ClaimRewards", Args = [new PyInt(MilestoneId)], Session = session });
            Assert.Equal(500_000m, await Isk(source, wallet));

            var restarted = (IService)new MilestoneMgrService(new LoginMilestoneStore(source, characters, wallets), new LocalTimers(), () => 3);
            await restarted.CallAsync(new ServiceCall { Method = "ClaimRewards", Args = [new PyInt(MilestoneId)], Session = session });
            Assert.Equal(500_000m, await Isk(source, wallet));
            Assert.True((await store.ReadAsync(Account)).Claimed);
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {characters} CASCADE; DROP SCHEMA IF EXISTS {wallet} CASCADE;");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task<decimal> Isk(NpgsqlDataSource source, string wallet)
    {
        await using var command = source.CreateCommand($"SELECT isk FROM {wallet}.balances WHERE owner_id = $1 AND account_key = 1000");
        command.Parameters.Add(new NpgsqlParameter { Value = Pilot });
        return (decimal)(await command.ExecuteScalarAsync() ?? 0m);
    }
}
