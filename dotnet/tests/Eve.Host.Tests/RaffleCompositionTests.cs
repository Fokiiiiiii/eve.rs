using Eve.Data;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>HostComposition uses a dedicated database populated with the pinned SDE schemas.</summary>
[Collection("host-composition")]
public sealed class RaffleCompositionTests
{
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public bool FailNextNotification { get; set; }
        public void Send(PyValue packet)
        {
            if (FailNextNotification && MachoPacket.Decode(packet)?.Type == MachoMessage.Notification)
            { FailNextNotification = false; throw new IOException("test notification sink failure"); }
            Packets.Add(packet);
        }
    }

    private static PyValue CallRequest(int callId, string method, params PyValue[] args)
    {
        var source = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(7), new PyInt(callId), PyNone.Instance]);
        var body = new PyTuple([new PyInt(1), new PyStr(method), new PyTuple(args), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        var head = new PyValue[] { new PyInt(6), source, MachoAddress.Any("raffleProxy").Encode(), new PyInt(90000002), payload, PyNone.Instance };
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - head.Length)]));
    }

    [Fact]
    public async Task Host_composition_registers_raffle_proxy_with_persistent_callback()
    {
        var sourceUrl = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must identify the local pinned-SDE database");
        var databaseUrl = new UriBuilder(sourceUrl) { Path = "/eve_raffle_host" }.Uri.ToString();
        var names = new[] { "EVE_PG_URL", "EVE_SIM", "EVE_MARKET_SEED", "EVE_WORMHOLES" };
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        HostRuntime? host = null;
        try
        {
            Environment.SetEnvironmentVariable("EVE_PG_URL", databaseUrl);
            Environment.SetEnvironmentVariable("EVE_SIM", "off");
            Environment.SetEnvironmentVariable("EVE_MARKET_SEED", "skip");
            Environment.SetEnvironmentVariable("EVE_WORMHOLES", "off");
            var settings = new HostSettings("", "disabled",
                new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
                new AccountOptions { AutoCreate = true }, StrictRpc: true);
            host = await HostComposition.BuildAsync(settings);
            var service = Assert.IsAssignableFrom<IService>(host.Registry.Lookup("raffleProxy"));
            Assert.True(service.HasMethod("CreateRaffle"));
            Assert.True(service.HasMethod("GetRaffle"));
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(databaseUrl));
            var items = new PgItemStore(source);
            var directory = new PgAccountDirectory(source, new AccountOptions { AutoCreate = true });
            var login = Assert.IsType<LoggedIn>(await directory.LoginAsync(
                new LoginAttempt("raffle-" + Guid.NewGuid().ToString("N"), new byte[] { 9, 9, 9 }, "en", "127.0.0.1"), CancellationToken.None));
            var seller = Random.Shared.NextInt64(90_000_000, 99_999_999);
            const long station = 60003760;
            long prize, token;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                prize = await items.CreateAsync(connection, transaction, new NewItem(587, seller, station, ItemFlags.Hangar, 1));
                token = await items.CreateAsync(connection, transaction, new NewItem(52568, seller, station, ItemFlags.Hangar, 2));
                await new PgWalletStore(source).OpenAsync(connection, transaction, seller, 875m, 0);
                await transaction.CommitAsync();
            }
            var sink = new Sink();
            var session = new ClientSession(sink) { UserId = login.AccountId, CharacterId = seller, SessionId = 7 };
            session.Set("charid", new PyLong(seller));
            session.Set("stationid", new PyLong(station));
            var data = Py.KeyVal(("item_id", new PyLong(prize)), ("type_id", new PyInt(587)),
                ("location_id", new PyLong(station)), ("token_id", new PyLong(token)),
                ("token_location_id", new PyLong(station)), ("ticket_count", new PyInt(8)),
                ("ticket_price", new PyInt(125)));
            var request = MarshalCodec.Decode(MarshalCodec.Encode(CallRequest(9, "CreateRaffle", data)));
            await new PacketDispatcher(host.Registry) { StrictRpc = true }.DispatchAsync(request, session);
            var packets = sink.Packets.Select(packet => Assert.IsType<MachoPacket>(
                MachoPacket.Decode(MarshalCodec.Decode(MarshalCodec.Encode(packet))))).ToArray();
            Assert.Single(packets, packet => packet.Type == MachoMessage.CallRsp);
            Assert.Contains(packets, packet => packet.Type == MachoMessage.Notification && packet.Dest.BroadcastId == "OnRaffleCreatedServer");
            var persisted = await items.GetAsync(prize);
            Assert.NotNull(persisted);
            Assert.Equal(ItemFlags.RaffleEscrow, persisted.Flag);
            Assert.Equal(seller, persisted.OwnerId);
            long raffleId;
            await using (var command = source.CreateCommand("SELECT raffle_id FROM raffle.raffle_records WHERE item_id=$1"))
            {
                command.Parameters.AddWithValue(prize);
                raffleId = (long)(await command.ExecuteScalarAsync())!;
            }
            sink.Packets.Clear();
            var dispatcher = new PacketDispatcher(host.Registry) { StrictRpc = true };
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                CallRequest(10, "BuyRandomTickets", new PyLong(raffleId), new PyInt(8)))), session);
            var failed = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(sink.Packets)));
            Assert.Equal(MachoMessage.ErrorResponse, failed.Type);
            Assert.Contains("NotEnoughISKError", System.Text.Encoding.Latin1.GetString(MarshalCodec.Encode(failed.Payload)));
            var wallets = new WalletTransactions(source);
            Assert.Equal(875m, await wallets.AccountBalanceAsync(new WalletAccount(seller, 1000)));
            await using (var command = source.CreateCommand("SELECT count(*) FROM raffle.raffle_tickets WHERE raffle_id=$1"))
            {
                command.Parameters.AddWithValue(raffleId);
                Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
            }
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.AdjustAsync(connection, transaction, seller, 125m, 1000125, 39, "test funding",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "fund-" + Guid.NewGuid().ToString("N"));
                await transaction.CommitAsync();
            }
            async Task<long> TicketCount()
            {
                await using var command = source.CreateCommand("SELECT count(*) FROM raffle.raffle_tickets WHERE raffle_id=$1");
                command.Parameters.AddWithValue(raffleId);
                return (long)(await command.ExecuteScalarAsync())!;
            }
            var raffleWatcherSink = new Sink();
            var ticketWatcherSink = new Sink();
            var raffleWatcher = new ClientSession(raffleWatcherSink) { UserId = login.AccountId, CharacterId = seller, SessionId = 71 };
            var ticketWatcher = new ClientSession(ticketWatcherSink) { UserId = login.AccountId, CharacterId = seller, SessionId = 72 };
            async Task Watch(ClientSession watcher, int callId, string method, params PyValue[] args)
            {
                await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(CallRequest(callId, method, args))), watcher);
            }
            await Watch(raffleWatcher, 20, "SubscribeToRaffle", new PyLong(raffleId));
            await Watch(ticketWatcher, 21, "SubscribeToTickets");
            await Watch(ticketWatcher, 22, "UnSubscribeFromTickets");
            raffleWatcherSink.Packets.Clear();
            ticketWatcherSink.Packets.Clear();
            var firstBulk = MarshalCodec.Decode(MarshalCodec.Encode(
                CallRequest(11, "BuyRandomTickets", new PyLong(raffleId), new PyInt(2))));
            sink.Packets.Clear();
            sink.FailNextNotification = true; // Caller is the first notification recipient.
            await dispatcher.DispatchAsync(firstBulk, session);
            Assert.Contains(sink.Packets, packet => MachoPacket.Decode(packet)?.Type == MachoMessage.CallRsp);
            Assert.Equal(2L, await TicketCount());
            Assert.Equal(750m, await wallets.AccountBalanceAsync(new WalletAccount(seller, 1000)));
            Assert.Equal(2, raffleWatcherSink.Packets.Count);
            Assert.Empty(ticketWatcherSink.Packets);
            raffleWatcherSink.Packets.Clear();
            sink.Packets.Clear();
            await dispatcher.DispatchAsync(firstBulk, session);
            Assert.Empty(raffleWatcherSink.Packets);
            Assert.Equal(2L, await TicketCount());
            Assert.Equal(750m, await wallets.AccountBalanceAsync(new WalletAccount(seller, 1000)));
            sink.Packets.Clear();
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                CallRequest(11, "BuyRandomTickets", new PyLong(raffleId), new PyInt(3)))), session);
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(sink.Packets))).Type);
            Assert.Equal(2L, await TicketCount());
            await Watch(raffleWatcher, 23, "UnsubscribeFromRaffle", new PyLong(raffleId));
            await Watch(ticketWatcher, 24, "SubscribeToTickets");
            raffleWatcherSink.Packets.Clear();
            ticketWatcherSink.Packets.Clear();
            sink.Packets.Clear();
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                CallRequest(12, "BuyRandomTickets", new PyLong(raffleId), new PyInt(2)))), session);
            Assert.Equal(4L, await TicketCount());
            Assert.Equal(500m, await wallets.AccountBalanceAsync(new WalletAccount(seller, 1000)));
            Assert.Empty(raffleWatcherSink.Packets);
            Assert.Equal(2, ticketWatcherSink.Packets.Count);
            ticketWatcher.Close();
            ticketWatcherSink.Packets.Clear();
            sink.Packets.Clear();
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                CallRequest(13, "BuyRandomTickets", new PyLong(raffleId), new PyInt(4)))), session);
            var purchasePackets = sink.Packets.Select(packet => Assert.IsType<MachoPacket>(MachoPacket.Decode(packet))).ToArray();
            Assert.Single(purchasePackets, packet => packet.Type == MachoMessage.CallRsp);
            Assert.Contains(purchasePackets, packet => packet.Type == MachoMessage.Notification && packet.Dest.BroadcastId == "OnRaffleFinishedServer");
            Assert.Empty(ticketWatcherSink.Packets);
            await using (var command = source.CreateCommand("SELECT count(*),count(DISTINCT ticket_number) FROM raffle.raffle_tickets WHERE raffle_id=$1"))
            {
                command.Parameters.AddWithValue(raffleId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(8, reader.GetInt64(0));
                Assert.Equal(8, reader.GetInt64(1));
            }
            sink.Packets.Clear();
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                CallRequest(14, "AwardItem", new PyLong(raffleId)))), session);
            Assert.Contains(sink.Packets, packet => MachoPacket.Decode(packet)?.Type == MachoMessage.CallRsp);
            Assert.Equal(ItemFlags.Hangar, (await items.GetAsync(prize))!.Flag);
            Assert.Equal(seller, (await items.GetAsync(prize))!.OwnerId);
            var bannedCall = new ServiceCall { Method = "AmIBanned", Session = session, Args = [] };
            Assert.Equal(PyBool.False, await service.CallAsync(bannedCall));
            await using (var command = source.CreateCommand("UPDATE identity.accounts SET banned=true WHERE id=$1"))
            {
                command.Parameters.AddWithValue(checked((int)login.AccountId));
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            Assert.Equal(PyBool.True, await service.CallAsync(bannedCall));
            var packetCount = sink.Packets.Count;
            await new PacketDispatcher(host.Registry) { StrictRpc = true }.DispatchAsync(request, session);
            Assert.Equal(packetCount + 1, sink.Packets.Count);
            var refusal = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(MarshalCodec.Encode(sink.Packets[^1]))));
            Assert.Equal(MachoMessage.ErrorResponse, refusal.Type);
            Assert.Equal(ItemFlags.Hangar, (await items.GetAsync(prize))!.Flag);
            Assert.Equal(1, (await items.GetAsync(token))!.Quantity);
        }
        finally
        {
            if (host is not null) await host.ShutdownAsync();
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
