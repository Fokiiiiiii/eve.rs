using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Wallet.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null)
            Skip = "EVE_PG_URL is not set";
    }
}

public class TransactionHistoryTests
{
    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet) { }
    }

    [PgFact]
    public async Task A_month_query_returns_every_journal_row_in_that_month()
    {
        var schema = $"wl_{Guid.NewGuid():N}"[..20];
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        try
        {
            await PgWalletStore.MigrateAsync(source, schema);
            var wallets = new PgWalletStore(source, schema);
            var transactions = new WalletTransactions(source, schema);
            const long ownerId = 140_000_001;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.OpenAsync(connection, transaction, ownerId, 0m, 0);
                var monthStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
                for (var i = 0; i < 125; i++)
                    await transactions.AdjustAsync(connection, transaction, ownerId, 1m, 1, 1, "Journal test",
                        monthStart.ToUnixTimeMilliseconds() + i, $"journal-{i}");
                await transactions.AdjustAsync(connection, transaction, ownerId, 1m, 1, 1, "Other month",
                    monthStart.AddMonths(1).ToUnixTimeMilliseconds(), "other-month");
                await transaction.CommitAsync();
            }

            var session = new ClientSession(new Sink()) { CharacterId = ownerId };
            var service = new AccountService(wallets, transactions: transactions);
            var result = await service.GetTransactions(new ServiceCall
            {
                Session = session,
                Method = "GetTransactions",
                Args = [new PyInt(1000), new PyInt(2024), new PyInt(1), PyBool.False],
                CallId = new PyInt(1),
            });

            Assert.Equal(125, Assert.IsType<PyList>(result).Items.Count);
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }
}
