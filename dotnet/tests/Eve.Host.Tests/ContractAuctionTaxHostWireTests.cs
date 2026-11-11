using Eve.Data;
using Eve.Host.Persistence;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class HostServerTests
{
    [PgFact]
    public async Task Registered_auction_creation_defers_sales_tax_until_seller_claims_proceeds()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_auction_tax_{suffix}";
        var inventorySchema = $"contract_auction_tax_items_{suffix}";
        var walletSchema = $"contract_auction_tax_wallet_{suffix}";
        var schedulerSchema = $"contract_auction_tax_scheduler_{suffix}";
        var eventsSchema = $"contract_auction_tax_events_{suffix}";
        var sdeSchema = $"sde_contract_auction_tax_{suffix}";
        var nowMs = FixedNowMs;
        const long issuerId = 42;
        const long bidderId = 43;
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, contractsSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schedulerSchema);
        await Eve.Events.EventBus.MigrateAsync(source, eventsSchema);
        await using (var createSde = source.CreateCommand($"""
            CREATE SCHEMA "{sdeSchema}";
            CREATE TABLE "{sdeSchema}".npcStations (key bigint PRIMARY KEY, doc jsonb NOT NULL);
            CREATE TABLE "{sdeSchema}".mapSolarSystems (key bigint PRIMARY KEY, doc jsonb NOT NULL);
            CREATE TABLE "{sdeSchema}".types (key bigint PRIMARY KEY, doc jsonb NOT NULL);
            """))
            await createSde.ExecuteNonQueryAsync();
        try
        {
            var items = new PgItemStore(source, inventorySchema);
            var wallets = new WalletTransactions(source, walletSchema);
            long offeredItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.EnsureAsync(connection, transaction, issuerId);
                await using var balance = new NpgsqlCommand(
                    $"UPDATE {walletSchema}.balances SET isk = 50000000 WHERE owner_id = $1 AND account_key = 1000",
                    connection, transaction);
                balance.Parameters.AddWithValue(issuerId);
                await balance.ExecuteNonQueryAsync();
                await wallets.EnsureAsync(connection, transaction, bidderId);
                await using var bidderBalance = new NpgsqlCommand(
                    $"UPDATE {walletSchema}.balances SET isk = 20000000 WHERE owner_id = $1 AND account_key = 1000",
                    connection, transaction);
                bidderBalance.Parameters.AddWithValue(bidderId);
                await bidderBalance.ExecuteNonQueryAsync();
                offeredItemId = await items.CreateAsync(connection, transaction,
                    new NewItem(34, issuerId, 3001, ItemFlags.Hangar, Quantity: 1), CancellationToken.None);
                await transaction.CommitAsync();
            }

            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(source, scheduler, schema: eventsSchema);
            var expiryJobs = new ContractExpiryJobs(source, items, wallets, scheduler, contractsSchema, events);
            var writer = new PersistenceContractWriteModel(source, items, wallets, () => nowMs,
                contractsSchema, expiryJobs, events);
            var readModel = new PersistenceContractReadModel(source, contractsSchema, sdeSchema, inventorySchema);
            StartContractHost(writer, readModel, characterIdForAccount: accountId => accountId,
                contractSkillLevel: (_, skillId, _) => ValueTask.FromResult(skillId == 16622 ? 4 : 0));
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();

            var bidderFront = await Handshaken(await ConnectAsync());
            var bidderOpen = Open(1_790_646_377_000UL * 15 + 3, (uint)bidderId);
            await bidderFront.SendAsync(bidderOpen);
            _ = await bidderFront.ReceiveAsync();

            async Task<PyValue> CallAsync(TestFront rpcFront, SessionOpen rpcOpen, uint callId, string method,
                PyValue[] arguments, bool includeConfirm = false)
            {
                var kwargs = new List<KeyValuePair<PyValue, PyValue>>
                {
                    new(new PyStr("machoVersion"), new PyInt(1)),
                };
                if (includeConfirm)
                    kwargs.Add(new(new PyStr("confirm"), PyNone.Instance));
                await rpcFront.SendAsync(new Packet(rpcOpen.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                    rpcOpen, callId, "contractProxy", method, new PyTuple(arguments), new PyDict(kwargs)))));
                while (true)
                {
                    var send = Assert.IsType<Send>(await rpcFront.ReceiveAsync());
                    var packet = MarshalCodec.Decode(send.Payload);
                    if (packet is not PyObject { Args: PyTuple args } || args.Items.Count < 3 ||
                        MachoAddress.Decode(args.Items[2]).CallId != new PyInt(callId))
                        continue;
                    Assert.Equal(new PyInt(7), args.Items[0]);
                    return Result(packet) ?? throw new InvalidOperationException($"{method} response has no result");
                }
            }

            var contractInfo = new PyObject(new PyStr("contracts.contractStruct.ContractStruct"), new PyTuple([
                new PyInt(2), PyBool.False, new PyInt(0), new PyInt(1440), new PyInt(0),
                new PyInt(3001), new PyInt(0), new PyInt(10_000_000), new PyInt(0), new PyInt(20_000_000),
                new PyStr("Auction tax timing"), new PyStr(""),
                new PyList([new PyTuple([new PyLong(offeredItemId), new PyInt(1)])]),
                new PyInt(ItemFlags.Hangar), new PyList([]), PyBool.False, PyBool.False]));
            var response = await CallAsync(front, open, 1, "CreateContract", [contractInfo], includeConfirm: true);
            var contractId = Assert.IsType<PyInt>(Assert.Single(Assert.IsType<PyList>(response).Items)).Value;
            await using (var terms = source.CreateCommand($"""
                SELECT creation_fee_isk, deposit_isk, sales_tax_isk, auction_accounting_level
                FROM {contractsSchema}.contract_records WHERE contract_id = $1
                """))
            {
                terms.Parameters.AddWithValue(contractId);
                await using var reader = await terms.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((40_000m, 100_000m, 0m, (short)4),
                    (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetInt16(3)));
            }
            Assert.Equal(49_860_000m, await wallets.AccountBalanceAsync(new WalletAccount(issuerId), CancellationToken.None));
            await using var taxJournal = source.CreateCommand($"""
                SELECT count(*) FROM {walletSchema}.journal
                WHERE owner_id = $1 AND ref_type_id = 73
                """);
            taxJournal.Parameters.AddWithValue(issuerId);
            Assert.Equal(0L, (long)(await taxJournal.ExecuteScalarAsync())!);

            Assert.Same(PyBool.True, await CallAsync(bidderFront, bidderOpen, 2, "PlaceBid",
                [new PyInt(contractId), new PyInt(10_000_000), PyBool.False]));
            await using (var bidEntry = source.CreateCommand($"""
                SELECT ref_type_id, amount, reference_id FROM {walletSchema}.journal
                WHERE owner_id = $1 ORDER BY id DESC LIMIT 1
                """))
            {
                bidEntry.Parameters.AddWithValue(bidderId);
                await using var reader = await bidEntry.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((63, -10_000_000m, (long?)contractId),
                    (reader.GetInt32(0), reader.GetDecimal(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
            }
            nowMs = checked(FixedNowMs + (long)TimeSpan.FromDays(1).TotalMilliseconds + 1);
            Assert.Same(PyBool.True, await CallAsync(front, open, 3, "FinishAuction",
                [new PyInt(contractId), PyBool.True]));
            Assert.Same(PyBool.True, await CallAsync(bidderFront, bidderOpen, 4, "FinishAuction",
                [new PyInt(contractId), PyBool.False]));

            Assert.Equal(59_900_000m, await wallets.AccountBalanceAsync(new WalletAccount(issuerId), CancellationToken.None));
            var journal = new List<(int RefType, decimal Amount, long TimeMs)>();
            await using (var entries = source.CreateCommand($"""
                SELECT ref_type_id, amount, time_ms FROM {walletSchema}.journal
                WHERE owner_id = $1 AND account_key = 1000 ORDER BY id
                """))
            {
                entries.Parameters.AddWithValue(issuerId);
                await using var reader = await entries.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    journal.Add((reader.GetInt32(0), reader.GetDecimal(1), reader.GetInt64(2)));
            }
            Assert.Contains((67, 10_000_000m, nowMs), journal);
            Assert.Contains((73, -60_000m, nowMs), journal);
            Assert.Contains((82, 100_000m, nowMs), journal);
            Assert.DoesNotContain(journal, entry => entry.RefType == 75);
            await using (var settledTax = source.CreateCommand($"""
                SELECT sales_tax_isk FROM {contractsSchema}.contract_records WHERE contract_id = $1
                """))
            {
                settledTax.Parameters.AddWithValue(contractId);
                Assert.Equal(60_000m, (decimal)(await settledTax.ExecuteScalarAsync())!);
            }

            long noBidItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                noBidItemId = await items.CreateAsync(connection, transaction,
                    new NewItem(34, issuerId, 3001, ItemFlags.Hangar, Quantity: 1), CancellationToken.None);
                await transaction.CommitAsync();
            }
            var noBidInfo = new PyObject(new PyStr("contracts.contractStruct.ContractStruct"), new PyTuple([
                new PyInt(2), PyBool.False, new PyInt(0), new PyInt(1440), new PyInt(0),
                new PyInt(3001), new PyInt(0), new PyInt(10_000_000), new PyInt(0), new PyInt(20_000_000),
                new PyStr("Auction no sale"), new PyStr(""),
                new PyList([new PyTuple([new PyLong(noBidItemId), new PyInt(1)])]),
                new PyInt(ItemFlags.Hangar), new PyList([]), PyBool.False, PyBool.False]));
            var noBidResponse = await CallAsync(front, open, 5, "CreateContract", [noBidInfo], includeConfirm: true);
            var noBidContractId = Assert.IsType<PyInt>(Assert.Single(Assert.IsType<PyList>(noBidResponse).Items)).Value;
            await using (var expire = source.CreateCommand($"""
                UPDATE {contractsSchema}.contract_records SET date_expired_filetime = $2 WHERE contract_id = $1
                """))
            {
                expire.Parameters.AddWithValue(noBidContractId);
                expire.Parameters.AddWithValue(Eve.Services.Py.FileTimeFromUnixMs(nowMs) - 1);
                await expire.ExecuteNonQueryAsync();
            }
            Assert.Same(PyBool.True, await CallAsync(front, open, 6, "FinishAuction",
                [new PyInt(noBidContractId), PyBool.True]));
            Assert.Equal(59_760_000m, await wallets.AccountBalanceAsync(new WalletAccount(issuerId), CancellationToken.None));
            var returnedNoBidItem = await items.GetAsync(noBidItemId, CancellationToken.None);
            Assert.NotNull(returnedNoBidItem);
            Assert.Equal((issuerId, 3001L, ItemFlags.Hangar),
                (returnedNoBidItem.OwnerId, returnedNoBidItem.LocationId, returnedNoBidItem.Flag));
            await using var noBidDepositRefund = source.CreateCommand($"""
                SELECT count(*) FROM {walletSchema}.journal
                WHERE owner_id = $1 AND reference_id = $2 AND ref_type_id = 82
                """);
            noBidDepositRefund.Parameters.AddWithValue(issuerId);
            noBidDepositRefund.Parameters.AddWithValue(noBidContractId);
            Assert.Equal(0L, (long)(await noBidDepositRefund.ExecuteScalarAsync())!);
        }
        finally
        {
            foreach (var schema in new[] { contractsSchema, inventorySchema, walletSchema, schedulerSchema, eventsSchema, sdeSchema })
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
