using Eve.Identity;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host;

/// <summary>
/// An operator's change to an account's stored subscription end time. The stored value is written first, so every answer
/// reads the new state; then each open session of the account is told its new clone grade (the notifications the client
/// expects on an expiry: OnSubscriptionChangedServer and OnSubscriptionTimeUpdateServer).
/// </summary>
public static class SubscriptionTransitions
{
    public static void SetSubscriptionEnd(
        IAccountSubscriptions subscriptions, OmegaLicenses licenses, IEnumerable<ClientSession> openSessions, uint accountId, DateTimeOffset endsAt)
    {
        subscriptions.SetSubscriptionEndsAt(accountId, endsAt);
        var license = licenses.Resolve(new PyInt((long)accountId));
        var cloneGrade = license.HasLicense ? 1L : 0L;
        foreach (var session in openSessions)
        {
            if (session.UserId == accountId)
            {
                SubscriptionMgrService.AnnounceCloneGrade(session, cloneGrade, license.ExpiryFileTime);
            }
        }
    }
}
