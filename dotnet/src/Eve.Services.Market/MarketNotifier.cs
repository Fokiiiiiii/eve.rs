using Eve.Market;
using Eve.Marshal;
using Eve.Wallet;

namespace Eve.Services.Market;

/// <summary>
/// The client's notices of a market receipt: the committed balances of the wallets it touched, and the OnOwnOrdersChanged of every order
/// that changed, to the characters that see it. The RPCs answer with it and the expiry jobs send it, since a job has no call to answer.
/// </summary>
public sealed class MarketNotifier(WalletTransactions wallets, CharacterSessions? sessions)
{
    public async Task PublishAsync(MarketReceipt receipt)
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
                sessions?.Of(characterId)?.SendNotification("OnOwnOrdersChanged","charid",
                    Py.List(MarketProxyService.OrderNotificationEntry(change.Order)),new PyStr(change.Reason),new PyInt(orderIsCorp ? 1 : 0));
        }
    }
}
