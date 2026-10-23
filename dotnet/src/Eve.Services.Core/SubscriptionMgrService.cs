using System.Globalization;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>Where an account's stored Omega subscription end time is read from, as FILETIME text (null when it has none).</summary>
public interface IOmegaAccountStore
{
    /// <summary>The account's stored end time as FILETIME text, trimmed, or null when it has none.</summary>
    string? GetOmegaExpiry(long accountId);
}

/// <summary>The outcome of looking up an account's Omega licence.</summary>
public sealed record OmegaLicense(bool HasLicense, long? ExpiryFileTime);

/// <summary>
/// Resolves an account's Omega licence from its stored subscription end time and the clock. Nothing else decides it: no
/// setting or environment variable grants or removes Omega, so the selection screen, the subscription services, the
/// clone-grade answers and the skill and injector rules all read the same state.
/// </summary>
public sealed class OmegaLicenses(IOmegaAccountStore accounts, Func<long> nowUnixMs)
{
    /// <summary>2100-01-01T00:00:00Z as a FILETIME: the end time every account is written with (SubscriptionPolicy in Eve.Identity).</summary>
    public const long DefaultOmegaExpiryFileTime = 157469184000000000L;

    /// <summary><see cref="DefaultOmegaExpiryFileTime"/> as the text the account store returns.</summary>
    public static string DefaultOmegaExpiryText => DefaultOmegaExpiryFileTime.ToString(CultureInfo.InvariantCulture);

    public long NowFileTime => Py.FileTimeFromUnixMs(nowUnixMs());

    /// <summary><c>BigInt(text)</c> for a stored expiry; 0 when JS would throw.</summary>
    private static long ParseExpiry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }
        var parsed = JsNumber.BigIntFromString(text);
        return parsed is { } value && value >= long.MinValue && value <= long.MaxValue ? (long)value : 0;
    }

    /// <summary>The account's stored end time (a FILETIME), or 0 when it has none.</summary>
    public long StoredExpiry(PyValue? userId)
    {
        var accountId = JsSemantics.ToPositiveInteger(userId);
        return accountId > 0 ? ParseExpiry(accounts.GetOmegaExpiry(accountId)) : 0;
    }

    public OmegaLicense Resolve(PyValue? userId)
    {
        var expiry = StoredExpiry(userId);
        return expiry > NowFileTime ? new OmegaLicense(true, expiry) : new OmegaLicense(false, null);
    }

    /// <summary>The clone grade derived from the same stored licence used by subscription services.</summary>
    public long CloneGradeOf(PyValue? userId) => Resolve(userId).HasLicense ? 1 : 0;

}

/// <summary>Subscription status queries the client makes while logging in, and the clone-grade notifications.</summary>
[Service("subscriptionMgr")]
public sealed partial class SubscriptionMgrService(OmegaLicenses licenses)
{
    private const long CloneStateAlpha = 0;
    private const long CloneStateOmega = 1;
    private const long FileTimeTicksPerDay = 864000000000L;

    /// <summary>The session attribute holding the clone grade last told to the session. The client never reads it.</summary>
    public const string AnnouncedCloneGradeKey = "subscriptionCloneGrade";

    /// <summary>The clone grade of an account (0 Alpha, 1 Omega), as the clone-grade answers report it.</summary>
    public long CloneGradeOf(PyValue? userId) => licenses.CloneGradeOf(userId);

    private (long CloneGrade, bool IsSubscribed, long? EndTime, long DaysRemaining) State(ServiceCall call)
    {
        var license = licenses.Resolve(call.Session.Get("userid"));
        var cloneGrade = license.HasLicense ? CloneStateOmega : CloneStateAlpha;
        long? endTime = license.HasLicense ? license.ExpiryFileTime : null;
        long days = 0;
        if (endTime is { } end)
        {
            var remaining = end - licenses.NowFileTime;
            if (remaining > 0)
            {
                days = (remaining + FileTimeTicksPerDay - 1) / FileTimeTicksPerDay;
            }
        }
        NoteCloneGrade(call.Session, cloneGrade, endTime);
        return (cloneGrade, license.HasLicense, endTime, days);
    }

    /// <summary>
    /// Tells a session its clone grade and end time: the client caches the grade (OnSubscriptionChangedServer) and shows the
    /// time (OnSubscriptionTimeUpdateServer; None for Alpha). Used when an operator changes the account's end time.
    /// </summary>
    public static void AnnounceCloneGrade(ClientSession session, long cloneGrade, long? endTime)
    {
        session.Set(AnnouncedCloneGradeKey, new PyInt(cloneGrade));
        session.SendNotification("OnSubscriptionChangedServer", "clientID", new PyInt(cloneGrade));
        session.SendNotification("OnSubscriptionTimeUpdateServer", "clientID", endTime is { } end ? new PyLong(end) : PyNone.Instance);
    }

    /// <summary>
    /// A query that finds the grade changed since the session last heard it (the end time passed while it was logged in)
    /// announces the change. The first answer only records the grade: the client has it from this answer.
    /// </summary>
    private static void NoteCloneGrade(ClientSession session, long cloneGrade, long? endTime)
    {
        var announced = session.Get(AnnouncedCloneGradeKey) is PyInt known ? known.Value : (long?)null;
        if (announced is null)
        {
            session.Set(AnnouncedCloneGradeKey, new PyInt(cloneGrade));
        }
        else if (announced != cloneGrade)
        {
            AnnounceCloneGrade(session, cloneGrade, endTime);
        }
    }

    private static PyValue EndTime(long? endTime) => endTime is { } value ? Py.FileTimeLong(value) : PyNone.Instance;

    private static void RequireNoArguments(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Args.Count != 0 || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("SUBSCRIPTION_ARGUMENTS_INVALID");
    }

    [Method("GetSubscriptionStatus")]
    public PyValue GetSubscriptionStatus(ServiceCall call)
    {
        RequireNoArguments(call);
        var state = State(call);
        return Py.KeyVal(
            ("isSubscribed", PyValue.Of(state.IsSubscribed)),
            ("subscriptionDaysRemaining", new PyInt(state.DaysRemaining)),
            ("subscriptionEndTime", EndTime(state.EndTime)));
    }

    [Method("GetSubscriptionInfo")]
    public PyValue GetSubscriptionInfo(ServiceCall call)
    {
        RequireNoArguments(call);
        var state = State(call);
        return Py.KeyVal(
            ("isSubscribed", PyValue.Of(state.IsSubscribed)),
            ("subscriptionDaysRemaining", new PyInt(state.DaysRemaining)),
            ("cloneGrade", new PyInt(state.CloneGrade)),
            ("subscriptionEndTime", EndTime(state.EndTime)));
    }

    [Method("GetSubscriptionTime")]
    public PyValue GetSubscriptionTime(ServiceCall call)
    {
        RequireNoArguments(call);
        return EndTime(State(call).EndTime);
    }

    [Method("GetCloneGrade")]
    public PyValue GetCloneGrade(ServiceCall call)
    {
        RequireNoArguments(call);
        return new PyInt(State(call).CloneGrade);
    }
}


[Service("loginRewardFacilities")]
public sealed partial class LoginRewardFacilitiesService(SubscriptionMgrService subscriptions)
{
    private readonly SubscriptionMgrService _subscriptions =
        subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));

    [Method("get_my_clone_grade")]
    public PyValue GetMyCloneGrade(ServiceCall call) => _subscriptions.GetCloneGrade(call);
}
