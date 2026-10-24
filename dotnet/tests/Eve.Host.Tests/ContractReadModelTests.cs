using Eve.Data;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Services.Core;
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
        var schedulerSchema = $"contract_scheduler_{suffix}";
        var eventsSchema = $"contract_events_{suffix}";
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, contractsSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schedulerSchema);
        await Eve.Events.EventBus.MigrateAsync(source, eventsSchema);
        var scheduler = new Eve.Scheduler.DurableScheduler(source, () => 1_800_000_000_000, schema: schedulerSchema);
        var events = new Eve.Events.EventBus(source, scheduler, schema: eventsSchema);
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

            var writer = new PersistenceContractWriteModel(source, itemStore, wallets, () => 1_800_000_000_000, contractsSchema, events: events);
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
            await using (var journal = source.CreateCommand($"SELECT count(*) FROM {walletSchema}.journal WHERE operation_key LIKE $1"))
            {
                journal.Parameters.AddWithValue($"contract:{created.ContractId}:create:%");
                Assert.Equal(3L, (long)(await journal.ExecuteScalarAsync())!);
            }
            await using (var notices = source.CreateCommand($"SELECT payload->>'name' FROM {eventsSchema}.outbox WHERE topic = $1 ORDER BY id"))
            {
                notices.Parameters.AddWithValue(Eve.Host.Persistence.ContractNotifications.Topic);
                Assert.Equal("OnContractCreated", (string?)await notices.ExecuteScalarAsync());
            }

            long foreignItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                foreignItemId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(34, 101, 600, ItemFlags.Hangar, Quantity: 1), CancellationToken.None);
                await transaction.CommitAsync();
            }
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
                    Title: "Foreign item",
                    Description: "",
                    OutstandingLimit: 500,
                    Items: [new Eve.Services.Core.ContractOfferedItem(foreignItemId, 1)]), CancellationToken.None).AsTask());
            Assert.Equal(948m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));
            Assert.Equal((long)101, (await itemStore.GetAsync(foreignItemId, CancellationToken.None))!.OwnerId);
            await using (var read = source.CreateCommand($"SELECT count(*) FROM {contractsSchema}.contract_records"))
                Assert.Equal(1L, (long)(await read.ExecuteScalarAsync())!);

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
            await using (var notices = source.CreateCommand($"SELECT count(*) FROM {eventsSchema}.outbox WHERE topic = $1"))
            {
                notices.Parameters.AddWithValue(Eve.Host.Persistence.ContractNotifications.Topic);
                Assert.Equal(1L, (long)(await notices.ExecuteScalarAsync())!);
            }
            await using (var read = source.CreateCommand($"SELECT count(*) FROM {contractsSchema}.contract_records"))
                Assert.Equal(1L, (long)(await read.ExecuteScalarAsync())!);
        }
        finally
        {
            foreach (var schema in new[] { contractsSchema, inventorySchema, walletSchema, schedulerSchema, eventsSchema })
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
    public async Task Contract_search_filters_persisted_public_rows_and_attaches_sde_item_and_location_data()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var schema = $"contract_search_{Guid.NewGuid():N}";
        var sdeSchema = $"sde_contract_search_{Guid.NewGuid():N}";
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, schema);
        await using (var createSde = source.CreateCommand($"""
            CREATE SCHEMA "{sdeSchema}";
            CREATE TABLE "{sdeSchema}".npcStations (ord integer NOT NULL, key bigint PRIMARY KEY, doc jsonb NOT NULL);
            CREATE TABLE "{sdeSchema}".mapSolarSystems (ord integer NOT NULL, key bigint PRIMARY KEY, doc jsonb NOT NULL);
            CREATE TABLE "{sdeSchema}".types (ord integer NOT NULL, key bigint PRIMARY KEY, doc jsonb NOT NULL);
            CREATE TABLE "{sdeSchema}".groups (ord integer NOT NULL, key bigint PRIMARY KEY, doc jsonb NOT NULL);
            """))
        {
            await createSde.ExecuteNonQueryAsync();
        }

        try
        {
            const long now = 134_000_000_000_000_000L;
            await using (var seed = source.CreateCommand($"""
                INSERT INTO "{schema}".contract_records
                    (contract_id, issuer_id, issuer_corporation_id, is_corporation_issued, acceptor_id, assignee_id,
                     contract_type, status, availability, start_station_id, end_station_id, date_accepted_filetime,
                     date_expired_filetime, num_days, reward_escrow, created_at_filetime, price_isk, reward_isk,
                     collateral_isk, title, description)
                VALUES
                    (10, 100, 200, false, 0, 0, 1, 0, 0, 6001, 0, 0, $1, 0, 0, $2, 100, 0, 0, 'Rifter', 'blueprint package'),
                    (11, 101, 200, false, 0, 0, 1, 0, 0, 6002, 0, 0, $1, 0, 0, $2, 100, 0, 0, 'Other', 'different system'),
                    (12, 102, 200, false, 0, 999, 1, 0, 1, 6001, 0, 0, $1, 0, 0, $2, 100, 0, 0, 'Private', 'not assigned to caller')
                """))
            {
                seed.Parameters.AddWithValue(now + 864_000_000_000L);
                seed.Parameters.AddWithValue(now);
                await seed.ExecuteNonQueryAsync();
            }
            await using (var items = source.CreateCommand($"""
                INSERT INTO "{schema}".contract_items (contract_id, item_id, type_id, quantity, in_crate, is_package)
                VALUES (10, 7001, 34, 2, true, false), (11, 7002, 35, 1, true, false)
                """))
            {
                await items.ExecuteNonQueryAsync();
            }
            await using (var seedSde = source.CreateCommand($$$"""
                INSERT INTO "{{{sdeSchema}}}".npcStations (ord, key, doc)
                VALUES (0, 6001, '{"solarSystemID":30000001}'::jsonb),
                       (1, 6002, '{"solarSystemID":30000002}'::jsonb);
                INSERT INTO "{{{sdeSchema}}}".mapSolarSystems (ord, key, doc)
                VALUES (0, 30000001, '{"constellationID":20000001,"regionID":10000001,"securityStatus":0.8,"name":{"en":"Solar System"}}'::jsonb),
                       (1, 30000002, '{"constellationID":20000002,"regionID":10000002,"securityStatus":0.2,"name":{"en":"Other System"}}'::jsonb);
                INSERT INTO "{{{sdeSchema}}}".types (ord, key, doc)
                VALUES (0, 34, '{"groupID":300,"volume":5,"name":{"en":"Rifter"}}'::jsonb),
                       (1, 35, '{"groupID":301,"volume":10,"name":{"en":"Other"}}'::jsonb);
                INSERT INTO "{{{sdeSchema}}}".groups (ord, key, doc)
                VALUES (0, 300, '{"categoryID":6}'::jsonb),
                       (1, 301, '{"categoryID":6}'::jsonb);
                """))
            {
                await seedSde.ExecuteNonQueryAsync();
            }

            var store = new PersistenceContractReadModel(source, schema, sdeSchema);
            var result = await store.SearchContractsAsync(new ContractSearchQuery(
                CharacterId: 42,
                CorporationId: 200,
                AllianceId: 300,
                Availability: 0,
                ContractType: 10,
                LocationId: 30000001,
                EndLocationId: 0,
                IssuerId: 0,
                MinPrice: 100,
                MaxPrice: null,
                MinReward: null,
                MaxReward: null,
                MinCollateral: null,
                MaxCollateral: null,
                MinVolume: 10,
                MaxVolume: 10,
                ExcludeTrade: true,
                ExcludeMultiple: true,
                ExcludeNoBuyout: false,
                Description: "blueprint",
                ItemTypeIds: [34],
                ItemCategoryId: 6,
                ItemGroupId: 300,
                ItemTypeName: "Rifter",
                SecurityClasses: [2],
                SearchHint: 0,
                SortBy: 1,
                SortDirection: 1,
                HasExplicitSortBy: true,
                StartNum: 0), now, CancellationToken.None);

            var entry = Assert.Single(result.Contracts);
            Assert.Equal(1, result.NumFound);
            Assert.Equal(10, entry.Contract.ContractId);
            Assert.Equal((30000001L, 20000001L, 10000001L), (entry.StartSolarSystemId, entry.StartConstellationId, entry.StartRegionId));
            Assert.Equal("Solar System", entry.StartSolarSystemName);
            Assert.Equal(10, entry.Volume);
            Assert.Equal(new[] { 34 }, entry.ItemTypeIds);
            Assert.Equal(new[] { 300 }, entry.ItemGroupIds);
            Assert.Equal(new[] { 6 }, entry.ItemCategoryIds);
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE; DROP SCHEMA IF EXISTS \"{sdeSchema}\" CASCADE");
            await cleanup.ExecuteNonQueryAsync();
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
            await using (var seedAuction = source.CreateCommand($"""
                UPDATE "{schema}".contract_records SET title = 'Auction', price_isk = 5000, reward_isk = 200
                WHERE contract_id = 6
                """))
            {
                await seedAuction.ExecuteNonQueryAsync();
            }
            await using (var seedItem = source.CreateCommand($"""
                INSERT INTO "{schema}".contract_items (contract_id, item_id, type_id, quantity, in_crate)
                VALUES (6, 9006, 34, 2, true)
                """))
            {
                await seedItem.ExecuteNonQueryAsync();
            }
            await using (var seedBids = source.CreateCommand($"""
                INSERT INTO "{schema}".contract_bids
                    (contract_id, bidder_id, amount, created_at_filetime, bidder_is_corporation, wallet_account_key, is_refunded)
                VALUES (6, 100, 5000, $1, false, 1000, false),
                       (7, 200, 6000, $1, true, 1000, false),
                       (6, 101, 5500, $1, false, 1000, true)
                """))
            {
                seedBids.Parameters.AddWithValue(now);
                await seedBids.ExecuteNonQueryAsync();
            }
            var store = new PersistenceContractReadModel(source, schema);

            var login = await store.GetLoginInfoAsync(100, 200, 300, now, CancellationToken.None);
            Assert.Equal(new[] { new Eve.Services.Core.ContractNeedsAttentionRow(3, 1), new Eve.Services.Core.ContractNeedsAttentionRow(4, 0), new Eve.Services.Core.ContractNeedsAttentionRow(5, 0) }, login.NeedsAttention);
            Assert.Equal(new[] { new Eve.Services.Core.ContractInProgressRow(2, 3001, 3002, now + 4 * 864_000_000_000L) }, login.InProgress);
            Assert.Equal(new[] { new Eve.Services.Core.ContractAssignedToMeRow(1, 100), new Eve.Services.Core.ContractAssignedToMeRow(7, 107) }, login.AssignedToMe);

            Assert.Equal(new Eve.Services.Core.ContractEscrowSummary(22m, 3),
                await store.GetMyContractEscrowAsync(100, 200, CancellationToken.None));
            Assert.Equal(new Eve.Services.Core.ContractEscrowSummary(27m, 1),
                await store.GetMyContractEscrowAsync(101, 200, CancellationToken.None));

            var personalBids = await store.GetMyBidsAsync(100, 200, false, now, CancellationToken.None);
            var personalBid = Assert.Single(personalBids.Contracts);
            Assert.Equal(6, personalBid.ContractId);
            Assert.Equal("Auction", personalBid.Title);
            Assert.Equal(5000m, personalBid.Price);
            Assert.Equal(new Eve.Services.Core.ContractReadItemSnapshot(9006, 34, 2, true, false), Assert.Single(personalBid.Items));
            Assert.Equal(new Eve.Services.Core.ContractReadBidSnapshot(1, 100, 5000m, now, false), Assert.Single(personalBid.Bids));

            var corporationBids = await store.GetMyBidsAsync(100, 200, true, now, CancellationToken.None);
            Assert.Equal(7, Assert.Single(corporationBids.Contracts).ContractId);

            var ownerPage = await store.GetContractListForOwnerAsync(100, 200, 100, 0, 1, 0, 1, null, now, CancellationToken.None);
            Assert.Equal(1, Assert.Single(ownerPage.Contracts).ContractId);
            var nextOwnerPage = await store.GetContractListForOwnerAsync(100, 200, 100, 0, 1, 0, 1, 1, now, CancellationToken.None);
            Assert.Empty(nextOwnerPage.Contracts);
            var attentionPage = await store.GetContractListForOwnerAsync(100, 200, 100, -1, 0, 0, 10, null, now, CancellationToken.None);
            Assert.Equal(new long[] { 5, 4 }, attentionPage.Contracts.Select(contract => contract.ContractId));

            var myIssued = await store.GetMyCurrentContractListAsync(100, 200, false, false, now, CancellationToken.None);
            Assert.Equal(1, Assert.Single(myIssued.Contracts).ContractId);
            var accepted = await store.GetMyCurrentContractListAsync(100, 200, true, false, now, CancellationToken.None);
            Assert.Equal(2, Assert.Single(accepted.Contracts).ContractId);
            var myExpired = await store.GetMyExpiredContractListAsync(100, 200, false, now, CancellationToken.None);
            Assert.Equal(new long[] { 5, 4 }, myExpired.Contracts.Select(contract => contract.ContractId));
            var corpExpired = await store.GetMyExpiredContractListAsync(100, 200, true, now, CancellationToken.None);
            Assert.Equal(3, Assert.Single(corpExpired.Contracts).ContractId);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetMyBidsAsync(100, 0, true, now, CancellationToken.None).AsTask());
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Expired_auction_settles_the_winning_bid_and_returns_the_public_deposit()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_auction_expiry_{suffix}";
        var inventorySchema = $"contract_auction_expiry_items_{suffix}";
        var walletSchema = $"contract_auction_expiry_wallet_{suffix}";
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
                foreach (var owner in new long[] { 100, 300 }) await wallets.EnsureAsync(connection, transaction, owner);
                await using var seed = new NpgsqlCommand($"UPDATE {walletSchema}.balances SET isk=5000 WHERE owner_id=100 AND account_key=1000", connection, transaction);
                await seed.ExecuteNonQueryAsync();
                await using var bidderSeed = new NpgsqlCommand($"UPDATE {walletSchema}.balances SET isk=5000 WHERE owner_id=300 AND account_key=1000", connection, transaction);
                await bidderSeed.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            long itemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                itemId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(34, 100, 600, ItemFlags.Hangar, Quantity: 1), CancellationToken.None);
                await transaction.CommitAsync();
            }
            const long now = 1_800_000_000_000;
            var writer = new PersistenceContractWriteModel(source, itemStore, wallets, () => now, contractsSchema);
            var created = await writer.CreateContractAsync(new Eve.Services.Core.CreateItemExchangeRequest(
                IssuerCharacterId: 100, IssuerCorporationId: 200, IsCorporationIssued: false,
                StartStationId: 600, StartStationDivision: ItemFlags.Hangar, Availability: 0, AssigneeId: 0,
                ExpiryMinutes: 1440, Price: 100, Reward: 0, BrokerFee: 0, Deposit: 50, SalesTax: 0,
                WalletAccountKey: 1000, Title: "Auction expiry", Description: "", OutstandingLimit: 500,
                Items: [new Eve.Services.Core.ContractOfferedItem(itemId, 1)], ContractType: 2, Collateral: 2000),
                CancellationToken.None);
            Assert.True(await writer.PlaceBidAsync(created.ContractId, 300, 301, 0, false, 1000, 1000, CancellationToken.None));
            var jobs = new ContractExpiryJobs(source, itemStore, wallets,
                new Eve.Scheduler.DurableScheduler(source, () => now), contractsSchema);

            Assert.True(await jobs.SettleExpiredAsync(created.ContractId, now + 100_000_000, CancellationToken.None));
            var delivered = await itemStore.GetAsync(created.EscrowItemIds.Single(), CancellationToken.None);
            Assert.NotNull(delivered);
            Assert.Equal((300L, 600L, ItemFlags.Hangar), (delivered.OwnerId, delivered.LocationId, delivered.Flag));
            Assert.Equal(6000m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));
            Assert.Equal(4000m, await wallets.AccountBalanceAsync(new WalletAccount(300), CancellationToken.None));
            await using var read = source.CreateCommand($"SELECT status, acceptor_id FROM {contractsSchema}.contract_records WHERE contract_id=$1");
            read.Parameters.AddWithValue(created.ContractId);
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((short)4, reader.GetInt16(0));
            Assert.Equal(300L, reader.GetInt64(1));
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
    public async Task Delete_auction_returns_items_reward_and_active_bids_and_cancels_expiry()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_delete_{suffix}";
        var inventorySchema = $"contract_delete_items_{suffix}";
        var walletSchema = $"contract_delete_wallet_{suffix}";
        var schedulerSchema = $"contract_delete_jobs_{suffix}";
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, contractsSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schedulerSchema);
        try
        {
            var itemStore = new PgItemStore(source, inventorySchema);
            var wallets = new WalletTransactions(source, walletSchema);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                foreach (var owner in new long[] { 100, 300 }) await wallets.EnsureAsync(connection, transaction, owner);
                await using var seed = new NpgsqlCommand($"UPDATE {walletSchema}.balances SET isk=1000 WHERE account_key=1000", connection, transaction);
                await seed.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            long itemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                itemId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(34, 100, 600, ItemFlags.Hangar, Quantity: 1), CancellationToken.None);
                await transaction.CommitAsync();
            }
            const long now = 1_800_000_000_000;
            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => now, schema: schedulerSchema);
            var expiryJobs = new ContractExpiryJobs(source, itemStore, wallets, scheduler, contractsSchema);
            var writer = new PersistenceContractWriteModel(source, itemStore, wallets, () => now, contractsSchema, expiryJobs);
            var created = await writer.CreateContractAsync(new Eve.Services.Core.CreateItemExchangeRequest(
                IssuerCharacterId: 100, IssuerCorporationId: 200, IsCorporationIssued: false,
                StartStationId: 600, StartStationDivision: ItemFlags.Hangar, Availability: 0, AssigneeId: 0,
                ExpiryMinutes: 1440, Price: 100, Reward: 25, BrokerFee: 0, Deposit: 0, SalesTax: 0,
                WalletAccountKey: 1000, Title: "Auction", Description: "", OutstandingLimit: 500,
                Items: [new Eve.Services.Core.ContractOfferedItem(itemId, 1)], ContractType: 2, Collateral: 2000),
                CancellationToken.None);
            Assert.True(await writer.PlaceBidAsync(created.ContractId, 300, 301, 0, false, 1000, 1000, CancellationToken.None));

            Assert.True(await writer.DeleteContractAsync(created.ContractId, 100, 200, false, CancellationToken.None));
            Assert.True(await writer.DeleteContractAsync(created.ContractId, 100, 200, false, CancellationToken.None));
            var returned = await itemStore.GetAsync(created.EscrowItemIds.Single(), CancellationToken.None);
            Assert.NotNull(returned);
            Assert.Equal((100L, 600L, ItemFlags.Hangar), (returned.OwnerId, returned.LocationId, returned.Flag));
            Assert.Equal(1000m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));
            Assert.Equal(1000m, await wallets.AccountBalanceAsync(new WalletAccount(300), CancellationToken.None));
            var jobs = await scheduler.ListAsync(ContractExpiryJobs.JobKind, "pending", cancellationToken: CancellationToken.None);
            Assert.DoesNotContain(jobs, job => job.Key == created.ContractId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await using var read = source.CreateCommand($"SELECT status, reward_escrow FROM {contractsSchema}.contract_records WHERE contract_id=$1");
            read.Parameters.AddWithValue(created.ContractId);
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((short)5, reader.GetInt16(0));
            Assert.Equal(0m, reader.GetDecimal(1));
        }
        finally
        {
            foreach (var schema in new[] { contractsSchema, inventorySchema, walletSchema, schedulerSchema })
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task Expired_item_exchange_returns_escrow_and_reward_and_marks_the_contract_expired()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_expiry_{suffix}";
        var inventorySchema = $"contract_expiry_items_{suffix}";
        var walletSchema = $"contract_expiry_wallet_{suffix}";
        var schedulerSchema = $"contract_expiry_jobs_{suffix}";
        var eventsSchema = $"contract_expiry_events_{suffix}";
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, contractsSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schedulerSchema);
        await Eve.Events.EventBus.MigrateAsync(source, eventsSchema);
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
                    new NewItem(34, 100, 600, ItemFlags.Hangar, Quantity: 1), CancellationToken.None);
                await transaction.CommitAsync();
            }
            const long createdAt = 1_800_000_000_000;
            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => createdAt, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(source, scheduler, schema: eventsSchema);
            var jobs = new ContractExpiryJobs(source, itemStore, wallets, scheduler, contractsSchema, events);
            var writer = new PersistenceContractWriteModel(source, itemStore, wallets, () => createdAt, contractsSchema, jobs, events);
            var created = await writer.CreateContractAsync(new Eve.Services.Core.CreateItemExchangeRequest(
                IssuerCharacterId: 100, IssuerCorporationId: 200, IsCorporationIssued: false,
                StartStationId: 600, StartStationDivision: ItemFlags.Hangar, Availability: 0, AssigneeId: 0,
                ExpiryMinutes: 1440, Price: 0, Reward: 50, BrokerFee: 0, Deposit: 0, SalesTax: 0,
                WalletAccountKey: 1000, Title: "Expiry", Description: "", OutstandingLimit: 500,
                Items: [new Eve.Services.Core.ContractOfferedItem(itemId, 1)]), CancellationToken.None);
            var scheduled = await scheduler.ListAsync(ContractExpiryJobs.JobKind, "pending", cancellationToken: CancellationToken.None);
            Assert.Contains(scheduled, job => job.Key == created.ContractId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && job.RunAtMs == createdAt + 86_400_000);

            const long restartedAt = createdAt + 86_400_001;
            var restartedScheduler = new Eve.Scheduler.DurableScheduler(source, () => restartedAt, schema: schedulerSchema);
            var restartedJobs = new ContractExpiryJobs(source, itemStore, wallets, restartedScheduler, contractsSchema, events);
            restartedJobs.Register();
            Assert.Equal(1, await restartedScheduler.RunDueAsync());
            Assert.False(await jobs.SettleExpiredAsync(created.ContractId, 1_900_000_000_000, CancellationToken.None));
            var returned = await itemStore.GetAsync(created.EscrowItemIds.Single(), CancellationToken.None);
            Assert.NotNull(returned);
            Assert.Equal((100L, 600L, ItemFlags.Hangar), (returned.OwnerId, returned.LocationId, returned.Flag));
            Assert.Equal(1000m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));
            await using var read = source.CreateCommand($"SELECT status, reward_escrow FROM {contractsSchema}.contract_records WHERE contract_id=$1");
            read.Parameters.AddWithValue(created.ContractId);
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((short)3, reader.GetInt16(0));
            Assert.Equal(0m, reader.GetDecimal(1));
            await reader.DisposeAsync();
            await using var notices = source.CreateCommand($"SELECT array_agg(payload->>'name' ORDER BY id) FROM {eventsSchema}.outbox WHERE topic = $1");
            notices.Parameters.AddWithValue(Eve.Host.Persistence.ContractNotifications.Topic);
            Assert.Equal(new[] { "OnContractCreated", "OnContractFailed" },
                (string[])(await notices.ExecuteScalarAsync())!);
        }
        finally
        {
            foreach (var schema in new[] { contractsSchema, inventorySchema, walletSchema, schedulerSchema, eventsSchema })
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task Concurrent_accepts_and_bids_settle_once_under_contract_row_lock()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_race_{suffix}";
        var inventorySchema = $"contract_race_items_{suffix}";
        var walletSchema = $"contract_race_wallet_{suffix}";
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
                foreach (var owner in new long[] { 100, 300, 400, 500, 600 })
                    await wallets.EnsureAsync(connection, transaction, owner);
                await using var seed = new NpgsqlCommand($"UPDATE {walletSchema}.balances SET isk=100000 WHERE account_key=1000", connection, transaction);
                await seed.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            long offeredItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                offeredItemId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(34, 100, 600, ItemFlags.Hangar, Quantity: 2), CancellationToken.None);
                await transaction.CommitAsync();
            }
            var writer = new PersistenceContractWriteModel(source, itemStore, wallets, () => 1_800_000_000_000, contractsSchema);
            var exchange = await writer.CreateItemExchangeAsync(new Eve.Services.Core.CreateItemExchangeRequest(
                IssuerCharacterId: 100, IssuerCorporationId: 200, IsCorporationIssued: false,
                StartStationId: 600, StartStationDivision: ItemFlags.Hangar, Availability: 0, AssigneeId: 0,
                ExpiryMinutes: 1440, Price: 100, Reward: 0, BrokerFee: 0, Deposit: 0, SalesTax: 0,
                WalletAccountKey: 1000, Title: "Concurrent exchange", Description: "", OutstandingLimit: 500,
                Items: [new Eve.Services.Core.ContractOfferedItem(offeredItemId, 1)]), CancellationToken.None);

            async Task<bool> TryAccept(long characterId)
            {
                try
                {
                    await writer.AcceptContractAsync(exchange.ContractId, characterId, characterId + 1000, 0,
                        false, 1000, 600, CancellationToken.None);
                    return true;
                }
                catch (Eve.Services.Core.ContractUserErrorException)
                {
                    return false;
                }
            }
            var acceptResults = await Task.WhenAll(TryAccept(300), TryAccept(400));
            Assert.Single(acceptResults, accepted => accepted);
            var acceptWinner = acceptResults[0] ? 300L : 400L;
            var acceptLoser = acceptWinner == 300 ? 400 : 300;
            var receivedItem = await itemStore.GetAsync(exchange.EscrowItemIds.Single(), CancellationToken.None);
            Assert.NotNull(receivedItem);
            Assert.Equal(acceptWinner, receivedItem.OwnerId);
            Assert.Equal(100000m, await wallets.AccountBalanceAsync(new WalletAccount(acceptLoser), CancellationToken.None));
            Assert.Equal(99900m, await wallets.AccountBalanceAsync(new WalletAccount(acceptWinner), CancellationToken.None));
            Assert.Equal(100100m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));

            long auctionItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                auctionItemId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(34, 100, 600, ItemFlags.Hangar, Quantity: 1), CancellationToken.None);
                await transaction.CommitAsync();
            }
            var auction = await writer.CreateContractAsync(new Eve.Services.Core.CreateItemExchangeRequest(
                IssuerCharacterId: 100, IssuerCorporationId: 200, IsCorporationIssued: false,
                StartStationId: 600, StartStationDivision: ItemFlags.Hangar, Availability: 0, AssigneeId: 0,
                ExpiryMinutes: 1440, Price: 10000, Reward: 0, BrokerFee: 0, Deposit: 0, SalesTax: 0,
                WalletAccountKey: 1000, Title: "Concurrent auction", Description: "", OutstandingLimit: 500,
                Items: [new Eve.Services.Core.ContractOfferedItem(auctionItemId, 1)], ContractType: 2,
                Collateral: 20000), CancellationToken.None);
            async Task<bool> TryBid(long characterId)
            {
                try
                {
                    return await writer.PlaceBidAsync(auction.ContractId, characterId, characterId + 1000, 0,
                        false, 1000, 10000, CancellationToken.None);
                }
                catch (Eve.Services.Core.ContractUserErrorException)
                {
                    return false;
                }
            }
            var bidResults = await Task.WhenAll(TryBid(500), TryBid(600));
            Assert.Single(bidResults, accepted => accepted);
            var bidWinner = bidResults[0] ? 500L : 600L;
            var bidLoser = bidWinner == 500 ? 600 : 500;
            Assert.Equal(90000m, await wallets.AccountBalanceAsync(new WalletAccount(bidWinner), CancellationToken.None));
            Assert.Equal(100000m, await wallets.AccountBalanceAsync(new WalletAccount(bidLoser), CancellationToken.None));
            await using (var bids = source.CreateCommand($"SELECT bidder_id, is_refunded FROM {contractsSchema}.contract_bids WHERE contract_id=$1"))
            {
                bids.Parameters.AddWithValue(auction.ContractId);
                await using var reader = await bids.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((bidWinner, false), (reader.GetInt64(0), reader.GetBoolean(1)));
                Assert.False(await reader.ReadAsync());
            }
            var auctionEscrow = await itemStore.GetAsync(auction.EscrowItemIds.Single(), CancellationToken.None);
            Assert.NotNull(auctionEscrow);
            Assert.Equal(1L, auctionEscrow.OwnerId);
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
