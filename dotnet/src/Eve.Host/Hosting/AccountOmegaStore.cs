using System.Globalization;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host;

/// <summary>
/// The Omega expiry every answer reads: the account's stored subscription end time (the accounts table), as the FILETIME
/// text the subscription rules parse. An account with no stored end time has no Omega licence.
/// </summary>
public sealed class AccountOmegaStore(IAccountSubscriptions subscriptions) : IOmegaAccountStore
{
    public string? GetOmegaExpiry(long accountId)
    {
        if (accountId <= 0 || accountId > uint.MaxValue)
        {
            return null;
        }
        return subscriptions.SubscriptionEndsAt((uint)accountId) is { } endsAt
            ? Py.FileTimeFromUnixMs(endsAt.ToUnixTimeMilliseconds()).ToString(CultureInfo.InvariantCulture)
            : null;
    }
}
