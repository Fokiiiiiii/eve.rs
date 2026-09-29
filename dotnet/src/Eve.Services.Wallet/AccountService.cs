using Eve.Macho;
using Eve.Marshal;
using Eve.Wallet;

namespace Eve.Services.Wallet;

/// <summary>The character's own money (<c>account</c>). A corporation's wallet needs a role no player has yet.</summary>
[Service("account")]
public sealed partial class AccountService(PgWalletStore wallets)
{
    /// <summary><c>GetCashBalance(corporation=0)</c>: the ISK the character has, as a real number.</summary>
    [Method("GetCashBalance")]
    public async ValueTask<PyValue> GetCashBalance(ServiceCall call)
    {
        if (JsSemantics.Truthy(call.Arg(0)))
        {
            throw MachoErrors.ThrowUserError(
                "CrpAccessDenied",
                ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/NoAccessToWalletDivision"))));
        }
        return call.Session.CharacterId is { } id && await wallets.GetAsync(id, call.Cancellation) is { } balance
            ? new PyReal((double)balance.Isk)
            : PyNone.Instance;
    }
}
