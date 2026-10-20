using Eve.Characters;
using Eve.Corporations;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Market;
using Eve.Wallet;

namespace Eve.Market.Tests;

/// <summary>
/// The RPCs M1 completes, read the way the V24.01 client reads them (proxy call sites cited on each test). The rows, the labelled refusals and the
/// notifications are checked on the marshalled wire values, not on the store's objects.
/// </summary>
public class MarketRpcTests
{
    private const int Tritanium = 34, Plex = 44992;

    private static ServiceCall Call(ClientSession session, string method, long id, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(id) };

    private static PyDict Fields(PyValue value) => Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args);
    private static PyValue Field(PyDict dict, string name) => dict.Entries.Single(e => JsSemantics.PlainText(e.Key) == name).Value;

    /// <summary>The value after a marshal round trip, as the client receives it; a cached result is unwrapped to its payload.</summary>
    private static PyValue Received(PyValue value)
    {
        var decoded = MarshalCodec.Decode(MarshalCodec.Encode(value));
        if (decoded is not PyObject { Name: var name } cached || JsSemantics.PlainText(name) != ObjectCaching.ResultClass) return decoded;
        var stream = Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(cached.Args).Items[1]);
        return stream.Decoded ?? MarshalCodec.Decode(stream.Raw);
    }

    /// <summary>A labelled refusal's name and its arguments, as the client's UserError reads them.</summary>
    private static (string Label, PyDict Arguments) UserError(MachoWrappedException error)
    {
        var header = Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(error.Error.Payload).Header);
        var args = Assert.IsType<PyTuple>(header.Items[1]);
        return (error.Error.UserErrorMessage, Assert.IsType<PyDict>(args.Items[1]));
    }

    private sealed class RecordingPacketSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class Seat(TradeRig rig, long actor, RecordingPacketSink sink, ClientSession session, MarketProxyService proxy)
    {
        public TradeRig Rig { get; } = rig;
        public long Actor { get; } = actor;
        public RecordingPacketSink Sink { get; } = sink;
        public ClientSession Session { get; } = session;
        public MarketProxyService Proxy { get; } = proxy;
    }

    /// <summary>The market service built as the Host builds it (CorporationComposition, EconomyRpc), for one character.</summary>
    private static async Task<Seat> SeatAsync(TradeRig rig, long actor, int sessionId = 101)
    {
        var sink = new RecordingPacketSink();
        var session = new ClientSession(sink) { SessionId = sessionId };
        session.Set("charid", new PyInt(actor));
        var sessions = new CharacterSessions();
        sessions.Enter(actor, session);
        var catalog = await MarketCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var proxy = new MarketProxyService(rig.Book, rig.Trading, rig.Topology, rig.Characters, rig.Corporations, rig.Wallets, catalog, () => rig.Now, rig.Expiry, sessions);
        return new Seat(rig, actor, sink, session, proxy);
    }

    private static PyValue Line(int typeId, long quantity, double price, long minVolume = 1) => Py.KeyVal(
        ("typeID", new PyInt(typeId)), ("quantity", new PyInt(quantity)), ("price", new PyReal(price)), ("minVolume", new PyInt(minVolume)));

    /// <summary>A number as the client reads it: an int or a real, whichever the marshal round trip gives.</summary>
    private static double Num(PyValue value) => value switch
    {
        PyInt n => n.Value,
        PyLong n => n.Value,
        PyReal n => n.Value,
        _ => throw new InvalidOperationException($"not a number: {value}"),
    };

    private static async Task<long> PlexStockAsync(TradeRig rig, long owner, long quantity)
    {
        await using var c = await rig.Source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var id = await rig.Items.CreateAsync(c, tx, new NewItem(Plex, owner, rig.Station.Id, ItemFlags.Hangar, quantity));
        await tx.CommitAsync();
        return id;
    }

    [PgFact]
    public async Task Station_and_system_asks_are_key_value_rows_read_by_name()
    {
        // This checks server-side fields only; it does not exercise MarketBase tuple conversion or V24 unmarshalling.
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Ask Reader");
        var seat = await SeatAsync(r, actor);
        await r.Book.SeedNpcAsync([new("ask", Tritanium, false, 10, 100, 32767, MarketLocation.From(r.Station), r.Now)]);
        foreach (var asks in new[] { await seat.Proxy.GetStationAsks(Call(seat.Session, "GetStationAsks", 1)), await seat.Proxy.GetSystemAsks(Call(seat.Session, "GetSystemAsks", 2)) })
        {
            var row = Assert.IsType<PyObject>(Assert.Single(Assert.IsType<PyDict>(Received(asks)).Entries).Value);
            Assert.Equal("util.KeyVal", JsSemantics.PlainText(row.Name));
            var fields = Assert.IsType<PyDict>(row.Args);
            Assert.Equal((double)(Tritanium), Num(Field(fields, "typeID")));
            Assert.Equal((double)(r.Station.Id), Num(Field(fields, "stationID")));
            Assert.Equal(10, Num(Field(fields, "price")));
            Assert.Equal(100, Num(Field(fields, "volRemaining")));
        }
    }

    [PgFact]
    public async Task Market_history_multi_refuses_more_than_500_type_ids()
    {
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Bounded History Reader");
        var seat = await SeatAsync(r, actor);
        var typeIds = new PyList(Enumerable.Range(1, 501).Select(id => (PyValue)new PyInt(id)).ToArray());

        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await seat.Proxy.GetHistoryForManyTypeIDs(Call(seat.Session, "GetHistoryForManyTypeIDs", 3, typeIds)));

        Assert.Equal("MARKET_BULK_LIMIT_EXCEEDED", refusal.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task Buy_multi_refuses_more_than_500_entries()
    {
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Bounded Multi Buyer");
        var seat = await SeatAsync(r, actor);
        var entries = new PyList(Enumerable.Repeat(Line(Tritanium, 1, 10), 501).ToArray());

        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await seat.Proxy.BuyMultipleItems(
                Call(seat.Session, "BuyMultipleItems", 3, new PyInt(r.Station.Id), entries, PyBool.False)));

        Assert.Equal("MARKET_BULK_LIMIT_EXCEEDED", refusal.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task Buy_multi_fills_each_line_at_its_limit_and_books_the_unfilled_rest_of_each_line()
    {
        // BuyMultipleItems (EVE.js proxy 6915): immediate buys; a line fills what matches at its limit and books the rest as a buy order; the answer is the
        // list of the orders created, which marketQuote BuyMulti (marketsvc.py:270-273) iterates and the client announces.
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Multi Buyer");
        var seat = await SeatAsync(r, actor);
        await r.Book.SeedNpcAsync([new("ask", Tritanium, false, 10, 100, 32767, MarketLocation.From(r.Station), r.Now)]);
        var created = await seat.Proxy.BuyMultipleItems(Call(seat.Session, "BuyMultipleItems", 3, new PyInt(r.Station.Id),
            new PyList([Line(Tritanium, 5, 10), Line(Tritanium, 0, 10), Line(Tritanium, 4, 9)]), PyBool.False));
        Assert.NotNull(created);
        var booked = Assert.Single((await r.Book.BookAsync(r.Station.RegionId, Tritanium, r.Now)).Bids);
        Assert.Equal((4, 9m, 90), (booked.VolumeRemaining, booked.Price, booked.Duration));
        // the 5 units of the first line are paid at 10; the booked line escrows 4 at 9 and pays its broker fee
        Assert.True((await r.WalletStore.GetAsync(actor))!.Isk <= 9950m - 36m);
        Assert.NotEmpty(seat.Sink.Packets);
        Assert.Equal(95, (await r.Book.BookAsync(r.Station.RegionId, Tritanium, r.Now)).Asks.Single().VolumeRemaining);
    }

    [PgFact]
    public async Task Buy_multi_rolls_back_every_line_when_a_later_line_cannot_be_funded()
    {
        await using var r=await TradeRig.Create();
        var actor=await r.Actor("Multi Buyer");
        var seat=await SeatAsync(r,actor);

        await Assert.ThrowsAsync<MachoWrappedException>(() => seat.Proxy.BuyMultipleItems(Call(seat.Session,"BuyMultipleItems",77,
            new PyInt(r.Station.Id),new PyList([Line(Tritanium,10,10),Line(Tritanium,100,1000)]),PyBool.False)).AsTask());
        Assert.Equal(10000m,(await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Empty((await r.Book.BookAsync(r.Station.RegionId,Tritanium,r.Now)).Bids);
        Assert.Empty(seat.Sink.Packets);

        var result=await seat.Proxy.BuyMultipleItems(Call(seat.Session,"BuyMultipleItems",77,
            new PyInt(r.Station.Id),new PyList([Line(Tritanium,10,10),Line(Tritanium,1,20)]),PyBool.False));
        Assert.IsType<PyObject>(result);
        Assert.Equal(9680m,(await r.WalletStore.GetAsync(actor))!.Isk);
        var bids=(await r.Book.BookAsync(r.Station.RegionId,Tritanium,r.Now)).Bids.OrderBy(order => order.Price).ToArray();
        Assert.Equal(2,bids.Length);
        Assert.Equal(new[] { 10m,20m },bids.Select(order => order.Price));
    }

    [PgFact]
    public async Task An_immediate_buy_that_matches_nothing_is_booked_with_its_escrow()
    {
        // PlaceBuyOrder with duration 0 (the client's BuyStuff default, the quick buy): no sell order to take, so the order is booked for the default
        // duration and its escrow is held, as for any buy order (the client announces the order it placed)
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Quick Buyer", 10000m);
        var seat = await SeatAsync(r, actor);
        Assert.Equal(PyNone.Instance, await seat.Proxy.PlaceBuyOrder(Call(seat.Session, "PlaceBuyOrder", 1, new PyInt(r.Station.Id), new PyInt(Tritanium),
            new PyReal(9), new PyInt(4), new PyInt(-1), new PyInt(1), new PyInt(0), PyBool.False, PyNone.Instance)));
        var booked = Assert.Single((await r.Book.BookAsync(r.Station.RegionId, Tritanium, r.Now)).Bids);
        Assert.Equal((4, 9m, 90), (booked.VolumeRemaining, booked.Price, booked.Duration));
        Assert.True((await r.WalletStore.GetAsync(actor))!.Isk <= 10000m - 36m);
    }

    [PgFact]
    public async Task Plex_sell_is_refused_for_another_type_and_a_plex_line_rests_and_its_price_can_be_modified()
    {
        // PlacePlexSellOrder (the client's SellPlex) and ModifyPlexCharOrder (the ModifyCharOrder path): the PLEX type only.
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Plex Seller", 10000m);
        var seat = await SeatAsync(r, actor);
        var stock = await PlexStockAsync(r, actor, 5);
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => seat.Proxy.PlacePlexSellOrder(Call(seat.Session, "PlacePlexSellOrder", 4,
            Py.KeyVal(("typeID", new PyInt(Tritanium)), ("itemID", new PyLong(stock)), ("price", new PyReal(12)), ("quantity", new PyInt(5)), ("stationID", new PyInt(r.Station.Id))),
            PyBool.False, new PyInt(90), PyNone.Instance)).AsTask());
        Assert.Equal("CustomInfo", refusal.Error.UserErrorMessage);
        // a corporate PLEX sell takes the corporate path like any item (no personal-market refusal); the stock is the seat's own, so the path refuses it
        var corporate = await Assert.ThrowsAsync<MachoWrappedException>(() => seat.Proxy.PlacePlexSellOrder(Call(seat.Session, "PlacePlexSellOrder", 5,
            Py.KeyVal(("typeID", new PyInt(Plex)), ("itemID", new PyLong(stock)), ("price", new PyReal(12)), ("quantity", new PyInt(5)), ("stationID", new PyInt(r.Station.Id))),
            PyBool.True, new PyInt(90), PyNone.Instance)).AsTask());
        Assert.Equal("CustomInfo", corporate.Error.UserErrorMessage);

        var placed = await seat.Proxy.PlacePlexSellOrder(Call(seat.Session, "PlacePlexSellOrder", 6,
            Py.KeyVal(("typeID", new PyInt(Plex)), ("itemID", new PyLong(stock)), ("price", new PyReal(12)), ("quantity", new PyInt(5)), ("stationID", new PyInt(r.Station.Id))),
            PyBool.False, new PyInt(90), PyNone.Instance));
        Assert.Equal(PyBool.True, placed);
        var order = Assert.Single((await r.Book.BookAsync(r.Station.RegionId, Plex, r.Now)).Asks);
        r.Now += MarketRules.ModificationDelayMs;
        Assert.Equal(PyNone.Instance, await seat.Proxy.ModifyPlexCharOrder(Call(seat.Session, "ModifyPlexCharOrder", 7, new PyLong(order.Id), new PyReal(13),
            PyBool.False, new PyInt(r.Station.Id), new PyInt(r.Station.SolarSystemId), new PyReal(12), new PyInt(-1), new PyInt(5),
            new PyLong(Py.FileTimeFromUnixMs(order.IssuedMs)))));
        Assert.Equal(13m, (await r.Book.BookAsync(r.Station.RegionId, Plex, r.Now)).Asks.Single().Price);
        // the Created notification of the sell and the Modified notification of the modify
        Assert.Equal(2, seat.Sink.Packets.Count);
    }

    [PgFact]
    public async Task A_fee_quote_that_differs_is_the_client_label_with_both_percentages()
    {
        // sellMulti.py:456 raises MktBrokersFeeUnexpected2 with actualBrokerFeePerc (100 x the rate) and expectedBrokerFeePercentage (the quote).
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Quote Seller");
        var seat = await SeatAsync(r, actor);
        var item = await r.Stock(actor, 5);
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => seat.Proxy.PlaceMultiSellOrder(Call(seat.Session, "PlaceMultiSellOrder", 8,
            new PyList([Py.KeyVal(("typeID", new PyInt(Tritanium)), ("itemID", new PyLong(item)), ("price", new PyReal(20)), ("quantity", new PyInt(5)),
                ("stationID", new PyInt(r.Station.Id)))]),
            PyBool.False, new PyInt(90), new PyReal(0.01))).AsTask());
        var (label, arguments) = UserError(refusal);
        Assert.Equal("MktBrokersFeeUnexpected2", label);
        Assert.Equal(3.0, Num(Field(arguments, "actualBrokerFeePerc")), 6);
        Assert.Equal(0.01, Num(Field(arguments, "expectedBrokerFeePercentage")), 6);
        Assert.Empty(seat.Sink.Packets);
    }

    [PgFact]
    public async Task A_minimum_volume_above_the_quantity_is_the_label_without_arguments()
    {
        // marketsvc.py:264-265 raises MktInvalidMinVolumeCannotExceedQuantity; the server sends the same label.
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Minimum Buyer", 100000m);
        var seat = await SeatAsync(r, actor);
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => seat.Proxy.PlaceBuyOrder(Call(seat.Session, "PlaceBuyOrder", 9,
            new PyInt(r.Station.Id), new PyInt(Tritanium), new PyReal(10), new PyInt(5), new PyInt(-1), new PyInt(6), new PyInt(0), PyBool.False)).AsTask());
        var (label, arguments) = UserError(refusal);
        Assert.Equal("MktInvalidMinVolumeCannotExceedQuantity", label);
        Assert.Empty(arguments.Entries);
    }

    [PgFact]
    public async Task A_corporation_order_row_carries_its_wallet_division_as_keyID()
    {
        // GetCorporationOrders rows: keyID is the division (accountKey), not 1000 (G1). quote.py:442-443 names the division from it.
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Division Trader", 10000000m);
        var corp = await r.Corporations.CreateAsync(actor, new("Orbital Works", "ORBIT", "", "", 0), "corporation");
        await using (var c = await r.Source.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await r.Wallets.TransferAsync(c, tx, new WalletAccount(actor), new WalletAccount(corp.Id, 1006), 1000m, 10, "Deposit", r.Now, "deposit", "test");
            await tx.CommitAsync();
        }
        await r.Corporations.SetAccountKeyAsync(actor, 1006);
        await r.Book.SeedNpcAsync([new("ask", Tritanium, false, 10, 1, 32767, MarketLocation.From(r.Station), r.Now)]);
        var seat = await SeatAsync(r, actor);
        await seat.Proxy.PlaceBuyOrder(Call(seat.Session, "PlaceBuyOrder", 10, new PyInt(r.Station.Id), new PyInt(Tritanium), new PyReal(5),
            new PyInt(2), new PyInt(-1), new PyInt(1), new PyInt(90), PyBool.True, PyNone.Instance));
        var rows = Assert.IsType<PyObject>(Received(await seat.Proxy.GetCorporationOrders(Call(seat.Session, "GetCorporationOrders", 11))));
        var line = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(Field(Fields(rows), "lines")).Items));
        Assert.Equal(new PyInt(1006), line.Items[18]);
        Assert.Equal(new PyInt(1), line.Items[14]);
    }

    [PgFact]
    public async Task Character_transactions_list_the_personal_fills_newest_first_and_honour_fromDate()
    {
        // CharGetTransactions (EVE.js 6582) feeds marketSvc.py:52-64: buyerID and sellerID name the characters, accountID the division of the viewer.
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Ledger Keeper", 100000m);
        var seat = await SeatAsync(r, actor);
        await r.Book.SeedNpcAsync([new("ask", Tritanium, false, 10, 100, 32767, MarketLocation.From(r.Station), r.Now)]);
        await seat.Proxy.BuyMultipleItems(Call(seat.Session, "BuyMultipleItems", 12, new PyInt(r.Station.Id), new PyList([Line(Tritanium, 2, 10)]), PyBool.False));
        r.Now += 60_000;
        await seat.Proxy.BuyMultipleItems(Call(seat.Session, "BuyMultipleItems", 13, new PyInt(r.Station.Id), new PyList([Line(Tritanium, 3, 10)]), PyBool.False));
        var all = Assert.IsType<PyList>(Received(await seat.Proxy.CharGetTransactions(Call(seat.Session, "CharGetTransactions", 14, PyNone.Instance)))).Items;
        Assert.Equal(2, all.Count);
        var newest = Fields(all[0]);
        var oldest = Fields(all[1]);
        Assert.Equal((double)(3), Num(Field(newest, "quantity")));
        Assert.Equal((double)(2), Num(Field(oldest, "quantity")));
        Assert.Equal((double)(actor), Num(Field(newest, "buyerID")));
        Assert.Equal((double)(1000), Num(Field(newest, "accountID")));
        Assert.Equal((double)(Tritanium), Num(Field(newest, "typeID")));
        Assert.Equal(10, Num(Field(newest, "price")));
        var cutoff = Py.FileTimeFromUnixMs(r.Now - 30_000);
        var recent = Assert.IsType<PyList>(Received(await seat.Proxy.CharGetTransactions(Call(seat.Session, "CharGetTransactions", 15, new PyLong(cutoff))))).Items;
        Assert.Single(recent);
    }

    [PgFact]
    public async Task A_seller_sees_the_sale_in_its_own_transactions()
    {
        // The seller side of a fill: sellerID is the character, buyerID the NPC corporation that owns the station's bid.
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Seller", 0m);
        var seat = await SeatAsync(r, actor);
        var item = await r.Stock(actor, 4);
        await r.Book.SeedNpcAsync([new("bid", Tritanium, true, 9, 100, 32767, MarketLocation.From(r.Station), r.Now)]);
        await seat.Proxy.PlaceMultiSellOrder(Call(seat.Session, "PlaceMultiSellOrder", 16,
            new PyList([Py.KeyVal(("typeID", new PyInt(Tritanium)), ("itemID", new PyLong(item)), ("price", new PyReal(9)), ("quantity", new PyInt(4)),
                ("stationID", new PyInt(r.Station.Id)))]), PyBool.False, new PyInt(0), PyNone.Instance));
        var row = Fields(Assert.Single(Assert.IsType<PyList>(Received(await seat.Proxy.CharGetTransactions(Call(seat.Session, "CharGetTransactions", 17, PyNone.Instance)))).Items));
        Assert.Equal((double)(actor), Num(Field(row, "sellerID")));
        Assert.Equal((double)(4), Num(Field(row, "quantity")));
        Assert.Equal((double)(1000), Num(Field(row, "accountID")));
        Assert.NotEqual(new PyInt(actor), Field(row, "buyerID"));
    }

    [PgFact]
    public async Task Corporation_transactions_show_the_division_fills_to_an_accountant_and_refuse_a_member_without_the_right()
    {
        // CorpGetTransactions (EVE.js 6601, 6616-6631): the accountant reads every division; a member without the right gets CrpAccessDenied (101).
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Books Keeper", 10000000m);
        var corp = await r.Corporations.CreateAsync(actor, new("Orbital Works", "ORBIT", "", "", 0), "corporation");
        await using (var c = await r.Source.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await r.Wallets.TransferAsync(c, tx, new WalletAccount(actor), new WalletAccount(corp.Id, 1006), 1000m, 10, "Deposit", r.Now, "deposit", "test");
            await tx.CommitAsync();
        }
        await r.Corporations.SetAccountKeyAsync(actor, 1006);
        await r.Book.SeedNpcAsync([new("ask", Tritanium, false, 10, 100, 32767, MarketLocation.From(r.Station), r.Now)]);
        var seat = await SeatAsync(r, actor);
        await seat.Proxy.BuyMultipleItems(Call(seat.Session, "BuyMultipleItems", 18, new PyInt(r.Station.Id), new PyList([Line(Tritanium, 5, 10)]), PyBool.True));
        var rows = Assert.IsType<PyList>(Received(await seat.Proxy.CorpGetTransactions(Call(seat.Session, "CorpGetTransactions", 19, PyNone.Instance, new PyInt(1006))))).Items;
        var row = Fields(Assert.Single(rows));
        Assert.Equal((double)(1006), Num(Field(row, "accountID")));
        Assert.Equal((double)(1006), Num(Field(row, "keyID")));
        Assert.Equal(actor, (long)Num(Field(row, "buyerID")));

        // a character outside the corporation (its NPC corporation) has no division read right
        var outsider = await SeatAsync(r, await r.Actor("Outsider"), 102);
        var denied = await Assert.ThrowsAsync<MachoWrappedException>(() => outsider.Proxy.CorpGetTransactions(Call(outsider.Session, "CorpGetTransactions", 20, PyNone.Instance, new PyInt(1006))).AsTask());
        Assert.Equal("CrpAccessDenied", UserError(denied).Label);
    }

    [PgFact]
    public async Task A_modification_on_a_stale_view_is_a_refusal_that_changes_nothing()
    {
        // ModifyCharOrder (the client's oldPrice and issueDate, marketsvc.py:285-288): a stale view is a refusal, and the price stays.
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Stale View", 10000m);
        var seat = await SeatAsync(r, actor);
        var placedAt = r.Now;
        await seat.Proxy.PlaceBuyOrder(Call(seat.Session, "PlaceBuyOrder", 20, new PyInt(r.Station.Id), new PyInt(Tritanium), new PyReal(5),
            new PyInt(2), new PyInt(-1), new PyInt(1), new PyInt(90), PyBool.False, PyNone.Instance));
        var order = Assert.Single((await r.Book.BookAsync(r.Station.RegionId, Tritanium, r.Now)).Bids);
        r.Now += MarketRules.ModificationDelayMs;
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => seat.Proxy.ModifyCharOrder(Call(seat.Session, "ModifyCharOrder", 21,
            new PyLong(order.Id), new PyReal(6), PyBool.True, new PyInt(r.Station.Id), new PyInt(r.Station.SolarSystemId), new PyReal(4), new PyInt(-1),
            new PyInt(2), new PyLong(Py.FileTimeFromUnixMs(placedAt)))).AsTask());
        Assert.Equal("CustomInfo", refusal.Error.UserErrorMessage);
        Assert.Equal(5m, (await r.Book.BookAsync(r.Station.RegionId, Tritanium, r.Now)).Bids.Single().Price);
        Assert.Empty(seat.Sink.Packets.Skip(1));
    }

    [PgFact]
    public async Task An_identical_placement_within_five_seconds_is_refused_and_a_later_one_is_placed()
    {
        // Q14: the same request from the same session, five seconds after the first, is not placed again; after the window it is.
        await using var r = await TradeRig.Create();
        var actor = await r.Actor("Repeater", 10000m);
        var seat = await SeatAsync(r, actor);
        Task<PyValue> Place(long callId) => seat.Proxy.PlaceBuyOrder(Call(seat.Session, "PlaceBuyOrder", callId, new PyInt(r.Station.Id), new PyInt(Tritanium),
            new PyReal(5), new PyInt(2), new PyInt(-1), new PyInt(1), new PyInt(90), PyBool.False, PyNone.Instance)).AsTask();
        await Place(30);
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => Place(31));
        Assert.Equal("CustomInfo", refusal.Error.UserErrorMessage);
        Assert.Single((await r.Book.BookAsync(r.Station.RegionId, Tritanium, r.Now)).Bids);
        r.Now += 6_000;
        await Place(32);
        Assert.Equal(2, (await r.Book.BookAsync(r.Station.RegionId, Tritanium, r.Now)).Bids.Count);
    }
}
