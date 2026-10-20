using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;

namespace Eve.Services.Wallet;

/// <summary>
/// <c>LPSvc</c>: the loyalty point balances of the character's wallet and of the character's corporation's wallet, read
/// from <see cref="LoyaltyPointStore"/>. The client reads both balances on first open (loyaltyPointsWalletSvc.py:29-38).
/// The CONCORD exchange and the two donations (transfers between wallets) are refused with the reason, not answered as done:
/// their rules (destination validation, the Evermark exception, director roles, membership age) are not built yet.
/// </summary>
public sealed class LoyaltyPointsService(LoyaltyPointStore store) : IService
{
    private static readonly string[] BalanceColumns = ["issuerCorpID", "loyaltyPoints"];

    private const string NotAvailable = "This loyalty point operation is not available on this server yet.";

    public string Name => "LPSvc";

    public bool HasMethod(string method) => method is
        "GetAllMyCharacterWalletLPBalances" or
        "GetAllMyCorporationWalletLPBalances" or
        "ExchangeConcordLP" or
        "TransferLPFromMyWalletToOtherCorp" or
        "TransferLPFromMyCorpWalletToOtherCorp";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "GetAllMyCharacterWalletLPBalances" => CharacterBalancesAsync(call),
        "GetAllMyCorporationWalletLPBalances" => CorporationBalancesAsync(call),
        "ExchangeConcordLP" or "TransferLPFromMyWalletToOtherCorp" or "TransferLPFromMyCorpWalletToOtherCorp" =>
            throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr(NotAvailable))),
        _ => throw new InvalidOperationException($"LPSvc has no method {call.Method}"),
    };

    private async ValueTask<PyValue> CharacterBalancesAsync(ServiceCall call)
    {
        var characterId = Positive(call.Session.Get("charid"));
        var holder = new LoyaltyHolder(LoyaltyHolderKind.Character, characterId);
        return await BalanceRowsAsync(holder, call.Cancellation);
    }

    private async ValueTask<PyValue> CorporationBalancesAsync(ServiceCall call)
    {
        var corporationId = Positive(call.Session.Get("corporationID")) is > 0 and var id
            ? id
            : Positive(call.Session.Get("corpid"));
        var holder = new LoyaltyHolder(LoyaltyHolderKind.Corporation, corporationId);
        return await BalanceRowsAsync(holder, call.Cancellation);
    }

    private async ValueTask<PyValue> BalanceRowsAsync(LoyaltyHolder holder, CancellationToken cancellationToken)
    {
        var balances = await store.ReadAsync(holder, cancellationToken);
        return Py.Rowset(BalanceColumns, balances.Select(balance => new PyTuple(
        [
            new PyInt(balance.IssuerCorpId),
            new PyInt(balance.Amount),
        ])));
    }

    private static long Positive(PyValue? value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => 0,
    };
}

public sealed class LoyaltyPointAnnouncer : IDisposable
{
    private readonly LoyaltyPointStore _store;
    private readonly CharacterSessions _sessions;

    public LoyaltyPointAnnouncer(LoyaltyPointStore store, CharacterSessions sessions)
    {
        _store = store;
        _sessions = sessions;
        store.BalanceCommitted += Changed;
    }

    private void Changed(LoyaltyPointChange change)
    {
        if (change.Holder.Kind == LoyaltyHolderKind.Character)
        {
            _sessions.Of(change.Holder.Id)?.SendNotification(
                "OnLPChange",
                "clientID",
                new PyStr("set"),
                new PyInt(change.IssuerCorpId),
                new PyInt(change.Before),
                new PyInt(change.After));
            return;
        }

        foreach (var session in _sessions.OfCorporation(change.Holder.Id))
        {
            session.SendNotification("OnCorpLPChange", "clientID", new PyStr(change.Reason), new PyInt(change.IssuerCorpId));
        }
    }

    public void Dispose() => _store.BalanceCommitted -= Changed;
}
