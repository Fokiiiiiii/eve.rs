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
        Announce(openSessions, accountId, endsAt, licenses.NowFileTime);
    }

    /// <summary>
    /// Tells the account's open sessions the clone grade and end time it now has (after the end time was stored and committed): Omega while the
    /// end time is later than <paramref name="nowFileTime"/>, Alpha (with no end time shown) otherwise.
    /// </summary>
    public static void Announce(IEnumerable<ClientSession> openSessions, uint accountId, DateTimeOffset endsAt, long nowFileTime)
    {
        var endFileTime = Py.FileTimeFromUnixMs(endsAt.ToUnixTimeMilliseconds());
        var omega = endFileTime > nowFileTime;
        foreach (var session in openSessions)
        {
            if (session.UserId == accountId)
            {
                SubscriptionMgrService.AnnounceCloneGrade(session, omega ? 1L : 0L, omega ? endFileTime : null);
            }
        }
    }
}
