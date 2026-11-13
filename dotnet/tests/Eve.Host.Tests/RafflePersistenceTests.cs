using Eve.Data;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Wallet;
using Eve.Scheduler;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class RafflePersistenceTests
{
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class ClosedSink : IPacketSink
    {
        public void Send(PyValue packet) => throw new InvalidOperationException("closed sink");
    }

    private static PyTuple NotificationArguments(MachoPacket packet)
    {
        var frame = Assert.IsType<PyTuple>(packet.Payload);
        var envelope = Assert.IsType<PyTuple>(Assert.Single(frame.Items));
        var inner = Assert.IsType<PyTuple>(MarshalCodec.Decode(Assert.IsType<PyBuffer>(envelope.Items[1]).Value));
        var body = Assert.IsType<PyTuple>(inner.Items[1]);
        return Assert.IsType<PyTuple>(body.Items[1]);
    }

    private static long Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected integer wire value; got {value.GetType().Name}.")
    };

    private static PyValue Field(PyValue value, string name) => Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries
        .Single(pair => JsSemantics.StringValue(pair.Key) == name).Value;

    [Fact]
    public async Task Migration_create_sellout_award_and_expiry_preserve_custody_and_money()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var inventory = "rf_i_" + suffix;
        var wallet = "rf_w_" + suffix;
        var identity = "rf_a_" + suffix;
        var raffle = "rf_r_" + suffix;
        var concurrentRaffle = "rf_rc_" + suffix;
        var schedulerSchema = "rf_s_" + suffix;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        try
        {
            await PgItemStore.MigrateAsync(source, inventory);
            await PgWalletStore.MigrateAsync(source, wallet);
            await Migrations.ApplyAsync(source, identity, PgAccountDirectory.MigrationSet());
            var directory = new PgAccountDirectory(source, new AccountOptions { AutoCreate = true }, identity);
            var login = Assert.IsType<LoggedIn>(await directory.LoginAsync(new LoginAttempt("raffle-test", new byte[] { 9, 9, 9 }, "en", "127.0.0.1"), CancellationToken.None));
            var items = new PgItemStore(source, inventory);
            var walletStore = new PgWalletStore(source, wallet);
            var wallets = new WalletTransactions(source, wallet);
            var store = new RaffleProxyStore(source, items, wallets, schema: raffle, identitySchema: identity);
            await store.MigrateAsync();
            await store.MigrateAsync();
            var concurrentStore = new RaffleProxyStore(source, items, wallets, schema: concurrentRaffle, identitySchema: identity);
            await concurrentStore.MigrateAsync();
            const long seller = 90000101, buyer = 90000102, rival = 90000103, poor = 90000104, station = 60003760;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var schedulerNow = now;
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            var scheduler = new DurableScheduler(source, () => schedulerNow, null, schedulerSchema);
            store.RegisterScheduler(scheduler);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                await walletStore.OpenAsync(connection, tx, seller, 0m, 0);
                await walletStore.OpenAsync(connection, tx, buyer, 10000m, 0);
                await walletStore.OpenAsync(connection, tx, rival, 10000m, 0);
                await walletStore.OpenAsync(connection, tx, poor, 0m, 0);
                await tx.CommitAsync();
            }
            async Task<long> Give(int type, long count)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var tx = await connection.BeginTransactionAsync();
                var id = await items.CreateAsync(connection, tx, new NewItem(type, seller, station, ItemFlags.Hangar, count));
                await tx.CommitAsync();
                return id;
            }
            ServiceCall Call(long character, params PyValue[] args)
            {
                var session = new ClientSession { CharacterId = character, UserId = login.AccountId };
                session.Set("stationid", new PyLong(station));
                return new ServiceCall { Method = "test", Args = args, Session = session };
            }
            async Task<(long Id, long Prize)> Create(int type = 587)
            {
                var prize = await Give(type, 1);
                var token = await Give(52568, 2);
                var data = Py.KeyVal(("owner_id", new PyLong(seller)), ("item_id", new PyLong(prize)),
                    ("type_id", new PyInt(type)), ("location_id", new PyLong(station)),
                    ("token_id", new PyLong(token)), ("token_location_id", new PyLong(station)),
                    ("ticket_count", new PyInt(8)), ("ticket_price", new PyInt(125)));
                return (await store.CreateRaffleAsync(Call(seller, data), now, 3_500_000), prize);
            }
            var concurrentPrize = await Give(587, 1);
            var concurrentToken = await Give(52568, 2);
            var concurrentData = Py.KeyVal(("item_id", new PyLong(concurrentPrize)), ("type_id", new PyInt(587)),
                ("location_id", new PyLong(station)), ("token_id", new PyLong(concurrentToken)),
                ("token_location_id", new PyLong(station)), ("ticket_count", new PyInt(8)), ("ticket_price", new PyInt(125)));
            var concurrentId = await concurrentStore.CreateRaffleAsync(Call(seller, concurrentData), now, 3_500_000);
            await using (var locker = await source.OpenConnectionAsync())
            await using (var lockTransaction = await locker.BeginTransactionAsync())
            {
                await using (var lockRow = new NpgsqlCommand(
                    $"SELECT raffle_id FROM {concurrentRaffle}.raffle_records WHERE raffle_id=$1 FOR UPDATE", locker, lockTransaction))
                {
                    lockRow.Parameters.AddWithValue(concurrentId);
                    Assert.Equal(concurrentId, (long)(await lockRow.ExecuteScalarAsync())!);
                }
                var concurrentExpiries = Task.WhenAll(
                    concurrentStore.ExpireDueAsync(now + 4L * 24 * 60 * 60 * 1000),
                    concurrentStore.ExpireDueAsync(now + 4L * 24 * 60 * 60 * 1000));
                var bothWaitingForLock = false;
                for (var attempt = 0; attempt < 500; attempt++)
                {
                    await using var blocked = source.CreateCommand($"""
                        SELECT count(*) FROM pg_stat_activity
                        WHERE datname=current_database() AND wait_event_type='Lock'
                          AND query LIKE '%FROM {concurrentRaffle}.raffle_records%'
                          AND query LIKE '%FOR UPDATE%'
                        """);
                    if ((long)(await blocked.ExecuteScalarAsync())! >= 2)
                    {
                        bothWaitingForLock = true;
                        break;
                    }
                    await Task.Delay(10);
                }
                Assert.True(bothWaitingForLock, "Both expiry transactions must reach the locked raffle row before release.");
                await lockTransaction.CommitAsync();
                await concurrentExpiries;
            }
            await using (var command = source.CreateCommand($"SELECT status FROM {concurrentRaffle}.raffle_records WHERE raffle_id=$1"))
            {
                command.Parameters.AddWithValue(concurrentId);
                Assert.Equal((short)9, (short)(await command.ExecuteScalarAsync())!);
            }
            Assert.Equal(ItemFlags.Hangar, (await items.GetAsync(concurrentPrize))!.Flag);
            // Published module type 2048 is a normal category 7 item, supported by HyperNet.
            var (moduleRaffle, modulePrize) = await Create(2048);
            Assert.True(moduleRaffle > 0);
            Assert.Equal(ItemFlags.RaffleEscrow, (await items.GetAsync(modulePrize))!.Flag);
            // PLEX is explicitly listed in pinned typeList 133 and cannot be a prize.
            var excludedPrize = await Give(44992, 1);
            var excludedToken = await Give(52568, 2);
            var excludedData = Py.KeyVal(("item_id", new PyLong(excludedPrize)), ("type_id", new PyInt(44992)),
                ("location_id", new PyLong(station)), ("token_id", new PyLong(excludedToken)),
                ("token_location_id", new PyLong(station)), ("ticket_count", new PyInt(8)), ("ticket_price", new PyInt(125)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateRaffleAsync(Call(seller, excludedData), now, 3_500_000));
            Assert.Equal(ItemFlags.Hangar, (await items.GetAsync(excludedPrize))!.Flag);
            Assert.Equal(2, (await items.GetAsync(excludedToken))!.Quantity);
            // A locked prize cannot gain children through either item creation or movement.
            var movable = await Give(587, 1);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => items.CreateAsync(connection, tx,
                    new NewItem(34, seller, modulePrize, ItemFlags.Hangar, 1)));
                await Assert.ThrowsAsync<InvalidOperationException>(() => items.MoveAsync(connection, tx, movable, modulePrize, ItemFlags.Hangar));
                await tx.RollbackAsync();
            }
            Assert.Equal(station, (await items.GetAsync(movable))!.LocationId);
            async Task AssertInvalidPrize(long invalidPrize, int invalidType)
            {
                var freshToken = await Give(52568, 2);
                var invalidData = Py.KeyVal(("item_id", new PyLong(invalidPrize)), ("type_id", new PyInt(invalidType)),
                    ("location_id", new PyLong(station)), ("token_id", new PyLong(freshToken)),
                    ("token_location_id", new PyLong(station)), ("ticket_count", new PyInt(8)), ("ticket_price", new PyInt(125)));
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateRaffleAsync(Call(seller, invalidData), now, 3_500_000));
                Assert.Equal(ItemFlags.Hangar, (await items.GetAsync(invalidPrize))!.Flag);
                Assert.Equal(2, (await items.GetAsync(freshToken))!.Quantity);
            }
            long parent;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                parent = await items.CreateAsync(connection, tx, new NewItem(683, seller, station, ItemFlags.Hangar, 1, true, BlueprintCopy: true));
                await items.CreateAsync(connection, tx, new NewItem(34, seller, parent, ItemFlags.Hangar, 1));
                await tx.CommitAsync();
            }
            await AssertInvalidPrize(parent, 683);
            var (id, prizeId) = await Create();
            long copyId, copyToken;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                copyId = await items.CreateAsync(connection, tx, new NewItem(683, seller, station, ItemFlags.Hangar, 1, true, BlueprintCopy: true));
                copyToken = await items.CreateAsync(connection, tx, new NewItem(52568, seller, station, ItemFlags.Hangar, 2));
                await tx.CommitAsync();
            }
            var copyData = Py.KeyVal(("item_id", new PyLong(copyId)), ("type_id", new PyInt(683)),
                ("location_id", new PyLong(station)), ("token_id", new PyLong(copyToken)),
                ("token_location_id", new PyLong(station)), ("ticket_count", new PyInt(8)),
                ("ticket_price", new PyInt(125)));
            var copyRaffle = await store.CreateRaffleAsync(Call(seller, copyData), now, 3_500_000);
            var browsingSession = Call(buyer).Session;
            async Task<PyList> Browse(PyDict filters, PyDict constraints)
            {
                var call = new ServiceCall { Method = "FilteredGrab", Session = browsingSession,
                    Args = [], Kwargs = Py.Dict(("filters", filters), ("constraints", constraints), ("size", new PyInt(10))) };
                return Assert.IsType<PyList>(await store.InvokeAsync("FilteredGrab", call, default));
            }
            PyValue Field(PyValue value, string key)
            {
                var fields = value is PyObject obj ? Assert.IsType<PyDict>(obj.Args) : Assert.IsType<PyDict>(value);
                return fields.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == key).Value;
            }
            var copies = await Browse(Py.Dict(("blueprint_type", new PyInt(2)), ("type_id", new PyInt(683))),
                Py.Dict(("min_ticket_price", new PyInt(125)), ("max_ticket_count", new PyInt(8))));
            var copy = Assert.Single(copies.Items);
            Assert.Equal(new PyLong(copyRaffle), Field(copy, "raffle_id"));
            Assert.Equal(PyBool.True, Field(Field(copy, "meta_data"), "is_copy"));
            Assert.Empty((await Browse(Py.Dict(("blueprint_type", new PyInt(1)), ("type_id", new PyInt(683))), Py.Dict())).Items);
            Assert.Empty((await Browse(Py.Dict(("type_id", new PyInt(683))), Py.Dict(("min_ticket_price", new PyInt(126))))).Items);
            var ore = await Browse(Py.Dict(("type_id", new PyInt(587))), Py.Dict());
            Assert.Equal(new PyLong(id), Field(Assert.Single(ore.Items), "raffle_id"));
            Assert.Equal(PyBool.False, Field(Field(Assert.Single(ore.Items), "meta_data"), "is_copy"));
            var escrow = await items.GetAsync(prizeId);
            Assert.NotNull(escrow);
            Assert.Equal(seller, escrow.OwnerId);
            Assert.Equal(station, escrow.LocationId);
            Assert.Equal(ItemFlags.RaffleEscrow, escrow.Flag);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
                await Assert.ThrowsAsync<InvalidOperationException>(() => items.DeleteAsync(connection, tx, prizeId));
            for (var number = 0; number < 8; number++)
                await store.BuyTicketAsync(id, buyer, number, now);
            var repeat = await store.BuyTicketAsync(id, buyer, 7, now);
            Assert.False(repeat.WasNew);
            var stats = await store.InvokeAsync("GetCreatedParticipated", Call(buyer), default);
            Assert.Equal(new PyInt(1), Field(stats, "raffles_participated"));
            Assert.Equal(new PyInt(1), Field(stats, "raffles_won"));
            var ownerStats = await store.InvokeAsync("GetCreatedParticipated", Call(seller), default);
            Assert.Equal(new PyInt(1), Field(ownerStats, "finished_undelivered"));
            Assert.Equal(new PyInt(2), Field(ownerStats, "created_running"));
            var stackPrize = await Give(2048, 2);
            var stackToken = await Give(52568, 2);
            var stackData = Py.KeyVal(("item_id", new PyLong(stackPrize)), ("type_id", new PyInt(2048)),
                ("location_id", new PyLong(station)), ("token_id", new PyLong(stackToken)),
                ("token_location_id", new PyLong(station)), ("ticket_count", new PyInt(8)),
                ("ticket_price", new PyInt(125)));
            var stackRaffle = await store.CreateRaffleAsync(Call(seller, stackData), now, 3_500_000);
            Assert.True(stackRaffle > 0);
            Assert.Equal(ItemFlags.RaffleEscrow, (await items.GetAsync(stackPrize))!.Flag);
            Assert.Equal(2L, (await items.GetAsync(stackPrize))!.Quantity);
            Assert.Equal(1L, (await items.GetAsync(stackToken))!.Quantity);
            var history = Assert.IsType<PyTuple>(await store.InvokeAsync("GetMyRaffleHistory", Call(buyer), default));
            var historyRow = Assert.Single(Assert.IsType<PyList>(history.Items[0]).Items);
            Assert.Equal(new PyLong(id), Field(historyRow, "raffle_id"));
            var prices = await store.InvokeAsync("GetActiveHistoricPrices", Call(buyer, new PyInt(587)), default);
            Assert.Equal(new PyInt(1), Field(prices, "historic_count"));
            await store.AwardItemAsync(Call(buyer), id);
            var delivered = await items.GetAsync(prizeId);
            Assert.NotNull(delivered);
            Assert.Equal(buyer, delivered.OwnerId);
            Assert.Equal(station, delivered.LocationId);
            Assert.Equal(ItemFlags.Hangar, delivered.Flag);
            Assert.Equal(9000m, await wallets.AccountBalanceAsync(new WalletAccount(buyer, 1000)));
            var (expiredId, expiredPrize) = await Create();
            await Assert.ThrowsAnyAsync<Exception>(() => store.BuyTicketAsync(expiredId, poor, 1, now));
            Assert.Equal(0m, await wallets.AccountBalanceAsync(new WalletAccount(poor, 1000)));
            async Task<bool> TryBuy(long character)
            {
                try { await store.BuyTicketAsync(expiredId, character, 0, now); return true; }
                catch (InvalidOperationException) { return false; }
            }
            var outcomes = await Task.WhenAll(TryBuy(buyer), TryBuy(rival));
            Assert.Single(outcomes, success => success);
            var paidBy = outcomes[0] ? buyer : rival;
            var active = Assert.IsType<PyList>(await store.InvokeAsync("GetMyActiveTickets", Call(paidBy), default));
            Assert.Equal(new PyLong(expiredId), Field(Assert.Single(active.Items), "raffle_id"));
            var paidBefore = outcomes[0] ? 9000m : 10000m;
            Assert.Equal(paidBefore - 125m, await wallets.AccountBalanceAsync(new WalletAccount(paidBy, 1000)));
            // Reconstruct both store and scheduler, as a host restart would, then process durable jobs.
            schedulerNow = now + 4L * 24 * 60 * 60 * 1000;
            var restartedScheduler = new DurableScheduler(source, () => schedulerNow, null, schedulerSchema);
            var restartedStore = new RaffleProxyStore(source, items, wallets, schema: raffle, identitySchema: identity);
            restartedStore.RegisterScheduler(restartedScheduler);
            var ownerSink = new Sink();
            var buyerSink = new Sink();
            var ownerSession = new ClientSession(ownerSink) { UserId = login.AccountId, CharacterId = seller, SessionId = 701 };
            var buyerSession = new ClientSession(buyerSink) { UserId = login.AccountId, CharacterId = paidBy, SessionId = 702 };
            var characterSessions = new CharacterSessions();
            characterSessions.Enter(seller, ownerSession);
            characterSessions.Enter(paidBy, buyerSession);
            restartedStore.SetCharacterSessions(characterSessions);
            using var walletAnnouncer = new WalletAnnouncer(wallets, characterSessions);
            var closedSession = new ClientSession(new ClosedSink()) { UserId = login.AccountId, CharacterId = seller, SessionId = 700 };
            foreach (var subscriber in new[] { closedSession, ownerSession, buyerSession })
                await restartedStore.InvokeAsync("SubscribeToRaffle", new ServiceCall
                {
                    Method = "SubscribeToRaffle", Session = subscriber, Args = [new PyLong(expiredId)],
                }, default);
            await restartedStore.ScheduleExistingAsync();
            Assert.True(await restartedScheduler.RunDueAsync() > 0);
            var ownerNotifications = ownerSink.Packets.Select(packet => Assert.IsType<MachoPacket>(MachoPacket.Decode(packet))).ToArray();
            Assert.All(ownerNotifications, notification => Assert.Equal(MachoMessage.Notification, notification.Type));
            var ownerItemNotice = Assert.Single(ownerNotifications, notification =>
                notification.Dest.BroadcastId == "OnItemChange" &&
                Number(Assert.IsType<PyPackedRow>(NotificationArguments(notification).Items[0]).Values[0]) == expiredPrize);
            var restoredRow = Assert.IsType<PyPackedRow>(NotificationArguments(ownerItemNotice).Items[0]);
            Assert.Equal(InventoryWire.Id(expiredPrize), restoredRow.Values[0]);
            Assert.Equal(new PyInt(ItemFlags.Hangar), restoredRow.Values[4]);
            Assert.Equal(new PyDict([new(new PyInt(4), new PyInt(ItemFlags.RaffleEscrow))]),
                Assert.IsType<PyDict>(NotificationArguments(ownerItemNotice).Items[1]));
            Assert.Equal("clientID", ownerItemNotice.Dest.IdType);
            var expiredRaffleNotices = ownerNotifications.Where(notification =>
                (notification.Dest.BroadcastId is "OnRaffleUpdatedServer" or "OnRaffleFinishedServer") &&
                Number(NotificationArguments(notification).Items[0]) == expiredId).ToArray();
            Assert.Equal(["OnRaffleUpdatedServer", "OnRaffleFinishedServer"],
                expiredRaffleNotices.Select(notification => notification.Dest.BroadcastId));
            Assert.Equal("clientID", expiredRaffleNotices[0].Dest.IdType);
            Assert.Equal("charid", expiredRaffleNotices[1].Dest.IdType);
            Assert.True(Array.IndexOf(ownerNotifications, ownerItemNotice) < Array.IndexOf(ownerNotifications, expiredRaffleNotices[0]));
            Assert.Equal(new PyInt(9), Field(NotificationArguments(expiredRaffleNotices[0]).Items[1], "raffle_status"));
            var finishedArguments = NotificationArguments(expiredRaffleNotices[1]);
            Assert.Equal(new PyLong(expiredId), finishedArguments.Items[0]);
            Assert.Same(PyNone.Instance, finishedArguments.Items[1]);

            var buyerNotifications = buyerSink.Packets.Select(packet => Assert.IsType<MachoPacket>(MachoPacket.Decode(packet))).ToArray();
            Assert.Equal(["OnAccountChange", "OnRaffleUpdatedServer", "OnRaffleFinishedServer"],
                buyerNotifications.Select(notification => notification.Dest.BroadcastId));
            Assert.All(buyerNotifications, notification => Assert.Equal(MachoMessage.Notification, notification.Type));
            Assert.Equal("clientID", buyerNotifications[0].Dest.IdType);
            Assert.Equal("clientID", buyerNotifications[1].Dest.IdType);
            Assert.Equal("charid", buyerNotifications[2].Dest.IdType);
            var refundedBalance = NotificationArguments(buyerNotifications[0]);
            Assert.Equal("cash", JsSemantics.StringValue(refundedBalance.Items[0]));
            Assert.Equal(new PyInt(paidBy), refundedBalance.Items[1]);
            Assert.Equal(new PyReal((double)paidBefore), refundedBalance.Items[2]);
            Assert.Equal(0, await restartedScheduler.RunDueAsync());
            Assert.Equal(ownerNotifications.Length, ownerSink.Packets.Count);
            Assert.Equal(3, buyerSink.Packets.Count);
            var returned = await items.GetAsync(expiredPrize);
            Assert.NotNull(returned);
            Assert.Equal(seller, returned.OwnerId);
            Assert.Equal(ItemFlags.Hangar, returned.Flag);
            Assert.Equal(paidBefore, await wallets.AccountBalanceAsync(new WalletAccount(paidBy, 1000)));

            // Simulate a persisted record from the former system-owned escrow layout.
            var (legacyId, legacyPrize) = await Create();
            const long legacyLocation = 9_400_000_123;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                await using var command = new NpgsqlCommand($"UPDATE {inventory}.items SET owner_id=1, location_id=$1 WHERE id=$2", connection, tx);
                command.Parameters.AddWithValue(legacyLocation);
                command.Parameters.AddWithValue(legacyPrize);
                await command.ExecuteNonQueryAsync();
                await using var record = new NpgsqlCommand($"UPDATE {raffle}.raffle_records SET escrow_location_id=$1 WHERE raffle_id=$2", connection, tx);
                record.Parameters.AddWithValue(legacyLocation);
                record.Parameters.AddWithValue(legacyId);
                await record.ExecuteNonQueryAsync();
                await tx.CommitAsync();
            }
            await store.MigrateAsync();
            await store.MigrateAsync();
            var recovered = await items.GetAsync(legacyPrize);
            Assert.NotNull(recovered);
            Assert.Equal(seller, recovered.OwnerId);
            Assert.Equal(station, recovered.LocationId);
            Assert.Equal(ItemFlags.RaffleEscrow, recovered.Flag);
        }
        finally
        {
            foreach (var schema in new[] { concurrentRaffle, raffle, schedulerSchema, inventory, wallet, identity })
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task Expiry_job_processes_its_raffle_when_more_than_one_sweep_batch_is_due()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var inventory = "rf_batch_i_" + suffix;
        var wallet = "rf_batch_w_" + suffix;
        var raffle = "rf_batch_r_" + suffix;
        var schedulerSchema = "rf_batch_s_" + suffix;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        try
        {
            await PgItemStore.MigrateAsync(source, inventory);
            await PgWalletStore.MigrateAsync(source, wallet);
            var items = new PgItemStore(source, inventory);
            var store = new RaffleProxyStore(source, items, new WalletTransactions(source, wallet), schema: raffle);
            await store.MigrateAsync();
            await DurableScheduler.MigrateAsync(source, schedulerSchema);

            const long owner = 90000401, station = 60003760;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using var scheduler = new DurableScheduler(source, () => now,
                new SchedulerOptions { Concurrency = 1 }, schedulerSchema);
            store.RegisterScheduler(scheduler);

            var raffleIds = new List<long>();
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                for (var index = 0; index <= 100; index++)
                {
                    var itemId = await items.CreateAsync(connection, transaction,
                        new NewItem(587, owner, station, ItemFlags.Hangar, 1));
                    await items.PutInRaffleEscrowAsync(connection, transaction, itemId, owner, station);
                    await using var insert = new NpgsqlCommand($"""
                        INSERT INTO {raffle}.raffle_records
                            (owner_id,item_id,type_id,location_id,ticket_count,ticket_price,created_at_ms,expires_at_ms,status)
                        VALUES ($1,$2,587,$3,8,125,$4,$5,6) RETURNING raffle_id
                        """, connection, transaction);
                    insert.Parameters.AddWithValue(owner);
                    insert.Parameters.AddWithValue(itemId);
                    insert.Parameters.AddWithValue(station);
                    insert.Parameters.AddWithValue(now - 4L * 24 * 60 * 60 * 1000);
                    insert.Parameters.AddWithValue(now - 1);
                    raffleIds.Add((long)(await insert.ExecuteScalarAsync())!);
                }
                await transaction.CommitAsync();
            }

            var target = raffleIds[^1];
            await scheduler.ScheduleAsync(new JobSpec("raffles.expire", now)
            {
                Key = target.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Payload = new { raffleId = target },
            });
            Assert.Equal(1, await scheduler.RunDueAsync());

            await using var statuses = source.CreateCommand($"SELECT raffle_id,status FROM {raffle}.raffle_records WHERE raffle_id=ANY($1)");
            statuses.Parameters.AddWithValue(raffleIds.ToArray());
            var statusById = new Dictionary<long, short>();
            await using var reader = await statuses.ExecuteReaderAsync();
            while (await reader.ReadAsync()) statusById.Add(reader.GetInt64(0), reader.GetInt16(1));
            Assert.Equal((short)9, statusById[target]);
            Assert.Equal((short)6, statusById[raffleIds[0]]);
        }
        finally
        {
            foreach (var schema in new[] { raffle, schedulerSchema, inventory, wallet })
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
