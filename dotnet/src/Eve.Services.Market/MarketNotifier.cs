using Eve.Macho;
using Eve.Inventory;
using Eve.Market;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Wallet;

namespace Eve.Services.Market;

public sealed class MarketNotifier(WalletTransactions wallets, CharacterSessions? sessions, PgItemStore items, ItemTypeCatalog itemTypes,
    Func<IReadOnlyList<ItemChanged>,ClientSession?,Task>? corporationInventoryObservers = null)
{
    private const string ObjectCachingService = "objectCaching";
    private const string MarketService = "marketProxy";
    private const int PlexTypeId = 44992;

    private sealed record BuyerDelivery(Item Item, ItemRow Row);

    public async Task PublishAsync(MarketReceipt receipt, MarketRequest? immediateFill = null, ClientSession? actorSession = null)
    {
        var deliveries = await ResolveDeliveriesAsync(receipt.DeliveredItemIds);

        // A successful replay announces the current balance, not the original receipt's historical balance.
        foreach (var account in receipt.ChangedWallets)
            if (await wallets.AccountBalanceAsync(account) is { } balance)
                wallets.AnnounceCommitted(new(account.OwnerId,account.AccountKey,balance));

        var changes=receipt.ChangedOrders;
        if (changes.Count==0 && receipt.ChangedOrder is { } legacyOrder)
            changes=[new(legacyOrder,receipt.ChangedOrderReason ?? "Modified",[legacyOrder.ActorId])];
        InvalidateBooks(receipt.TouchedBooks);
        InvalidateOwnerLists(changes);
        foreach (var change in changes)
        {
            var orderIsCorp=change.Order.WalletOwnerId != change.Order.ActorId;
            foreach (var characterId in change.RecipientCharacterIds.Distinct())
                sessions?.Of(characterId)?.SendNotification("OnOwnOrdersChanged","charid",
                    Py.List(MarketProxyService.OrderNotificationEntry(change.Order)),new PyStr(change.Reason),new PyInt(orderIsCorp ? 1 : 0));
        }
        if (immediateFill is { } request && receipt.OrderId is null && receipt.FilledQuantity > 0 &&
            (actorSession ?? sessions?.Of(request.ActorId)) is { } session)
            session.SendNotification("OnOwnOrdersChanged","charid",Py.List(),
                new PyStr("Filled"),new PyInt(request.UseCorporation ? 1 : 0));

        await PublishInventoryChangesAsync(receipt.InventoryChanges,actorSession);

        foreach (var buyer in deliveries.GroupBy(delivery => delivery.Item.OwnerId))
            if (sessions?.Of(buyer.Key) is { } buyerSession)
                buyerSession.SendNotification("OnItemsChanged","charid",
                    new PyList([.. buyer.Select(delivery => InventoryWire.PackedRow(delivery.Row))]),
                    CreatedItemDifferences(),PyNone.Instance);
        foreach (var delivery in deliveries)
            sessions?.Of(delivery.Item.OwnerId)?.SendNotification("OnMarketItemReceived","clientID",
                InventoryWire.Id(delivery.Item.Id),new PyInt(delivery.Item.TypeId),new PyInt(delivery.Item.Flag),
                InventoryWire.Id(delivery.Item.LocationId));
    }

    private async Task PublishInventoryChangesAsync(IReadOnlyList<MarketInventoryChange> inventoryChanges,ClientSession? actorSession)
    {
        if (inventoryChanges.Count==0) return;
        var changes=inventoryChanges.Select(change => new ItemChanged(
            change.After ?? change.Before with { LocationId=CharacterShips.JunkLocation },
            change.Before.LocationId,change.Before.Flag,change.Before.Quantity,change.Before.OwnerId)).ToArray();
        var typeIds=changes.Select(change => change.After.TypeId).Distinct().ToArray();
        var types=await itemTypes.GetAsync(typeIds);
        foreach (var change in inventoryChanges.Zip(changes))
        {
            var session=actorSession?.CharacterId==change.First.CharacterId ? actorSession : sessions?.Of(change.First.CharacterId);
            if (session is null) continue;
            var itemChange=change.Second;
            if (!types.TryGetValue(itemChange.After.TypeId,out var type))
                throw new InvalidOperationException($"Committed market inventory type {itemChange.After.TypeId} is missing from the SDE.");
            ShipNotifications.SendItemChange(session,itemChange,item => new ItemRow(item.Id,item.TypeId,item.OwnerId,item.LocationId,
                item.Flag,item.Singleton ? -1 : item.Quantity,type.GroupId,type.CategoryId));
        }
        if (corporationInventoryObservers is not null)
        {
            try { await corporationInventoryObservers(changes,actorSession); }
            catch (Exception error) { Console.Error.WriteLine($"eve-host: market corporation inventory notification failed: {error.Message}"); }
        }
    }

    /// <summary>Completes BuyMultipleItems once, after its per-line fills have committed and their deliveries are announced.</summary>
    public void PublishImmediateBuyOrders(IReadOnlyList<MarketOwnedOrder> orders)
    {
        if (orders.Count == 0) return;
        var first=orders[0];
        if (orders.Any(order => order.ActorId != first.ActorId || order.WalletOwnerId != first.WalletOwnerId ||
            order.AccountKey != first.AccountKey))
            throw new InvalidOperationException("A multi-buy completion must describe one pilot and wallet.");

        var isCorp=first.WalletOwnerId != first.ActorId;
        var ownerChange=new MarketOrderChange(first,"Expiry",[first.ActorId]);
        InvalidateOwnerLists([ownerChange]);
        sessions?.Of(first.ActorId)?.SendNotification("OnOwnOrdersChanged","charid",
            Py.List([.. orders.Select(MarketProxyService.OrderNotificationEntry)]),new PyStr("Expiry"),new PyInt(isCorp ? 1 : 0));
    }

    private async Task<IReadOnlyList<BuyerDelivery>> ResolveDeliveriesAsync(IReadOnlyList<long> itemIds)
    {
        if (itemIds.Count == 0) return [];

        var deliveredItems = new List<Item>(itemIds.Count);
        foreach (var itemId in itemIds)
            deliveredItems.Add(await items.GetAsync(itemId) ??
                throw new InvalidOperationException($"Committed market delivery item {itemId} is missing from inventory."));

        var types = await itemTypes.GetAsync(deliveredItems.Select(item => item.TypeId));
        var result = new List<BuyerDelivery>(deliveredItems.Count);
        foreach (var item in deliveredItems)
        {
            if (!types.TryGetValue(item.TypeId,out var type))
                throw new InvalidOperationException($"Committed market delivery type {item.TypeId} is missing from the SDE.");
            result.Add(new(item,new ItemRow(item.Id,item.TypeId,item.OwnerId,item.LocationId,item.Flag,
                item.Singleton ? -1 : item.Quantity,type.GroupId,type.CategoryId)));
        }
        return result;
    }

    // The buyer has no cached previous container or stack size for an item just delivered.
    private static PyDict CreatedItemDifferences() => new([
        new KeyValuePair<PyValue,PyValue>(new PyInt(3),new PyInt(0)),
        new KeyValuePair<PyValue,PyValue>(new PyInt(9),new PyInt(0)),
    ]);

    /// <summary>
    /// The book results of a region: the orders of each type that changed, and the station and system asks, which every session of the region
    /// keys by its own station or system. A session whose known region differs keys no book of this region and is not asked.
    /// </summary>
    private void InvalidateBooks(IReadOnlyList<TouchedBook> books)
    {
        foreach (var regionId in books.Select(book => book.RegionId).Distinct())
        {
            var regional = SessionsInRegion(regionId);
            foreach (var session in regional)
            {
                Invalidate(session,"GetStationAsks");
                Invalidate(session,"GetSystemAsks");
            }
            foreach (var typeId in books.Where(book => book.RegionId==regionId).Select(book => book.TypeId).Distinct())
            {
                foreach (var session in regional)
                {
                    Invalidate(session,"GetOrders",new PyInt(typeId));
                    if (typeId==PlexTypeId)
                    {
                        Invalidate(session,"GetPlexOrders");
                        Invalidate(session,"GetPlexBest");
                    }
                }
            }
        }
    }

    private void InvalidateOwnerLists(IReadOnlyList<MarketOrderChange> changes)
    {
        foreach (var change in changes)
        {
            var corporate=change.Order.WalletOwnerId != change.Order.ActorId;
            foreach (var characterId in change.RecipientCharacterIds.Distinct())
            {
                var session=sessions?.Of(characterId);
                Invalidate(session,corporate ? "GetCorporationOrders" : "GetCharOrders");
                Invalidate(session,"GetMarketOrderHistory");
            }
        }
    }

    /// <summary>The sessions that may hold a result of a region: every session with a character, unless its known region differs.</summary>
    private IReadOnlyList<ClientSession> SessionsInRegion(int regionId)
    {
        IReadOnlyList<ClientSession> all=sessions?.All() ?? [];
        return [.. all.Where(session => session.GetInt("regionid") is not long known || known==regionId)];
    }

    /// <summary>Asks the client to drop one cached result: objectCaching.InvalidateCachedMethodCall(service, method, args), sent to its objectCaching service.</summary>
    private static void Invalidate(ClientSession? session,string method,params PyValue[] arguments)
    {
        if (session is null || session.ClientId <= 0) return;
        session.Send(ClientCallRequest.Build(session.ClientId,session.UserId ?? 0,ObjectCachingService,"InvalidateCachedMethodCall",
            [new PyStr(MarketService),new PyStr(method),.. arguments],ClientCallRequest.NextCallId()));
    }
}
