using Eve.Marshal;
using Eve.Services;
using Eve.Services.Market;

namespace Eve.Market.Tests;

public class ProtocolTests
{
    private static ServiceCall Call(ClientSession session,string method,long id,params PyValue[] args) => new() { Session=session,Method=method,Args=args,CallId=new PyInt(id) };
    private static PyDict Fields(PyValue value) => Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args);
    private static PyValue Field(PyDict dict,string name) => dict.Entries.Single(e => Eve.Macho.JsSemantics.PlainText(e.Key)==name).Value;

    private static string NotificationName(PyValue packet)
    {
        var fields=Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args);
        var destination=Assert.IsType<PyObject>(fields.Items[2]);
        var address=Assert.IsType<PyTuple>(destination.Args);
        return Eve.Macho.JsSemantics.PlainText(address.Items[1])!;
    }


    private static PyTuple NotificationArguments(PyValue packet)
    {
        var fields=Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args);
        var envelope=Assert.IsType<PyTuple>(fields.Items[4]);
        var chunk=Assert.IsType<PyTuple>(envelope.Items[0]);
        var buffer=Assert.IsType<PyBuffer>(chunk.Items[1]);
        var inner=Assert.IsType<PyTuple>(MarshalCodec.Decode(buffer.Value));
        var payload=Assert.IsType<PyTuple>(inner.Items[1]);
        return Assert.IsType<PyTuple>(payload.Items[1]);
    }

    private sealed class RecordingPacketSink : Eve.Services.IPacketSink
    {
        public List<PyValue> Packets { get; }=[];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    [PgFact]
    public async Task Native_market_rpc_reads_descriptor_rows_buys_sells_and_cancels_without_node()
    {
        await using var r=await TradeRig.Create();
        var actor=await r.Actor("Trader One");
        var sink=new RecordingPacketSink();
        var session=new ClientSession(sink) { SessionId=101 };
        session.Set("charid",new PyInt(actor));
        var characterSessions=new CharacterSessions();
        characterSessions.Enter(actor,session);
        var catalog=await MarketCatalog.LoadAsync(new Eve.Sde.SdeReader(r.Source));
        var proxy=new MarketProxyService(r.Book,r.Trading,r.Topology,r.Characters,r.Wallets,catalog,() => r.Now,characterSessions);
        var events=new List<Eve.Wallet.WalletBalanceChange>();
        r.Wallets.BalanceCommitted+=events.Add;
        var groups=Fields(proxy.GetMarketGroups(Call(session,"GetMarketGroups",0)));
        Assert.Equal(10,Assert.IsType<PyList>(Field(groups,"header")).Items.Count);
        Assert.Contains(Assert.IsType<PyList>(Field(groups,"lines")).Items,row =>
            Assert.IsType<PyTuple>(row).Items[1]==new PyInt(4));
        await r.Book.SeedNpcAsync([new("ask",34,false,10,100,32767,MarketLocation.From(r.Station),r.Now)]);
        var cached=Assert.IsType<PyObject>(MarshalCodec.Decode(MarshalCodec.Encode(await proxy.GetOrders(Call(session,"GetOrders",1,new PyInt(34))))));
        Assert.Equal(ObjectCaching.ResultClass,Eve.Macho.JsSemantics.PlainText(cached.Name));
        var stream=Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(cached.Args).Items[1]);
        var result=stream.Decoded ?? MarshalCodec.Decode(stream.Raw);
        var halves=Assert.IsType<PyTuple>(result);
        var asks=Fields(halves.Items[0]);
        var descriptor=Assert.IsType<PyObjectEx>(Field(asks,"header"));
        Assert.Equal(15,Descriptor.FromHeader(descriptor).Columns.Count);
        var line=Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(Field(asks,"lines")).Items));
        Assert.Equal(new PyInt(10),line.Items[0]);
        Assert.IsType<PyLong>(line.Items[4]);
        Assert.IsType<PyLong>(line.Items[8]);
        Assert.Equal(new PyInt(-1),line.Items[14]);
        Assert.Equal(PyNone.Instance,await proxy.PlaceBuyOrder(Call(session,"PlaceBuyOrder",2,new PyInt(r.Station.Id),new PyInt(34),
            new PyReal(10),new PyInt(5),new PyInt(-1),new PyInt(1),new PyInt(0),PyBool.False)));
        Assert.Equal(9950m,(await r.WalletStore.GetAsync(actor))!.Isk);
        Assert.Equal(9950m,Assert.Single(events).Balance);
        var oldHistory=Fields(await proxy.GetOldPriceHistory(Call(session,"GetOldPriceHistory",3,new PyInt(34))));
        var newHistory=Fields(await proxy.GetNewPriceHistory(Call(session,"GetNewPriceHistory",4,new PyInt(34))));
        Assert.Empty(Assert.IsType<PyList>(Field(oldHistory,"lines")).Items);
        Assert.Single(Assert.IsType<PyList>(Field(newHistory,"lines")).Items);
        var many=Assert.IsType<PyDict>(await proxy.GetHistoryForManyTypeIDs(Call(session,"GetHistoryForManyTypeIDs",5,new PyList([new PyInt(34)]))));
        var historyPair=Assert.IsType<PyList>(Assert.Single(many.Entries).Value);
        Assert.Equal(2,historyPair.Items.Count);
        var bought=Assert.Single(await r.Items.InLocationAsync(r.Station.Id,actor));
        var sell=Py.Dict(("stationID",new PyInt(r.Station.Id)),("typeID",new PyInt(34)),("itemID",new PyLong(bought.Id)),
            ("price",new PyReal(12)),("quantity",new PyInt(5)));
        Assert.Equal(PyBool.True,await proxy.PlaceMultiSellOrder(Call(session,"PlaceMultiSellOrder",6,new PyList([sell]),PyBool.False,new PyInt(90))));
        Assert.Equal(2,sink.Packets.Count);
        Assert.All(sink.Packets,packet => Assert.Equal("OnOwnOrdersChanged",NotificationName(packet)));
        var own=(await r.Book.BookAsync(r.Station.RegionId,34,r.Now)).Asks.Single(o => !o.IsNpc);
        Assert.Equal(PyNone.Instance,await proxy.CancelCharOrder(Call(session,"CancelCharOrder",7,new PyLong(own.Id))));
        Assert.Equal(3,sink.Packets.Count);
        Assert.All(sink.Packets,packet => Assert.Equal("OnOwnOrdersChanged",NotificationName(packet)));
        Assert.Equal(5,Assert.Single(await r.Items.InLocationAsync(r.Station.Id,actor)).Quantity);
    }


    [Fact]
    public void Order_wire_preserves_same_station_jump_sentinel()
    {
        var location = new Eve.Market.MarketLocation(10, 1, 100, 1000);
        var order = new Eve.Market.MarketOrder(1, 34, false, 10m, 1, 1, 1,
            Eve.Market.MarketRules.RangeStation, location, 0, 90);
        var rowset = Eve.Services.Market.MarketWire.Orders([order],
            new Eve.Market.MarketTopology([]), location);
        var fields = Assert.IsType<PyDict>(Assert.IsType<PyObject>(rowset).Args);
        var lines = Assert.IsType<PyList>(Field(fields, "lines"));
        var line = Assert.IsType<PyTuple>(Assert.Single(lines.Items));

        Assert.Equal(new PyInt(-1), line.Items[14]);
    }


    [PgFact]
    public async Task Market_client_plex_history_escrow_and_order_history_reads_are_native()
    {
        await using var r=await TradeRig.Create();
        var actor=await r.Actor("Plex Trader");
        var sink=new RecordingPacketSink();
        var session=new ClientSession(sink) { SessionId=202 };
        session.Set("charid",new PyInt(actor));
        var characterSessions=new CharacterSessions();
        characterSessions.Enter(actor,session);
        var catalog=await MarketCatalog.LoadAsync(new Eve.Sde.SdeReader(r.Source));
        var proxy=new MarketProxyService(r.Book,r.Trading,r.Topology,r.Characters,r.Wallets,catalog,() => r.Now,characterSessions);
        const int plex=44992;
        var location=MarketLocation.From(r.Station);
        await r.Book.SeedNpcAsync([new("ask",plex,false,1000,3,32767,location,r.Now)]);

        Assert.Equal(PyNone.Instance,await proxy.PlaceBuyOrder(Call(session,"PlaceBuyOrder",20,
            new PyInt(r.Station.Id),new PyInt(plex),new PyReal(900),new PyInt(2),new PyInt(-1),
            new PyInt(1),new PyInt(90),PyBool.False)));
        var notification=NotificationArguments(Assert.Single(sink.Packets));
        var changed=Assert.IsType<PyList>(notification.Items[0]);
        var changedOrder=Fields(Assert.Single(changed.Items));
        Assert.Equal(new PyInt(plex),Field(changedOrder,"typeID"));
        Assert.Equal("Created",Eve.Macho.JsSemantics.PlainText(notification.Items[1]));
        Assert.Equal(new PyInt(0),notification.Items[2]);
        var cachedPlex=Assert.IsType<PyObject>(MarshalCodec.Decode(MarshalCodec.Encode(
            await proxy.GetPlexOrders(Call(session,"GetPlexOrders",21)))));
        Assert.Equal(ObjectCaching.ResultClass,Eve.Macho.JsSemantics.PlainText(cachedPlex.Name));
        var plexStream=Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(cachedPlex.Args).Items[1]);
        var plexBook=Assert.IsType<PyTuple>(plexStream.Decoded ?? MarshalCodec.Decode(plexStream.Raw));
        var plexAsks=Fields(plexBook.Items[0]);
        var askLine=Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(Field(plexAsks,"lines")).Items));
        Assert.Equal(new PyInt(plex),askLine.Items[2]);
        Assert.Equal(new PyInt(1000),askLine.Items[0]);

        var bestCached=Assert.IsType<PyObject>(MarshalCodec.Decode(MarshalCodec.Encode(
            await proxy.GetPlexBest(Call(session,"GetPlexBest",22)))));
        Assert.Equal(ObjectCaching.ResultClass,Eve.Macho.JsSemantics.PlainText(bestCached.Name));
        var bestStream=Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(bestCached.Args).Items[1]);
        var best=Assert.IsType<PyDict>(bestStream.Decoded ?? MarshalCodec.Decode(bestStream.Raw));
        var bestRow=Fields(Assert.Single(best.Entries).Value);
        Assert.Equal(new PyInt(1000),Field(bestRow,"price"));
        Assert.Equal(new PyInt(3),Field(bestRow,"volRemaining"));
        Assert.Equal(new PyInt(plex),Field(bestRow,"typeID"));

        var escrow=Fields(await proxy.GetCharEscrow(Call(session,"GetCharEscrow",23)));
        Assert.Equal(new PyInt(1800),Field(escrow,"iskEscrow"));
        Assert.Equal(new PyInt(0),Field(escrow,"itemsEscrow"));
        var sellItem=await r.Stock(actor,2);
        var sell=Py.Dict(("stationID",new PyInt(r.Station.Id)),("typeID",new PyInt(34)),
            ("itemID",new PyLong(sellItem)),("price",new PyReal(12)),("quantity",new PyInt(2)));
        Assert.Equal(PyBool.True,await proxy.PlaceMultiSellOrder(Call(session,"PlaceMultiSellOrder",30,
            new PyList([sell]),PyBool.False,new PyInt(90))));
        var escrowWithItems=Fields(await proxy.GetCharEscrow(Call(session,"GetCharEscrow",31)));
        Assert.Equal(new PyInt(1800),Field(escrowWithItems,"iskEscrow"));
        Assert.Equal(new PyInt(2),Field(escrowWithItems,"itemsEscrow"));

        Assert.Equal(PyNone.Instance,await proxy.PlaceBuyOrder(Call(session,"PlaceBuyOrder",24,
            new PyInt(r.Station.Id),new PyInt(plex),new PyReal(1000),new PyInt(1),new PyInt(-1),
            new PyInt(1),new PyInt(0),PyBool.False)));
        Assert.Equal(3,sink.Packets.Count);
        var filledNotification=NotificationArguments(sink.Packets[2]);
        Assert.Equal("Filled",Eve.Macho.JsSemantics.PlainText(filledNotification.Items[1]));
        var filledOrders=Assert.IsType<PyList>(filledNotification.Items[0]);
        var immediateOrder=Fields(Assert.Single(filledOrders.Items));
        Assert.Equal(new PyLong(0),Field(immediateOrder,"orderID"));
        Assert.Equal(new PyInt(plex),Field(immediateOrder,"typeID"));
        Assert.Equal(new PyInt(0),Field(immediateOrder,"volRemaining"));
        var histories=Assert.IsType<PyDict>(await proxy.GetPlexHistory(Call(session,"GetPlexHistory",25)));
        var pair=Assert.IsType<PyList>(Assert.Single(histories.Entries).Value);
        Assert.Equal(2,pair.Items.Count);
        Assert.Single(Assert.IsType<PyList>(Field(Fields(pair.Items[1]),"lines")).Items);
        Assert.Single(Assert.IsType<PyList>(Field(Fields(await proxy.GetPlexNewPriceHistory(
            Call(session,"GetPlexNewPriceHistory",26))),"lines")).Items);
        var oldPlex=Fields(await proxy.GetPlexOldPriceHistory(Call(session,"GetPlexOldPriceHistory",29)));
        Assert.Empty(Assert.IsType<PyList>(Field(oldPlex,"lines")).Items);

        var order=(await r.Trading.OwnerOrdersAsync(actor,false,false)).Single(order=>order.Order.TypeId==plex);
        Assert.Equal(PyNone.Instance,await proxy.CancelCharOrder(Call(session,"CancelCharOrder",27,new PyLong(order.Order.Id))));
        var historyCached=Assert.IsType<PyObject>(MarshalCodec.Decode(MarshalCodec.Encode(
            await proxy.GetMarketOrderHistory(Call(session,"GetMarketOrderHistory",28)))));
        Assert.Equal(ObjectCaching.ResultClass,Eve.Macho.JsSemantics.PlainText(historyCached.Name));
        var historyStream=Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(historyCached.Args).Items[1]);
        var history=Fields(historyStream.Decoded ?? MarshalCodec.Decode(historyStream.Raw));
        var historyLine=Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(Field(history,"lines")).Items));
        Assert.Equal(new PyInt(3),historyLine.Items[19]);
        Assert.All(sink.Packets,packet => Assert.Equal("OnOwnOrdersChanged",NotificationName(packet)));
    }


    [PgFact]
    public async Task Market_price_history_is_scoped_to_the_callers_region()
    {
        await using var r=await TradeRig.Create();
        var otherStation=r.Stations.All.First(station=>station.RegionId!=r.Station.RegionId);
        long secondActor;
        await using (var connection=await r.Source.OpenConnectionAsync())
        await using (var transaction=await connection.BeginTransactionAsync())
        {
            secondActor=await r.Characters.InsertAsync(connection,transaction,new Eve.Characters.NewCharacter(
                1,"Other Region Trader",0,1,1,1,11,1376,1000044,null,
                otherStation.Id,otherStation.SolarSystemId,otherStation.ConstellationId,otherStation.RegionId,null,null));
            await r.WalletStore.OpenAsync(connection,transaction,secondActor,10000m,0);
            await transaction.CommitAsync();
        }
        var firstActor=await r.Actor("First Region Trader");
        var firstSession=new ClientSession(new RecordingPacketSink()) { SessionId=301 };
        firstSession.Set("charid",new PyInt(firstActor));
        var secondSession=new ClientSession(new RecordingPacketSink()) { SessionId=302 };
        secondSession.Set("charid",new PyInt(secondActor));
        var sessions=new CharacterSessions();
        sessions.Enter(firstActor,firstSession);
        sessions.Enter(secondActor,secondSession);
        var catalog=await MarketCatalog.LoadAsync(new Eve.Sde.SdeReader(r.Source));
        var proxy=new MarketProxyService(r.Book,r.Trading,r.Topology,r.Characters,r.Wallets,catalog,() => r.Now,sessions);
        const int type=34;
        await r.Book.SeedNpcAsync([
            new("ask:origin",type,false,10,1,32767,MarketLocation.From(r.Station),r.Now),
            new("ask:other",type,false,100,2,32767,MarketLocation.From(otherStation),r.Now),
        ]);
        Assert.Equal(PyNone.Instance,await proxy.PlaceBuyOrder(Call(firstSession,"PlaceBuyOrder",31,
            new PyInt(r.Station.Id),new PyInt(type),new PyReal(10),new PyInt(1),new PyInt(-1),new PyInt(1),new PyInt(0),PyBool.False)));
        Assert.Equal(PyNone.Instance,await proxy.PlaceBuyOrder(Call(secondSession,"PlaceBuyOrder",32,
            new PyInt(otherStation.Id),new PyInt(type),new PyReal(100),new PyInt(2),new PyInt(-1),new PyInt(1),new PyInt(0),PyBool.False)));

        var first=Fields(await proxy.GetNewPriceHistory(Call(firstSession,"GetNewPriceHistory",33,new PyInt(type))));
        var second=Fields(await proxy.GetNewPriceHistory(Call(secondSession,"GetNewPriceHistory",34,new PyInt(type))));
        var firstRow=Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(Field(first,"lines")).Items));
        var secondRow=Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(Field(second,"lines")).Items));
        Assert.Equal(new PyLong(1),firstRow.Items[4]);
        Assert.Equal(new PyLong(2),secondRow.Items[4]);
        var many=Assert.IsType<PyDict>(await proxy.GetHistoryForManyTypeIDs(Call(firstSession,"GetHistoryForManyTypeIDs",35,
            new PyList([new PyInt(type)]))));
        var pair=Assert.IsType<PyList>(Assert.Single(many.Entries).Value);
        var manyNew=Fields(pair.Items[1]);
        var manyRow=Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(Field(manyNew,"lines")).Items));
        Assert.Equal(new PyLong(1),manyRow.Items[4]);
    }


    [PgFact]
    public async Task Ask_summary_rpcs_are_type_ordered_and_exclude_unknown_player_structures()
    {
        await using var r=await TradeRig.Create();
        var actor=await r.Actor("Summary Trader");
        var session=new ClientSession(new RecordingPacketSink()) { SessionId=104 };
        session.Set("charid",new PyInt(actor));
        var characterSessions=new CharacterSessions();
        characterSessions.Enter(actor,session);
        var catalog=await MarketCatalog.LoadAsync(new Eve.Sde.SdeReader(r.Source));
        Assert.Contains(r.Station.Id,catalog.NpcStationIds);
        var proxy=new MarketProxyService(r.Book,r.Trading,r.Topology,r.Characters,r.Wallets,catalog,() => r.Now,characterSessions);

        var location=MarketLocation.From(r.Station);
        var remoteStation=Assert.Single(r.Stations.All.Where(s=>s.RegionId==r.Station.RegionId && s.SolarSystemId!=r.Station.SolarSystemId).Take(1));
        var remoteLocation=MarketLocation.From(remoteStation);
        await r.Book.SeedNpcAsync([
            new("summary-200-high",200,false,12m,3,32767,location,r.Now),
            new("summary-200-best",200,false,10m,2,32767,location,r.Now),
            new("summary-100-public",100,false,30m,1,32767,location,r.Now),
            new("summary-200-remote-tie",200,false,9m,6,32767,remoteLocation,r.Now),
            new("summary-300-remote",300,false,5m,7,32767,remoteLocation,r.Now),
        ]);

        var structureId=1_000_000_000_000L+actor;
        await using(var connection=await r.Source.OpenConnectionAsync())
        await using(var command=new Npgsql.NpgsqlCommand($"""
            INSERT INTO {r.MarketSchema}.orders(
                type_id,bid,price,vol_remaining,vol_entered,min_volume,range_value,station_id,
                solar_system_id,constellation_id,region_id,issued_ms,duration_days,is_npc,
                actor_id,wallet_owner_id,account_key
            ) VALUES
                ($1,false,$2,$3,$3,1,32767,$4,$5,$6,$7,$8,90,false,$9,$9,1000),
                ($10,false,$11,$12,$12,1,32767,$4,$5,$6,$7,$8,90,false,$9,$9,1000),
                ($13,false,$14,$15,$15,1,32767,$16,$5,$6,$7,$8,90,false,$9,$9,1000)
            """,connection))
        {
            command.Parameters.AddWithValue(100);
            command.Parameters.AddWithValue(1m);
            command.Parameters.AddWithValue(50L);
            command.Parameters.AddWithValue(structureId);
            command.Parameters.AddWithValue(r.Station.SolarSystemId);
            command.Parameters.AddWithValue(r.Station.ConstellationId);
            command.Parameters.AddWithValue(r.Station.RegionId);
            command.Parameters.AddWithValue(r.Now);
            command.Parameters.AddWithValue(actor);
            command.Parameters.AddWithValue(50);
            command.Parameters.AddWithValue(1m);
            command.Parameters.AddWithValue(90L);
            command.Parameters.AddWithValue(200);
            command.Parameters.AddWithValue(9m);
            command.Parameters.AddWithValue(4L);
            command.Parameters.AddWithValue(r.Station.Id);
            await command.ExecuteNonQueryAsync();
        }

        double NumberValue(PyValue value) => value switch { PyInt number => number.Value, PyLong number => number.Value, PyReal number => number.Value, _ => throw new InvalidDataException("Expected a numeric PyValue.") };
        PyDict CachedDict(PyValue value)
        {
            var cached=Assert.IsType<PyObject>(MarshalCodec.Decode(MarshalCodec.Encode(value)));
            var args=Assert.IsType<PyTuple>(cached.Args);
            var stream=Assert.IsType<PySubStream>(args.Items[1]);
            return Assert.IsType<PyDict>(stream.Decoded ?? MarshalCodec.Decode(stream.Raw));
        }
        PyValue ByType(PyDict dict,int typeId) => dict.Entries.Single(entry=>NumberValue(entry.Key)==typeId).Value;
        decimal Decimal(PyValue value) => (decimal)NumberValue(value);
        int IntegerValue(PyValue value) => checked((int)NumberValue(value));
        long LongValue(PyValue value) => checked((long)NumberValue(value));

        var groupRows=Assert.IsType<PyList>(Field(Fields(proxy.GetMarketGroups(Call(session,"GetMarketGroups",40))),"lines")).Items;
        var groupIds=groupRows.Select(row=>IntegerValue(Assert.IsType<PyTuple>(row).Items[1])).ToArray();
        Assert.Equal(groupIds.OrderBy(id=>id),groupIds);

        var station=CachedDict(await proxy.GetStationAsks(Call(session,"GetStationAsks",41)));
        var system=CachedDict(await proxy.GetSystemAsks(Call(session,"GetSystemAsks",42)));
        var region=Assert.IsType<PyDict>(await proxy.GetRegionBest(Call(session,"GetRegionBest",43)));
        Assert.Equal(new[]{100,200},station.Entries.Select(entry=>IntegerValue(entry.Key)));
        Assert.Equal(new[]{100,200},system.Entries.Select(entry=>IntegerValue(entry.Key)));
        Assert.Equal(new[]{100,200,300},region.Entries.Select(entry=>IntegerValue(entry.Key)));

        foreach(var dict in new[]{station,system})
        {
            var type100=Assert.IsType<PyTuple>(ByType(dict,100));
            Assert.Equal(30m,Decimal(type100.Items[0]));
            Assert.Equal(1m,Decimal(type100.Items[1]));
            Assert.Equal(100,IntegerValue(type100.Items[2]));
            Assert.Equal(r.Station.Id,LongValue(type100.Items[3]));

            var type200=Assert.IsType<PyTuple>(ByType(dict,200));
            Assert.Equal(9m,Decimal(type200.Items[0]));
            Assert.Equal(9m,Decimal(type200.Items[1]));
            Assert.Equal(200,IntegerValue(type200.Items[2]));
            Assert.Equal(r.Station.Id,LongValue(type200.Items[3]));
        }

        var region100=Fields(ByType(region,100));
        Assert.Equal(30m,Decimal(Field(region100,"price")));
        Assert.Equal(1m,Decimal(Field(region100,"volRemaining")));
        Assert.Equal(100,IntegerValue(Field(region100,"typeID")));
        Assert.Equal(r.Station.Id,LongValue(Field(region100,"stationID")));
        var region200=Fields(ByType(region,200));
        Assert.Equal(9m,Decimal(Field(region200,"price")));
        Assert.Equal(15m,Decimal(Field(region200,"volRemaining")));
        Assert.Equal(200,IntegerValue(Field(region200,"typeID")));
        Assert.Equal(Math.Min(r.Station.Id,remoteStation.Id),LongValue(Field(region200,"stationID")));
        var region300=Fields(ByType(region,300));
        Assert.Equal(5m,Decimal(Field(region300,"price")));
        Assert.Equal(7m,Decimal(Field(region300,"volRemaining")));
        Assert.Equal(300,IntegerValue(Field(region300,"typeID")));
        Assert.Equal(remoteStation.Id,LongValue(Field(region300,"stationID")));
    }
}
