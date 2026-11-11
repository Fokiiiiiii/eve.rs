using System.Net.Sockets;
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
    public async Task Registered_auction_buyout_waits_for_issuer_and_winner_claims()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_buyout_{suffix}";
        var inventorySchema = $"contract_buyout_items_{suffix}";
        var walletSchema = $"contract_buyout_wallet_{suffix}";
        var schedulerSchema = $"contract_buyout_scheduler_{suffix}";
        var sdeSchema = $"sde_contract_buyout_{suffix}";
        var eventsSchema = $"contract_buyout_events_{suffix}";
        const long issuerId = 100;
        const long issuerCorporationId = 200;
        const long previousBidderId = 300;
        const long winnerId = 42;
        const long stationId = 600;
        const long nowMs = FixedNowMs;
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
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                foreach (var ownerId in new[] { issuerId, previousBidderId, (long)winnerId })
                    await wallets.EnsureAsync(connection, transaction, ownerId);
                await using var balances = new NpgsqlCommand(
                    $"UPDATE {walletSchema}.balances SET isk = 50000 WHERE account_key = 1000", connection, transaction);
                await balances.ExecuteNonQueryAsync();
                await using var issuerBalance = new NpgsqlCommand(
                    $"UPDATE {walletSchema}.balances SET isk = 200000 WHERE owner_id = $1 AND account_key = 1000",
                    connection, transaction);
                issuerBalance.Parameters.AddWithValue(issuerId);
                await issuerBalance.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }

            long itemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                itemId = await items.CreateAsync(connection, transaction,
                    new NewItem(34, issuerId, stationId, ItemFlags.Hangar, Quantity: 1), CancellationToken.None);
                await transaction.CommitAsync();
            }

            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(source, scheduler, schema: eventsSchema);
            var expiryJobs = new ContractExpiryJobs(source, items, wallets, scheduler, contractsSchema, events);
            var writer = new PersistenceContractWriteModel(source, items, wallets, () => nowMs,
                contractsSchema, expiryJobs, events);
            var created = await writer.CreateContractAsync(new CreateItemExchangeRequest(
                IssuerCharacterId: issuerId,
                IssuerCorporationId: issuerCorporationId,
                IsCorporationIssued: false,
                StartStationId: stationId,
                StartStationDivision: ItemFlags.Hangar,
                Availability: 0,
                AssigneeId: 0,
                ExpiryMinutes: 1440,
                Price: 1000,
                Reward: 0,
                BrokerFee: 10_000,
                Deposit: 100_000,
                SalesTax: 0,
                WalletAccountKey: 1000,
                Title: "Auction buyout",
                Description: "",
                OutstandingLimit: 500,
                Items: [new ContractOfferedItem(itemId, 1)],
                ContractType: 2,
                Collateral: 20000), CancellationToken.None);

            var readModel = new PersistenceContractReadModel(source, contractsSchema, sdeSchema, inventorySchema);
            StartContractHost(writer, readModel,
                characterIdForAccount: accountId => accountId);

            async Task<List<(string Name, string CharacterId)>> ReadNotificationsAsync()
            {
                var notifications = new List<(string, string)>();
                await using var command = source.CreateCommand($"""
                    SELECT payload->>'name', payload->>'characterId'
                    FROM {eventsSchema}.outbox WHERE topic = $1 ORDER BY id
                    """);
                command.Parameters.AddWithValue(ContractNotifications.Topic);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    notifications.Add((reader.GetString(0), reader.GetString(1)));
                return notifications;
            }

            var previousBidderFront = await Handshaken(await ConnectAsync());
            var previousBidderOpen = Open(sessionId: 1_790_646_377_000UL * 15 + 2, userId: 300);
            await previousBidderFront.SendAsync(previousBidderOpen);
            _ = await previousBidderFront.ReceiveAsync();
            var winnerFront = await Handshaken(await ConnectAsync());
            var winnerOpen = Open();
            await winnerFront.SendAsync(winnerOpen);
            _ = await winnerFront.ReceiveAsync();

            async Task<PyValue> CallAsync(TestFront front, SessionOpen open, long callId, string method,
                params PyValue[] arguments)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                    Packets.CallWithKwargs(open, callId, "contractProxy", method,
                        new PyTuple(arguments),
                        Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));
                while (true)
                {
                    var send = Assert.IsType<Send>(await front.ReceiveAsync());
                    var response = MarshalCodec.Decode(send.Payload);
                    if (response is not PyObject { Args: PyTuple args } || args.Items.Count < 3 ||
                        args.Items[0] is not PyInt { Value: 7 or 15 } ||
                        MachoAddress.Decode(args.Items[2]).CallId != new PyInt(callId))
                        continue;
                    var error = args.Items[0] is PyInt { Value: 15 } ? FormatRpcValue(args.Items[4]) : "";
                    Assert.True(args.Items[0] is PyInt { Value: 7 },
                        $"Unexpected auction RPC state: {args.Items[0]} {error} {_lastContractRpcProblem}");
                    return Result(response) ?? throw new InvalidOperationException("auction RPC response has no result");
                }
            }

            Assert.Same(PyBool.True, await CallAsync(previousBidderFront, previousBidderOpen, 1, "PlaceBid",
                new PyInt(created.ContractId), new PyInt(10000), PyBool.False));
            Assert.Equal(new[] { ("OnContractCreated", "100") }, await ReadNotificationsAsync());
            Assert.Same(PyBool.True, await CallAsync(winnerFront, winnerOpen, 2, "PlaceBid",
                new PyInt(created.ContractId), new PyInt(20000), PyBool.False));
            Assert.Equal(new[]
            {
                ("OnContractCreated", "100"),
                ("OnContractOutbid", "300"),
                ("OnContractBuyout", "100"),
            },
                await ReadNotificationsAsync());

            await using (var contract = source.CreateCommand($"""
                SELECT status, acceptor_id, date_expired_filetime
                FROM {contractsSchema}.contract_records WHERE contract_id = $1
                """))
            {
                contract.Parameters.AddWithValue(created.ContractId);
                await using var reader = await contract.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((short)0, reader.GetInt16(0));
                Assert.Equal(0L, reader.GetInt64(1));
                Assert.True(reader.GetInt64(2) < Eve.Services.Py.FileTimeFromUnixMs(nowMs));
            }
            Assert.Equal(30000m, await wallets.AccountBalanceAsync(new WalletAccount(winnerId), CancellationToken.None));
            Assert.Equal(50000m, await wallets.AccountBalanceAsync(new WalletAccount(previousBidderId), CancellationToken.None));
            Assert.Equal(90000m, await wallets.AccountBalanceAsync(new WalletAccount(issuerId), CancellationToken.None));
            var escrowedItem = await items.GetAsync(created.EscrowItemIds.Single(), CancellationToken.None);
            Assert.NotNull(escrowedItem);
            Assert.Equal(1L, escrowedItem.OwnerId);

            var issuerFront = await Handshaken(await ConnectAsync());
            var issuerOpen = Open(sessionId: 1_790_646_377_000UL * 15 + 1, userId: 100);
            await issuerFront.SendAsync(issuerOpen);
            _ = await issuerFront.ReceiveAsync();
            Assert.Same(PyBool.True, await CallAsync(issuerFront, issuerOpen, 3, "FinishAuction",
                new PyInt(created.ContractId), PyBool.True));
            Assert.Equal(200000m, await wallets.AccountBalanceAsync(new WalletAccount(issuerId), CancellationToken.None));
            await using (var sellerJournal = source.CreateCommand($"""
                SELECT ref_type_id, amount FROM {walletSchema}.journal
                WHERE owner_id = $1 AND reference_id = $2 AND ref_type_id IN (67, 73, 82)
                ORDER BY id
                """))
            {
                sellerJournal.Parameters.AddWithValue(issuerId);
                sellerJournal.Parameters.AddWithValue(created.ContractId);
                var entries = new List<(int RefType, decimal Amount)>();
                await using var reader = await sellerJournal.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    entries.Add((reader.GetInt32(0), reader.GetDecimal(1)));
                Assert.Equal(new[] { (67, 20000m), (73, -10000m), (82, 100000m) }, entries);
            }
            await using (var sellerClaim = source.CreateCommand(
                $"SELECT status FROM {contractsSchema}.contract_records WHERE contract_id = $1"))
            {
                sellerClaim.Parameters.AddWithValue(created.ContractId);
                Assert.Equal((short)2, (short)(await sellerClaim.ExecuteScalarAsync())!);
            }
            Assert.Equal(new[]
            {
                ("OnContractCreated", "100"),
                ("OnContractOutbid", "300"),
                ("OnContractBuyout", "100"),
            },
                await ReadNotificationsAsync());
            Assert.Equal(1L, (await items.GetAsync(created.EscrowItemIds.Single(), CancellationToken.None))!.OwnerId);

            Assert.Same(PyBool.True, await CallAsync(winnerFront, winnerOpen, 4, "FinishAuction",
                new PyInt(created.ContractId), PyBool.False));
            await using (var finished = source.CreateCommand($"""
                SELECT status, acceptor_id
                FROM {contractsSchema}.contract_records WHERE contract_id = $1
                """))
            {
                finished.Parameters.AddWithValue(created.ContractId);
                await using var reader = await finished.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((short)4, reader.GetInt16(0));
                Assert.Equal(winnerId, reader.GetInt64(1));
            }
            var deliveredItem = await items.GetAsync(created.EscrowItemIds.Single(), CancellationToken.None);
            Assert.NotNull(deliveredItem);
            Assert.Equal((winnerId, stationId, ItemFlags.Hangar),
                (deliveredItem.OwnerId, deliveredItem.LocationId, deliveredItem.Flag));
            Assert.Equal(30000m, await wallets.AccountBalanceAsync(new WalletAccount(winnerId), CancellationToken.None));
            Assert.Equal(200000m, await wallets.AccountBalanceAsync(new WalletAccount(issuerId), CancellationToken.None));
            Assert.Equal(new[]
            {
                ("OnContractCreated", "100"),
                ("OnContractOutbid", "300"),
                ("OnContractBuyout", "100"),
                ("OnContractCompleted", "100"),
                ("OnContractCompleted", "42"),
            }, await ReadNotificationsAsync());
        }
        finally
        {
            foreach (var schema in new[] { contractsSchema, inventorySchema, walletSchema, schedulerSchema, sdeSchema, eventsSchema })
            {
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
