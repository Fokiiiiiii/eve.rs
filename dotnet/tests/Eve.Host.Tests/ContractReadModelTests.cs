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
                OutstandingLimit: 500,
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

            await Assert.ThrowsAsync<Eve.Services.Core.ContractUserErrorException>(() => writer.CreateItemExchangeAsync(
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
                    OutstandingLimit: 500,
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
                    OutstandingLimit: 500,
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
    public async Task Item_exchange_acceptance_transfers_requested_and_offered_items_and_settles_wallet_legs_atomically()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_accept_{suffix}";
        var inventorySchema = $"contract_accept_items_{suffix}";
        var walletSchema = $"contract_accept_wallet_{suffix}";
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
                await wallets.EnsureAsync(connection, transaction, 300);
                await using var seed = new NpgsqlCommand($"UPDATE {walletSchema}.balances SET isk=1000 WHERE account_key=1000", connection, transaction);
                await seed.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            long offeredId;
            long requestedId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                offeredId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(34, 100, 600, ItemFlags.Hangar, Quantity: 2), CancellationToken.None);
                requestedId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(35, 300, 600, ItemFlags.Hangar, Quantity: 5), CancellationToken.None);
                await transaction.CommitAsync();
            }

            var writer = new PersistenceContractWriteModel(source, itemStore, wallets, () => 1_800_000_000_000, contractsSchema);
            var created = await writer.CreateContractAsync(new Eve.Services.Core.CreateItemExchangeRequest(
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
                BrokerFee: 0,
                Deposit: 0,
                SalesTax: 0,
                WalletAccountKey: 1000,
                Title: "Exchange",
                Description: "",
                OutstandingLimit: 500,
                Items: [new Eve.Services.Core.ContractOfferedItem(offeredId, 2)],
                RequestedItems: [new Eve.Services.Core.ContractRequestedItem(35, 3)]), CancellationToken.None);

            var results = await Task.WhenAll(
                writer.AcceptItemExchangeAsync(created.ContractId, 300, 400, 0, false, 1000, CancellationToken.None).AsTask(),
                writer.AcceptItemExchangeAsync(created.ContractId, 300, 400, 0, false, 1000, CancellationToken.None).AsTask());
            foreach (var accepted in results)
                Assert.Equal((created.ContractId, (short)1, (short)4, 300L), (accepted.ContractId, accepted.Type, accepted.Status, accepted.AcceptorId));
            Assert.Equal(1050m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));
            Assert.Equal(950m, await wallets.AccountBalanceAsync(new WalletAccount(300), CancellationToken.None));
            var offered = await itemStore.GetAsync(created.EscrowItemIds.Single(), CancellationToken.None);
            Assert.NotNull(offered);
            Assert.Equal((300L, 600L, ItemFlags.Hangar), (offered.OwnerId, offered.LocationId, offered.Flag));
            var requested = await itemStore.GetAsync(requestedId, CancellationToken.None);
            Assert.NotNull(requested);
            Assert.Equal((300L, 600L, ItemFlags.Hangar), (requested.OwnerId, requested.LocationId, requested.Flag));
            Assert.Equal(2, requested.Quantity);
            await using var delivered = source.CreateCommand($"SELECT COALESCE(sum(quantity), 0)::bigint FROM {inventorySchema}.items WHERE owner_id=100 AND location_id=600 AND type_id=35 AND flag=4");
            Assert.Equal(3L, (long)(await delivered.ExecuteScalarAsync())!);
            await using var read = source.CreateCommand($"SELECT status, acceptor_id, date_accepted_filetime, reward_isk, reward_escrow FROM {contractsSchema}.contract_records WHERE contract_id=$1");
            read.Parameters.AddWithValue(created.ContractId);
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((short)4, reader.GetInt16(0));
            Assert.Equal(300L, reader.GetInt64(1));
            Assert.True(reader.GetInt64(2) > 0);
            Assert.Equal(50m, reader.GetDecimal(3));
            Assert.Equal(0m, reader.GetDecimal(4));
            await reader.DisposeAsync();
            var escrowSummary = await new PersistenceContractReadModel(source, contractsSchema)
                .GetMyContractEscrowAsync(100, 200, CancellationToken.None);
            Assert.Equal((0m, 0L), (escrowSummary.IskEscrow, escrowSummary.ItemsEscrow));
            await using var crate = source.CreateCommand($"SELECT count(*) FROM {contractsSchema}.contract_items WHERE contract_id=$1 AND in_crate");
            crate.Parameters.AddWithValue(created.ContractId);
            Assert.Equal(0L, (long)(await crate.ExecuteScalarAsync())!);
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
    public async Task Auction_buyout_refunds_previous_bidder_and_transfers_items_and_sale_proceeds()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_bid_{suffix}";
        var inventorySchema = $"contract_bid_items_{suffix}";
        var walletSchema = $"contract_bid_wallet_{suffix}";
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
                foreach (var owner in new long[] { 100, 300, 400 })
                    await wallets.EnsureAsync(connection, transaction, owner);
                await using var seed = new NpgsqlCommand($"UPDATE {walletSchema}.balances SET isk=50000 WHERE account_key=1000", connection, transaction);
                await seed.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            long itemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                itemId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(34, 100, 600, ItemFlags.Hangar, Quantity: 2), CancellationToken.None);
                await transaction.CommitAsync();
            }
            var writer = new PersistenceContractWriteModel(source, itemStore, wallets, () => 1_800_000_000_000, contractsSchema);
            var created = await writer.CreateContractAsync(new Eve.Services.Core.CreateItemExchangeRequest(
                IssuerCharacterId: 100,
                IssuerCorporationId: 200,
                IsCorporationIssued: false,
                StartStationId: 600,
                StartStationDivision: ItemFlags.Hangar,
                Availability: 0,
                AssigneeId: 0,
                ExpiryMinutes: 1440,
                Price: 10000,
                Reward: 0,
                BrokerFee: 0,
                Deposit: 0,
                SalesTax: 0,
                WalletAccountKey: 1000,
                Title: "Auction",
                Description: "",
                OutstandingLimit: 500,
                Items: [new Eve.Services.Core.ContractOfferedItem(itemId, 2)],
                ContractType: 2,
                Collateral: 20000), CancellationToken.None);

            Assert.True(await writer.PlaceBidAsync(created.ContractId, 300, 301, 0, false, 1000, 10000, CancellationToken.None));
            Assert.True(await writer.PlaceBidAsync(created.ContractId, 400, 401, 0, false, 1000, 20000, CancellationToken.None));

            Assert.Equal(70000m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));
            Assert.Equal(50000m, await wallets.AccountBalanceAsync(new WalletAccount(300), CancellationToken.None));
            Assert.Equal(30000m, await wallets.AccountBalanceAsync(new WalletAccount(400), CancellationToken.None));
            var item = await itemStore.GetAsync(created.EscrowItemIds.Single(), CancellationToken.None);
            Assert.NotNull(item);
            Assert.Equal((400L, 600L, ItemFlags.Hangar), (item.OwnerId, item.LocationId, item.Flag));
            await using var read = source.CreateCommand($"SELECT status, acceptor_id, reward_isk, reward_escrow FROM {contractsSchema}.contract_records WHERE contract_id=$1");
            read.Parameters.AddWithValue(created.ContractId);
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((short)4, reader.GetInt16(0));
            Assert.Equal(400L, reader.GetInt64(1));
            Assert.Equal(0m, reader.GetDecimal(2));
            Assert.Equal(0m, reader.GetDecimal(3));
            await reader.DisposeAsync();
            await using var bids = source.CreateCommand($"SELECT bidder_id, amount, is_refunded FROM {contractsSchema}.contract_bids WHERE contract_id=$1 ORDER BY bid_id");
            bids.Parameters.AddWithValue(created.ContractId);
            await using var bidReader = await bids.ExecuteReaderAsync();
            Assert.True(await bidReader.ReadAsync());
            Assert.Equal((300L, 10000m, true), (bidReader.GetInt64(0), bidReader.GetDecimal(1), bidReader.GetBoolean(2)));
            Assert.True(await bidReader.ReadAsync());
            Assert.Equal((400L, 20000m, false), (bidReader.GetInt64(0), bidReader.GetDecimal(1), bidReader.GetBoolean(2)));
            Assert.False(await bidReader.ReadAsync());
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
    public async Task Courier_acceptance_and_failure_settle_collateral_and_return_the_package()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_courier_{suffix}";
        var inventorySchema = $"contract_courier_items_{suffix}";
        var walletSchema = $"contract_courier_wallet_{suffix}";
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
                foreach (var owner in new long[] { 100, 300 })
                    await wallets.EnsureAsync(connection, transaction, owner);
                await using var seed = new NpgsqlCommand($"UPDATE {walletSchema}.balances SET isk=50000 WHERE account_key=1000", connection, transaction);
                await seed.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            long itemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                itemId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(34, 100, 600, ItemFlags.Hangar, Quantity: 2), CancellationToken.None);
                await transaction.CommitAsync();
            }
            var writer = new PersistenceContractWriteModel(source, itemStore, wallets, () => 1_800_000_000_000, contractsSchema);
            var created = await writer.CreateContractAsync(new Eve.Services.Core.CreateItemExchangeRequest(
                IssuerCharacterId: 100, IssuerCorporationId: 200, IsCorporationIssued: false, StartStationId: 600,
                StartStationDivision: ItemFlags.Hangar, Availability: 0, AssigneeId: 0, ExpiryMinutes: 1440,
                Price: 0, Reward: 5000, BrokerFee: 0, Deposit: 0, SalesTax: 0, WalletAccountKey: 1000,
                Title: "Courier", Description: "", OutstandingLimit: 500,
                Items: [new Eve.Services.Core.ContractOfferedItem(itemId, 2)], ContractType: 3,
                EndStationId: 700, Collateral: 10000, NumDays: 7), CancellationToken.None);

            var accepted = await writer.AcceptContractAsync(created.ContractId, 300, 301, 0, false, 1000, 600, CancellationToken.None);
            Assert.Equal((short)1, accepted.Status);
            Assert.Equal(40000m, await wallets.AccountBalanceAsync(new WalletAccount(300), CancellationToken.None));
            Assert.True(await writer.CompleteContractAsync(created.ContractId, 5, 300, 301, 600, false, CancellationToken.None));

            Assert.Equal(60000m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));
            Assert.Equal(40000m, await wallets.AccountBalanceAsync(new WalletAccount(300), CancellationToken.None));
            var item = await itemStore.GetAsync(created.EscrowItemIds.Single(), CancellationToken.None);
            Assert.NotNull(item);
            Assert.Equal((300L, 600L, ItemFlags.Hangar), (item.OwnerId, item.LocationId, item.Flag));
            var wrapId = await source.CreateCommand($"SELECT plastic_wrap_item_id FROM {contractsSchema}.contract_records WHERE contract_id={created.ContractId}").ExecuteScalarAsync();
            Assert.Equal(0L, (long)(wrapId ?? -1L));

            long successItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                successItemId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(35, 100, 600, ItemFlags.Hangar, Quantity: 1), CancellationToken.None);
                await transaction.CommitAsync();
            }
            var successContract = await writer.CreateContractAsync(new Eve.Services.Core.CreateItemExchangeRequest(
                IssuerCharacterId: 100, IssuerCorporationId: 200, IsCorporationIssued: false, StartStationId: 600,
                StartStationDivision: ItemFlags.Hangar, Availability: 0, AssigneeId: 0, ExpiryMinutes: 1440,
                Price: 0, Reward: 5000, BrokerFee: 0, Deposit: 0, SalesTax: 0, WalletAccountKey: 1000,
                Title: "Courier success", Description: "", OutstandingLimit: 500,
                Items: [new Eve.Services.Core.ContractOfferedItem(successItemId, 1)], ContractType: 3,
                EndStationId: 700, Collateral: 10000, NumDays: 7), CancellationToken.None);
            await writer.AcceptContractAsync(successContract.ContractId, 300, 301, 0, false, 1000, 600, CancellationToken.None);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var getWrap = new NpgsqlCommand($"SELECT plastic_wrap_item_id FROM {contractsSchema}.contract_records WHERE contract_id=$1", connection, transaction);
                getWrap.Parameters.AddWithValue(successContract.ContractId);
                var successWrapId = (long)(await getWrap.ExecuteScalarAsync() ?? 0L);
                await itemStore.MoveToOwnerAsync(connection, transaction, successWrapId, 300, 700, ItemFlags.Hangar, CancellationToken.None);
                await transaction.CommitAsync();
            }
            Assert.True(await writer.CompleteContractAsync(successContract.ContractId, 4, 300, 301, 700, false, CancellationToken.None));
            Assert.Equal(55000m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));
            Assert.Equal(45000m, await wallets.AccountBalanceAsync(new WalletAccount(300), CancellationToken.None));
            var deliveredItem = await itemStore.GetAsync(successContract.EscrowItemIds.Single(), CancellationToken.None);
            Assert.NotNull(deliveredItem);
            Assert.Equal((100L, 700L, ItemFlags.Hangar), (deliveredItem.OwnerId, deliveredItem.LocationId, deliveredItem.Flag));
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
    public async Task Outstanding_limit_refuses_creation_without_charging_and_requested_terms_are_stored()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_limit_{suffix}";
        var inventorySchema = $"contract_limit_items_{suffix}";
        var walletSchema = $"contract_limit_wallet_{suffix}";
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
            Eve.Services.Core.CreateItemExchangeRequest Request(long quantity, int limit, IReadOnlyList<Eve.Services.Core.ContractRequestedItem>? requested = null) =>
                new(IssuerCharacterId: 100, IssuerCorporationId: 200, IsCorporationIssued: false, StartStationId: 600,
                    StartStationDivision: ItemFlags.Hangar, Availability: 0, AssigneeId: 0, ExpiryMinutes: 1440,
                    Price: 100, Reward: 0, BrokerFee: 1, Deposit: 0, SalesTax: 0, WalletAccountKey: 1000,
                    Title: "Limited", Description: "", OutstandingLimit: limit,
                    Items: [new Eve.Services.Core.ContractOfferedItem(itemId, quantity)], RequestedItems: requested);

            var first = await writer.CreateContractAsync(
                Request(2, 1, [new Eve.Services.Core.ContractRequestedItem(35, 4)]), CancellationToken.None);
            Assert.Equal(999m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));
            await using (var read = source.CreateCommand($"SELECT type_id, quantity FROM {contractsSchema}.contract_requested_items WHERE contract_id=$1"))
            {
                read.Parameters.AddWithValue(first.ContractId);
                await using var reader = await read.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(35, reader.GetInt32(0));
                Assert.Equal(4L, reader.GetInt64(1));
            }

            var refused = await Assert.ThrowsAsync<Eve.Services.Core.ContractUserErrorException>(() =>
                writer.CreateContractAsync(Request(1, 1), CancellationToken.None).AsTask());
            Assert.Equal("CustomInfo", refused.Message);
            Assert.Equal(3, (await itemStore.GetAsync(itemId, CancellationToken.None))!.Quantity);
            await using (var read = source.CreateCommand($"SELECT count(*) FROM {contractsSchema}.contract_records"))
                Assert.Equal(1L, (long)(await read.ExecuteScalarAsync())!);

            var second = await writer.CreateContractAsync(Request(1, 2), CancellationToken.None);
            Assert.Equal(998m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));
            Assert.NotEqual(first.ContractId, second.ContractId);
            Assert.Equal(2, (await itemStore.GetAsync(itemId, CancellationToken.None))!.Quantity);
            await using (var read = source.CreateCommand($"SELECT count(*) FROM {contractsSchema}.contract_records"))
                Assert.Equal(2L, (long)(await read.ExecuteScalarAsync())!);
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
