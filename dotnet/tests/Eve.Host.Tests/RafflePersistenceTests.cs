using Eve.Data;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
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

    [Fact]
    public async Task Migration_create_sellout_award_and_expiry_preserve_custody_and_money()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var inventory = "rf_i_" + suffix;
        var wallet = "rf_w_" + suffix;
        var identity = "rf_a_" + suffix;
        var raffle = "rf_r_" + suffix;
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
            await AssertInvalidPrize(await Give(2048, 2), 2048);
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
            var closedSession = new ClientSession(new ClosedSink()) { UserId = login.AccountId, CharacterId = seller, SessionId = 700 };
            foreach (var subscriber in new[] { closedSession, ownerSession, buyerSession })
                await restartedStore.InvokeAsync("SubscribeToRaffle", new ServiceCall
                {
                    Method = "SubscribeToRaffle", Session = subscriber, Args = [new PyLong(expiredId)],
                }, default);
            await restartedStore.ScheduleExistingAsync();
            Assert.True(await restartedScheduler.RunDueAsync() > 0);
            foreach (var sink in new[] { ownerSink, buyerSink })
            {
                var notification = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(sink.Packets)));
                Assert.Equal(MachoMessage.Notification, notification.Type);
                Assert.Equal("OnRaffleFinishedServer", notification.Dest.BroadcastId);
                var frame = Assert.IsType<PyTuple>(notification.Payload);
                var envelope = Assert.IsType<PyTuple>(Assert.Single(frame.Items));
                var buffer = Assert.IsType<PyBuffer>(envelope.Items[1]);
                var inner = Assert.IsType<PyTuple>(MarshalCodec.Decode(buffer.Value));
                var body = Assert.IsType<PyTuple>(inner.Items[1]);
                var arguments = Assert.IsType<PyTuple>(body.Items[1]);
                Assert.Equal(new PyLong(expiredId), arguments.Items[0]);
                Assert.Same(PyNone.Instance, arguments.Items[1]);
            }
            Assert.Equal(0, await restartedScheduler.RunDueAsync());
            Assert.Single(ownerSink.Packets);
            Assert.Single(buyerSink.Packets);
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
            foreach (var schema in new[] { raffle, schedulerSchema, inventory, wallet, identity })
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
