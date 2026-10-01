using Eve.Macho;
using Eve.Marshal;
using Eve.Wallet;

namespace Eve.Services.Wallet;

/// <summary>Personal and role-gated corporate balances (<c>account</c>), always encoded as money reals.</summary>
[Service("account")]
public sealed partial class AccountService(PgWalletStore wallets, Eve.Corporations.CorporationStore? corporations = null, WalletTransactions? transactions = null)
{
    /// <summary><c>GetCashBalance(corporation=0)</c>: the ISK the character has, as a real number.</summary>
    [Method("GetCashBalance")]
    public async ValueTask<PyValue> GetCashBalance(ServiceCall call)
    {
        if (JsSemantics.Truthy(call.Kwarg("isCorpWallet") ?? PyNone.Instance) || JsSemantics.Truthy(call.Arg(0)))
        {
            if (call.Session.CharacterId is { } actor && corporations is not null && transactions is not null)
            {
                try
                {
                    var raw = call.Kwarg("accountKey") ?? call.Arg(1);
                    var key = raw switch
                    {
                        PyInt n when n.Value is >= 1000 and <= 1006 => (int)n.Value,
                        PyStr { Value:"cash" } => 1000,
                        PyStr s when s.Value.Length == 5 && s.Value.StartsWith("cash",StringComparison.Ordinal) && s.Value[4] is >= '2' and <= '7' => 999 + s.Value[4] - '0',
                        _ => 1000,
                    };
                    var account = await corporations.WalletAccountAsync(actor,key,false,call.Cancellation);
                    return new PyReal((double)(await transactions.AccountBalanceAsync(account,call.Cancellation) ?? 0m));
                }
                catch (Eve.Corporations.CorporationError) { }
            }
            throw MachoErrors.ThrowUserError(
                "CrpAccessDenied",
                ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/NoAccessToWalletDivision"))));
        }
        return call.Session.CharacterId is { } id && await wallets.GetAsync(id, call.Cancellation) is { } balance
            ? new PyReal((double)balance.Isk)
            : PyNone.Instance;
    }
}
