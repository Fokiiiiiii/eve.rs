using Eve.Data;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private static PyValue WireCallResult(MachoPacket packet)
    {
        var payload = Assert.IsType<PyTuple>(packet.Payload);
        var result = Assert.Single(payload.Items);
        return result is PySubStream { Decoded: { } inner } ? inner : result;
    }

    private static PyObjectEx WrappedException(MachoPacket packet)
    {
        var error = Assert.IsType<PyTuple>(packet.Payload);
        var wrapped = Assert.IsType<PyTuple>(error.Items[2]);
        return Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(Assert.Single(wrapped.Items)).Decoded);
    }

    [Fact]
    public async Task Raffle_proxy_registered_route_reads_real_postgres_raffle_and_returns_wire_error()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var inventory = "rw_i_" + suffix;
        var wallet = "rw_w_" + suffix;
        var identity = "rw_a_" + suffix;
        var raffle = "rw_r_" + suffix;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        try
        {
            await PgItemStore.MigrateAsync(source, inventory);
            await PgWalletStore.MigrateAsync(source, wallet);
            await Migrations.ApplyAsync(source, identity, PgAccountDirectory.MigrationSet());
            var directory = new PgAccountDirectory(source, new AccountOptions { AutoCreate = true }, identity);
            var login = Assert.IsType<LoggedIn>(await directory.LoginAsync(
                new LoginAttempt("raffle-wire", new byte[] { 9, 9, 9 }, "en", "127.0.0.1"), CancellationToken.None));
            var items = new PgItemStore(source, inventory);
            var store = new RaffleProxyStore(source, items, new WalletTransactions(source, wallet), schema: raffle, identitySchema: identity);
            await store.MigrateAsync();
            const long seller = 90000201, buyer = 90000202, station = 60003760;
            long prize, token, activeShipPrize, activeShipToken;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                prize = await items.CreateAsync(connection, tx, new NewItem(587, seller, station, ItemFlags.Hangar, 1));
                token = await items.CreateAsync(connection, tx, new NewItem(52568, seller, station, ItemFlags.Hangar, 2));
                activeShipPrize = await items.CreateAsync(connection, tx, new NewItem(587, seller, station, ItemFlags.Hangar, 1));
                activeShipToken = await items.CreateAsync(connection, tx, new NewItem(52568, seller, station, ItemFlags.Hangar, 2));
                await new PgWalletStore(source, wallet).OpenAsync(connection, tx, seller, 10000m, 0);
                await new PgWalletStore(source, wallet).OpenAsync(connection, tx, buyer, 10000m, 0);
                await tx.CommitAsync();
            }
            var session = new ClientSession { CharacterId = seller };
            session.Set("stationid", new PyLong(station));
            var data = Py.KeyVal(("item_id", new PyLong(prize)), ("type_id", new PyInt(587)),
                ("location_id", new PyLong(station)), ("token_id", new PyLong(token)),
                ("token_location_id", new PyLong(station)), ("ticket_count", new PyInt(8)),
                ("ticket_price", new PyInt(125)));
            var id = await store.CreateRaffleAsync(new ServiceCall { Method = "CreateRaffle", Args = [data], Session = session },
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 3_500_000);
            Start(strictRpc: true, accounts: directory, configure: registry => registry.Register(new RaffleProxyService(new RaffleProxyCallbacks(store.InvokeAsync))),
                sessionOpened: opened => { opened.UserId = login.AccountId; opened.CharacterId = seller; opened.Set("charid", new PyLong(seller)); opened.Set("stationid", new PyLong(station)); opened.Set("shipid", new PyLong(activeShipPrize)); });
            var front = await Handshaken(await ConnectAsync());
            var open = Open();
            await front.SendAsync(open);
            await front.ReceiveAsync();
            var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
            long callId = 0;
            async Task<MachoPacket> InvokePacket(string method, params PyValue[] args)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                    Packets.CallWithKwargs(open, ++callId, "raffleProxy", method, new PyTuple(args), kwargs))));
                var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
                return Assert.IsType<MachoPacket>(MachoPacket.Decode(response));
            }
            async Task<(int Type, PyValue? Value)> Invoke(string method, params PyValue[] args)
            {
                var packet = await InvokePacket(method, args);
                return (packet.Type, packet.Type == MachoMessage.CallRsp ? WireCallResult(packet) : null);
            }
            var activeShipData = Py.KeyVal(("item_id", new PyLong(activeShipPrize)), ("type_id", new PyInt(587)),
                ("location_id", new PyLong(station)), ("token_id", new PyLong(activeShipToken)),
                ("token_location_id", new PyLong(station)), ("ticket_count", new PyInt(8)), ("ticket_price", new PyInt(125)));
            var activeShipFailure = await InvokePacket("CreateRaffle", activeShipData);
            Assert.Equal(MachoMessage.ErrorResponse, activeShipFailure.Type);
            var createError = WrappedException(activeShipFailure);
            var createHeader = Assert.IsType<PyTuple>(createError.Header);
            Assert.Equal("raffles.CreateError", Assert.IsType<PyToken>(createHeader.Items[0]).Value);
            Assert.Equal("ItemInventoryError", JsSemantics.PlainText(Assert.Single(Assert.IsType<PyTuple>(createHeader.Items[1]).Items)));
            Assert.Equal("ItemInventoryError", JsSemantics.PlainText(Assert.Single(Assert.IsType<PyDict>(createHeader.Items[2]).Entries).Value));
            Assert.Equal(ItemFlags.Hangar, (await items.GetAsync(activeShipPrize))!.Flag);
            Assert.Equal(2, (await items.GetAsync(activeShipToken))!.Quantity);
            var read = await Invoke("GetRaffle", new PyLong(id));
            Assert.Equal(MachoMessage.CallRsp, read.Type);
            var fields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(read.Value).Args);
            Assert.Equal(new[] { "running_id", "raffle_id", "owner_id", "location_id", "solar_system_id", "item_id", "type_id",
                    "ticket_count", "ticket_price", "restriction_id", "creation_time", "expiration_time", "sold_ticket_count",
                    "sold_tickets", "winning_ticket", "raffle_status", "end_date", "meta_data" }.Order(StringComparer.Ordinal),
                fields.Entries.Select(pair => JsSemantics.StringValue(pair.Key)).Order(StringComparer.Ordinal));
            Assert.Contains(fields.Entries, pair => JsSemantics.StringValue(pair.Key) == "raffle_id" && pair.Value == new PyLong(id));
            Assert.Contains(fields.Entries, pair => JsSemantics.StringValue(pair.Key) == "item_id" && pair.Value == new PyLong(prize));
            var missingRaffle = await InvokePacket("GetRaffle", new PyLong(id + 10000));
            Assert.Equal(MachoMessage.ErrorResponse, missingRaffle.Type);
            var missingRaffleHeader = Assert.IsType<PyTuple>(WrappedException(missingRaffle).Header);
            Assert.Equal("raffles.RaffleNotFoundError", Assert.IsType<PyToken>(missingRaffleHeader.Items[0]).Value);
            Assert.Equal("RaffleNotFoundError", JsSemantics.PlainText(Assert.Single(Assert.IsType<PyTuple>(missingRaffleHeader.Items[1]).Items)));
            var browseKwargs = Py.Dict(("machoVersion", new PyInt(1)),
                ("filters", Py.Dict(("type_id", new PyInt(587)))),
                ("constraints", Py.Dict(("min_ticket_price", new PyInt(125)), ("max_ticket_count", new PyInt(8)))),
                ("size", new PyInt(1)));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, "raffleProxy", "FilteredGrab", new PyTuple([]), browseKwargs))));
            var browseResponse = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(browseResponse)).Type);
            var browsed = Assert.Single(Assert.IsType<PyList>(Result(browseResponse)).Items);
            var browseFields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(browsed).Args);
            Assert.Contains(browseFields.Entries, pair => JsSemantics.StringValue(pair.Key) == "raffle_id" && pair.Value == new PyLong(id));
            var browseWithoutSize = Py.Dict(("machoVersion", new PyInt(1)),
                ("filters", Py.Dict(("type_id", new PyInt(587)))), ("constraints", Py.Dict()));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, "raffleProxy", "FilteredGrab", new PyTuple([]), browseWithoutSize))));
            var defaultBrowse = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            Assert.Single(Assert.IsType<PyList>(Result(defaultBrowse)).Items);
            Assert.Single(Assert.IsType<PyList>((await Invoke("Grab")).Value).Items);

            // One committed purchase emits two update notifications. A retry of the same
            // ticket must not generate a second notification or debit.
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, "raffleProxy", "BuyTicket", new PyTuple([new PyLong(id), new PyInt(0)]), kwargs))));
            var types = new List<int>();
            var notificationNames = new List<string?>();
            var buyPackets = new List<MachoPacket>();
            for (var i = 0; i < 3; i++)
            {
                var send = Assert.IsType<Send>(await front.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload)));
                Assert.True(packet.Type != MachoMessage.ErrorResponse, $"BuyTicket returned error: {System.Text.Encoding.Latin1.GetString(MarshalCodec.Encode(packet.Payload))}");
                types.Add(packet.Type);
                buyPackets.Add(packet);
                if (packet.Type == MachoMessage.Notification) notificationNames.Add(packet.Dest.BroadcastId);
            }
            Assert.Single(types, type => type == MachoMessage.CallRsp);
            var purchaseFields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(WireCallResult(
                Assert.Single(buyPackets, packet => packet.Type == MachoMessage.CallRsp))).Args);
            Assert.Contains(purchaseFields.Entries, pair => JsSemantics.StringValue(pair.Key) == "raffle_id" && pair.Value == new PyLong(id));
            Assert.Contains("OnTicketsUpdatedServer", notificationNames);
            Assert.Contains("OnRaffleUpdatedServer", notificationNames);
            Assert.Equal(MachoMessage.CallRsp, (await Invoke("BuyTicket", new PyLong(id), new PyInt(0))).Type);
            Assert.Equal(MachoMessage.CallRsp, (await Invoke("GetRaffle", new PyLong(id))).Type);
            Assert.Equal(9875m, await new WalletTransactions(source, wallet).AccountBalanceAsync(new WalletAccount(seller, 1000)));
            var statistics = Assert.IsType<PyDict>(Assert.IsType<PyObject>((await Invoke("GetCreatedParticipated")).Value).Args);
            Assert.Contains(statistics.Entries, pair => JsSemantics.StringValue(pair.Key) == "raffles_participated" && pair.Value == new PyInt(1));
            var active = Assert.Single(Assert.IsType<PyList>((await Invoke("GetMyActiveTickets")).Value).Items);
            var activeFields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(active).Args);
            Assert.Contains(activeFields.Entries, pair => JsSemantics.StringValue(pair.Key) == "raffle_id" && pair.Value == new PyLong(id));
            var prices = Assert.IsType<PyDict>(Assert.IsType<PyObject>((await Invoke("GetActiveHistoricPrices", new PyInt(587))).Value).Args);
            Assert.Contains(prices.Entries, pair => JsSemantics.StringValue(pair.Key) == "active_count" && pair.Value == new PyInt(1));
            long secondPrize, secondToken;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                secondPrize = await items.CreateAsync(connection, tx, new NewItem(587, seller, station, ItemFlags.Hangar, 1));
                secondToken = await items.CreateAsync(connection, tx, new NewItem(52568, seller, station, ItemFlags.Hangar, 2));
                await tx.CommitAsync();
            }
            var secondData = Py.KeyVal(("item_id", new PyLong(secondPrize)), ("type_id", new PyInt(587)),
                ("location_id", new PyLong(station)), ("token_id", new PyLong(secondToken)),
                ("token_location_id", new PyLong(station)), ("ticket_count", new PyInt(8)), ("ticket_price", new PyInt(125)));
            var secondId = await store.CreateRaffleAsync(new ServiceCall { Method = "CreateRaffle", Args = [secondData], Session = session },
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 3_500_000);
            await store.BuyRandomTicketsAsync(secondId, buyer, 8, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var history = Assert.IsType<PyTuple>((await Invoke("GetMyRaffleHistory")).Value);
            var historyRows = Assert.IsType<PyList>(history.Items[0]);
            Assert.Equal(new PyInt(50), history.Items[1]);
            Assert.Equal(2, historyRows.Items.Count);
            var newest = Assert.IsType<PyDict>(Assert.IsType<PyObject>(historyRows.Items[0]).Args);
            var newestId = newest.Entries.Single(pair => JsSemantics.StringValue(pair.Key) == "raffle_id").Value;
            var cursor = newest.Entries.Single(pair => JsSemantics.StringValue(pair.Key) == "running_id").Value;
            Assert.Equal(new PyLong(secondId), newestId);
            var historyKwargs = Py.Dict(("machoVersion", new PyInt(1)), ("running_id", cursor));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, "raffleProxy", "GetMyRaffleHistory", new PyTuple([]), historyKwargs))));
            var nextPage = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            var older = Assert.Single(Assert.IsType<PyList>(Assert.IsType<PyTuple>(Result(nextPage)).Items[0]).Items);
            var olderFields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(older).Args);
            Assert.Contains(olderFields.Entries, pair => JsSemantics.StringValue(pair.Key) == "raffle_id" && pair.Value == new PyLong(id));
            var zeroTicketPurchase = await InvokePacket("BuyRandomTickets", new PyLong(secondId), new PyInt(0));
            Assert.Equal(MachoMessage.ErrorResponse, zeroTicketPurchase.Type);
            var zeroTicketError = Assert.IsType<PyTuple>(WrappedException(zeroTicketPurchase).Header);
            Assert.Equal("raffles.TicketUnavailableError", Assert.IsType<PyToken>(zeroTicketError.Items[0]).Value);
            Assert.Equal("TicketUnavailableError", JsSemantics.PlainText(Assert.Single(Assert.IsType<PyTuple>(zeroTicketError.Items[1]).Items)));
        }
        finally
        {
            foreach (var schema in new[] { raffle, inventory, wallet, identity })
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
