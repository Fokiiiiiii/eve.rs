using Eve.Data;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class ContractCourierDeadlineTests
{
    [Fact]
    public async Task Accepted_courier_fails_at_its_delivery_deadline_and_forfeits_collateral()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"courier_deadline_{suffix}";
        var inventorySchema = $"courier_deadline_items_{suffix}";
        var walletSchema = $"courier_deadline_wallet_{suffix}";
        var schedulerSchema = $"courier_deadline_jobs_{suffix}";
        var eventsSchema = $"courier_deadline_events_{suffix}";
        const long nowMs = 1_800_000_000_000;
        const long contractId = 8810;
        const long issuerId = 100;
        const long acceptorId = 300;
        var nowFileTime = Eve.Services.Py.FileTimeFromUnixMs(nowMs);
        var escrowLocation = 9_300_000_000 + contractId;

        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, contractsSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schedulerSchema);
        await Eve.Events.EventBus.MigrateAsync(source, eventsSchema);
        try
        {
            var items = new PgItemStore(source, inventorySchema);
            var wallets = new WalletTransactions(source, walletSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(source, scheduler, schema: eventsSchema);
            var expiryJobs = new ContractExpiryJobs(source, items, wallets, scheduler, contractsSchema, events);
            long wrapId;
            long contentId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                foreach (var ownerId in new[] { issuerId, acceptorId })
                    await wallets.EnsureAsync(connection, transaction, ownerId);
                await using (var balances = new NpgsqlCommand(
                    $"UPDATE {walletSchema}.balances SET isk = 100000 WHERE owner_id IN ($1, $2) AND account_key = 1000",
                    connection, transaction))
                {
                    balances.Parameters.AddWithValue(issuerId);
                    balances.Parameters.AddWithValue(acceptorId);
                    await balances.ExecuteNonQueryAsync();
                }
                wrapId = await items.CreateAsync(connection, transaction,
                    new NewItem(3468, 1, escrowLocation, ItemFlags.Hangar, Singleton: true), CancellationToken.None);
                contentId = await items.CreateAsync(connection, transaction,
                    new NewItem(34, 1, escrowLocation, ItemFlags.Hangar, Quantity: 2), CancellationToken.None);
                await using (var contract = new NpgsqlCommand($"""
                    INSERT INTO {contractsSchema}.contract_records
                        (contract_id, issuer_id, issuer_corporation_id, is_corporation_issued,
                         contract_type, status, availability, acceptor_id, start_station_id, end_station_id,
                         plastic_wrap_item_id, date_expired_filetime, num_days, collateral_isk,
                         collateral_escrow, reward_escrow, issuer_wallet_account_key, issuer_item_flag,
                         acceptor_is_corporation, acceptor_wallet_account_key, created_at_filetime)
                    VALUES ($1, $2, 200, false, 3, 0, 0, 0, 3001, 3002, $3, $4,
                            1, 5000, 0, 2000, 1000, 4, false, 1000, $5)
                    """, connection, transaction))
                {
                    contract.Parameters.AddWithValue(contractId);
                    contract.Parameters.AddWithValue(issuerId);
                    contract.Parameters.AddWithValue(wrapId);
                    contract.Parameters.AddWithValue(nowFileTime + TimeSpan.TicksPerDay * 3);
                    contract.Parameters.AddWithValue(nowFileTime);
                    await contract.ExecuteNonQueryAsync();
                }
                await using (var contents = new NpgsqlCommand($"""
                    INSERT INTO {contractsSchema}.contract_items (contract_id, item_id, type_id, quantity, in_crate)
                    VALUES ($1, $2, 34, 2, true)
                    """, connection, transaction))
                {
                    contents.Parameters.AddWithValue(contractId);
                    contents.Parameters.AddWithValue(contentId);
                    await contents.ExecuteNonQueryAsync();
                }
                await expiryJobs.ScheduleAsync(connection, transaction, contractId,
                    nowFileTime + TimeSpan.TicksPerDay * 3, CancellationToken.None);
                await transaction.CommitAsync();
            }

            var writer = new PersistenceContractWriteModel(source, items, wallets, () => nowMs,
                contractsSchema, expiryJobs, events);
            await writer.AcceptContractAsync(contractId, acceptorId, 301, 0, false, 1000, 3001, CancellationToken.None);
            Assert.Equal(95_000m, await wallets.AccountBalanceAsync(new WalletAccount(acceptorId), CancellationToken.None));
            Assert.False(await expiryJobs.SettleExpiredAsync(contractId, nowMs + (long)TimeSpan.FromHours(23).TotalMilliseconds,
                CancellationToken.None));
            Assert.Equal(95_000m, await wallets.AccountBalanceAsync(new WalletAccount(acceptorId), CancellationToken.None));

            Assert.True(await expiryJobs.SettleExpiredAsync(contractId, nowMs + (long)TimeSpan.FromDays(1).TotalMilliseconds + 1,
                CancellationToken.None));

            await using (var contract = source.CreateCommand(
                $"SELECT status, collateral_escrow, reward_escrow, plastic_wrap_item_id FROM {contractsSchema}.contract_records WHERE contract_id=$1"))
            {
                contract.Parameters.AddWithValue(contractId);
                await using var reader = await contract.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((short)7, reader.GetInt16(0));
                Assert.Equal(0m, reader.GetDecimal(1));
                Assert.Equal(0m, reader.GetDecimal(2));
                Assert.Equal(0L, reader.GetInt64(3));
            }
            Assert.Equal(107_000m, await wallets.AccountBalanceAsync(new WalletAccount(issuerId), CancellationToken.None));
            Assert.Equal(95_000m, await wallets.AccountBalanceAsync(new WalletAccount(acceptorId), CancellationToken.None));
            Assert.Null(await items.GetAsync(wrapId, CancellationToken.None));
            var contentsAfterFailure = await items.GetAsync(contentId, CancellationToken.None);
            Assert.NotNull(contentsAfterFailure);
            Assert.Equal((acceptorId, 3001L), (contentsAfterFailure.OwnerId, contentsAfterFailure.LocationId));
            await using var itemState = source.CreateCommand(
                $"SELECT in_crate FROM {contractsSchema}.contract_items WHERE contract_id=$1 AND item_id=$2");
            itemState.Parameters.AddWithValue(contractId);
            itemState.Parameters.AddWithValue(contentId);
            Assert.False((bool)(await itemState.ExecuteScalarAsync())!);
            await using var notifications = source.CreateCommand(
                $"SELECT payload->>'name', count(*) FROM {eventsSchema}.outbox WHERE topic=$1 AND payload->>'name'='OnContractFailed' GROUP BY payload->>'name'");
            notifications.Parameters.AddWithValue(ContractNotifications.Topic);
            await using var noticeRows = await notifications.ExecuteReaderAsync();
            Assert.True(await noticeRows.ReadAsync());
            Assert.Equal("OnContractFailed", noticeRows.GetString(0));
            Assert.Equal(2L, noticeRows.GetInt64(1));
            Assert.False(await noticeRows.ReadAsync());
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
}
