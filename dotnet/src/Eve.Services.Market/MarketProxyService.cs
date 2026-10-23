using System.Collections.Concurrent;
using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Market;
using Eve.Wallet;

namespace Eve.Services.Market;

[Service("marketProxy")]
public sealed partial class MarketProxyService(MarketStore store,MarketTrading trading,MarketTopology topology,
    PgCharacterStore characters,Eve.Corporations.CorporationStore corporations,WalletTransactions wallets,MarketCatalog catalog,Func<long> now,MarketExpiryWorker expiry,CharacterSessions? characterSessions=null)
{
    private static long Integer(PyValue v,long fallback = 0) => v switch
    {
        PyInt n => n.Value,
        PyLong n => n.Value,
        PyReal n when double.IsFinite(n.Value) && n.Value >= -9_223_372_036_854_775_808d &&
            n.Value < 9_223_372_036_854_775_808d && Math.Truncate(n.Value) == n.Value => (long)n.Value,
        _ => fallback,
    };

    private static int Int32(PyValue value, long fallback = 0)
    {
        var numeric = value switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            PyReal real when double.IsFinite(real.Value) &&
                real.Value == Math.Truncate(real.Value) &&
                real.Value >= int.MinValue && real.Value <= int.MaxValue => (long)real.Value,
            PyReal => throw Error(new MarketError("MARKET_ARGUMENT_OUT_OF_RANGE")),
            _ => fallback,
        };
        if (numeric is < int.MinValue or > int.MaxValue)
            throw Error(new MarketError("MARKET_ARGUMENT_OUT_OF_RANGE"));
        return (int)numeric;
    }

    private static decimal Money(PyValue v) => v switch
    {
        PyInt n => n.Value,PyLong n => n.Value,PyReal n when double.IsFinite(n.Value) && Math.Abs(n.Value)<=MarketRules.MaximumOrderPrice =>
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
        MarketError market => MarketErrorPayload(market),
        Eve.Corporations.CorporationError { Message: "CORPORATION_ACCESS_DENIED" } => MachoErrors.ThrowUserError("CustomInfo",
            ("info",new PyStr("Your character does not hold the market rights of that corporation."))),
        _ => MachoErrors.ThrowUserError("CustomInfo",("info",new PyStr(e.Message))),
    };

    /// <summary>
    /// A market refusal as the client raises it. A code the client has a UserError label for is sent as that label with its arguments
    /// (MktBrokersFeeUnexpected2 carries the fee percentages, sellMulti.py:448-456, buyThisTypeWindow.py:693); every other code is a
    /// CustomInfo sentence, not the raw code.
    /// </summary>
    private static MachoWrappedException MarketErrorPayload(MarketError error) => error.Code switch
    {
        "StructureMarketDenied" => MachoErrors.ThrowUserError(error.Code,
            [("structureName",(PyValue)new PyStr(Convert.ToString(error.Arguments[0].Value,System.Globalization.CultureInfo.InvariantCulture) ?? "Structure"))]),
        "MktInvalidMinVolumeCannotExceedQuantity" => MachoErrors.ThrowUserError(error.Code),
        "MARKET_BULK_LIMIT_EXCEEDED" => MachoErrors.ThrowUserError(error.Code),
        "MARKET_TYPE_ID_INVALID" => MachoErrors.ThrowUserError(error.Code),
        "MARKET_BULK_ARGUMENT_INVALID" => MachoErrors.ThrowUserError(error.Code),
        "MktBrokersFeeUnexpected2" => MachoErrors.ThrowUserError(error.Code,
            [.. error.Arguments.Select(argument => (argument.Name,(PyValue)Py.Number(Convert.ToDouble(argument.Value,System.Globalization.CultureInfo.InvariantCulture))))]),
        "MARKET_MODIFY_TOO_SOON" => MachoErrors.ThrowUserError("CustomInfo",("info",new PyStr(
            $"That order was placed or modified less than five minutes ago; it can be modified in {Convert.ToInt64(error.Arguments[0].Value,System.Globalization.CultureInfo.InvariantCulture)} seconds."))),
        _ => MachoErrors.ThrowUserError("CustomInfo",("info",new PyStr(MarketErrorText(error.Code)))),
    };

    private static string MarketErrorText(string code) => code switch
    {
        "MARKET_REMOTE_SKILL_REQUIRED" => "That station is beyond the trading range your skills allow for this order.",
        "MARKET_VISIBILITY_REQUIRED" => "Your Visibility skill does not allow a buy order with that range.",
        "MARKET_ORDER_LIMIT" => "You have reached your limit of open market orders.",
        "MARKET_ITEM_CUSTODY_INVALID" => "The items to sell are not in this station's hangar.",
        "MARKET_ESCROW_INCONSISTENT" => "The order's escrow does not match its book, so nothing was changed.",
        "MARKET_ORDER_NOT_ACTIVE" => "That order is no longer active.",
        "MARKET_ORDER_NOT_FOUND" => "That order does not exist.",
        "MARKET_ORDER_ACCESS_DENIED" => "You may not change that order.",
        "MARKET_ORDER_NOT_EXPIRED" => "That order has not expired yet.",
        "MARKET_OPERATION_CONFLICT" => "The same request was sent with different terms.",
        "MARKET_SETTLEMENT_RECONCILIATION_REQUIRED" => "The market could not settle that trade, so nothing was changed.",
        "MARKET_STATION_NOT_FOUND" => "That station has no market.",
        "CHARACTER_NOT_FOUND" => "The character is not known to the market.",
        "INVALID_MARKET_PRICE" => "That price is not a valid order price.",
        "MARKET_PRICE_TICK" => "Order prices have at most four significant figures.",
        "MARKET_ORDER_STALE" => "That order has changed since it was shown. Reload the orders and try again.",
        "MARKET_DUPLICATE_PLACEMENT" => "An identical order was placed a moment ago, so it was not placed again.",
        "INVALID_MARKET_DURATION_OR_RANGE" => "That duration or range is not offered for a market order.",
        "MARKET_ARGUMENT_OUT_OF_RANGE" => "One of the market values is outside the supported range.",
        _ => "The market refused the request.",
    };
    private readonly MarketNotifier notifier = new(wallets,characterSessions);
    private Task Publish(MarketReceipt receipt) => notifier.PublishAsync(receipt);

    internal static PyValue OrderNotificationEntry(MarketOwnedOrder owned)
    {
        var order=owned.Order;
        var ownerId=owned.WalletOwnerId != owned.ActorId ? owned.WalletOwnerId : owned.ActorId;
        return Py.KeyVal(
            ("orderID",new PyLong(order.Id)),
            ("typeID",new PyInt(order.TypeId)),
            ("charID",new PyInt(ownerId)),
            ("regionID",new PyInt(order.Location.RegionId)),
            ("stationID",new PyLong(order.Location.StationId)),
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
            ("keyID",new PyInt(owned.AccountKey)),
            ("solarSystemID",new PyInt(order.Location.SolarSystemId)),
            ("escrow",Py.Number(MarketRules.RoundIsk((double)owned.Escrow))));
    }
    private const int PlexTypeId = 44992;
    private const int MaxBuyMultipleItems = 500;
    private const int MaxMultiSellOrderEntries = 500;
    private const int MaxHistoryTypeIds = 500;
    /// <summary>Q14: the window in which an identical placement from the same session is refused.</summary>
    private const long DuplicateWindowMs = 5_000;
    private readonly ConcurrentDictionary<long,(MarketRequest Request,long AtMs)> _lastPlacement = new();

    /// <summary>
    /// Q14: a placement identical to the session's last successful one, within five seconds, is refused. The client sends no operation key
    /// (each send has a new call id), so a repeated send is what this sees: a documented stop-gap until the client has a key.
    /// </summary>
    private void RefuseRepeatedPlacement(ServiceCall call,MarketRequest request)
    {
        if (_lastPlacement.TryGetValue(call.Session.SessionId,out var last) && last.Request == request && now() - last.AtMs < DuplicateWindowMs)
            throw Error(new MarketError("MARKET_DUPLICATE_PLACEMENT"));
    }

    private void RecordPlacement(ServiceCall call,MarketRequest request) => _lastPlacement[call.Session.SessionId] = (request,now());

    private static long FileTimeToUnixMs(PyValue value) => (Integer(value) - 116444736000000000L) / 10_000;

    [Method("StartupCheck")]
    public PyValue StartupCheck(ServiceCall call) => PyNone.Instance;

    [Method("GetMarketGroups")]
    public PyValue GetMarketGroups(ServiceCall call) => Py.Rowset(
        ["parentGroupID","marketGroupID","marketGroupName","description","graphicID","hasTypes","iconID","dataID","marketGroupNameID","descriptionID"],
        catalog.Groups.Select(group => (PyValue)Py.Tuple(
            group.ParentId is { } parent ? new PyInt(parent) : PyNone.Instance,new PyInt(group.Id),new PyStr(group.Name),
            new PyStr(group.Description),PyNone.Instance,group.HasTypes ? PyBool.True : PyBool.False,
            group.IconId is { } icon ? new PyInt(icon) : PyNone.Instance,new PyInt(group.Id),PyNone.Instance,PyNone.Instance)));

    private async Task<IReadOnlyList<(MarketOrder Order,long Quantity)>> VisibleSummaries(
        long actorId,IReadOnlyList<(MarketOrder Order,long Quantity)> rows,CancellationToken token)
    {
        var access=new Dictionary<long,bool>();
        var visible=new List<(MarketOrder Order,long Quantity)>(rows.Count);
        foreach (var row in rows)
        {
            var locationId=row.Order.Location.StationId;
            if (!access.TryGetValue(locationId,out var allowed))
                access[locationId]=allowed=await trading.CanViewAtAsync(actorId,locationId,token);
            if (allowed) visible.Add(row);
        }
        return visible.GroupBy(row => row.Order.TypeId).Select(group =>
        {
            var best=group.OrderBy(row => row.Order.Price).ThenBy(row => row.Order.IssuedMs).ThenBy(row => row.Order.Id).First();
            var quantity=group.Aggregate(0L,(total,row) => long.MaxValue-total<row.Quantity ? long.MaxValue : total+row.Quantity);
            return (Order:best.Order,Quantity:quantity);
        }).ToArray();
    }

    private async Task<IReadOnlyList<MarketOrder>> VisibleOrders(
        long actorId,IReadOnlyList<MarketOrder> rows,CancellationToken token)
    {
        var access=new Dictionary<long,bool>();
        var visible=new List<MarketOrder>(rows.Count);
        foreach (var row in rows)
        {
            var locationId=row.Location.StationId;
            if (!access.TryGetValue(locationId,out var allowed))
                access[locationId]=allowed=await trading.CanViewAtAsync(actorId,locationId,token);
            if (allowed) visible.Add(row);
        }
        return visible;
    }

    private static long MarketLocationId(ClientSession session,long fallback)
    {
        var stationId=session.GetInt("stationid") ?? 0;
        return stationId>0 ? stationId : session.GetInt("structureid") ?? fallback;
    }

    [Method("GetStationAsks")]
    public async ValueTask<PyValue> GetStationAsks(ServiceCall call)
    {
        var actor=call.Session.CharacterId;
        var character=actor is { } id ? await characters.GetAsync(id,call.Cancellation) : null;
        var locationId=MarketLocationId(call.Session,character?.StationId ?? 0);
        IReadOnlyList<(MarketOrder Order,long Quantity)> rows=[];
        if(actor is { } characterId && locationId>0)
        {
            try
            {
                var location=await trading.RequireMarketLocationAsync(characterId,locationId,call.Cancellation);
                if(location.RegionId>0)
                    rows=await store.StationAsksAsync(location.RegionId,locationId,now(),call.Cancellation);
            }
            catch(MarketError error) { throw Error(error); }
        }
        var entries=rows.Select(row=>new KeyValuePair<PyValue,PyValue>(
            new PyInt(row.Order.TypeId),MarketWire.BestByOrderTuple(row.Order,row.Quantity))).ToArray();
        return ObjectCaching.MethodResult(new PyDict(entries),now(),sessionInfo:"stationid");
    }

    [Method("GetSystemAsks")]
    public async ValueTask<PyValue> GetSystemAsks(ServiceCall call)
    {
        var actor=call.Session.CharacterId;
        var character=actor is { } id ? await characters.GetAsync(id,call.Cancellation) : null;
        IReadOnlyList<(MarketOrder Order,long Quantity)> rows=[];
        if(actor is { } characterId && character is not null && character.SolarSystemId>0 && character.RegionId>0)
        {
            rows=await store.SystemAsksAsync(character.RegionId,character.SolarSystemId,now(),call.Cancellation);
            rows=await VisibleSummaries(characterId,rows,call.Cancellation);
        }
        var entries=rows.Select(row=>new KeyValuePair<PyValue,PyValue>(
            new PyInt(row.Order.TypeId),MarketWire.BestByOrderTuple(row.Order,row.Quantity))).ToArray();
        return ObjectCaching.MethodResult(new PyDict(entries),now(),sessionInfo:"solarsystemid2");
    }

    [Method("GetRegionBest")]
    public async ValueTask<PyValue> GetRegionBest(ServiceCall call)
    {
        var region=await MarketRegion(call);
        var rows=region>0 && call.Session.CharacterId is { } actor
            ? await VisibleSummaries(actor,await store.RegionBestAsync(region,now(),call.Cancellation),call.Cancellation)
            : [];
        var entries=rows.Select(row=>new KeyValuePair<PyValue,PyValue>(
            new PyInt(row.Order.TypeId),MarketWire.BestByOrder(row.Order,row.Quantity))).ToArray();
        return new PyDict(entries);
    }

    private async Task<int> MarketRegion(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor) return 0;
        var character=await characters.GetAsync(actor,call.Cancellation);
        return character?.RegionId ?? 0;
    }

    private Task<IReadOnlyList<MarketHistory>> HistoryForRegion(int region,int type,CancellationToken token) =>
        region>0 && type>0 ? store.HistoryAsync(region,type,0,token) : Task.FromResult<IReadOnlyList<MarketHistory>>([]);

    [Method("GetOldPriceHistory")]
    public async ValueTask<PyValue> GetOldPriceHistory(ServiceCall call)
    {
        var type=Int32(call.Arg(0));
        return MarketHistoryWire.Pair(now(),await HistoryForRegion(await MarketRegion(call),type,call.Cancellation)).Old;
    }

    [Method("GetNewPriceHistory")]
    public async ValueTask<PyValue> GetNewPriceHistory(ServiceCall call)
    {
        var type=Int32(call.Arg(0));
        return MarketHistoryWire.Pair(now(),await HistoryForRegion(await MarketRegion(call),type,call.Cancellation)).New;
    }

    [Method("GetHistoryForManyTypeIDs")]
    public async ValueTask<PyValue> GetHistoryForManyTypeIDs(ServiceCall call)
    {
        var request = call.Arg(0);
        var requestedTypes = request switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            PyObjectEx
            {
                Kind: ObjectExKind.Ex1,
                Header: PyTuple { Items: [PyToken { Value: "__builtin__.set" }, PyTuple { Items: [PyList setItems] }] },
                List.Count: 0,
                Dict.Count: 0,
            } => setItems.Items,
            _ => throw Error(new MarketError("MARKET_TYPE_ID_INVALID")),
        };
        if (requestedTypes.Count > MaxHistoryTypeIds)
            throw Error(new MarketError("MARKET_BULK_LIMIT_EXCEEDED"));
        var typeIds = requestedTypes.Select(value => value switch
        {
            PyInt n when n.Value is > 0 and <= int.MaxValue => (int)n.Value,
            PyLong n when n.Value is > 0 and <= int.MaxValue => (int)n.Value,
            PyReal n when double.IsFinite(n.Value) && n.Value > 0 && n.Value <= int.MaxValue && n.Value == Math.Truncate(n.Value) => (int)n.Value,
            _ => throw Error(new MarketError("MARKET_TYPE_ID_INVALID")),
        }).Distinct().ToArray();
        if(typeIds.Length==0) return new PyDict([]);
        var region=await MarketRegion(call);
        var entries=new List<KeyValuePair<PyValue,PyValue>>(typeIds.Length);
        foreach(var typeId in typeIds)
        {
            var pair=MarketHistoryWire.Pair(now(),await HistoryForRegion(region,typeId,call.Cancellation));
            entries.Add(new(new PyInt(typeId),new PyList([pair.Old,pair.New])));
        }
        return new PyDict(entries);
    }

    [Method("GetOrders")]
    public async ValueTask<PyValue> GetOrders(ServiceCall call)
    {
        var type=Int32(call.Arg(0));
        var actor=call.Session.CharacterId;
        var c=actor is { } id ? await characters.GetAsync(id,call.Cancellation) : null;
        var locationId=MarketLocationId(call.Session,c?.StationId ?? 0);
        var viewer=c is null ? default : new MarketLocation(locationId,c.SolarSystemId,c.ConstellationId,c.RegionId);
        var book=c is null || type<=0 ? new MarketBook([],[]) : await store.BookAsync(c.RegionId,type,now(),call.Cancellation);
        if (actor is { } actorId)
            book=new MarketBook(await VisibleOrders(actorId,book.Asks,call.Cancellation),await VisibleOrders(actorId,book.Bids,call.Cancellation));
        return ObjectCaching.MethodResult(Py.Tuple(MarketWire.Orders(book.Asks,topology,viewer),MarketWire.Orders(book.Bids,topology,viewer)),
            now(),sessionInfo:"solarsystemid2");
    }

    private static PyValue OwnerOrderRowset(IReadOnlyList<MarketOwnedOrder> orders,bool? corporation)
    {
        var columns=new[]
        {
            "orderID","typeID","charID","regionID","stationID","range","bid","price","volEntered","volRemaining",
            "issueDate","minVolume","contraband","duration","isCorp","solarSystemID","escrow","constellationID",
            "keyID","orderState","lastStateChange",
        };
        var rows=orders.Select(owned =>
        {
            var order=owned.Order;
            var isCorp=corporation ?? owned.WalletOwnerId!=owned.ActorId;
            var escrow=order.Bid ? MarketRules.RoundIsk((double)order.Price*order.VolumeRemaining) : 0d;
            return (PyValue)Py.Tuple(
                new PyLong(order.Id),new PyInt(order.TypeId),new PyInt(isCorp ? 0 : owned.ActorId),
                new PyInt(order.Location.RegionId),new PyLong(order.Location.StationId),new PyInt(order.Range),
                new PyInt(order.Bid ? 1 : 0),Py.Number(MarketRules.RoundIsk((double)order.Price)),
                new PyInt(order.VolumeEntered),new PyInt(order.VolumeRemaining),
                new PyLong(Py.FileTimeFromUnixMs(order.IssuedMs)),new PyInt(order.MinimumVolume),new PyInt(0),
                new PyInt(order.Duration),new PyInt(isCorp ? 1 : 0),new PyInt(order.Location.SolarSystemId),
                Py.Number(escrow),new PyInt(order.Location.ConstellationId),new PyInt(owned.AccountKey),new PyInt(owned.State),
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
            if (await characters.GetAsync(actor,call.Cancellation) is not { } character ||
                await corporations.GetAsync(character.CorporationId,call.Cancellation) is not { Deleted:false } corporation)
                return OwnerOrderRowset([],true);
            if (corporation.IsNpc)
                return OwnerOrderRowset([],true);
            if (!await corporations.CanReadCorporationMarketOrdersAsync(actor,call.Cancellation))
                throw new Eve.Corporations.CorporationError("CORPORATION_ACCESS_DENIED");
            var orders=await trading.OwnerOrdersAsync(actor,true,false,call.Cancellation);
            return ObjectCaching.MethodResult(OwnerOrderRowset(orders,true),now(),sessionInfo:"corpid");
        }
        catch (Exception e) when (e is MarketError or Eve.Corporations.CorporationError) { throw Error(e); }
    }

    [Method("GetCharEscrow")]
    public async ValueTask<PyValue> GetCharEscrow(ServiceCall call)
    {
        if(call.Session.CharacterId is not { } actor)
            return Py.KeyVal(("iskEscrow",Py.Number(0)),("itemsEscrow",Py.Number(0)));
        try
        {
            await expiry.SweepAsync(call.Cancellation, readTriggered: true);
            var orders=await trading.OwnerOrdersAsync(actor,false,false,call.Cancellation);
            var isk=orders.Where(order=>order.Order.Bid).Sum(order=>order.Escrow);
            var items=orders.Where(order=>!order.Order.Bid).Sum(order=>order.Order.VolumeRemaining);
            return Py.KeyVal(("iskEscrow",Py.Number(MarketRules.RoundIsk((double)isk))),("itemsEscrow",Py.Number(items)));
        }
        catch(Exception e) when(e is MarketError or Eve.Corporations.CorporationError or MoneyLimitException or InsufficientFundsException) { throw Error(e); }
    }

    [Method("GetMarketOrderHistory")]
    public async ValueTask<PyValue> GetMarketOrderHistory(ServiceCall call)
    {
        if(call.Session.CharacterId is not { } actor) return OwnerOrderRowset([],null);
        try
        {
            await expiry.SweepAsync(call.Cancellation, readTriggered: true);
            var characterOrders=await trading.OwnerOrdersAsync(actor,false,true,call.Cancellation);
            var corporationOrders=await trading.OwnerOrdersAsync(actor,true,true,call.Cancellation);
            var history=characterOrders.Concat(corporationOrders).Where(order=>order.State!=0).ToArray();
            return ObjectCaching.MethodResult(OwnerOrderRowset(history,null),now(),sessionInfo:"charid");
        }
        catch(Exception e) when(e is MarketError or Eve.Corporations.CorporationError or MoneyLimitException or InsufficientFundsException) { throw Error(e); }
    }

    [Method("GetPlexOrders")]
    public async ValueTask<PyValue> GetPlexOrders(ServiceCall call)
    {
        var actor=call.Session.CharacterId;
        var character=actor is { } characterId ? await characters.GetAsync(characterId,call.Cancellation) : null;
        var locationId=MarketLocationId(call.Session,character?.StationId ?? 0);
        var viewer=character is null ? default : new MarketLocation(locationId,character.SolarSystemId,character.ConstellationId,character.RegionId);
        var book=character is null || character.RegionId<=0 ? new MarketBook([],[]) : await store.BookAsync(character.RegionId,PlexTypeId,now(),call.Cancellation);
        if (actor is { } actorId)
            book=new MarketBook(await VisibleOrders(actorId,book.Asks,call.Cancellation),await VisibleOrders(actorId,book.Bids,call.Cancellation));
        return ObjectCaching.MethodResult(Py.Tuple(MarketWire.Orders(book.Asks,topology,viewer),MarketWire.Orders(book.Bids,topology,viewer)),
            now(),sessionInfo:"regionid");
    }

    [Method("GetPlexBest")]
    public async ValueTask<PyValue> GetPlexBest(ServiceCall call)
    {
        var region=await MarketRegion(call);
        var entries=new List<KeyValuePair<PyValue,PyValue>>();
        if(region>0 && call.Session.CharacterId is { } actorId)
        {
            var book=await store.BookAsync(region,PlexTypeId,now(),call.Cancellation);
            var asks=await VisibleOrders(actorId,book.Asks,call.Cancellation);
            if(asks.Count>0)
            {
                var best=asks[0];
                var quantity=asks.TakeWhile(order=>order.Price==best.Price).Sum(order=>order.VolumeRemaining);
                entries.Add(new(new PyInt(PlexTypeId),MarketWire.BestByOrder(best,quantity)));
            }
        }
        return ObjectCaching.MethodResult(new PyDict(entries),now(),sessionInfo:"regionid");
    }

    [Method("GetPlexHistory")]
    public async ValueTask<PyValue> GetPlexHistory(ServiceCall call)
    {
        var pair=MarketHistoryWire.Pair(now(),await HistoryForRegion(await MarketRegion(call),PlexTypeId,call.Cancellation));
        return new PyDict([new KeyValuePair<PyValue,PyValue>(new PyInt(PlexTypeId),new PyList([pair.Old,pair.New]))]);
    }

    [Method("GetPlexOldPriceHistory")]
    public async ValueTask<PyValue> GetPlexOldPriceHistory(ServiceCall call) =>
        MarketHistoryWire.Pair(now(),await HistoryForRegion(await MarketRegion(call),PlexTypeId,call.Cancellation)).Old;

    [Method("GetPlexNewPriceHistory")]
    public async ValueTask<PyValue> GetPlexNewPriceHistory(ServiceCall call) =>
        MarketHistoryWire.Pair(now(),await HistoryForRegion(await MarketRegion(call),PlexTypeId,call.Cancellation)).New;
    [Method("PlaceBuyOrder")]
    public async ValueTask<PyValue> PlaceBuyOrder(ServiceCall call)
    {
        var request=new MarketRequest(Actor(call),Int32(call.Arg(1)),true,Money(call.Arg(2)),Integer(call.Arg(3)),Integer(call.Arg(0)),
            Int32(call.Arg(6)),Int32(call.Arg(4),-1),Integer(call.Arg(5),1),UseCorporation:JsSemantics.Truthy(call.Arg(7)),
            ExpectedBrokerFee:call.Arg(8) is PyReal quote ? quote.Value : call.Arg(8) is PyInt whole ? whole.Value : null);
        try
        {
            RefuseRepeatedPlacement(call,request);
            var receipt=await trading.PlaceAsync(request,Key(call,"buy"),call.Cancellation);
            RecordPlacement(call,request);
            await Publish(receipt);
            return PyNone.Instance;
        }
        catch (Exception e) when (e is MarketError or MoneyLimitException or InsufficientFundsException or Eve.Corporations.CorporationError) { throw Error(e); }
    }
    [Method("PlaceMultiSellOrder")]
    public async ValueTask<PyValue> PlaceMultiSellOrder(ServiceCall call)
    {
        Actor(call);
        if (call.Arg(0) is not (PyList or PyTuple))
            throw Error(new MarketError("MARKET_BULK_ARGUMENT_INVALID"));
        var entries = Sequence(call.Arg(0));
        if (entries.Count > MaxMultiSellOrderEntries)
            throw Error(new MarketError("MARKET_BULK_LIMIT_EXCEEDED"));
        var duration = Int32(call.Arg(2));
        var useCorporation = JsSemantics.Truthy(call.Arg(1));
        var itemIds = new HashSet<long>();
        foreach (var entry in entries)
        {
            var typeId = Int32(Field(entry, "typeID"));
            var quantity = Integer(Field(entry, "quantity"));
            var stationId = Integer(Field(entry, "stationID"));
            var itemId = Integer(Field(entry, "itemID"));
            var price = Money(Field(entry, "price"));
            if (typeId <= 0 || quantity <= 0 || stationId <= 0 || itemId <= 0 || price <= 0 || !itemIds.Add(itemId))
                throw Error(new MarketError("MARKET_BULK_ARGUMENT_INVALID"));
        }
        var index = 0;
        var traded = false;
        foreach (var entry in entries)
            traded |= await SellEntry(call, entry, useCorporation, duration, call.Arg(3), Key(call, "sell:" + index++));
        return traded ? PyBool.True : PyBool.False;
    }

    /// <summary>One sell line (MarketTrading.PlaceAsync): the stack moves into the order's escrow, or fills the book at once. Answers whether it traded or rests.</summary>
    private async Task<bool> SellEntry(ServiceCall call, PyValue entry, bool useCorporation, int duration, PyValue expectedFee, string key)
    {
        var request = new MarketRequest(Actor(call), Int32(Field(entry, "typeID")), false, Money(Field(entry, "price")), Integer(Field(entry, "quantity")),
            Integer(Field(entry, "stationID")), duration, ItemId: Integer(Field(entry, "itemID")), UseCorporation: useCorporation,
            ExpectedBrokerFee: expectedFee is PyReal quote ? quote.Value : expectedFee is PyInt whole ? whole.Value : null);
        try
        {
            RefuseRepeatedPlacement(call, request);
            var receipt = await trading.PlaceAsync(request, key, call.Cancellation);
            RecordPlacement(call, request);
            await Publish(receipt);
            return receipt.OrderId is not null || receipt.FilledQuantity > 0;
        }
        catch (Exception e) when (e is MarketError or MoneyLimitException or InsufficientFundsException or Eve.Corporations.CorporationError) { throw Error(e); }
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
            await expiry.SweepAsync(call.Cancellation, readTriggered: true);
            var receipt=await trading.ModifyAsync(Actor(call),Integer(call.Arg(0)),Money(call.Arg(1)),Key(call,"modify"),call.Cancellation,
                expectedPrice:call.Arg(5) is null or PyNone ? null : Money(call.Arg(5)),
                expectedIssuedMs:call.Arg(8) is null or PyNone ? null : FileTimeToUnixMs(call.Arg(8)));
            await Publish(receipt);
            return PyNone.Instance;
        }
        catch (Exception e) when (e is MarketError or MoneyLimitException or InsufficientFundsException or Eve.Corporations.CorporationError) { throw Error(e); }
    }

    /// <summary>
    /// One immediate buy per line (EVE.js proxy 6915): the line's price is the limit, the range is the station. What a line does not fill is booked as a
    /// buy order; the answer is the list of the orders created, which the client announces (marketsvc BuyMulti).
    /// </summary>
    [Method("BuyMultipleItems")]
    public async ValueTask<PyValue> BuyMultipleItems(ServiceCall call)
    {
        if (call.Arg(1) is not (PyList or PyTuple))
            throw Error(new MarketError("MARKET_BULK_ARGUMENT_INVALID"));
        var entries = Sequence(call.Arg(1));
        if (entries.Count > MaxBuyMultipleItems)
            throw Error(new MarketError("MARKET_BULK_LIMIT_EXCEEDED"));
        var actor = Actor(call);
        var stationId = Integer(call.Arg(0));
        var useCorporation = JsSemantics.Truthy(call.Arg(2));
        var requests = new List<(MarketRequest Request,string OperationKey)>();
        foreach (var (entry,line) in entries.Select((entry,line)=>(entry,line)))
        {
            var typeId = Int32(Field(entry, "typeID"));
            var quantity = Integer(Field(entry, "quantity"));
            var rawMinimumVolume = Field(entry, "minVolume");
            var minimumVolume = rawMinimumVolume is PyNone ? 1 : Integer(rawMinimumVolume, long.MinValue);
            var price = Money(Field(entry, "price"));
            if (typeId <= 0 || quantity < 0 || minimumVolume <= 0 || price <= 0 || quantity > 0 && minimumVolume > quantity)
                throw Error(new MarketError("MARKET_BULK_ARGUMENT_INVALID"));
            if (quantity == 0)
                continue;
            var request = new MarketRequest(actor, typeId, true, price, quantity, stationId, 0,
                MarketRules.RangeStation, minimumVolume, UseCorporation: useCorporation);
            RefuseRepeatedPlacement(call, request);
            if (requests.Any(previous => previous.Request == request))
                throw Error(new MarketError("MARKET_DUPLICATE_PLACEMENT"));
            requests.Add((request,Key(call,"buy-multi:" + line)));
        }
        try
        {
            var receipts = await trading.PlaceBuyOrdersAsync(requests, call.Cancellation);
            foreach (var line in requests) RecordPlacement(call, line.Request);
            var created = new List<MarketOwnedOrder>();
            foreach (var receipt in receipts)
            {
                await Publish(receipt);
                created.AddRange(receipt.ChangedOrders.Where(change => change.Reason == "Created").Select(change => change.Order));
            }
            return OwnerOrderRowset(created, null);
        }
        catch (Exception e) when (e is MarketError or MoneyLimitException or InsufficientFundsException or Eve.Corporations.CorporationError) { throw Error(e); }
    }

    /// <summary>A PLEX sell line (the client's SellPlex): the PLEX type only; it sells personally or for the corporation like any other item. Answers whether it traded or rests.</summary>
    [Method("PlacePlexSellOrder")]
    public async ValueTask<PyValue> PlacePlexSellOrder(ServiceCall call)
    {
        var entry = call.Arg(0);
        if (Integer(Field(entry, "typeID")) != PlexTypeId)
            throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("PLEX sell orders must use a PLEX inventory item.")));
        var traded = await SellEntry(call, entry, JsSemantics.Truthy(call.Arg(1)), Int32(call.Arg(2)), call.Arg(3), Key(call, "plex-sell:0"));
        return traded ? PyBool.True : PyBool.False;
    }

    /// <summary>EVE.js proxy 7274 delegates to ModifyCharOrder, and so does this.</summary>
    [Method("ModifyPlexCharOrder")]
    public ValueTask<PyValue> ModifyPlexCharOrder(ServiceCall call) => ModifyCharOrder(call);

    /// <summary>
    /// The character's transactions (EVE.js proxy 6582): the fills where the character's personal wallet bought or sold, newest first,
    /// from the fromDate (a FILETIME; none is every fill).
    /// </summary>
    [Method("CharGetTransactions")]
    public async ValueTask<PyValue> CharGetTransactions(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor) return Py.List();
        var fills = await store.WalletFillsAsync(actor, 1000, FromDateMs(call.Arg(0)), call.Cancellation);
        return Py.List([.. fills.Select(fill => TransactionRow(fill, actor))]);
    }

    /// <summary>
    /// The corporation's wallet transactions (EVE.js proxy 6601): one division, or every division when the accountKey is null. A viewer who may
    /// not read the division is refused with CrpAccessDenied, as the reference refuses it (the rule is CanReadWalletDivisionAsync).
    /// </summary>
    [Method("CorpGetTransactions")]
    public async ValueTask<PyValue> CorpGetTransactions(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } actor) return Py.List();
        int? accountKey = call.Arg(1) is null or PyNone ? null : Int32(call.Arg(1), 1000);
        try
        {
            if (!await corporations.CanReadWalletDivisionAsync(actor, accountKey, call.Cancellation))
                throw MachoErrors.ThrowUserError("CrpAccessDenied", ("reason", Py.Tuple(new PyInt(101),
                    new PyStr("UI/Corporations/AccessRestrictions/NoAccessToWalletDivision"))));
            var character = await characters.GetAsync(actor, call.Cancellation);
            if (character is null || character.CorporationId <= 0) return Py.List();
            var fills = await store.WalletFillsAsync(character.CorporationId, accountKey, FromDateMs(call.Arg(0)), call.Cancellation);
            return Py.List([.. fills.Select(fill => TransactionRow(fill, character.CorporationId))]);
        }
        catch (Exception e) when (e is Eve.Corporations.CorporationError) { throw Error(e); }
    }

    /// <summary>A fromDate (a FILETIME, which the transaction rows are filtered on) as Unix milliseconds; none or an unset value is 0.</summary>
    private static long FromDateMs(PyValue value)
    {
        var fileTime = Integer(value);
        return fileTime > 0 ? Math.Max(0, (fileTime - 116444736000000000L) / 10_000) : 0;
    }

    /// <summary>
    /// One fill as the client's transaction tabs read it (marketSvc.py 41-64): accountID and keyID are the division on the viewer's side, so the
    /// client tells a company trade from a personal one; buyerID and sellerID are the characters that acted.
    /// </summary>
    private static PyValue TransactionRow(MarketFill fill, long viewerWallet)
    {
        var viewerAccount = fill.BuyerWallet == viewerWallet ? fill.BuyerAccount : fill.SellerAccount;
        return Py.KeyVal(
            ("transactionID", new PyLong(fill.Id)),
            ("transactionDate", new PyLong(Py.FileTimeFromUnixMs(fill.TimeMs))),
            ("typeID", new PyInt(fill.TypeId)),
            ("quantity", new PyInt(fill.Quantity)),
            ("price", Py.Number((double)fill.Price)),
            ("stationID", new PyLong(fill.StationId)),
            ("locationID", new PyLong(fill.StationId)),
            ("buyerID", new PyLong(fill.BuyerId)),
            ("sellerID", new PyLong(fill.SellerId)),
            ("accountID", new PyInt(viewerAccount)),
            ("buyerAccountID", new PyInt(fill.BuyerAccount)),
            ("sellerAccountID", new PyInt(fill.SellerAccount)),
            ("journalRefID", new PyInt(-1)),
            ("keyID", new PyInt(viewerAccount)));
    }
}
