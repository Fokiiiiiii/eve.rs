using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Market;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Market.Tests;

/// <summary>
/// prices, the volume (the sum of the quantities) and the number of fills. "Old" holds the days before today and "new" the day in progress,
/// which is how the client reads them (marketsvc.py:344-366). Read through the market service, as the client reads them.
/// </summary>
public class MarketHistoryTests
{
    private const int Tritanium = 34, Mexallon = 36, Plex = 44992;
    private const long Day = 86_400_000;

    private static ServiceCall Call(ClientSession session, string method, long id, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(id) };

    private sealed class Outbox : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    /// <summary>A character's session and the market service built as the host builds it, for one viewer.</summary>
    private static async Task<(ClientSession Session, MarketProxyService Proxy)> ViewerAsync(TradeRig rig, long actor, long sessionId)
    {
        var session = new ClientSession(new Outbox()) { SessionId = sessionId };
        session.Set("charid", new PyInt(actor));
        var sessions = new CharacterSessions();
        sessions.Enter(actor, session);
        var catalog = await MarketCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        return (session, new MarketProxyService(rig.Book, rig.Trading, rig.Topology, rig.Characters, rig.Corporations, rig.Wallets, catalog,
            () => rig.Now, rig.Expiry, sessions));
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

    private static double Num(PyValue value) => value switch
    {
        PyInt n => n.Value,
        PyLong n => n.Value,
        PyReal n => n.Value,
        _ => throw new InvalidOperationException($"not a number: {value}"),
    };

    /// <summary>The unix day of a row's historyDate (a FILETIME: a whole day in 100 ns units).</summary>
    private static long UnixDay(PyValue fileTime)
    {
        var filetime = fileTime switch { PyLong l => l.Value, PyInt i => i.Value, _ => throw new InvalidOperationException("not a FILETIME") };
        return (filetime - 116444736000000000L) / 10_000 / Day;
    }

    private static void AssertRow(PyValue row, long unixDay, double low, double high, double average, double volume, double orders)
    {
        var items = Assert.IsType<PyTuple>(row).Items;
        Assert.Equal(unixDay, UnixDay(items[0]));
        Assert.Equal(low, Num(items[1]));
        Assert.Equal(high, Num(items[2]));
        Assert.Equal(average, Num(items[3]), 6);
        Assert.Equal(volume, Num(items[4]));
        Assert.Equal(orders, Num(items[5]));
    }

    /// <summary>A buy that takes what the NPC supply offers at or under its limit, at the time given (the fill is stamped with it).</summary>
    private static async Task BuyAsync(TradeRig rig, long buyer, int typeId, decimal limit, long quantity, long atMs, string key)
    {
        rig.Now = atMs;
        await rig.Trading.PlaceAsync(new MarketRequest(buyer, typeId, true, limit, quantity, rig.Station.Id, 0, -1, 1), key);
    }

    private static MarketLocation Here(TradeRig rig) => MarketLocation.From(rig.Station);

    /// <summary>The Tritanium supply of the test station: 5 at 10, 5 at 12, 2 at 20 and 1 at 30.</summary>
    private static async Task TritaniumSupplyAsync(TradeRig rig) => await rig.Book.SeedNpcAsync(
        [new("ask-10", Tritanium, false, 10, 5, 32767, Here(rig), rig.Now), new("ask-12", Tritanium, false, 12, 5, 32767, Here(rig), rig.Now),
         new("ask-20", Tritanium, false, 20, 2, 32767, Here(rig), rig.Now), new("ask-30", Tritanium, false, 30, 1, 32767, Here(rig), rig.Now)]);

    /// <summary>The fills of three days: two days before today (two fills, then one), and today (one fill).</summary>
    private static async Task ThreeDaysOfTradeAsync(TradeRig rig, long buyer)
    {
        var today = rig.Now / Day;
        await TritaniumSupplyAsync(rig);
        await BuyAsync(rig, buyer, Tritanium, 30, 10, (today - 2) * Day + 3_600_000, "day-2");   // 5 at 10 and 5 at 12
        await BuyAsync(rig, buyer, Tritanium, 30, 2, (today - 1) * Day + 3_600_000, "day-1");    // 2 at 20
        await BuyAsync(rig, buyer, Tritanium, 30, 1, today * Day + 3_600_000, "today");          // 1 at 30
    }

    [PgFact]
    public async Task Each_day_is_one_row_and_the_day_in_progress_is_the_new_row()
    {
        await using var r = await TradeRig.Create();
        var buyer = await r.Actor("History Buyer", 1_000_000m);
        var today = r.Now / Day;
        await ThreeDaysOfTradeAsync(r, buyer);
        var (session, proxy) = await ViewerAsync(r, buyer, 401);

        var old = Lines(Received(await proxy.GetOldPriceHistory(Call(session, "GetOldPriceHistory", 1, new PyInt(Tritanium)))));
        var fresh = Lines(Received(await proxy.GetNewPriceHistory(Call(session, "GetNewPriceHistory", 2, new PyInt(Tritanium)))));

        Assert.Equal(2, old.Count);
        AssertRow(old[0], today - 2, low: 10, high: 12, average: 11, volume: 10, orders: 2);
        AssertRow(old[1], today - 1, low: 20, high: 20, average: 20, volume: 2, orders: 1);
        AssertRow(Assert.Single(fresh), today, low: 30, high: 30, average: 30, volume: 1, orders: 1);
    }

    [PgFact]
    public async Task The_seven_day_average_the_client_takes_is_the_volume_weighted_mean_of_the_rows()
    {
        // GetAveragePrice (marketsvc.py:368-386) sums avgPrice x volume over the rows of the last seven days and divides by the volume.
        await using var r = await TradeRig.Create();
        var buyer = await r.Actor("Average Buyer", 1_000_000m);
        var today = r.Now / Day;
        await ThreeDaysOfTradeAsync(r, buyer);
        var (session, proxy) = await ViewerAsync(r, buyer, 402);
        var rows = Lines(Received(await proxy.GetOldPriceHistory(Call(session, "GetOldPriceHistory", 1, new PyInt(Tritanium)))))
            .Concat(Lines(Received(await proxy.GetNewPriceHistory(Call(session, "GetNewPriceHistory", 2, new PyInt(Tritanium))))))
            .Where(row => UnixDay(Assert.IsType<PyTuple>(row).Items[0]) >= today - 7)
            .Select(row => Assert.IsType<PyTuple>(row).Items).ToList();
        var volume = rows.Sum(items => Num(items[4]));
        var priceVolume = rows.Sum(items => Num(items[3]) * Num(items[4]));
        // (11 x 10 + 20 x 2 + 30 x 1) / 13: the fill prices weighted by the quantities, not the mean of the day averages
        Assert.Equal(13, volume);
        Assert.Equal(180.0 / 13.0, priceVolume / volume, 9);
    }

    [PgFact]
    public async Task A_type_without_fills_and_a_region_without_trades_have_no_rows()
    {
        await using var r = await TradeRig.Create();
        var buyer = await r.Actor("Quiet Buyer", 1_000_000m);
        await ThreeDaysOfTradeAsync(r, buyer);
        var (session, proxy) = await ViewerAsync(r, buyer, 403);
        Assert.Empty(Lines(Received(await proxy.GetOldPriceHistory(Call(session, "GetOldPriceHistory", 1, new PyInt(Mexallon))))));
        Assert.Empty(Lines(Received(await proxy.GetNewPriceHistory(Call(session, "GetNewPriceHistory", 2, new PyInt(Mexallon))))));
        var elsewhere = r.Stations.All.First(station => station.RegionId != r.Station.RegionId);
        var quiet = await ActorAtAsync(r, elsewhere, "Nobody Trades");
        var (quietSession, quietProxy) = await ViewerAsync(r, quiet, 404);
        Assert.Empty(Lines(Received(await quietProxy.GetOldPriceHistory(Call(quietSession, "GetOldPriceHistory", 3, new PyInt(Tritanium))))));
        Assert.Empty(Lines(Received(await quietProxy.GetNewPriceHistory(Call(quietSession, "GetNewPriceHistory", 4, new PyInt(Tritanium))))));
    }

    [PgFact]
    public async Task The_history_belongs_to_the_callers_region_and_a_trader_elsewhere_sees_none()
    {
        await using var r = await TradeRig.Create();
        var buyer = await r.Actor("Near Buyer", 1_000_000m);
        await ThreeDaysOfTradeAsync(r, buyer);
        var elsewhere = r.Stations.All.First(station => station.RegionId != r.Station.RegionId);
        var far = await ActorAtAsync(r, elsewhere, "Far Trader");
        var (nearSession, nearProxy) = await ViewerAsync(r, buyer, 405);
        var (farSession, farProxy) = await ViewerAsync(r, far, 406);

        Assert.Equal(2, Lines(Received(await nearProxy.GetOldPriceHistory(Call(nearSession, "GetOldPriceHistory", 1, new PyInt(Tritanium))))).Count);
        Assert.Empty(Lines(Received(await farProxy.GetOldPriceHistory(Call(farSession, "GetOldPriceHistory", 2, new PyInt(Tritanium))))));
        Assert.Empty(Lines(Received(await farProxy.GetNewPriceHistory(Call(farSession, "GetNewPriceHistory", 3, new PyInt(Tritanium))))));
        var farMany = Assert.IsType<PyDict>(await farProxy.GetHistoryForManyTypeIDs(Call(farSession, "GetHistoryForManyTypeIDs", 4,
            new PyList([new PyInt(Tritanium)]))));
        var pair = Assert.IsType<PyList>(Assert.Single(farMany.Entries).Value);
        Assert.All(pair.Items, rowset => Assert.Empty(Lines(rowset)));
    }

    /// <summary>A character at the given station (the rig's own actor helper puts every character at its station).</summary>
    private static async Task<long> ActorAtAsync(TradeRig rig, Station station, string name)
    {
        await using var c = await rig.Source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var id = await rig.Characters.InsertAsync(c, tx, new NewCharacter(1, name, 0, 1, 1, 1, 11, 1376, 1000044, null,
            station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
        await rig.WalletStore.OpenAsync(c, tx, id, 0m, 0);
        await tx.CommitAsync();
        return id;
    }

    [PgFact]
    public async Task PLEX_history_is_its_own_type_and_reads_as_the_pair_of_its_days()
    {
        await using var r = await TradeRig.Create();
        var buyer = await r.Actor("Plex Buyer", 5_000_000m);
        var today = r.Now / Day;
        await r.Book.SeedNpcAsync([new("ask", Plex, false, 1000000, 3, 32767, Here(r), r.Now)]);
        await BuyAsync(r, buyer, Plex, 1000000, 1, (today - 1) * Day + 3_600_000, "plex-day-1");
        await BuyAsync(r, buyer, Plex, 1000000, 1, today * Day + 3_600_000, "plex-today");
        var (session, proxy) = await ViewerAsync(r, buyer, 407);

        AssertRow(Assert.Single(Lines(Received(await proxy.GetPlexOldPriceHistory(Call(session, "GetPlexOldPriceHistory", 1))))),
            today - 1, low: 1000000, high: 1000000, average: 1000000, volume: 1, orders: 1);
        AssertRow(Assert.Single(Lines(Received(await proxy.GetPlexNewPriceHistory(Call(session, "GetPlexNewPriceHistory", 2))))),
            today, low: 1000000, high: 1000000, average: 1000000, volume: 1, orders: 1);
        var pairs = Assert.IsType<PyDict>(await proxy.GetPlexHistory(Call(session, "GetPlexHistory", 3)));
        var pair = Assert.IsType<PyList>(Assert.Single(pairs.Entries).Value);
        Assert.Single(Lines(Received(pair.Items[0])));
        Assert.Single(Lines(Received(pair.Items[1])));
        Assert.Empty(Lines(Received(await proxy.GetOldPriceHistory(Call(session, "GetOldPriceHistory", 4, new PyInt(Tritanium))))));
    }

    [PgFact]
    public async Task A_fill_that_rolls_back_leaves_no_history_row_and_the_earlier_fill_stays()
    {
        await using var r = await TradeRig.Create();
        var buyer = await r.Actor("Rollback Buyer", 1_000_000m);
        await TritaniumSupplyAsync(r);
        await BuyAsync(r, buyer, Tritanium, 30, 1, r.Now, "kept");   // one fill at 10, committed
        Assert.Single(await r.Book.HistoryAsync(r.Station.RegionId, Tritanium, 0));
        await using (var fail = r.Source.CreateCommand($"ALTER TABLE {r.MarketSchema}.fills ADD CONSTRAINT no_fills CHECK(quantity=0) NOT VALID"))
            await fail.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<PostgresException>(() => r.Trading.PlaceAsync(new MarketRequest(buyer, Tritanium, true, 30, 1, r.Station.Id, 0, -1, 1), "rolled"));

        var rows = await r.Book.HistoryAsync(r.Station.RegionId, Tritanium, 0);
        var row = Assert.Single(rows);
        Assert.Equal(1, row.Volume);
        Assert.Equal(1, row.Orders);
    }
}
