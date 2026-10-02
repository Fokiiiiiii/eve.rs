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
        Assert.Equal(new PyInt(0),line.Items[14]);
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
        Assert.Equal("OnOwnOrdersChanged",NotificationName(Assert.Single(sink.Packets)));
        var own=(await r.Book.BookAsync(r.Station.RegionId,34,r.Now)).Asks.Single(o => !o.IsNpc);
        Assert.Equal(PyNone.Instance,await proxy.CancelCharOrder(Call(session,"CancelCharOrder",7,new PyLong(own.Id))));
        Assert.Equal(2,sink.Packets.Count);
        Assert.All(sink.Packets,packet => Assert.Equal("OnOwnOrdersChanged",NotificationName(packet)));
        Assert.Equal(5,Assert.Single(await r.Items.InLocationAsync(r.Station.Id,actor)).Quantity);
    }
}
