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

    private static PyValue CallRequestWithKwargs(int callId, string method, PyTuple args, PyDict kwargs)
    {
        var source = new PyTuple([new PyInt(MachoAddress.TypeClient), new PyLong(7), new PyInt(callId), PyNone.Instance]);
        var body = new PyTuple([new PyInt(1), new PyStr(method), args, kwargs]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        var head = new PyValue[] { new PyInt(6), source, MachoAddress.Any("raffleProxy").Encode(), new PyInt(90000002), payload, PyNone.Instance };
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - head.Length)]));
    }

    private static PyTuple NotificationArguments(MachoPacket packet)
    {
        var frame = Assert.IsType<PyTuple>(packet.Payload);
        var envelope = Assert.IsType<PyTuple>(Assert.Single(frame.Items));
        var inner = Assert.IsType<PyTuple>(MarshalCodec.Decode(Assert.IsType<PyBuffer>(envelope.Items[1]).Value));
        var body = Assert.IsType<PyTuple>(inner.Items[1]);
        return Assert.IsType<PyTuple>(body.Items[1]);
    }

    private static PyValue CallResult(MachoPacket packet)
    {
        var payload = Assert.IsType<PyTuple>(packet.Payload);
        var result = Assert.Single(payload.Items);
        return result is PySubStream { Decoded: { } inner } ? inner : result;
    }

    private static PyValue Field(PyValue value, string name) => Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries
        .Single(pair => JsSemantics.StringValue(pair.Key) == name).Value;

    [Fact]
    public async Task Host_composition_registers_raffle_proxy_with_persistent_callback()
    {
        var sourceUrl = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must identify the local pinned-SDE database");
        var databaseUrl = sourceUrl;
        var crimewatchSchema = "rf_crew_" + Guid.NewGuid().ToString("N")[..12];
        var names = new[] { "EVE_PG_URL", "EVE_SIM", "EVE_MARKET_SEED", "EVE_WORMHOLES", "EVE_CRIMEWATCH_SCHEMA" };
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        HostRuntime? host = null;
        try
        {
            Environment.SetEnvironmentVariable("EVE_PG_URL", databaseUrl);
            Environment.SetEnvironmentVariable("EVE_SIM", "off");
            Environment.SetEnvironmentVariable("EVE_MARKET_SEED", "skip");
            Environment.SetEnvironmentVariable("EVE_WORMHOLES", "off");
            Environment.SetEnvironmentVariable("EVE_CRIMEWATCH_SCHEMA", crimewatchSchema);
            var settings = new HostSettings("", "disabled",
                new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
                new AccountOptions { AutoCreate = true }, StrictRpc: true);
            const long gameNow = 2_000_000_000_000;
            host = await HostComposition.BuildAsync(settings, () => gameNow);
            var service = Assert.IsAssignableFrom<IService>(host.Registry.Lookup("raffleProxy"));
            Assert.All(new[] { "AmIBanned", "AwardItem", "BuyRandomTickets", "BuyTicket", "CreateRaffle",
                    "FilteredGrab", "GetActiveHistoricPrices", "GetCreatedParticipated", "GetMyActiveTickets",
                    "GetMyRaffleHistory", "GetRaffle", "Grab", "SubscribeToRaffle", "SubscribeToTickets",
                    "UnsubscribeFromRaffle", "UnSubscribeFromTickets" }, method => Assert.True(service.HasMethod(method), method));
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(databaseUrl));
            var items = new PgItemStore(source);
            var directory = new PgAccountDirectory(source, new AccountOptions { AutoCreate = true });
            var login = Assert.IsType<LoggedIn>(await directory.LoginAsync(
                new LoginAttempt("raffle-" + Guid.NewGuid().ToString("N"), new byte[] { 9, 9, 9 }, "en", "127.0.0.1"), CancellationToken.None));
            var seller = Random.Shared.NextInt64(90_000_000, 99_999_999);
            var buyer = seller + 1;
            const long station = 60003760;
            long prize, token;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                prize = await items.CreateAsync(connection, transaction, new NewItem(587, seller, station, ItemFlags.Hangar, 1));
                token = await items.CreateAsync(connection, transaction, new NewItem(52568, seller, station, ItemFlags.Hangar, 2));
                await new PgWalletStore(source).OpenAsync(connection, transaction, seller, 875m, 0);
                await new PgWalletStore(source).OpenAsync(connection, transaction, buyer, 2_000m, 0);
                await transaction.CommitAsync();
            }
            var sink = new Sink();
            var session = new ClientSession(sink) { UserId = login.AccountId, CharacterId = seller, SessionId = 7 };
            host.CharacterSessions?.Enter(seller, session);
            session.Set("charid", new PyLong(seller));
            session.Set("stationid", new PyLong(station));
            var buyerSink = new Sink();
            var buyerSession = new ClientSession(buyerSink) { UserId = login.AccountId, CharacterId = buyer, SessionId = 8 };
            host.CharacterSessions?.Enter(buyer, buyerSession);
            buyerSession.Set("charid", new PyLong(buyer));
            buyerSession.Set("stationid", new PyLong(station));
            var data = Py.KeyVal(("item_id", new PyLong(prize)), ("type_id", new PyInt(587)),
                ("location_id", new PyLong(station)), ("token_id", new PyLong(token)),
                ("token_location_id", new PyLong(station)), ("ticket_count", new PyInt(8)),
                ("ticket_price", new PyInt(125)));
            var request = MarshalCodec.Decode(MarshalCodec.Encode(CallRequest(9, "CreateRaffle", data)));
            await new PacketDispatcher(host.Registry) { StrictRpc = true }.DispatchAsync(request, session);
            var packets = sink.Packets.Select(packet => Assert.IsType<MachoPacket>(
                MachoPacket.Decode(MarshalCodec.Decode(MarshalCodec.Encode(packet))))).ToArray();
            var creationResponse = Assert.Single(packets, packet => packet.Type == MachoMessage.CallRsp);
            var creationResult = Assert.IsType<PyLong>(CallResult(creationResponse));
            var creationNotifications = packets.Where(packet => packet.Type == MachoMessage.Notification).ToArray();
            Assert.Equal(["OnItemChange", "OnItemChange", "OnRaffleCreatedServer"],
                creationNotifications.Select(packet => packet.Dest.BroadcastId));
            Assert.True(Array.FindIndex(packets, packet => packet.Type == MachoMessage.CallRsp) <
                Array.FindIndex(packets, packet => packet.Type == MachoMessage.Notification && packet.Dest.BroadcastId == "OnRaffleCreatedServer"));
            Assert.All(creationNotifications.Take(2), packet => Assert.Equal("clientID", packet.Dest.IdType));
            var changedRows = creationNotifications.Take(2).Select(packet =>
                Assert.IsType<PyPackedRow>(NotificationArguments(packet).Items[0])).ToArray();
            Assert.Equal(InventoryWire.Id(prize), changedRows[0].Values[0]);
            Assert.Equal(new PyInt(ItemFlags.RaffleEscrow), changedRows[0].Values[4]);
            Assert.Equal(InventoryWire.Id(token), changedRows[1].Values[0]);
            Assert.Equal(new PyInt(1), changedRows[1].Values[5]);
            var prizeBefore = Assert.IsType<PyDict>(NotificationArguments(creationNotifications[0]).Items[1]);
            Assert.Equal(new PyInt(ItemFlags.Hangar), Assert.Single(prizeBefore.Entries).Value);
            var tokenBefore = Assert.IsType<PyDict>(NotificationArguments(creationNotifications[1]).Items[1]);
            Assert.Equal(new PyInt(2), Assert.Single(tokenBefore.Entries).Value);
            Assert.Equal("clientID", creationNotifications[2].Dest.IdType);
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
            Assert.Equal(raffleId, creationResult.Value);
            var createdArguments = NotificationArguments(creationNotifications[2]);
            Assert.Equal(new PyLong(raffleId), createdArguments.Items[0]);
            Assert.Equal(new PyLong(raffleId), Field(createdArguments.Items[1], "raffle_id"));
            Assert.Equal(new PyInt(6), Field(createdArguments.Items[1], "raffle_status"));
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
            async Task Watch(ClientSession watcher, Sink watcherSink, int callId, string method, params PyValue[] args)
            {
                watcherSink.Packets.Clear();
                await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(CallRequest(callId, method, args))), watcher);
                var response = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(watcherSink.Packets)));
                Assert.Equal(MachoMessage.CallRsp, response.Type);
                Assert.Same(PyNone.Instance, CallResult(response));
            }
            await Watch(raffleWatcher, raffleWatcherSink, 20, "SubscribeToRaffle", new PyLong(raffleId));
            await Watch(ticketWatcher, ticketWatcherSink, 21, "SubscribeToTickets");
            await Watch(ticketWatcher, ticketWatcherSink, 22, "UnSubscribeFromTickets");
            raffleWatcherSink.Packets.Clear();
            ticketWatcherSink.Packets.Clear();
            var firstBulk = MarshalCodec.Decode(MarshalCodec.Encode(
                CallRequest(11, "BuyRandomTickets", new PyLong(raffleId), new PyInt(2))));
            sink.Packets.Clear();
            buyerSink.Packets.Clear();
            buyerSink.FailNextNotification = true; // Wallet notification failure must not undo a committed purchase.
            await dispatcher.DispatchAsync(firstBulk, buyerSession);
            var firstBulkResponse = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(buyerSink.Packets,
                packet => MachoPacket.Decode(packet)?.Type == MachoMessage.CallRsp)));
            var firstBulkData = Assert.IsType<PyDict>(Assert.IsType<PyObject>(CallResult(firstBulkResponse)).Args);
            Assert.Contains(firstBulkData.Entries, pair => JsSemantics.StringValue(pair.Key) == "raffle_id" && pair.Value == new PyLong(raffleId));
            Assert.Equal(2L, await TicketCount());
            Assert.Equal(1_750m, await wallets.AccountBalanceAsync(new WalletAccount(buyer, 1000)));
            Assert.Equal(875m, await wallets.AccountBalanceAsync(new WalletAccount(seller, 1000)));
            Assert.Equal(2, raffleWatcherSink.Packets.Count);
            Assert.Empty(ticketWatcherSink.Packets);
            raffleWatcherSink.Packets.Clear();
            buyerSink.Packets.Clear();
            await dispatcher.DispatchAsync(firstBulk, buyerSession);
            Assert.DoesNotContain(buyerSink.Packets, packet => MachoPacket.Decode(packet)?.Type == MachoMessage.Notification);
            Assert.Empty(raffleWatcherSink.Packets);
            Assert.Equal(2L, await TicketCount());
            Assert.Equal(1_750m, await wallets.AccountBalanceAsync(new WalletAccount(buyer, 1000)));
            buyerSink.Packets.Clear();
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                CallRequest(11, "BuyRandomTickets", new PyLong(raffleId), new PyInt(3)))), buyerSession);
            Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(buyerSink.Packets))).Type);
            Assert.Equal(2L, await TicketCount());
            await Watch(raffleWatcher, raffleWatcherSink, 23, "UnsubscribeFromRaffle", new PyLong(raffleId));
            await Watch(ticketWatcher, ticketWatcherSink, 24, "SubscribeToTickets");
            raffleWatcherSink.Packets.Clear();
            ticketWatcherSink.Packets.Clear();
            sink.Packets.Clear();
            buyerSink.Packets.Clear();
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                CallRequest(12, "BuyRandomTickets", new PyLong(raffleId), new PyInt(2)))), buyerSession);
            Assert.Equal(4L, await TicketCount());
            Assert.Equal(1_500m, await wallets.AccountBalanceAsync(new WalletAccount(buyer, 1000)));
            Assert.Equal(875m, await wallets.AccountBalanceAsync(new WalletAccount(seller, 1000)));
            var committedCallNotices = buyerSink.Packets.Select(packet => Assert.IsType<MachoPacket>(MachoPacket.Decode(packet)))
                .Where(packet => packet.Type == MachoMessage.Notification).ToArray();
            Assert.Equal(["OnAccountChange", "OnTicketsUpdatedServer", "OnRaffleUpdatedServer"],
                committedCallNotices.Select(packet => packet.Dest.BroadcastId));
            Assert.Equal("charid", committedCallNotices[1].Dest.IdType);
            var ticketUpdates = Assert.IsType<PyDict>(NotificationArguments(committedCallNotices[1]).Items[0]);
            Assert.Equal(new PyInt(4), ticketUpdates.Entries.Single(pair => pair.Key == new PyLong(raffleId)).Value);
            var updatedRaffle = NotificationArguments(committedCallNotices[2]).Items[1];
            Assert.Equal(new PyLong(raffleId), Field(updatedRaffle, "raffle_id"));
            Assert.Equal(new PyInt(6), Field(updatedRaffle, "raffle_status"));
            var accountChange = NotificationArguments(committedCallNotices[0]);
            Assert.Equal("cash", JsSemantics.StringValue(accountChange.Items[0]));
            Assert.Equal(new PyInt(buyer), accountChange.Items[1]);
            Assert.Equal(new PyReal(1_500), accountChange.Items[2]);
            var ownerPurchaseNotices = sink.Packets.Select(packet => Assert.IsType<MachoPacket>(MachoPacket.Decode(packet)))
                .Where(packet => packet.Type == MachoMessage.Notification).ToArray();
            Assert.Equal(["OnTicketsUpdatedServer", "OnRaffleUpdatedServer"], ownerPurchaseNotices.Select(packet => packet.Dest.BroadcastId));
            Assert.Equal("charid", ownerPurchaseNotices[0].Dest.IdType);
            Assert.Empty(raffleWatcherSink.Packets);
            Assert.Equal(2, ticketWatcherSink.Packets.Count);
            ticketWatcher.Close();
            ticketWatcherSink.Packets.Clear();
            sink.Packets.Clear();
            buyerSink.Packets.Clear();
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                CallRequest(13, "BuyRandomTickets", new PyLong(raffleId), new PyInt(4)))), buyerSession);
            var purchasePackets = buyerSink.Packets.Select(packet => Assert.IsType<MachoPacket>(MachoPacket.Decode(packet))).ToArray();
            Assert.Single(purchasePackets, packet => packet.Type == MachoMessage.CallRsp);
            Assert.Contains(purchasePackets, packet => packet.Type == MachoMessage.Notification && packet.Dest.BroadcastId == "OnRaffleFinishedServer");
            Assert.Contains(purchasePackets, packet => packet.Type == MachoMessage.Notification &&
                packet.Dest.BroadcastId == "OnRaffleFinishedServer" && packet.Dest.IdType == "charid");
            var buyerFinish = NotificationArguments(Assert.Single(purchasePackets, packet =>
                packet.Type == MachoMessage.Notification && packet.Dest.BroadcastId == "OnRaffleFinishedServer"));
            Assert.Equal(new PyLong(raffleId), buyerFinish.Items[0]);
            Assert.Equal(new PyLong(raffleId), Field(buyerFinish.Items[1], "raffle_id"));
            Assert.Equal(new PyLong(buyer), Field(buyerFinish.Items[1], "owner_id"));
            Assert.InRange(Assert.IsType<PyInt>(Field(buyerFinish.Items[1], "number")).Value, 0L, 7L);
            var sellerFinished = sink.Packets.Select(packet => Assert.IsType<MachoPacket>(MachoPacket.Decode(packet)))
                .Where(packet => packet.Type == MachoMessage.Notification).ToArray();
            Assert.Equal(["OnAccountChange", "OnTicketsUpdatedServer", "OnRaffleUpdatedServer", "OnRaffleFinishedServer"],
                sellerFinished.Select(packet => packet.Dest.BroadcastId));
            Assert.Equal(new PyReal(1_825), NotificationArguments(sellerFinished[0]).Items[2]);
            Assert.Equal(1_825m, await wallets.AccountBalanceAsync(new WalletAccount(seller, 1000)));
            Assert.Equal(1_000m, await wallets.AccountBalanceAsync(new WalletAccount(buyer, 1000)));
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
            buyerSink.Packets.Clear();
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                CallRequest(14, "AwardItem", new PyLong(raffleId)))), buyerSession);
            var awardPackets = buyerSink.Packets.Select(packet => Assert.IsType<MachoPacket>(MachoPacket.Decode(packet))).ToArray();
            var awardResponse = Assert.Single(awardPackets, packet => packet.Type == MachoMessage.CallRsp);
            Assert.Same(PyNone.Instance, CallResult(awardResponse));
            Assert.Equal(["OnItemChange", "OnRaffleUpdatedServer"], awardPackets.Where(packet =>
                packet.Type == MachoMessage.Notification).Select(packet => packet.Dest.BroadcastId));
            var awardedRow = Assert.IsType<PyPackedRow>(NotificationArguments(awardPackets[0]).Items[0]);
            Assert.Equal(InventoryWire.Id(prize), awardedRow.Values[0]);
            Assert.Equal(new PyInt(buyer), awardedRow.Values[2]);
            Assert.Equal(new PyInt(ItemFlags.Hangar), awardedRow.Values[4]);
            var awardBefore = Assert.IsType<PyDict>(NotificationArguments(awardPackets[0]).Items[1]);
            Assert.Equal(new PyDict([new(new PyInt(2), new PyInt(seller)), new(new PyInt(4), new PyInt(ItemFlags.RaffleEscrow))]), awardBefore);
            var previousOwnerAward = sink.Packets.Select(packet => Assert.IsType<MachoPacket>(MachoPacket.Decode(packet)))
                .Where(packet => packet.Type == MachoMessage.Notification).ToArray();
            Assert.Equal(["OnItemChange", "OnRaffleUpdatedServer"], previousOwnerAward.Select(packet => packet.Dest.BroadcastId));
            Assert.Equal(new PyInt(buyer), Assert.IsType<PyPackedRow>(NotificationArguments(previousOwnerAward[0]).Items[0]).Values[2]);
            Assert.Equal(new PyInt(8), Field(NotificationArguments(awardPackets[1]).Items[1], "raffle_status"));
            Assert.Equal(ItemFlags.Hangar, (await items.GetAsync(prize))!.Flag);
            Assert.Equal(buyer, (await items.GetAsync(prize))!.OwnerId);

            // The V24 browse path shows a private raffle to its owner and ticket holders only.
            long privatePrize, privateToken;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                privatePrize = await items.CreateAsync(connection, transaction, new NewItem(2048, seller, station, ItemFlags.Hangar, 2));
                privateToken = await items.CreateAsync(connection, transaction, new NewItem(52568, seller, station, ItemFlags.Hangar, 1));
                await transaction.CommitAsync();
            }
            var privateData = Py.KeyVal(("item_id", new PyLong(privatePrize)), ("type_id", new PyInt(2048)),
                ("location_id", new PyLong(station)), ("token_id", new PyLong(privateToken)),
                ("token_location_id", new PyLong(station)), ("ticket_count", new PyInt(8)),
                ("ticket_price", new PyInt(125)), ("restriction_id", new PyInt(1)));
            sink.Packets.Clear();
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(CallRequest(18, "CreateRaffle", privateData))), session);
            var privateCreate = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(sink.Packets,
                packet => MachoPacket.Decode(packet)?.Type == MachoMessage.CallRsp)));
            var privateRaffleId = Assert.IsType<PyLong>(CallResult(privateCreate)).Value;
            var privateEscrow = await items.GetAsync(privatePrize);
            Assert.NotNull(privateEscrow);
            Assert.Equal(2L, privateEscrow.Quantity);
            Assert.Equal(ItemFlags.RaffleEscrow, privateEscrow.Flag);

            async Task<PyList> Browse(ClientSession viewer, Sink viewerSink, int callId)
            {
                viewerSink.Packets.Clear();
                var kwargs = Py.Dict(("filters", Py.Dict()), ("constraints", Py.Dict()));
                await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                    CallRequestWithKwargs(callId, "FilteredGrab", new PyTuple([]), kwargs))), viewer);
                var response = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(viewerSink.Packets)));
                Assert.Equal(MachoMessage.CallRsp, response.Type);
                return Assert.IsType<PyList>(CallResult(response));
            }

            var ownerBrowse = await Browse(session, sink, 30);
            Assert.Contains(ownerBrowse.Items, row => Field(row, "raffle_id") == new PyLong(privateRaffleId));
            var buyerBrowseBeforePurchase = await Browse(buyerSession, buyerSink, 31);
            Assert.DoesNotContain(buyerBrowseBeforePurchase.Items, row => Field(row, "raffle_id") == new PyLong(privateRaffleId));

            buyerSink.Packets.Clear();
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(
                CallRequest(32, "BuyTicket", new PyLong(privateRaffleId), new PyInt(0)))), buyerSession);
            Assert.Single(buyerSink.Packets, packet => MachoPacket.Decode(packet)?.Type == MachoMessage.CallRsp);
            var buyerBrowseAfterPurchase = await Browse(buyerSession, buyerSink, 33);
            Assert.Contains(buyerBrowseAfterPurchase.Items, row => Field(row, "raffle_id") == new PyLong(privateRaffleId));
            var outsiderSink = new Sink();
            var outsider = new ClientSession(outsiderSink) { UserId = login.AccountId, CharacterId = buyer + 2, SessionId = 42 };
            var outsiderBrowse = await Browse(outsider, outsiderSink, 34);
            Assert.DoesNotContain(outsiderBrowse.Items, row => Field(row, "raffle_id") == new PyLong(privateRaffleId));

            sink.Packets.Clear();
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(CallRequest(15, "AmIBanned"))), session);
            var bannedStatus = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(sink.Packets)));
            Assert.Equal(MachoMessage.CallRsp, bannedStatus.Type);
            Assert.Equal(PyBool.False, CallResult(bannedStatus));
            var anonymousSink = new Sink();
            var anonymousSession = new ClientSession(anonymousSink) { SessionId = 51 };
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(CallRequest(17, "AmIBanned"))), anonymousSession);
            var anonymousStatus = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(anonymousSink.Packets)));
            Assert.Equal(MachoMessage.CallRsp, anonymousStatus.Type);
            Assert.Equal(PyBool.False, CallResult(anonymousStatus));
            await using (var command = source.CreateCommand("UPDATE identity.accounts SET banned=true WHERE id=$1"))
            {
                command.Parameters.AddWithValue(checked((int)login.AccountId));
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            sink.Packets.Clear();
            await dispatcher.DispatchAsync(MarshalCodec.Decode(MarshalCodec.Encode(CallRequest(16, "AmIBanned"))), session);
            bannedStatus = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(sink.Packets)));
            Assert.Equal(MachoMessage.CallRsp, bannedStatus.Type);
            Assert.Equal(PyBool.True, CallResult(bannedStatus));
            var packetCount = sink.Packets.Count;
            await new PacketDispatcher(host.Registry) { StrictRpc = true }.DispatchAsync(request, session);
            Assert.Equal(packetCount + 1, sink.Packets.Count);
            var refusal = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(MarshalCodec.Encode(sink.Packets[^1]))));
            Assert.Equal(MachoMessage.ErrorResponse, refusal.Type);
            Assert.Equal(ItemFlags.Hangar, (await items.GetAsync(prize))!.Flag);
            Assert.Equal(1, (await items.GetAsync(token))!.Quantity);
            await using (var command = source.CreateCommand("SELECT created_at_ms,expires_at_ms FROM raffle.raffle_records WHERE raffle_id=$1"))
            {
                command.Parameters.AddWithValue(raffleId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(gameNow, reader.GetInt64(0));
                Assert.Equal(gameNow + 3L * 24 * 60 * 60 * 1000, reader.GetInt64(1));
            }
        }
        finally
        {
            if (host is not null) await host.ShutdownAsync();
            await using var cleanupSource = NpgsqlDataSource.Create(PgConnection.ForTest(databaseUrl));
            await using var clean = cleanupSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{crimewatchSchema}\" CASCADE");
            await clean.ExecuteNonQueryAsync();
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task Raffle_proxy_uses_HyperNet_kill_switch_error_on_registered_host_wire()
    {
        var databaseUrl = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL must identify the local pinned-SDE database");
        var schema = "raffle_kill_" + Guid.NewGuid().ToString("N");
        var crimewatchSchema = "rf_kill_cw_" + Guid.NewGuid().ToString("N")[..12];
        var names = new[] { "EVE_PG_URL", "EVE_SIM", "EVE_MARKET_SEED", "EVE_WORMHOLES", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CRIMEWATCH_SCHEMA" };
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        HostRuntime? host = null;
        try
        {
            Environment.SetEnvironmentVariable("EVE_PG_URL", databaseUrl);
            Environment.SetEnvironmentVariable("EVE_SIM", "off");
            Environment.SetEnvironmentVariable("EVE_MARKET_SEED", "skip");
            Environment.SetEnvironmentVariable("EVE_WORMHOLES", "off");
            Environment.SetEnvironmentVariable("EVE_GLOBAL_CONFIG_SCHEMA", schema);
            Environment.SetEnvironmentVariable("EVE_CRIMEWATCH_SCHEMA", crimewatchSchema);
            host = await HostComposition.BuildAsync(new HostSettings("", "disabled",
                new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled", HyperNetKillSwitch = true },
                new AccountOptions { AutoCreate = true }, StrictRpc: true));

            var sink = new Sink();
            var session = new ClientSession(sink) { SessionId = 91 };
            await new PacketDispatcher(host.Registry) { StrictRpc = true }.DispatchAsync(
                MarshalCodec.Decode(MarshalCodec.Encode(CallRequest(1, "AmIBanned"))), session);
            var response = Assert.IsType<MachoPacket>(MachoPacket.Decode(Assert.Single(sink.Packets)));
            Assert.Equal(MachoMessage.ErrorResponse, response.Type);
            var responsePayload = Assert.IsType<PyTuple>(response.Payload);
            Assert.Equal((int)MachoErrorType.WrappedException, Assert.IsType<PyInt>(responsePayload.Items[1]).Value);
            var wrapped = Assert.IsType<PyTuple>(responsePayload.Items[2]);
            var error = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(Assert.Single(wrapped.Items)).Decoded);
            var header = Assert.IsType<PyTuple>(error.Header).Items;
            Assert.Equal("raffles.RafflesError", Assert.IsType<PyToken>(header[0]).Value);
            Assert.Equal(new PyTuple([new PyStr("HyperNetDisabled")]), Assert.IsType<PyTuple>(header[1]));
            var state = Assert.IsType<PyDict>(header[2]);
            Assert.Equal(new PyStr("HyperNetDisabled"), state.Entries.Single(pair => JsSemantics.StringValue(pair.Key) == "msg").Value);
        }
        finally
        {
            if (host is not null) await host.ShutdownAsync();
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(databaseUrl));
            await using (var command = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE; DROP SCHEMA IF EXISTS \"{crimewatchSchema}\" CASCADE"))
                await command.ExecuteNonQueryAsync();
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
