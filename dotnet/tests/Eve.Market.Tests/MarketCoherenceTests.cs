using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Market;
using Eve.Wallet;

namespace Eve.Market.Tests;

/// <summary>
/// M4: two sessions in one region. The client keeps a book (GetOrders, the station and system asks) and its owner lists in objectCaching until
/// the server drops them with InvalidateCachedMethodCall (objectCaching.py:222-238). Each change sends that invalidation before its notices,
/// the next read shows the change, and a session in another region is not asked to drop this region's book.
/// </summary>
public class MarketCoherenceTests
{
    private const int Tritanium = 34;
    private const long Day = 86_400_000;
    private const long Grace = MarketExpiry.GraceMs;

    private static ServiceCall Call(ClientSession session, string method, long id, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(id) };

    private sealed class Outbox : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class Viewer(long actor, Outbox outbox, ClientSession session, MarketProxyService proxy)
    {
        public long Actor { get; } = actor;
        public Outbox Outbox { get; } = outbox;
        public ClientSession Session { get; } = session;
        public MarketProxyService Proxy { get; } = proxy;
    }

    /// <summary>A character's session, registered in the shared session registry as the host registers it at character selection.</summary>
    private static async Task<Viewer> ViewerAsync(TradeRig rig, CharacterSessions sessions, long actor, int number, int? regionId = null)
    {
        var outbox = new Outbox();
        var session = new ClientSession(outbox) { SessionId = number, ClientId = 1000 + number, UserId = 500 + number };
        session.Set("charid", new PyInt(actor));
        session.Set("regionid", new PyInt(regionId ?? rig.Station.RegionId));
        sessions.Enter(actor, session);
        var catalog = await MarketCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var proxy = new MarketProxyService(rig.Book, rig.Trading, rig.Topology, rig.Characters, rig.Corporations, rig.Wallets, catalog,
            () => rig.Now, rig.Expiry, sessions);
        return new Viewer(actor, outbox, session, proxy);
    }

