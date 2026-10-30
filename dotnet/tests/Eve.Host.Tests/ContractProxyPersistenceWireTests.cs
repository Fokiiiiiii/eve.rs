using System.Net;
using System.Net.Sockets;
using Eve.Data;
using Eve.Host;
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

public partial class HostServerTests
{
    [Fact]
    public async Task Finish_auction_settles_expired_bid_and_persists_winner_inventory_and_notification()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"cfac_{suffix}";
        var inventorySchema = $"cfai_{suffix}";
        var walletSchema = $"cfaw_{suffix}";
        var schedulerSchema = $"cfas_{suffix}";
        var eventsSchema = $"cfae_{suffix}";
        var sdeSchema = $"cfad_{suffix}";
        const long nowMs = 1_800_000_000_000;
        var nowFileTime = Eve.Services.Py.FileTimeFromUnixMs(nowMs);
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, contractsSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        await Eve.Scheduler.DurableScheduler.MigrateAsync(source, schedulerSchema);
        await Eve.Events.EventBus.MigrateAsync(source, eventsSchema);
        try
        {
            await using (var sde = source.CreateCommand($"""
                CREATE SCHEMA "{sdeSchema}";
                CREATE TABLE "{sdeSchema}".npcStations (key bigint PRIMARY KEY, doc jsonb NOT NULL);
                CREATE TABLE "{sdeSchema}".mapSolarSystems (key bigint PRIMARY KEY, doc jsonb NOT NULL);
                CREATE TABLE "{sdeSchema}".types (key bigint PRIMARY KEY, doc jsonb NOT NULL);
                """))
                await sde.ExecuteNonQueryAsync();
            var itemStore = new PgItemStore(source, inventorySchema);
            var wallets = new WalletTransactions(source, walletSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(source, scheduler, schema: eventsSchema);
            var expiryJobs = new ContractExpiryJobs(source, itemStore, wallets, scheduler, contractsSchema, events);
            const long contractId = 7301;
            const long bidderId = 42;
            long offeredItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.EnsureAsync(connection, transaction, bidderId);
                await wallets.EnsureAsync(connection, transaction, 100);
                offeredItemId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(34, 1, 9_300_000_000 + contractId, 0, Quantity: 2), CancellationToken.None);
                await using var seed = new NpgsqlCommand($"""
                    INSERT INTO {contractsSchema}.contract_records
                        (contract_id, issuer_id, issuer_corporation_id, contract_type, status,
                         start_station_id, date_expired_filetime, created_at_filetime, title)
                    VALUES ($1, 100, 84, 2, 0, 3001, $2, $3, 'Auction')
                    """, connection, transaction);
                seed.Parameters.AddWithValue(contractId);
                seed.Parameters.AddWithValue(nowFileTime - 100);
                seed.Parameters.AddWithValue(nowFileTime - 1000);
                await seed.ExecuteNonQueryAsync();
                await using var bid = new NpgsqlCommand($"""
                    INSERT INTO {contractsSchema}.contract_bids
                        (contract_id, bidder_id, amount, created_at_filetime, bidder_is_corporation)
                    VALUES ($1, $2, 500, $3, false)
                    """, connection, transaction);
                bid.Parameters.AddWithValue(contractId);
                bid.Parameters.AddWithValue(bidderId);
                bid.Parameters.AddWithValue(nowFileTime - 500);
                await bid.ExecuteNonQueryAsync();
                await using var offered = new NpgsqlCommand($"""
                    INSERT INTO {contractsSchema}.contract_items
                        (contract_id, item_id, type_id, quantity, in_crate)
                    VALUES ($1, $2, 34, 2, true)
                    """, connection, transaction);
                offered.Parameters.AddWithValue(contractId);
                offered.Parameters.AddWithValue(offeredItemId);
                await offered.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            var reads = new PersistenceContractReadModel(source, contractsSchema, sdeSchema);
            StartContractHost(new PersistenceContractWriteModel(source, itemStore, wallets, () => nowMs,
                contractsSchema, expiryJobs, events), reads);
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 1, "contractProxy", "FinishAuction", new PyTuple([new PyInt(contractId), PyBool.False]),
                Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));
            PyValue? result = null;
            while (result is null)
            {
                var send = Assert.IsType<Send>(await front.ReceiveAsync());
                var response = MarshalCodec.Decode(send.Payload);
                if (response is not PyObject { Args: PyTuple args } || args.Items.Count < 3 ||
                    MachoAddress.Decode(args.Items[2]).CallId != new PyInt(1))
                    continue;
                Assert.Equal(new PyInt(7), args.Items[0]);
                result = Result(response);
            }
            Assert.Same(PyBool.True, result);
            await using (var state = source.CreateCommand($"SELECT status, acceptor_id FROM {contractsSchema}.contract_records WHERE contract_id=$1"))
            {
                state.Parameters.AddWithValue(contractId);
                await using var reader = await state.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((short)4, reader.GetInt16(0));
                Assert.Equal(bidderId, reader.GetInt64(1));
            }
            var delivered = await itemStore.GetAsync(offeredItemId, CancellationToken.None);
            Assert.NotNull(delivered);
            Assert.Equal((bidderId, 3001L, ItemFlags.Hangar), (delivered.OwnerId, delivered.LocationId, delivered.Flag));
            Assert.Equal(500m, await wallets.AccountBalanceAsync(new WalletAccount(100), CancellationToken.None));
            await using var notice = source.CreateCommand($"SELECT payload->>'name' FROM {eventsSchema}.outbox WHERE topic=$1");
            notice.Parameters.AddWithValue(ContractNotifications.Topic);
            Assert.Equal("OnContractCompleted", (string?)await notice.ExecuteScalarAsync());
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

    [Fact]
    public async Task Create_contract_persists_items_fees_expiry_and_creation_notification_over_host()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"ccrc_{suffix}";
        var inventorySchema = $"ccri_{suffix}";
        var walletSchema = $"ccrw_{suffix}";
        var schedulerSchema = $"ccrs_{suffix}";
        var eventsSchema = $"ccre_{suffix}";
        const long nowMs = 1_800_000_000_000;
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
            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(source, scheduler, schema: eventsSchema);
            var expiryJobs = new ContractExpiryJobs(source, itemStore, wallets, scheduler, contractsSchema, events);
            long offeredItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.EnsureAsync(connection, transaction, 42);
                await using var balance = new NpgsqlCommand($"UPDATE {walletSchema}.balances SET isk=1000000 WHERE owner_id=42 AND account_key=1000", connection, transaction);
                await balance.ExecuteNonQueryAsync();
                offeredItemId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(34, 42, 3001, ItemFlags.Hangar, Quantity: 3), CancellationToken.None);
                await transaction.CommitAsync();
            }
            var writer = new PersistenceContractWriteModel(source, itemStore, wallets, () => nowMs,
                contractsSchema, expiryJobs, events);
            StartContractHost(writer);
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            var creation = new PyObject(new PyStr("contracts.contractStruct.ContractStruct"), new PyTuple([
                new PyInt(1), PyBool.False, new PyInt(0), new PyInt(1440), new PyInt(0),
                new PyInt(3001), new PyInt(0), new PyInt(1000), new PyInt(0), new PyInt(0),
                new PyStr("persistent contract"), new PyStr(""),
                new PyList([new PyTuple([new PyInt(offeredItemId), new PyInt(2)])]),
                new PyInt(4), new PyList([]), PyBool.False, PyBool.False]));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 1, "contractProxy", "CreateContract", new PyTuple([creation]),
                Eve.Services.Py.Dict(("machoVersion", new PyInt(1)), ("confirm", PyNone.Instance))))));
            PyValue? result = null;
            while (result is null)
            {
                var send = Assert.IsType<Send>(await front.ReceiveAsync());
                var response = MarshalCodec.Decode(send.Payload);
                if (response is not PyObject { Args: PyTuple args } || args.Items.Count < 3 ||
                    MachoAddress.Decode(args.Items[2]).CallId != new PyInt(1))
                    continue;
                Assert.Equal(new PyInt(7), args.Items[0]);
                result = Result(response);
            }
            var contractId = Assert.IsType<PyInt>(Assert.Single(Assert.IsType<PyList>(result).Items)).Value;
            await using (var state = source.CreateCommand($"SELECT status, title, reward_escrow, price_isk FROM {contractsSchema}.contract_records WHERE contract_id=$1"))
            {
                state.Parameters.AddWithValue(contractId);
                await using var reader = await state.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((short)0, reader.GetInt16(0));
                Assert.Equal("persistent contract", reader.GetString(1));
                Assert.Equal(0m, reader.GetDecimal(2));
                Assert.Equal(1000m, reader.GetDecimal(3));
            }
            Assert.Equal(1, (await itemStore.GetAsync(offeredItemId, CancellationToken.None))!.Quantity);
            await using var escrowIdQuery = source.CreateCommand($"SELECT item_id FROM {contractsSchema}.contract_items WHERE contract_id=$1");
            escrowIdQuery.Parameters.AddWithValue(contractId);
            var escrowItemId = (long)(await escrowIdQuery.ExecuteScalarAsync())!;
            var escrowItem = await itemStore.GetAsync(escrowItemId, CancellationToken.None);
            Assert.NotNull(escrowItem);
            Assert.Equal(9_300_000_000 + contractId, escrowItem.LocationId);
            Assert.Equal(2, escrowItem.Quantity);
            Assert.True(await wallets.AccountBalanceAsync(new WalletAccount(42), CancellationToken.None) < 1_000_000m);
            await using var expiry = source.CreateCommand($"SELECT count(*) FROM {schedulerSchema}.jobs WHERE kind=$1 AND key=$2");
            expiry.Parameters.AddWithValue(ContractExpiryJobs.JobKind);
            expiry.Parameters.AddWithValue(contractId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(1L, (long)(await expiry.ExecuteScalarAsync())!);
            await using var notice = source.CreateCommand($"SELECT payload->>'name' FROM {eventsSchema}.outbox WHERE topic=$1 ORDER BY id");
            notice.Parameters.AddWithValue(ContractNotifications.Topic);
            Assert.Equal("OnContractCreated", (string?)await notice.ExecuteScalarAsync());
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
    public async Task Delete_contract_calls_persist_status_and_batch_keeps_per_contract_failures()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"cdw_{suffix}";
        var inventorySchema = $"cdi_{suffix}";
        var walletSchema = $"cdwlt_{suffix}";
        const long nowMs = 1_800_000_000_000;
        var nowFileTime = Eve.Services.Py.FileTimeFromUnixMs(nowMs);
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(connectionString));
        await PersistenceContractReadModel.MigrateAsync(source, contractsSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        try
        {
            await using (var seed = source.CreateCommand($"""
                INSERT INTO {contractsSchema}.contract_records
                    (contract_id, issuer_id, issuer_corporation_id, contract_type, status,
                     start_station_id, date_expired_filetime, created_at_filetime)
                VALUES (7201, 42, 84, 1, 0, 3001, $1, $2),
                       (7202, 99, 84, 1, 0, 3001, $1, $2),
                       (7203, 42, 84, 1, 0, 3001, $1, $2)
                """))
            {
                seed.Parameters.AddWithValue(nowFileTime + 86_400_000);
                seed.Parameters.AddWithValue(nowFileTime - 1000);
                await seed.ExecuteNonQueryAsync();
            }
            StartContractHost(new PersistenceContractWriteModel(source, new PgItemStore(source, inventorySchema),
                new WalletTransactions(source, walletSchema), () => nowMs, contractsSchema));
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            async Task<PyValue> Rpc(uint id, string method, PyValue[] args)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, id, "contractProxy", method, new PyTuple(args),
                    Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));
                while (true)
                {
                    var send = Assert.IsType<Send>(await front.ReceiveAsync());
                    var response = MarshalCodec.Decode(send.Payload);
                    if (response is not PyObject { Args: PyTuple callArgs } || callArgs.Items.Count < 3 ||
                        MachoAddress.Decode(callArgs.Items[2]).CallId != new PyInt(id))
                        continue;
                    Assert.Equal(new PyInt(7), callArgs.Items[0]);
                    return Result(response)!;
                }
            }

            Assert.Same(PyBool.True, await Rpc(1, "DeleteContract", [new PyInt(7201)]));
            var batch = Assert.IsType<PyTuple>(await Rpc(2, "DeleteMultipleContracts",
                [new PyList([new PyInt(7202), new PyLong(7203)])]));
            Assert.Equal([new PyInt(7203)], Assert.IsType<PyList>(batch.Items[0]).Items);
            Assert.Equal([new PyInt(7202)], Assert.IsType<PyList>(batch.Items[1]).Items);
            await using var statuses = source.CreateCommand($"SELECT contract_id, status FROM {contractsSchema}.contract_records ORDER BY contract_id");
            await using var reader = await statuses.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((7201L, (short)5), (reader.GetInt64(0), reader.GetInt16(1)));
            Assert.True(await reader.ReadAsync());
            Assert.Equal((7202L, (short)0), (reader.GetInt64(0), reader.GetInt16(1)));
            Assert.True(await reader.ReadAsync());
            Assert.Equal((7203L, (short)5), (reader.GetInt64(0), reader.GetInt16(1)));
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
    public async Task Complete_contract_finishes_fails_and_rejects_with_persisted_notifications()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"ccwc_{suffix}";
        var inventorySchema = $"ccwi_{suffix}";
        var walletSchema = $"ccww_{suffix}";
        var schedulerSchema = $"ccws_{suffix}";
        var eventsSchema = $"ccwe_{suffix}";
        const long nowMs = 1_800_000_000_000;
        var nowFileTime = Eve.Services.Py.FileTimeFromUnixMs(nowMs);
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
            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(source, scheduler, schema: eventsSchema);
            var wraps = new Dictionary<long, long>();
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                foreach (var contractId in new long[] { 7101, 7102 })
                    wraps[contractId] = await itemStore.CreateAsync(connection, transaction,
                        new NewItem(3468, 42, 3001, ItemFlags.Hangar, Singleton: true), CancellationToken.None);
                await using var seed = new NpgsqlCommand($"""
                    INSERT INTO {contractsSchema}.contract_records
                        (contract_id, issuer_id, issuer_corporation_id, contract_type, status, acceptor_id, assignee_id,
                         start_station_id, end_station_id, date_accepted_filetime, date_expired_filetime,
                         created_at_filetime, plastic_wrap_item_id, num_days)
                    VALUES
                        (7101, 100, 84, 3, 1, 42, 0, 3001, 3001, $1, $2, $3, $4, 2),
                        (7102, 100, 84, 3, 1, 42, 0, 3001, 3001, $1, $2, $3, $5, 2),
                        (7103, 100, 84, 3, 0, 0, 42, 3001, 3001, 0, $2, $3, 0, 0)
                    """, connection, transaction);
                seed.Parameters.AddWithValue(nowFileTime - 1000);
                seed.Parameters.AddWithValue(nowFileTime + 86_400_000);
                seed.Parameters.AddWithValue(nowFileTime - 2000);
                seed.Parameters.AddWithValue(wraps[7101]);
                seed.Parameters.AddWithValue(wraps[7102]);
                await seed.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }

            StartContractHost(new PersistenceContractWriteModel(source, itemStore, wallets, () => nowMs,
                contractsSchema, events: events));
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            async Task<PyValue> Rpc(uint id, long contractId, int status)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, id, "contractProxy", "CompleteContract",
                    new PyTuple([new PyInt(contractId), new PyInt(status)]),
                    Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));
                while (true)
                {
                    var send = Assert.IsType<Send>(await front.ReceiveAsync());
                    var response = MarshalCodec.Decode(send.Payload);
                    if (response is not PyObject { Args: PyTuple args } || args.Items.Count < 3 ||
                        MachoAddress.Decode(args.Items[2]).CallId != new PyInt(id))
                        continue;
                    Assert.Equal(new PyInt(7), args.Items[0]);
                    return Result(response)!;
                }
            }

            Assert.Same(PyBool.True, await Rpc(1, 7101, 4));
            Assert.Same(PyBool.True, await Rpc(2, 7102, 7));
            Assert.Same(PyBool.True, await Rpc(3, 7103, 6));
            await using (var state = source.CreateCommand($"SELECT contract_id, status FROM {contractsSchema}.contract_records ORDER BY contract_id"))
            await using (var reader = await state.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal((7101L, (short)4), (reader.GetInt64(0), reader.GetInt16(1)));
                Assert.True(await reader.ReadAsync());
                Assert.Equal((7102L, (short)7), (reader.GetInt64(0), reader.GetInt16(1)));
                Assert.True(await reader.ReadAsync());
                Assert.Equal((7103L, (short)6), (reader.GetInt64(0), reader.GetInt16(1)));
            }
            Assert.Null(await itemStore.GetAsync(wraps[7101], CancellationToken.None));
            Assert.Null(await itemStore.GetAsync(wraps[7102], CancellationToken.None));
            await using var notifications = source.CreateCommand($"SELECT payload->>'name' FROM {eventsSchema}.outbox WHERE topic=$1 ORDER BY id");
            notifications.Parameters.AddWithValue(ContractNotifications.Topic);
            var names = new List<string>();
            await using (var reader = await notifications.ExecuteReaderAsync())
                while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            Assert.Equal(["OnContractCompleted", "OnContractFailed", "OnContractRejected", "OnContractRejected"], names);
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
    public async Task Accept_contract_runs_through_host_and_persists_courier_state_and_notification()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must name the assigned PostgreSQL lane");
        var suffix = Guid.NewGuid().ToString("N");
        var contractsSchema = $"contract_accept_wire_{suffix}";
        var inventorySchema = $"contract_accept_wire_items_{suffix}";
        var walletSchema = $"contract_accept_wire_wallet_{suffix}";
        var schedulerSchema = $"contract_accept_wire_scheduler_{suffix}";
        var eventsSchema = $"contract_accept_wire_events_{suffix}";
        const long nowMs = 1_800_000_000_000;
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
            var scheduler = new Eve.Scheduler.DurableScheduler(source, () => nowMs, schema: schedulerSchema);
            var events = new Eve.Events.EventBus(source, scheduler, schema: eventsSchema);
            const long contractId = 7001;
            const long issuerId = 100;
            const long stationId = 3001;
            var expiry = Eve.Services.Py.FileTimeFromUnixMs(nowMs + 86_400_000);
            var created = Eve.Services.Py.FileTimeFromUnixMs(nowMs - 1000);
            long wrapItemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                wrapItemId = await itemStore.CreateAsync(connection, transaction,
                    new NewItem(3468, 1, 9_300_000_000 + contractId, ItemFlags.Hangar, Singleton: true), CancellationToken.None);
                await using var seed = new NpgsqlCommand($"""
                    INSERT INTO {contractsSchema}.contract_records
                        (contract_id, issuer_id, issuer_corporation_id, is_corporation_issued,
                         contract_type, status, availability, start_station_id, end_station_id,
                         date_expired_filetime, created_at_filetime, plastic_wrap_item_id, num_days)
                    VALUES ($1, $2, 84, false, 3, 0, 0, $3, 4001, $4, $5, $6, 2)
                    """, connection, transaction);
                seed.Parameters.AddWithValue(contractId);
                seed.Parameters.AddWithValue(issuerId);
                seed.Parameters.AddWithValue(stationId);
                seed.Parameters.AddWithValue(expiry);
                seed.Parameters.AddWithValue(created);
                seed.Parameters.AddWithValue(wrapItemId);
                await seed.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }

            var writer = new PersistenceContractWriteModel(source, itemStore, wallets, () => nowMs,
                contractsSchema, events: events);
            StartContractHost(writer);
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            _ = await front.ReceiveAsync();
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, 1, "contractProxy", "AcceptContract", new PyTuple([new PyInt(contractId), PyBool.False]),
                Eve.Services.Py.Dict(("machoVersion", new PyInt(1)))))));

            PyValue? result = null;
            while (result is null)
            {
                var send = Assert.IsType<Send>(await front.ReceiveAsync());
                var response = MarshalCodec.Decode(send.Payload);
                if (response is not PyObject { Args: PyTuple args } || args.Items.Count < 3 ||
                    MachoAddress.Decode(args.Items[2]).CallId != new PyInt(1))
                    continue;
                Assert.Equal(new PyInt(7), args.Items[0]);
                result = Result(response);
            }

            var accepted = Assert.IsType<PyObject>(result);
            var fields = Fields(accepted);
            Assert.Equal(new PyInt(contractId), fields["contractID"]);
            Assert.Equal(new PyInt(3), fields["type"]);
            Assert.Equal(new PyInt(1), fields["status"]);
            Assert.Equal(new PyInt(issuerId), fields["issuerID"]);
            Assert.Equal(new PyInt(42), fields["acceptorID"]);
            Assert.True(((PyLong)fields["dateAccepted"]).Value > 0);

            await using (var state = source.CreateCommand($"SELECT status, acceptor_id, date_accepted_filetime FROM {contractsSchema}.contract_records WHERE contract_id=$1"))
            {
                state.Parameters.AddWithValue(contractId);
                await using var reader = await state.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((short)1, reader.GetInt16(0));
                Assert.Equal(42L, reader.GetInt64(1));
                Assert.True(reader.GetInt64(2) > 0);
            }
            var movedWrap = await itemStore.GetAsync(wrapItemId, CancellationToken.None);
            Assert.NotNull(movedWrap);
            Assert.Equal((42L, stationId, ItemFlags.Hangar), (movedWrap.OwnerId, movedWrap.LocationId, movedWrap.Flag));
            await using var notification = source.CreateCommand($"SELECT payload->>'name' FROM {eventsSchema}.outbox WHERE topic=$1 ORDER BY id");
            notification.Parameters.AddWithValue(ContractNotifications.Topic);
            Assert.Equal("OnContractAccepted", (string?)await notification.ExecuteScalarAsync());
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
