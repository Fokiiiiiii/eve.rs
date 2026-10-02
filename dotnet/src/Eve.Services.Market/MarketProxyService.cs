using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Market;
using Eve.Wallet;

namespace Eve.Services.Market;

[Service("marketProxy")]
public sealed partial class MarketProxyService(MarketStore store,MarketTrading trading,MarketTopology topology,
    PgCharacterStore characters,WalletTransactions wallets,MarketCatalog catalog,Func<long> now,CharacterSessions? characterSessions=null)
{
    private static long Integer(PyValue v,long fallback = 0) => v switch
    {
        PyInt n => n.Value,PyLong n => n.Value,PyReal n when double.IsFinite(n.Value) && n.Value>=long.MinValue && n.Value<=long.MaxValue => (long)n.Value,_ => fallback,
    };
    private static decimal Money(PyValue v) => v switch
    {
        PyInt n => n.Value,PyLong n => n.Value,PyReal n when double.IsFinite(n.Value) && Math.Abs(n.Value)<=(double)decimal.MaxValue =>
            (decimal)MarketRules.RoundIsk(n.Value),_ => 0m,
    };
    private static IReadOnlyList<PyValue> Sequence(PyValue v) => v switch { PyList l => l.Items,PyTuple t => t.Items,_ => [] };
    private static PyValue Field(PyValue v,string name)
    {
        var dict=v switch { PyDict d => d,PyObject { Args:PyDict d } => d,_ => null };
        return dict?.Entries.FirstOrDefault(e => JsSemantics.PlainText(e.Key)==name).Value ?? PyNone.Instance;
    }
    private static string Key(ServiceCall c,string action) => $"rpc:{c.Session.SessionId}:{Integer(c.CallId)}:{c.Session.CharacterId}:{action}";
    private static long Actor(ServiceCall c) => c.Session.CharacterId ?? throw MachoErrors.ThrowUserError("CustomInfo",("info",new PyStr("A character is required.")));
    private static MachoWrappedException Error(Exception e) => e switch
    {
        MoneyLimitException => MachoErrors.ThrowUserError("CustomInfo",("info",new PyStr("BALANCE_LIMIT_EXCEEDED"))),
        InsufficientFundsException => MachoErrors.ThrowUserError("CustomInfo",("info",new PyStr("Not enough ISK."))),
        _ => MachoErrors.ThrowUserError("CustomInfo",("info",new PyStr(e.Message))),
    };
    private async Task Publish(MarketReceipt receipt)
    {
        // A successful replay announces the current balance, not the original receipt's historical balance.
        foreach (var account in receipt.ChangedWallets)
            if (await wallets.AccountBalanceAsync(account) is { } balance)
                wallets.AnnounceCommitted(new(account.OwnerId,account.AccountKey,balance));

        var changes=receipt.ChangedOrders;
        if (changes.Count==0 && receipt.ChangedOrder is { } legacyOrder)
            changes=[new(legacyOrder,receipt.ChangedOrderReason ?? "Modified",[legacyOrder.ActorId])];
        foreach (var change in changes)
        {
            var orderIsCorp=change.Order.WalletOwnerId != change.Order.ActorId;
            foreach (var characterId in change.RecipientCharacterIds.Distinct())
                characterSessions?.Of(characterId)?.SendNotification("OnOwnOrdersChanged","charid",
                    Py.List(OrderNotificationEntry(change.Order)),new PyStr(change.Reason),new PyInt(orderIsCorp ? 1 : 0));
        }
    }

    private static PyValue OrderNotificationEntry(MarketOwnedOrder owned)
    {
        var order=owned.Order;
        var ownerId=owned.WalletOwnerId != owned.ActorId ? owned.WalletOwnerId : owned.ActorId;
        return Py.KeyVal(
            ("orderID",new PyLong(order.Id)),
            ("typeID",new PyInt(order.TypeId)),
            ("charID",new PyInt(ownerId)),
            ("regionID",new PyInt(order.Location.RegionId)),
            ("stationID",new PyInt(order.Location.StationId)),
            ("range",new PyInt(order.Range)),
            ("bid",new PyInt(order.Bid ? 1 : 0)),
            ("price",Py.Number(MarketRules.RoundIsk((double)order.Price))),
            ("volEntered",new PyInt(order.VolumeEntered)),
            ("volRemaining",new PyInt(order.VolumeRemaining)),
            ("issueDate",new PyLong(Py.FileTimeFromUnixMs(order.IssuedMs))),
            ("minVolume",new PyInt(order.MinimumVolume)),
            ("contraband",new PyInt(0)),
            ("duration",new PyInt(order.Duration)),
            ("isCorp",new PyInt(owned.WalletOwnerId != owned.ActorId ? 1 : 0)),
            ("solarSystemID",new PyInt(order.Location.SolarSystemId)),
            ("escrow",Py.Number(MarketRules.RoundIsk((double)owned.Escrow))));
    }
    [Method("StartupCheck")]
    public PyValue StartupCheck(ServiceCall call) => PyNone.Instance;

    [Method("GetMarketGroups")]
    public PyValue GetMarketGroups(ServiceCall call) => Py.Rowset(
        ["parentGroupID","marketGroupID","marketGroupName","description","graphicID","hasTypes","iconID","dataID","marketGroupNameID","descriptionID"],
        catalog.Groups.Select(group => (PyValue)Py.Tuple(
            group.ParentId is { } parent ? new PyInt(parent) : PyNone.Instance,new PyInt(group.Id),new PyStr(group.Name),
            new PyStr(group.Description),PyNone.Instance,group.HasTypes ? PyBool.True : PyBool.False,
            group.IconId is { } icon ? new PyInt(icon) : PyNone.Instance,new PyInt(group.Id),PyNone.Instance,PyNone.Instance)));


    [Method("GetOldPriceHistory")]
    public async ValueTask<PyValue> GetOldPriceHistory(ServiceCall call)
    {
        var type=(int)Integer(call.Arg(0));
        if(type<=0) return MarketHistoryWire.Rowset([]);
        var history=await store.HistoriesAsync([type],call.Cancellation);
        return MarketHistoryWire.Pair(history[type]).Old;
    }

    [Method("GetNewPriceHistory")]
    public async ValueTask<PyValue> GetNewPriceHistory(ServiceCall call)
    {
        var type=(int)Integer(call.Arg(0));
        if(type<=0) return MarketHistoryWire.Rowset([]);
        var history=await store.HistoriesAsync([type],call.Cancellation);
        return MarketHistoryWire.Pair(history[type]).New;
    }

    [Method("GetHistoryForManyTypeIDs")]
    public async ValueTask<PyValue> GetHistoryForManyTypeIDs(ServiceCall call)
    {
        var typeIds=Sequence(call.Arg(0)).Select(value => value switch
        {
            PyInt n when n.Value>0 && n.Value<=int.MaxValue => (int)n.Value,
            PyLong n when n.Value>0 && n.Value<=int.MaxValue => (int)n.Value,
            PyReal n when double.IsFinite(n.Value) && n.Value>0 && n.Value<=int.MaxValue && n.Value==Math.Truncate(n.Value) => (int)n.Value,
            _ => 0,
        }).Where(id=>id>0).Distinct().ToArray();
        if(typeIds.Length==0) return new PyDict([]);
        var histories=await store.HistoriesAsync(typeIds,call.Cancellation);
        return new PyDict([.. typeIds.Select(typeId =>
        {
            var pair=MarketHistoryWire.Pair(histories[typeId]);
            return new KeyValuePair<PyValue,PyValue>(new PyInt(typeId),new PyList([pair.Old,pair.New]));
        })]);
    }

    [Method("GetOrders")]
    public async ValueTask<PyValue> GetOrders(ServiceCall call)
    {
        var type=(int)Integer(call.Arg(0));
        var c=call.Session.CharacterId is { } id ? await characters.GetAsync(id,call.Cancellation) : null;
        var viewer=c is null ? default : new MarketLocation(c.StationId,c.SolarSystemId,c.ConstellationId,c.RegionId);
        var book=c is null || type<=0 ? new MarketBook([],[]) : await store.BookAsync(c.RegionId,type,now(),call.Cancellation);
        return ObjectCaching.MethodResult(Py.Tuple(MarketWire.Orders(book.Asks,topology,viewer),MarketWire.Orders(book.Bids,topology,viewer)),
            now(),sessionInfo:"solarsystemid2");
    }


    private static PyValue OwnerOrderRowset(IReadOnlyList<MarketOwnedOrder> orders,bool corporation)
    {
        var columns=new[]
        {
            "orderID","typeID","charID","regionID","stationID","range","bid","price","volEntered","volRemaining",
            "issueDate","minVolume","contraband","duration","isCorp","solarSystemID","escrow","constellationID",
            "keyID","orderState","lastStateChange",
        };
        var rows=orders.Where(order=>order.State==0).Select(owned =>
        {
            var order=owned.Order;
            var escrow=order.Bid ? MarketRules.RoundIsk((double)order.Price*order.VolumeRemaining) : 0d;
            return (PyValue)Py.Tuple(
                new PyLong(order.Id),new PyInt(order.TypeId),new PyInt(corporation ? 0 : owned.ActorId),
                new PyInt(order.Location.RegionId),new PyInt(order.Location.StationId),new PyInt(order.Range),
                new PyInt(order.Bid ? 1 : 0),Py.Number(MarketRules.RoundIsk((double)order.Price)),
                new PyInt(order.VolumeEntered),new PyInt(order.VolumeRemaining),
                new PyLong(Py.FileTimeFromUnixMs(order.IssuedMs)),new PyInt(order.MinimumVolume),new PyInt(0),
                new PyInt(order.Duration),new PyInt(corporation ? 1 : 0),new PyInt(order.Location.SolarSystemId),
                Py.Number(escrow),new PyInt(order.Location.ConstellationId),new PyInt(1000),new PyInt(owned.State),
                owned.State==0 ? PyNone.Instance : new PyLong(Py.FileTimeFromUnixMs(
                    owned.LastStateChangeMs>0 ? owned.LastStateChangeMs : order.IssuedMs)));
        });
        return Py.Rowset(columns,rows);
    }

    [Method("GetCharOrders")]
    public async ValueTask<PyValue> GetCharOrders(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor) return OwnerOrderRowset([],false);
        try
        {
            var orders=await trading.OwnerOrdersAsync(actor,false,false,call.Cancellation);
            return ObjectCaching.MethodResult(OwnerOrderRowset(orders,false),now(),sessionInfo:"charid");
        }
        catch (Exception e) when (e is MarketError or Eve.Corporations.CorporationError) { throw Error(e); }
    }

    [Method("GetCorporationOrders")]
    public async ValueTask<PyValue> GetCorporationOrders(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor) return OwnerOrderRowset([],true);
        try
        {
            var orders=await trading.OwnerOrdersAsync(actor,true,false,call.Cancellation);
            return ObjectCaching.MethodResult(OwnerOrderRowset(orders,true),now(),sessionInfo:"corpid");
        }
        catch (Exception e) when (e is MarketError or Eve.Corporations.CorporationError) { throw Error(e); }
    }
    [Method("PlaceBuyOrder")]
    public async ValueTask<PyValue> PlaceBuyOrder(ServiceCall call)
    {
        var request=new MarketRequest(Actor(call),(int)Integer(call.Arg(1)),true,Money(call.Arg(2)),Integer(call.Arg(3)),Integer(call.Arg(0)),
            (int)Integer(call.Arg(6)),(int)Integer(call.Arg(4),-1),Integer(call.Arg(5),1),UseCorporation:JsSemantics.Truthy(call.Arg(7)),
            ExpectedBrokerFee:call.Arg(8) is PyReal quote ? quote.Value : call.Arg(8) is PyInt whole ? whole.Value : null);
        try
        {
            var receipt=await trading.PlaceAsync(request,Key(call,"buy"),call.Cancellation);
            if (receipt.OrderId is null && receipt.FilledQuantity==0)
                throw MachoErrors.ThrowUserError("CustomInfo",("info",new PyStr("No matching sell orders were available at the requested price.")));
            await Publish(receipt);
            return PyNone.Instance;
        }
        catch (Exception e) when (e is MarketError or MoneyLimitException or InsufficientFundsException or Eve.Corporations.CorporationError) { throw Error(e); }
    }
    [Method("PlaceMultiSellOrder")]
    public async ValueTask<PyValue> PlaceMultiSellOrder(ServiceCall call)
    {
        var actor=Actor(call);
        var index=0;
        var traded=false;
        foreach (var entry in Sequence(call.Arg(0)))
        {
            var request=new MarketRequest(actor,(int)Integer(Field(entry,"typeID")),false,Money(Field(entry,"price")),Integer(Field(entry,"quantity")),
                Integer(Field(entry,"stationID")),(int)Integer(call.Arg(2)),ItemId:Integer(Field(entry,"itemID")),UseCorporation:JsSemantics.Truthy(call.Arg(1)),
                ExpectedBrokerFee:call.Arg(3) is PyReal quote ? quote.Value : call.Arg(3) is PyInt whole ? whole.Value : null);
            try
            {
                var receipt=await trading.PlaceAsync(request,Key(call,"sell:"+index++),call.Cancellation);
                traded |= receipt.OrderId is not null || receipt.FilledQuantity>0;
                await Publish(receipt);
            }
            catch (Exception e) when (e is MarketError or MoneyLimitException or InsufficientFundsException or Eve.Corporations.CorporationError) { throw Error(e); }
        }
        return traded ? PyBool.True : PyBool.False;
    }
    [Method("CancelCharOrder")]
    public async ValueTask<PyValue> CancelCharOrder(ServiceCall call)
    {
        try
        {
            var receipt=await trading.CancelAsync(Actor(call),Integer(call.Arg(0)),Key(call,"cancel"),call.Cancellation);
            await Publish(receipt);
            return PyNone.Instance;
        }
        catch (Exception e) when (e is MarketError or MoneyLimitException or InsufficientFundsException or Eve.Corporations.CorporationError) { throw Error(e); }
    }

[Method("ModifyCharOrder")]
    public async ValueTask<PyValue> ModifyCharOrder(ServiceCall call)
    {
        try
        {
            foreach (var expired in await trading.ExpireDueAsync(call.Cancellation))
                await Publish(expired);
            var receipt=await trading.ModifyAsync(Actor(call),Integer(call.Arg(0)),Money(call.Arg(1)),Key(call,"modify"),call.Cancellation);
            await Publish(receipt);
            return PyNone.Instance;
        }
        catch (Exception e) when (e is MarketError or MoneyLimitException or InsufficientFundsException or Eve.Corporations.CorporationError) { throw Error(e); }
    }
}
