using Eve.Market;
using Eve.Marshal;
using Eve.Wallet;

namespace Eve.Services.Market;

/// <summary>
/// The client's notices of a market receipt, and the cache coherence that goes with them. Before the notices, the cached results that the
/// receipt made stale are dropped (M4). The client keeps a book (GetOrders, the station and system asks) and its owner lists in objectCaching
/// for the whole run (versionCheck "run", objectCaching.py:576-588), so a change is pushed as InvalidateCachedMethodCall, the mechanism the
/// fittings use (FittingMgrServices.cs:248). The client adds its own session attribute (region, system, station, character, corporation) to
/// the key, so the arguments name only the method and its own arguments. The notices reach the characters that see the order: the RPCs answer
/// with them, and the expiry jobs send them, since a job has no call to answer.
/// </summary>
public sealed class MarketNotifier(WalletTransactions wallets, CharacterSessions? sessions)
{
    private const string ObjectCachingService = "objectCaching";
    private const string MarketService = "marketProxy";
    private const int PlexTypeId = 44992;

    public async Task PublishAsync(MarketReceipt receipt)
    {
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
    }

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

    /// <summary>The owner lists of each character a change reaches: its own orders, or the corporation's for a corporation order, and its history.</summary>
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
