using Eve.Marshal;
using Eve.Wallet;

namespace Eve.Services.Wallet;

/// <summary>Protocol-side observer of explicit, committed wallet changes.</summary>
public sealed class WalletAnnouncer : IDisposable
{
    private readonly WalletTransactions _transactions;
    private readonly CharacterSessions _sessions;

    public WalletAnnouncer(WalletTransactions transactions,CharacterSessions sessions)
    {
        _transactions=transactions; _sessions=sessions;
        transactions.BalanceCommitted += Changed;
    }
    private void Changed(WalletBalanceChange change)
    {
        var key=change.AccountKey == 1000 ? "cash" : $"cash{change.AccountKey-999}";
        var character=_sessions.Of(change.OwnerId);
        if (character is not null)
        {
            character.SendNotification("OnAccountChange","clientID",new PyStr(key),new PyInt(change.OwnerId),new PyReal((double)change.Balance));
            return;
        }
        foreach(var session in _sessions.OfCorporation(change.OwnerId))
            session.SendNotification("OnAccountChange","*corpid&corpAccountKey",new PyStr(key),new PyInt(change.OwnerId),new PyReal((double)change.Balance));
    }
    public void Dispose() => _transactions.BalanceCommitted -= Changed;
}