    private static PyValue Received(PyValue value)
    {
        var decoded = MarshalCodec.Decode(MarshalCodec.Encode(value));
        if (decoded is not PyObject { Name: var name } cached || JsSemantics.PlainText(name) != ObjectCaching.ResultClass) return decoded;
        var stream = Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(cached.Args).Items[1]);
        return stream.Decoded ?? MarshalCodec.Decode(stream.Raw);
    }

    private static PyValue Field(PyValue value, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries.Single(e => JsSemantics.PlainText(e.Key) == name).Value;

    private static IReadOnlyList<PyValue> Lines(PyValue rowset) => Assert.IsType<PyList>(Field(rowset, "lines")).Items;

    /// <summary>The ask side of a book read: the rows of the first side of the (asks, bids) pair.</summary>
    private static IReadOnlyList<PyValue> Asks(PyValue book) => Lines(Assert.IsType<PyTuple>(Received(book)).Items[0]);

    private static double Num(PyValue value) => value switch
    {
        PyInt n => n.Value,
        PyLong n => n.Value,
        PyReal n => n.Value,
        _ => throw new InvalidOperationException($"not a number: {value}"),
    };

    private static string Text(PyValue value) => value switch
    {
        PyInt n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PyLong n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => JsSemantics.PlainText(value) ?? string.Empty,
    };

    /// <summary>The objectCaching invalidations a session was sent, each as its arguments in order: "marketProxy GetOrders 34".</summary>
    private static List<string> Invalidations(Outbox outbox)
    {
        var found = new List<string>();
        foreach (var packet in outbox.Packets.Select(p => MarshalCodec.Decode(MarshalCodec.Encode(p))))
        {
            if (packet is not PyObject { Name: var name } call || JsSemantics.PlainText(name) != ClientCallRequest.ClassName) continue;
            var entry = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(call.Args).Items[4]).Items[0]);
            var stream = Assert.IsType<PySubStream>(entry.Items[1]);
            var body = Assert.IsType<PyTuple>(stream.Decoded ?? MarshalCodec.Decode(stream.Raw));
            if (JsSemantics.PlainText(body.Items[1]) != "InvalidateCachedMethodCall") continue;
            found.Add(string.Join(" ", Assert.IsType<PyList>(body.Items[2]).Items.Select(Text)));
        }
        return found;
    }

    /// <summary>The OnOwnOrdersChanged notices a session was sent, as the reason and the order ids of the rows (balance notices are not these).</summary>
    private static List<(string Reason, List<long> OrderIds)> Notices(Outbox outbox)
    {
        var found = new List<(string, List<long>)>();
        foreach (var packet in outbox.Packets)
        {
            if (packet is not PyObject { Name: var name } call || JsSemantics.PlainText(name) != ClientNotification.ClassName) continue;
            var entry = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(call.Args).Items[4]).Items[0]);
            var notification = Assert.IsType<PyTuple>(MarshalCodec.Decode(Assert.IsType<PyBuffer>(entry.Items[1]).Value.ToArray()));
            var payload = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(notification.Items[1]).Items[1]);
            if (payload.Items.Count != 3 || payload.Items[0] is not PyList rows) continue;
            found.Add((JsSemantics.PlainText(payload.Items[1]) ?? string.Empty, rows.Items.Select(row => (long)Num(Field(row, "orderID"))).ToList()));
        }
        return found;
    }

    private static void AssertInvalidated(Viewer viewer, string method, params string[] arguments) =>
        Assert.Contains(string.Join(" ", new[] { "marketProxy", method }.Concat(arguments)), Invalidations(viewer.Outbox));

    private static void AssertNotified(Viewer viewer, string reason, long orderId) =>
        Assert.Contains(Notices(viewer.Outbox), notice => notice.Reason == reason && notice.OrderIds.Contains(orderId));

    private static PyValue SellLine(long item, long typeId, double price, long quantity, long stationId) => Py.KeyVal(
        ("typeID", new PyInt(typeId)), ("itemID", new PyLong(item)), ("price", new PyReal(price)), ("quantity", new PyInt(quantity)),
        ("stationID", new PyInt(stationId)));

    /// <summary>The seller places one sell order of 4 Tritanium at 9 in the station, and returns its order id.</summary>
    private static async Task<long> PlaceSellAsync(TradeRig rig, Viewer seller, long duration = 30, long quantity = 4)
    {
        var item = await rig.Stock(seller.Actor, quantity);
        await seller.Proxy.PlaceMultiSellOrder(Call(seller.Session, "PlaceMultiSellOrder", 2,
            new PyList([SellLine(item, Tritanium, 9, quantity, rig.Station.Id)]), PyBool.False, new PyInt(duration), PyNone.Instance));
        var own = Lines(Received(await seller.Proxy.GetCharOrders(Call(seller.Session, "GetCharOrders", 3))));
        return (long)Num(Assert.IsType<PyTuple>(own.Last()).Items[0]);
    }

    private static async Task<PyValue> BookAsync(Viewer viewer, long id) =>
        await viewer.Proxy.GetOrders(Call(viewer.Session, "GetOrders", id, new PyInt(Tritanium)));

    [PgFact]
    public async Task A_placed_sell_drops_the_region_book_in_the_other_session_and_its_next_read_shows_the_order()
    {
        await using var r = await TradeRig.Create();
        var sessions = new CharacterSessions();
        var seller = await ViewerAsync(r, sessions, await r.Actor("Seller", 10_000m), 201);
        var buyer = await ViewerAsync(r, sessions, await r.Actor("Buyer"), 202);
        var elsewhere = await ViewerAsync(r, sessions, await r.Actor("Elsewhere"), 203, r.Station.RegionId + 1);
        Assert.Empty(Asks(await BookAsync(buyer, 1)));   // the buyer's client caches the empty book

        var orderId = await PlaceSellAsync(r, seller);

        AssertInvalidated(buyer, "GetOrders", "34");
        AssertInvalidated(buyer, "GetStationAsks");
        AssertInvalidated(buyer, "GetSystemAsks");
        Assert.Empty(Invalidations(elsewhere.Outbox));
        AssertNotified(seller, "Created", orderId);
        var book = Assert.Single(Asks(await BookAsync(buyer, 3)));
        Assert.Equal(9, Num(Assert.IsType<PyTuple>(book).Items[0]));
        Assert.Equal(4, Num(Assert.IsType<PyTuple>(book).Items[1]));
        Assert.Equal(orderId, Num(Assert.IsType<PyTuple>(book).Items[4]));
    }

    [PgFact]
    public async Task A_cancel_drops_the_region_book_and_the_owner_lists_and_the_owner_is_notified()
    {
        await using var r = await TradeRig.Create();
        var sessions = new CharacterSessions();
        var seller = await ViewerAsync(r, sessions, await r.Actor("Seller", 10_000m), 211);
        var buyer = await ViewerAsync(r, sessions, await r.Actor("Buyer"), 212);
        var orderId = await PlaceSellAsync(r, seller);
        Assert.Single(Asks(await BookAsync(buyer, 1)));   // cached with the order in it

        await seller.Proxy.CancelCharOrder(Call(seller.Session, "CancelCharOrder", 4, new PyLong(orderId), PyNone.Instance));

        AssertInvalidated(buyer, "GetOrders", "34");
        AssertInvalidated(seller, "GetCharOrders");
        AssertInvalidated(seller, "GetMarketOrderHistory");
        AssertNotified(seller, "Cancelled", orderId);
        Assert.Empty(Asks(await BookAsync(buyer, 2)));
    }

    [PgFact]
    public async Task A_fill_drops_the_region_book_and_the_maker_owner_list_and_the_maker_is_notified()
    {
        await using var r = await TradeRig.Create();
        var sessions = new CharacterSessions();
        var seller = await ViewerAsync(r, sessions, await r.Actor("Seller", 10_000m), 221);
        var buyer = await ViewerAsync(r, sessions, await r.Actor("Buyer"), 222);
        var orderId = await PlaceSellAsync(r, seller);
        Assert.Single(Asks(await BookAsync(buyer, 1)));   // cached with the order in it

        await buyer.Proxy.PlaceBuyOrder(Call(buyer.Session, "PlaceBuyOrder", 5, new PyInt(r.Station.Id), new PyInt(Tritanium), new PyReal(9),
            new PyInt(4), new PyInt(-1), new PyInt(1), new PyInt(0), PyBool.False, PyNone.Instance));

        AssertInvalidated(buyer, "GetOrders", "34");
        AssertInvalidated(seller, "GetCharOrders");
        AssertNotified(seller, "Filled", orderId);
        Assert.DoesNotContain(Invalidations(buyer.Outbox), line => line.Contains("GetCharOrders"));
        Assert.Empty(Asks(await BookAsync(buyer, 2)));
    }

    [PgFact]
    public async Task An_expiry_drops_the_region_book_and_the_owner_is_notified_by_the_expiry_job()
    {
        await using var r = await TradeRig.Create();
        var sessions = new CharacterSessions();
        r.Notifier = new MarketNotifier(r.Wallets, sessions);
        var seller = await ViewerAsync(r, sessions, await r.Actor("Seller", 10_000m), 231);
        var buyer = await ViewerAsync(r, sessions, await r.Actor("Buyer"), 232);
        var placedAt = r.Now;
        var orderId = await PlaceSellAsync(r, seller, duration: 1);
        Assert.Single(Asks(await BookAsync(buyer, 1)));   // cached with the order in it

        r.Now = placedAt + Day + Grace;
        await r.Expiry.SweepAsync();

        AssertInvalidated(buyer, "GetOrders", "34");
        AssertInvalidated(seller, "GetCharOrders");
        AssertNotified(seller, "Expired", orderId);
        Assert.Empty(Asks(await BookAsync(buyer, 2)));
    }

    /// <summary>A character who joins the corporation through the application flow, with the given market roles when there are any.</summary>
    private static async Task<long> JoinAsync(TradeRig rig, long founder, long corporationId, string name, long? roles)
    {
        var character = await rig.Actor(name);
        var application = await rig.Corporations.ApplyAsync(character, corporationId, "Please accept.");
        await rig.Corporations.RespondAsync(founder, application, 6, "Please accept.", "Welcome");
        await rig.Corporations.RespondAsync(character, application, 2, "Please accept.", "Accepted");
        if (roles is { } masks)
        {
            var joined = (await rig.Corporations.MemberAsync(character))!;
            await rig.Corporations.UpdateMemberAsync(founder, joined with { Masks = new RoleMasks(masks) });
        }
        return character;
    }

    [PgFact]
    public async Task A_corporate_placement_fill_and_cancel_reach_every_member_with_the_market_rights_and_no_other_member()
    {
        await using var r = await TradeRig.Create();
        var sessions = new CharacterSessions();
        var founder = await r.Actor("Coherence Founder", 10_000_000m);
        var corp = await r.Corporations.CreateAsync(founder, new("Coherence Works", "CHWRK", "", "", 0), "coherence-corp");
        await using (var c = await r.Source.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await r.Wallets.TransferAsync(c, tx, new WalletAccount(founder), new WalletAccount(corp.Id, 1006), 1000m, 10, "Deposit", r.Now, "deposit", "test");
            await tx.CommitAsync();
        }
        await r.Corporations.SetAccountKeyAsync(founder, 1006);
        var trader = await JoinAsync(r, founder, corp.Id, "Trading Member", CorporationRoles.Trader);
        var bystander = await JoinAsync(r, founder, corp.Id, "Bystander", null);
        var founderView = await ViewerAsync(r, sessions, founder, 241);
        var traderView = await ViewerAsync(r, sessions, trader, 242);
        var bystanderView = await ViewerAsync(r, sessions, bystander, 243);
        var seller = await ViewerAsync(r, sessions, await r.Actor("Seller", 10_000m), 244);

        // the corporation's bid, placed by the founder from the corporation's wallet
        await founderView.Proxy.PlaceBuyOrder(Call(founderView.Session, "PlaceBuyOrder", 6, new PyInt(r.Station.Id), new PyInt(Tritanium),
            new PyReal(10), new PyInt(5), new PyInt(-1), new PyInt(1), new PyInt(1), PyBool.True, PyNone.Instance));
        var bidId = Assert.Single(Notices(founderView.Outbox), notice => notice.Reason == "Created").OrderIds.Single();
        AssertNotified(traderView, "Created", bidId);
        Assert.Empty(Notices(bystanderView.Outbox));

        // a sale into the bid fills it: the maker's corporation hears of the fill
        var item = await r.Stock(seller.Actor, 5);
        await seller.Proxy.PlaceMultiSellOrder(Call(seller.Session, "PlaceMultiSellOrder", 7,
            new PyList([SellLine(item, Tritanium, 10, 5, r.Station.Id)]), PyBool.False, new PyInt(0), PyNone.Instance));
        AssertNotified(traderView, "Filled", bidId);
        AssertInvalidated(traderView, "GetCorporationOrders");
        Assert.Empty(Notices(bystanderView.Outbox));

        // a second bid, cancelled by the founder: the trader hears of the cancel
        await founderView.Proxy.PlaceBuyOrder(Call(founderView.Session, "PlaceBuyOrder", 8, new PyInt(r.Station.Id), new PyInt(Tritanium),
            new PyReal(10), new PyInt(3), new PyInt(-1), new PyInt(1), new PyInt(1), PyBool.True, PyNone.Instance));
        var secondId = Notices(founderView.Outbox).Where(notice => notice.Reason == "Created").Select(notice => notice.OrderIds.Single()).Max();
        await founderView.Proxy.CancelCharOrder(Call(founderView.Session, "CancelCharOrder", 9, new PyLong(secondId), PyNone.Instance));
        AssertNotified(traderView, "Cancelled", secondId);
        Assert.Empty(Notices(bystanderView.Outbox));
    }
}
