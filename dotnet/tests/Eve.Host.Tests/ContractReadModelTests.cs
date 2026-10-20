using Eve.Data;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class ContractReadModelTests
{
    [Fact]
    public async Task Item_exchange_creation_stages_stack_and_wallet_charges_in_one_transaction()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_write_{suffix}";
        var inventorySchema = $"contract_items_{suffix}";
        var walletSchema = $"contract_wallet_{suffix}";
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, contractsSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        try
        {
            var itemStore = new PgItemStore(source, inventorySchema);
            var wallets = new WalletTransactions(source, walletSchema);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.EnsureAsync(connection, transaction, 100);
                await using var seed = new NpgsqlCommand($"UPDATE {walletSchema}.balances SET isk=1000 WHERE owner_id=100 AND account_key=1000", connection, transaction);
                await seed.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            long itemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                itemId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(34, 100, 600, ItemFlags.Hangar, Quantity: 5), CancellationToken.None);
                await transaction.CommitAsync();
            }

            var writer = new PersistenceContractWriteModel(source, itemStore, wallets, () => 1_800_000_000_000, contractsSchema);
            var created = await writer.CreateItemExchangeAsync(new Eve.Services.Core.CreateItemExchangeRequest(
                IssuerCharacterId: 100,
                IssuerCorporationId: 200,
                IsCorporationIssued: false,
                StartStationId: 600,
                StartStationDivision: ItemFlags.Hangar,
                Availability: 0,
                AssigneeId: 0,
                ExpiryMinutes: 1440,
                Price: 100,
                Reward: 50,
                BrokerFee: 1,
                Deposit: 0,
                SalesTax: 1,
                WalletAccountKey: 1000,
                Title: "Test exchange",
                Description: "",
                Items: [new Eve.Services.Core.ContractOfferedItem(itemId, 2)]), CancellationToken.None);

            Assert.Single(created.EscrowItemIds);
            var sourceItem = await itemStore.GetAsync(itemId, CancellationToken.None);
            Assert.NotNull(sourceItem);
            Assert.Equal(3, sourceItem.Quantity);
            var escrowItem = await itemStore.GetAsync(created.EscrowItemIds[0], CancellationToken.None);
            Assert.NotNull(escrowItem);
            Assert.Equal((long)1, escrowItem.OwnerId);
            Assert.Equal(9_300_000_000 + created.ContractId, escrowItem.LocationId);
            Assert.Equal(2, escrowItem.Quantity);
            await using (var read = source.CreateCommand($"SELECT contract_type, price_isk, reward_escrow, collateral_isk, end_station_id, num_days FROM {contractsSchema}.contract_records WHERE contract_id=$1"))
            {
                read.Parameters.AddWithValue(created.ContractId);
                await using var reader = await read.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((short)1, reader.GetInt16(0));
                Assert.Equal(100m, reader.GetDecimal(1));
                Assert.Equal(50m, reader.GetDecimal(2));
                Assert.Equal(0m, reader.GetDecimal(3));
                Assert.Equal(0L, reader.GetInt64(4));
                Assert.Equal(0, reader.GetInt32(5));
            }
            Assert.Equal(948m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));

            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CreateItemExchangeAsync(
                new Eve.Services.Core.CreateItemExchangeRequest(
                    IssuerCharacterId: 100,
                    IssuerCorporationId: 200,
                    IsCorporationIssued: false,
                    StartStationId: 600,
                    StartStationDivision: ItemFlags.Hangar,
                    Availability: 0,
                    AssigneeId: 0,
                    ExpiryMinutes: 1440,
                    Price: 10,
                    Reward: 0,
                    BrokerFee: 1,
                    Deposit: 0,
                    SalesTax: 0,
                    WalletAccountKey: 1000,
                    Title: "Rollback exchange",
                    Description: "",
                    Items: [new Eve.Services.Core.ContractOfferedItem(itemId, 5)]), CancellationToken.None).AsTask());
            Assert.Equal(948m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));
            await using (var read = source.CreateCommand($"SELECT count(*) FROM {contractsSchema}.contract_records"))
                Assert.Equal(1L, (long)(await read.ExecuteScalarAsync())!);
            Assert.Equal(3, (await itemStore.GetAsync(itemId, CancellationToken.None))!.Quantity);

            await Assert.ThrowsAsync<InsufficientFundsException>(() => writer.CreateItemExchangeAsync(
                new Eve.Services.Core.CreateItemExchangeRequest(
                    IssuerCharacterId: 100,
                    IssuerCorporationId: 200,
                    IsCorporationIssued: false,
                    StartStationId: 600,
                    StartStationDivision: ItemFlags.Hangar,
                    Availability: 0,
                    AssigneeId: 0,
                    ExpiryMinutes: 1440,
                    Price: 10,
                    Reward: 10_000,
                    BrokerFee: 1,
                    Deposit: 0,
                    SalesTax: 0,
                    WalletAccountKey: 1000,
                    Title: "Insufficient balance",
                    Description: "",
                    Items: [new Eve.Services.Core.ContractOfferedItem(itemId, 1)]), CancellationToken.None).AsTask());
            Assert.Equal(948m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));
            Assert.Equal(3, (await itemStore.GetAsync(itemId, CancellationToken.None))!.Quantity);
            await using (var read = source.CreateCommand($"SELECT count(*) FROM {contractsSchema}.contract_records"))
                Assert.Equal(1L, (long)(await read.ExecuteScalarAsync())!);
        }
        finally
        {
            foreach (var schema in new[] { contractsSchema, inventorySchema, walletSchema })
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task Login_and_escrow_reads_use_persisted_contract_rows_and_owner_scopes()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var schema = $"contract_test_{Guid.NewGuid():N}";
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, schema);
        try
        {
            var now = 134_000_000_000_000_000L;
            await SeedAsync(source, schema, now);
            var store = new PersistenceContractReadModel(source, schema);

            var login = await store.GetLoginInfoAsync(100, 200, 300, now, CancellationToken.None);
            Assert.Equal(new[] { new Eve.Services.Core.ContractNeedsAttentionRow(3, 1), new Eve.Services.Core.ContractNeedsAttentionRow(4, 0), new Eve.Services.Core.ContractNeedsAttentionRow(5, 0) }, login.NeedsAttention);
            Assert.Equal(new[] { new Eve.Services.Core.ContractInProgressRow(2, 3001, 3002, now + 4 * 864_000_000_000L) }, login.InProgress);
            Assert.Equal(new[] { new Eve.Services.Core.ContractAssignedToMeRow(1, 100), new Eve.Services.Core.ContractAssignedToMeRow(7, 107) }, login.AssignedToMe);

            Assert.Equal(new Eve.Services.Core.ContractEscrowSummary(22m, 3),
                await store.GetMyContractEscrowAsync(100, 200, CancellationToken.None));
            Assert.Equal(new Eve.Services.Core.ContractEscrowSummary(27m, 1),
                await store.GetMyContractEscrowAsync(101, 200, CancellationToken.None));
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedAsync(NpgsqlDataSource source, string schema, long now)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{schema}".contract_records
                (contract_id, issuer_id, issuer_corporation_id, is_corporation_issued, acceptor_id, assignee_id,
                 contract_type, status, start_station_id, end_station_id, date_accepted_filetime,
                 date_expired_filetime, num_days, reward_escrow, created_at_filetime)
            VALUES
                (1, 100, 200, false, 0, 100, 1, 0, 0, 0, 0, $1, 0, 10, $2),
                (2, 101, 200, false, 100, 0, 3, 1, 3001, 3002, $2 - 864000000000, $1, 5, 20, $2),
                (3, 101, 200, true, 0, 0, 1, 7, 0, 0, 0, $1, 0, 7, $2),
                (4, 102, 200, false, 100, 0, 1, 6, 0, 0, 0, $1, 0, 2, $2),
                (5, 100, 200, false, 0, 0, 1, 0, 0, 0, 0, $3, 0, 5, $2),
                (6, 106, 200, false, 0, 400, 2, 0, 0, 0, 0, $1, 0, 0, $2),
                (7, 107, 200, false, 0, 300, 2, 0, 0, 0, 0, $1, 0, 0, $2)
            """, connection);
        command.Parameters.AddWithValue(now + 10_000_000L);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(now - 1L);
        await command.ExecuteNonQueryAsync();

        await using var items = new NpgsqlCommand($"""
            INSERT INTO "{schema}".contract_items (contract_id, item_id, type_id, quantity, in_crate, is_package)
            VALUES (1, 9001, 34, 1, true, false), (1, 9002, 35, 5, false, true), (3, 9003, 36, 1, true, false)
            """, connection);
        await items.ExecuteNonQueryAsync();
    }
}
